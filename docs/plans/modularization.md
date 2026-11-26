# Modülerizasyon Planı (God-File Refactoring)

Bu plan, Inbrisk kod tabanındaki aşırı büyümüş, birden fazla sorumluluğu tek dosyada toplayan ("god-file") C# dosyalarını **sıfır davranış değişikliği (zero-regression)** ilkesiyle küçük, tek sorumluluklu ve bağımsız test edilebilir modüllere ayırmayı hedefler.

---

## 1. Mevcut Envanter (En Kritik God-File'lar)

| Öncelik | Dosya | Satır | Temel Sorun |
|---|---|---:|---|
| **P0** | `src/Inbrisk.Mcp/InbriskTools.cs` | 11.485 | Tüm MCP araçları, plan/batch motoru, seçici çözümleme, ekran yakalama, pencere yönetimi, tarifler, adaptörler tek sınıfta |
| **P0** | `src/Inbrisk.Platform.Windows/Apps/AppService.cs` | 2.831 | Uygulama keşfi, AUMID/UWP çözümleme, launch, readiness bekleme, kapanış ve süreç sahipliği tek dosyada |
| **P1** | `src/Inbrisk.Cli/GhostControlCommand.cs` | 2.461 | CLI argüman ayrıştırma, tray yaşam döngüsü, PiP kontrolü, IPC istemcisi, log/status tek dosyada |
| **P1** | `src/Inbrisk.Platform.Windows/Uia/UiaBackend.cs` | 2.028 | Ağaç tarama/budama, CacheRequest, pattern çağrıları, ilişkisel sorgular, yeniden çözümleme tek dosyada |
| **P1** | `src/Inbrisk.Runtime/GhostWorkerDaemon.cs` | 2.009 | Oturum içi worker döngüsü, yakalama akışı, girdi yürütme, UIA köprüsü ve IPC protokol işleyicileri iç içe |
| **P1** | `src/Inbrisk.Mcp/GhostMcpBridge.cs` | 1.974 | Ghost MCP araçları, oturum yaşam döngüsü, batch yönlendirme ve hata çevirisi tek sınıfta |
| **P1** | `src/Inbrisk.Platform.Windows/GhostDesktopInput.cs` & `src/Inbrisk.Runtime/GhostDesktopInput.cs` | 1.961 + 715 | İki katmanda aynı dosya adı; Win32 girdi enjeksiyonu ile runtime koordinasyonu karışmış |
| **P2** | `src/Inbrisk.Platform.Windows/GhostInputExecutor.cs` | 1.528 | Klavye, fare, sürükleme, hotkey ve tuş basılı tutma yürütücüleri tek dosyada |
| **P2** | `src/Inbrisk.Cli/Ui/InbriskControlWindow.cs` & `SetupApp.cs` | 1.445 + 1.221 | WPF XAML/kod-arkası içinde iş mantığı, ayar yönetimi ve UI çizimi iç içe (MVVM yok) |
| **P2** | `src/Inbrisk.Runtime/GhostKeyboardForwarder.cs` | 1.314 | Global hotkey + PiP klavye yakalama + IPC yönlendirme tek sınıfta |
| **P2** | `src/Inbrisk.Cli/Program.cs` | 1.259 | Tüm CLI alt komutlarının yönlendirme ve gövdeleri tek dosyada |
| **P2** | `src/Inbrisk.Platform.Windows/Hud/ActivityHudService.cs` | 1.248 | HUD pencere çizimi, animasyon/durum makinesi ve politika dinleyicisi tek sınıfta |
| **P2** | `src/Inbrisk.Runtime/Executor.cs` | 1.166 | Aksiyon yürütme, retry, fallback, doğrulama ve telemetri iç içe |

---

## 2. Temel Refactoring İlkeleri

