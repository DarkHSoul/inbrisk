# Full Test Pipeline Creation & Test Kalite Denetim Planı

Bu plan üç hedefi tek bir disiplin altında birleştirir:
1. **İki katmanlı otomatik CI/CD test pipeline'ı** kurmak.
2. **Mevcut 1.020+ testi denetleyerek "yalandan / yüzeysel / sahte" testleri ayıklamak** ve şu an kırık olan 18 testi onarmak.
3. **Hiç test edilmemiş kritik modüller** (Reflex Engine, Ghost OS çekirdeği, güvenlik kapıları, BlenderAdapter, SilentInput) için gerçek, davranışı doğrulayan testler eklemek.

---

## 1. Otomatik Test Pipeline Mimarisi (İki Katmanlı)

Windows GUI otomasyon testleri (UIA, `SendInput`, WGC ekran yakalama, global hotkey, RDP/Ghost) aktif ve kilitsiz bir masaüstü oturumu gerektirir. Bu nedenle tek tip CI yerine **iki katmanlı** yapı kurulacaktır:

### Katman 1 (Tier 1) — Headless Birim & Sözleşme Testleri (Her Push / PR)
- **Çalışma ortamı:** GitHub Actions `windows-latest` (ücretsiz/standart runner).
- **Dosya:** `.github/workflows/ci.yml`
- **Filtre:** `--filter "Category!=Desktop&Category!=GhostInteractive&Category!=Benchmark"`
- **Kapsam:**
  - `dotnet build inbrisk.sln -c Release --no-restore` (derleme + analyzer uyarı kontrolü)
  - Saf birim testleri: `CoordinateMapper`, `SafetyPolicy`, `ExpectGuardValidator`, `StateVersionTracker`, `ReflexPolicy`, `LaunchPathGate`, `PipeClientAuthenticator`, `UntrustedContentMarker`, `UpdateSignatureVerifier`, `RecipeParameterizer`, `ErrorVocabulary`, `DynamicToolsetManager`, `DesktopArbiter`, `DesktopStateMemory`, `GhostIpcBus` (in-memory/pipe), `PlanEngine` (mock backend ile).
  - Kod kapsama (code coverage): `coverlet.collector` ile Cobertura raporu + PR özeti.
  - Süre hedefi: **< 2 dakika**.

