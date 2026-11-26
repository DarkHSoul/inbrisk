# Sıradaki TODO — `HumanInterventionMode` Uygulama Planı

`ARCHITECTURE.md` ("Human emergency stop (M5)") bölümünde temeli atılan ve [EmergencyControl.cs](file:///c:/Users/Ahmet/Documents/inbrisk/src/Inbrisk.Mcp/EmergencyControl.cs) 9. satırda yalnızca enum olarak tanımlı olan `HumanInterventionMode` özelliğinin uçtan uca hayata geçirilmesi planıdır.

```csharp
public enum HumanInterventionMode { Off, PauseOnKeyboard, PauseOnMouse, PauseOnAnyInput }
```

---

## 1. Amaç ve Davranış Sözleşmesi

AI ajanı bilgisayar üzerinde aktif bir plan veya girdi yürütürken, **kullanıcı fiziksel olarak klavyeye veya fareye dokunursa**:
- İnject edilmiş (ajanın kendi gönderdiği) girdiler **yok sayılır** (`LLKHF_INJECTED` / `LLMHF_INJECTED`).
- Gerçek insan girdisi algılandığında ajan derhal **temiz güvenli noktada duraklatılır (`Paused`)** — acil durdurma (`EmergencyStopped`) ile karıştırılmaz; kullanıcı panik hotkey'ini (`Ctrl+Alt+Pause`) her zaman üstün öncelikle kullanmaya devam edebilir.
- HUD (`ActivityHudService`) amber insan-kontrolü durumuna geçer: `⏸ İnsan müdahalesi algılandı (Klavye/Fare)`.
- Bekleyen fiziksel tuş/fare basılı tutmaları (`ReleaseAll()`) güvenli şekilde bırakılır.
- Devam etme: Kullanıcı `computer_resume_run` veya tray/HUD üzerinden (veya isteğe bağlı yapılandırılabilir sakinlik süresi — varsayılan **manuel devam**) onay verene kadar ajan yeni fiziksel girdi enjekte edemez.

---

## 2. Teknik Tasarım

### 2.1 Düşük Seviye Fiziksel Girdi İzleyici (`Inbrisk.Platform.Windows`)
Yeni sınıf: `src/Inbrisk.Platform.Windows/Input/PhysicalInputMonitor.cs`
- Dedike mesaj döngüsü thread'i üzerinde `SetWindowsHookEx(WH_KEYBOARD_LL)` ve `SetWindowsHookEx(WH_MOUSE_LL)` kurar (yalnızca `Mode != Off` ve ajan aktifken veya sürekli hafif filtre ile).
- **Injected vs Physical ayrımı:**
  - Klavye (`KBDLLHOOKSTRUCT.flags`): `LLKHF_INJECTED (0x10)` veya `LLKHF_LOWER_IL_INJECTED (0x02)` set ise **yok say**.
  - Fare (`MSLLHOOKSTRUCT.flags`): `LLMHF_INJECTED (0x01)` veya `LLMHF_LOWER_IL_INJECTED (0x02)` set ise **yok say**.
- **Gürültü / Yanlış Tetikleme Koruması (Debounce & Deadzone):**
  - Fare için 1–2 piksellik sensör titremesini engellemek adına yapılandırılabilir hareket eşiği (varsayılan `MouseMoveThresholdPx = 12`, tıklama/tekerlek için eşik `0`).
  - Panic/Resume hotkey tuş kombinasyonları `GlobalHotkeyService` önceliğinde kalır, müdahale duraklatmasıyla çakışmaz.
  - Hook callback'i kesinlikle bloklanmaz (< 1 ms): yalnızca lock-free sinyal/event kuyruğuna yazar, `CallNextHookEx` hemen çağrılır (Windows'un hook'u düşürmesini önler).

### 2.2 `EmergencyControl` & `McpSession` Entegrasyonu (`Inbrisk.Mcp`)
- `EmergencyControl` içine `HumanInterventionMode Mode` özelliği ve `IPhysicalInputMonitor` arayüzü enjekte edilir (testlerde `FakePhysicalInputMonitor` ile tamamen headless test edilebilir).
- Fiziksel girdi sinyali geldiğinde ve aktif bir mutasyon/plan çalışıyorsa:
  1. `ComputerControlState` → `Paused` (sebep: `HumanKeyboardIntervention` / `HumanMouseIntervention`).
  2. Aktif run varsa `PauseRun(reason)` tetiklenir, `ReleaseAll()` çağrılır.
  3. JSONL telemetri + MCP `inbrisk/desktop_event` bildirimi yayınlanır.
  4. Ghost OS modunda (`SessionDesktop.Ghost`) çalışırken kullanıcının ana masaüstündeki klavye/faresi **müdahale olarak sayılmaz** (Ghost OS'un varlık amacı budur).

### 2.3 Ayarlar, UI ve MCP Görünürlüğü
- `UserSettings.cs`: `HumanInterventionMode` (varsayılan: `PauseOnAnyInput` veya `Off` — kullanıcı tercihine göre) + `MouseMoveThresholdPx` kalıcı kaydedilir.
- `InbriskControlWindow.cs` (Safety sayfası): Mod seçici (Kapalı / Yalnızca Klavye / Yalnızca Fare / Klavye ve Fare) + fare hassasiyet ayarı.
- `computer_capabilities` & `computer_observe`: Aktif `humanInterventionMode` ve `pausedByHumanIntervention` durumunu raporlar.

---

## 3. Uygulama Adımları

1. `Inbrisk.Core`: `IPhysicalInputMonitor` arayüzü, `PhysicalInputEvent` kaydı ve `UserSettings` alanlarını ekle.
2. `Inbrisk.Platform.Windows`: `PhysicalInputMonitor` (`WH_KEYBOARD_LL` / `WH_MOUSE_LL`, `LLKHF_INJECTED`/`LLMHF_INJECTED` filtresi, fare deadzone, temiz `Dispose`).
3. `Inbrisk.Mcp`: `EmergencyControl` ve `McpSession` entegrasyonu; aktif plan duraklatma + Ghost OS istisnası + HUD bildirimi.
4. `Inbrisk.Cli`: `InbriskControlWindow` Safety sekmesine mod seçici ekle.
5. **Testler (`HumanInterventionModeTests.cs`):**
   - `Off` modunda fiziksel girdi duraklatmaz.
   - `PauseOnKeyboard` modunda fiziksel tuş duraklatır, fare duraklatmaz.
   - `PauseOnMouse` modunda eşik üstü fare hareketi/tıklaması duraklatır, mikro titreme (< threshold) duraklatmaz.
   - `LLKHF_INJECTED` / `LLMHF_INJECTED` bayraklı olaylar hiçbir modda yanlış duraklatma yapmaz.
   - `SessionDesktop.Ghost` modundayken ana masaüstü girdisi ghost planını duraklatmaz.
   - Panic hotkey (`Ctrl+Alt+Pause`) `Paused` durumunu her zaman `EmergencyStopped` durumuna yükseltir.