1. **Önce `partial class` + dosya bölme, sonra sorumluluk ayrıştırma:**
   - Özellikle `InbriskTools.cs` (MCP SDK'nın `[McpServerToolType]` taraması) için ilk adım `partial class InbriskTools` ile alan bazlı dosyalara bölmek, ikinci adım ise iş mantığını `Inbrisk.Runtime` servislerine çekmektir.
2. **Dış API ve MCP şema uyumluluğu korunur:**
   - Araç adları (`computer_*`), parametre adları, JSON çıktı şemaları ve hata kodları (`OutcomeKind`) değişmez.
3. **Dosya boyutu üst sınırı (hedef):**
   - Yeni/bölünmüş dosyalar tercihen **250–450 satır** aralığında tutulur; 600 satırı aşan sınıflar incelemeye tabi olur.
4. **Her faz sonunda doğrulama:**
   - `dotnet build inbrisk.sln` (0 hata, yeni uyarı yok) + ilgili xUnit test alt kümesi yeşil olmadan sonraki faza geçilmez.

---

## 3. Fazlı Uygulama Planı

### Faz 1 — `InbriskTools.cs` (11.485 satır) Bölünmesi
Hedef klasör: `src/Inbrisk.Mcp/Tools/`

1. `InbriskTools.Observation.cs` — `computer_observe`, `computer_find`, `computer_inspect`, `computer_read`, `computer_screenshot`
2. `InbriskTools.Interaction.cs` — `computer_click`, `computer_type`, `computer_key`, `computer_hotkey`, `computer_scroll`, `computer_drag`, `computer_invoke`, `computer_set_value`, `computer_reset_input`
3. `InbriskTools.Execution.cs` — `computer_run`, `computer_batch`, `computer_do`, `computer_pause_run`, `computer_resume_run`, `computer_cancel_task`
4. `InbriskTools.PlanEngine.cs` — `scan`/`for_each` döngü motoru, değişken çözümleme (`$var`, `{{var}}`), koşul değerlendirici (`ifExists`, `ifValue` vb.)
5. `InbriskTools.Windows.cs` — `computer_windows`, `computer_launch`, `computer_close_window`, `computer_focus_window`, `computer_cleanup`
6. `InbriskTools.Wait.cs` — `computer_wait`, `computer_wait_for`, `computer_wait_for_change`, `computer_wait_for_stable`
7. `InbriskTools.MemoryAndRecipes.cs` — `computer_screen_memory`, `computer_save_recipe`, `computer_run_recipe`, `computer_list_recipes`
8. `InbriskTools.SessionAndMeta.cs` — `computer_session`, `computer_capabilities`, `computer_toolset`, `computer_adapter`
9. `InbriskTools.Helpers.cs` — Ortak hedef çözümleme, delta üretimi, hata biçimlendirme ve yanıt diyeti yardımcıları

**İkinci aşama (Faz 1b):** `InbriskTools.PlanEngine.cs` içindeki saf iş mantığını `Inbrisk.Runtime/Planning/` altına taşıyarak MCP katmanından bağımsız birim test edilebilir hale getirmek.

---

### Faz 2 — `AppService.cs` (2.831 satır) ve `UiaBackend.cs` (2.028 satır)

#### `AppService.cs` → `src/Inbrisk.Platform.Windows/Apps/`
- `AppResolver.cs` — İsim/yol/AUMID/Store uygulaması çözümleme ve belirsizlik (ambiguity) skorlaması
- `AppLauncher.cs` — `Process.Start` / `ShellExecuteEx` / UWP `ApplicationActivationManager` başlatma
- `AppReadinessWaiter.cs` — Yeni pencere bekleme, `ApplicationFrameHost` eşleştirme, erken çıkış tespiti
- `AppLifecycleManager.cs` — Güvenli kapatma (`WM_CLOSE`), süreç ağacı koruması, oturum temizliği
- `AppService.cs` — İnce orkestratör (~300 satır)

#### `UiaBackend.cs` → `src/Inbrisk.Platform.Windows/Uia/`
- `UiaTreeWalker.cs` — Ağaç gezintisi, budama pipeline'ı (offscreen, derinlik, kontrol tipi, dedup)
- `UiaPatternExecutor.cs` — `Invoke`, `Value`, `Toggle`, `Selection`, `ExpandCollapse`, `RangeValue`, `Scroll`
- `UiaCacheRequestFactory.cs` — `CacheRequest` şablonları ve önbellekli okuma yardımcıları
- `UiaElementResolver.cs` — İlişkisel seçiciler (`within`, `ancestor`, `labelledBy`, `nearText`) ve stale re-resolve
- `UiaBackend.cs` — İnce cephe (~350 satır)

---

### Faz 3 — Ghost OS Modülerleştirme (~14.000 satır)

Ghost OS kodları şu an `Cli`, `Mcp`, `Runtime`, `Platform.Windows` ve `Core` içine dağınık ve çok büyük dosyalardan oluşuyor.

1. **İsim çakışmasını gider:**
   - `src/Inbrisk.Platform.Windows/GhostDesktopInput.cs` → `GhostNativeInputDriver.cs`
   - `src/Inbrisk.Runtime/GhostDesktopInput.cs` → `GhostInputCoordinator.cs`
2. **`GhostControlCommand.cs` (2.461 satır) → `src/Inbrisk.Cli/Ghost/`:**
   - `GhostCliParser.cs`, `GhostTrayCommand.cs`, `GhostPipCommand.cs`, `GhostStatusCommand.cs`, `GhostStopCommand.cs`, `GhostLogsCommand.cs`
3. **`GhostWorkerDaemon.cs` (2.009 satır) → `src/Inbrisk.Runtime/Ghost/Worker/`:**
   - `GhostWorkerHost.cs` (yaşam döngüsü), `GhostWorkerCaptureLoop.cs`, `GhostWorkerCommandRouter.cs`, `GhostWorkerUiaHandler.cs`
4. **`GhostInputExecutor.cs` (1.528 satır) → `src/Inbrisk.Platform.Windows/Ghost/Input/`:**
   - `GhostMouseInjector.cs`, `GhostKeyboardInjector.cs`, `GhostKeyHoldTracker.cs`
5. **`GhostMcpBridge.cs` (1.974 satır) → `src/Inbrisk.Mcp/Ghost/`:**
   - `GhostMcpTools.cs`, `GhostMcpBatchAdapter.cs`, `GhostMcpSessionRouter.cs`
6. **`GhostKeyboardForwarder.cs` (1.314 satır) → `src/Inbrisk.Runtime/Ghost/Input/`:**
   - `GhostHotkeyController.cs`, `GhostPipKeyForwarder.cs`

---

### Faz 4 — CLI, WPF UI ve Runtime Çekirdeği

1. **`src/Inbrisk.Cli/Program.cs` (1.259 satır) → `src/Inbrisk.Cli/Commands/`:**
   - Her CLI komutu ayrı sınıf (`McpCommand.cs`, `ObserveCommand.cs`, `SetupCliCommand.cs`, `DoctorCliCommand.cs`, `UiCommand.cs` vb.).
2. **`InbriskControlWindow.cs` (1.445 satır) & `SetupApp.cs` (1.221 satır) → `src/Inbrisk.Cli/Ui/`:**
   - İş mantığını `ControlWindowViewModel.cs` / `SetupViewModel.cs` sınıflarına çekip saf C# birim testlerine açmak.
   - Sayfaları (Dashboard, Hosts, Safety, Appearance, Logs, Backups) ayrı user-control/bölüm dosyalarına ayırmak.
3. **`Executor.cs` (1.166 satır) → `src/Inbrisk.Runtime/Execution/`:**
   - `ActionPipeline.cs`, `ActionRetryPolicy.cs`, `ActionFallbackChain.cs`, `ActionTelemetryRecorder.cs`.
4. **`ActivityHudService.cs` (1.248 satır) → `src/Inbrisk.Platform.Windows/Hud/`:**
   - `HudStateMachine.cs` (saf mantık, test edilebilir) + `HudOverlayWindow.cs` (Win32/WPF çizim).

---

## 4. Kabul Kriterleri

- `src/` içinde 1.000 satırı aşan dosya kalmaması (veya istisna için açık gerekçe).
- Tüm modüllerde iş mantığının Win32/WPF/MCP kabuğundan ayrılması ve birim test edilebilir arayüzler (`interface`) üzerinden bağlanması.
- `dotnet build inbrisk.sln` temiz + `docs/plans/full-test-pipeline-creation.md` altındaki Tier 1 & Tier 2 testlerinin tamamının yeşil olması.