### Katman 2 (Tier 2) — Gerçek Masaüstü Entegrasyon & E2E Testleri (Gece / Release / Manuel Tetik)
- **Çalışma ortamı:** Self-hosted Windows runner (aktif interaktif masaüstü oturumu olan makine veya otomatik logon'lu Windows VM).
- **Dosya:** `.github/workflows/desktop-e2e.yml`
- **Yürütme:** Mevcut [run_tests.ps1](file:///c:/Users/Ahmet/Documents/inbrisk/run_tests.ps1) betiği üzerinden:
  - Öksüz `Inbrisk.TestApp` / `testhost` süreçlerini temizler.
  - Testleri **tek işçi (`MaxCpuCount=1`)** ile çalıştırır (odak çakışmasını önler).
  - `--blame-hang --blame-hang-timeout 120s` ile asılı kalan testi yakalar ve dump alır.
  - Başarısızlıkta ekran görüntüsü, `out/test.out`, `out/test.err` ve `*.trx` dosyalarını GitHub Actions Artifact olarak yükler.
- **Kapsam:** `Inbrisk.TestApp` ile UIA, `SendInput`, `WgcSession`/`GdiCapture`, OCR, `GlobalHotkeyService`, `AppService` launch/close, `GhostLocalDesktop`, `HumanTakeover`, `ReflexEngine` gerçek modal yakalama.

### Test Kategorizasyon Standardı (xUnit Trait)
Tüm test sınıflarına açık `[Trait("Category", "...")]` etiketi eklenecek:
- `Unit` — Harici OS/pencere bağımlılığı yok, milisaniyeler içinde çalışır (Tier 1).
- `Contract` — MCP JSON şeması, named-pipe protokolü, hata sözlüğü, serileştirme (Tier 1).
- `Desktop` — `Inbrisk.TestApp` veya gerçek Windows penceresi/odak/SendInput kullanır (Tier 2).
- `GhostInteractive` — Ayrı masaüstü / RDP / PiP penceresi gerektirir (Tier 2).
- `Benchmark` — Performans ölçümü, zaman bütçesi assertion'ları içerir (Tier 2 / Nightly).

---

## 2. "Yalandan Test" (Shallow / Fake Test) Denetim Protokolü

Mevcut test paketinde 72 dosya ve 1.020 `[Fact]` + 4 `[Theory]` bulunuyor. İlk otomatik taramada bile dikkat çekici sinyaller yakalandı:
- **81 adet boş `catch {}` bloğu** (hataları sessizce yutan test/cleanup kalıpları)
- **4 adet erken `return;`** (koşul sağlanmayınca testi `Skip` etmek yerine sessizce **Passed** geçiren kalıplar)
- **`PhaseFBenchmarkTests.cs`:** 1 `[Fact]`, **0 assertion** (sadece çalışıp bitiyor, hiçbir şey doğrulamıyor)
- **Son koşuda 18 başarısız test** (`out/test.out` — özellikle `EndToEndAndEdgeCaseTests` 5/5 fail)

### Denetim Kontrol Listesi (Her Test Dosyası İçin)
Bir testin "gerçek" sayılabilmesi için aşağıdaki 6 kriteri geçmesi zorunludur:

1. **Sıfır Sessiz Geçiş:** Ortam hazır değilse `return;` ile sessizce geçmek yasaktır; ya `Xunit.SkippableFact` ile açıkça `Skip` edilir ya da fixture fail eder.
2. **Boş `catch {}` Yasağı (Assert yolunda):** Cleanup/Dispose dışındaki hiçbir test gövdesinde exception yutulamaz.
3. **Mutasyon / Negatif Kontrol:** Bir test, hedeflediği üretim kodundaki kilit `if` veya dönüş değeri bozulduğunda **mutlaka FAIL olmalıdır** (kritik güvenlik ve politika sınıflarında Stryker.NET ile mutation testing uygulanacak).
4. **Sadece Mock'u Test Etmeme:** Mock'a ne verdiyse aynısını geri okuyup `Assert.Equal` diyen totolojik testler tespit edilip gerçek bileşenlerle değiştirilecek.
5. **Yan Etki Doğrulaması:** `Click`, `Type`, `Invoke`, `Close`, `Launch`, `Pause` testleri yalnızca `result.Success == true` kontrolüyle yetinmeyecek; hedef uygulamadaki (`TestApp`) gerçek durum değişikliğini (metin, checkbox, pencere kapanışı, OS süreç durumu) bağımsız olarak doğrulayacak.
6. **Determinizm:** Sabit `Thread.Sleep` yerine olay/durum tabanlı bekleme (`WaitFor`) kullanılacak; 3 ardışık koşuda 0 flake verecek.

---

## 3. Mevcut 18 Kırık Testin Onarım Planı (`out/test.out`)

Son koşudaki 18 başarısız test 4 kök neden grubuna ayrılıp çözülecek:

| Grup | Kırık Testler | Olası Kök Neden & Aksiyon |
|---|---|---|
| **Grup A: E2E & TestApp Odak/Başlatma** | `EndToEndAndEdgeCaseTests` (5/5), `TopologyTests` (2), `HardeningTests.FocusWindow_AutoVerified_ForegroundHwnd`, `HardeningTests.NoProgress_TerminatesLoop` | `TestApp` penceresinin ön plana alınamaması veya fixture kapanış/açılış yarış durumu; `DesktopFixture` yaşam döngüsü ve odak stratejisi düzeltilecek. |
| **Grup B: Vision / Capture** | `CaptureTests.PixelOnly_VisionFindsMarker_AndClickHits` | DPI ölçekleme / WGC-GDI çerçeve eşleşmesi veya işaretçi koordinat sapması incelenecek. |
| **Grup C: Yaşam Döngüsü & Kapatma** | `SpeedAndStreamliningTests` (3), `WindowProtectionAndModalTests.ProcessProvenance_AdoptsLaunchedProcessNamesOnSubsequentWindow`, `PhaseFRound2Tests.LaunchReadiness_ProcessExitsBeforeReady_ReturnsFailure`, `LaunchIntegrationTests.ActivityIndicator_ActiveDuringLaunchWait` | Son commit'lerdeki `LaunchPathGate` / süreç sahipliği değişikliklerinin test beklentileriyle senkronizasyonu kontrol edilecek. |
| **Grup D: Önbellek & Tarif** | `PhaseGRound1CacheTests.MonitorCache_SessionDispose_ReleasesResources`, `ReliabilityAndPerformancePhaseTests.SaveRecipe_AcceptsLegacyRawJsonSteps` | Dispose kaynak sayacı ve eski JSON şema geriye dönük uyumluluk testi onarılacak. |

---

## 4. Eksik Testlerin Eklenmesi (Öncelik Sırasıyla)

### Faz A — Güvenlik Kritik Kapılar (Tier 1 — En Yüksek Öncelik)
1. **`LaunchPathGateTests.cs`:** İmzalı vs imzasız binary, allowlist eşleşmesi, sistem dizinleri, yol traversal / spoofing denemeleri, onay reddi.
2. **`PipeClientAuthenticatorTests.cs`:** Aynı kullanıcı/farklı kullanıcı SID reddi, geçersiz/sahte PID reddi, yetkisiz istemci bağlantı kesme.
3. **`UntrustedContentMarkerTests.cs`:** Ekran/pencere/terminal metinlerinin `untrusted: true` + `source` provenance ile işaretlenmesi, prompt-injection görünümlü metinlerin ham komut olarak geçmemesi.
4. **`ConfirmChallengeTests.cs`:** Onay bekleyen tehlikeli aksiyonların `AutoConfirm` ile aşılamaması, timeout, red ve geçerli onay akışları.
5. **`UpdateSignatureVerifierTests.cs` & `SelfInstallerTests.cs`:** Bozuk imza reddi, SHA-256 uyuşmazlığı reddi, geçerli imza kabulü, kurulum dizini bütünlüğü.

### Faz B — Reflex Engine (Tier 1 + Tier 2)
6. **`ReflexPolicyTests.cs` (Tier 1):** `off` / `closeOnly` / `save` / `discard` politikaları; UAC, kimlik bilgisi (credential) ve bilinmeyen modallarda **kesinlikle otomatik tıklama yapmayıp `InterruptedByDialog` ile iptal etme** garantisi.
7. **`ReflexEngineTests.cs` (Tier 1 + Tier 2):** `TestApp` içinde sahte güncelleme/kaydetme/hata modalları açtırıp arka planda WM mesajıyla odak çalmadan kapatıldığını ve planın kaldığı yerden devam ettiğini doğrulama; telemetride `reflex.modals` kaydı kontrolü.

### Faz C — Ghost OS Çekirdeği (Tier 1 + Tier 2)
8. **`GhostIpcBusTests.cs` (Tier 1):** Named-pipe çerçeveleme, bağlantı kopması/yeniden bağlanma, bozuk mesaj reddi, eşzamanlı istek sırası.
9. **`PipInputTranslatorTests.cs` (Tier 1):** PiP pencere koordinatlarının farklı scale/DPI/fullscreen/offset kombinasyonlarında ghost masaüstü fiziksel piksel koordinatlarına doğru çevrilmesi.
10. **`GhostBatchDispatcherTests.cs` & `GhostKeyboardForwarderTests.cs` (Tier 1):** Çok adımlı ghost plan yürütme, `Ctrl+Alt+I` / `Ctrl+Shift+P` / `Esc` hotkey durum geçişleri.
11. **`GhostWorkerDaemonTests.cs` & `GhostInputExecutorTests.cs` (Tier 1/2):** Mock IPC + gerçek tuş basılı tutma / bırakma (`ReleaseAll`) garantisi.
12. **`GhostWindowSurfaceManagerTests.cs` (Tier 2):** DWM thumbnail kayıt/boyutlandırma/temizleme yaşam döngüsü.
13. **`GhostUserProvisionerTests.cs` & `GhostRdpConnectorTests.cs` (Tier 1 sözleşme):** Komut oluşturma, kimlik bilgisi temizleme (`stop` sonrası temp/cred silinmesi), hata durumları (gerçek kullanıcı oluşturmadan mock sistem soyutlamasıyla).

### Faz D — Özel Adaptörler, Sessiz Girdi & UI ViewModel
14. **`SilentInputTests.cs` (Tier 2):** Odakta olmayan `TestApp` penceresine `silent: true` ile tıklama/yazma yapıldığında odağın çalınmadığının ve desteklenmeyen türde `SendInput`'a sessizce düşmeyip `NotSupported` döndüğünün doğrulanması.
15. **`BlenderAdapterTests.cs` (Tier 1):** Köprü protokolü serileştirme, `auto_install` yol üretimi, bağlantı hatası yönetimi.
16. **`ControlWindowViewModelTests.cs` & `SetupViewModelTests.cs` (Modülerizasyon sonrası Tier 1):** Ayar kaydetme, tema/hotkey doğrulama, host bağlama durum geçişleri.

---

## 5. Başarı Ölçütleri (Definition of Done)

- GitHub Actions `ci.yml` her PR'da çalışır ve **%100 yeşil** (< 2 dk).
- Self-hosted `desktop-e2e.yml` haftalık/release koşusunda **0 fail, 0 hang, 0 öksüz süreç**.
- Güvenlik sınıflarında (`SafetyPolicy`, `EmergencyControl`, `LaunchPathGate`, `PipeClientAuthenticator`, `ReflexPolicy`) Stryker.NET mutasyon skoru **≥ %80**.
- Test paketinde assert içermeyen `[Fact]`, sessiz `return;` ve assert yolunda boş `catch {}` sayısı **0**.
