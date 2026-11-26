# Changelog

Tüm önemli değişiklikler bu dosyada [Keep a Changelog](https://keepachangelog.com/tr/1.1.0/) ve [Semantic Versioning](https://semver.org/) formatında tutulur.

## Sürümleme Sırası (Roadmap)
- `v0.1.0-alpha.1`, `v0.1.0-alpha.2`, ... — Erken alfa: çekirdek mimari, modülerizasyon, güvenlik ve test altyapısı
- `v0.2.0-alpha.1` — Reflex Engine + Ghost OS kararlılaştırma + tam E2E otomasyon
- `v0.5.0-beta.1` — Özellik dondurma (feature freeze), imzalı `uiAccess` kurulum, uzun süreli kararlılık
- `v1.0.0-rc.1` → `v1.0.0` — Üretim sürümü

---

## [v0.1.0-alpha.1] - Unreleased

### Added
- **HumanInterventionMode (`Off` / `PauseOnKeyboard` / `PauseOnMouse` / `PauseOnAnyInput`):**
  - `WH_KEYBOARD_LL` / `WH_MOUSE_LL` tabanlı `PhysicalInputMonitor` ile enjekte edilmiş (`LLKHF_INJECTED` / `LLMHF_INJECTED`) ve gerçek kullanıcı girdilerinin ayrılması.
  - Fare mikro-titreme eşiği (`MouseMoveThresholdPx`), Ghost OS istisnası ve `InbriskControlWindow` Safety sekmesi ayarları.
- **Authenticode Kod İmzalama & Opt-in `uiAccess`:**
  - `tools/New-DevCodeSignCert.ps1` ve `tools/Sign-Artifact.ps1` (yerel sertifika, PFX ve Azure Trusted Signing desteği).
  - `-p:InbriskUiAccess=true` ile `app.uiaccess.manifest` desteği ve `Inbrisk.Setup` içinde Win32 `WinVerifyTrust` tabanlı `AuthenticodeVerifier`.
- **Otomatik GitHub Actions Test & Release Pipeline:**
  - Push sonrası otomatik derleme, Tier 1 headless test koşumu, opsiyonel Authenticode imzalama ve `InbriskSetup.exe` + `Inbrisk-x64.zip` + `SHA256SUMS.txt` ile GitHub Release yayını.
- **Proje Dokümantasyonu & Planlar (`docs/`):**
  - Modülerizasyon, tam test pipeline/kalite denetimi, `HumanInterventionMode` ve kod imzalama rehberleri.

### Changed
- **Kapsamlı Modülerizasyon (Faz 1–4):**
  - `InbriskTools.cs` (11.485 → 124 satır + `Tools/` altında 9 alan modülü), `AppService.cs`, `UiaBackend.cs`, Ghost OS dosyaları, `Executor.cs` ve `Program.cs` sıfır regresyonla bölündü.
  - Çift `GhostDesktopInput.cs` isim çakışması giderildi.
- **Depo Hijyeni:**
  - Kök dizindeki eski köprü script'leri (`tools/legacy-bridge/`), `Modelfile` (`tools/ollama/`) ve denetim belgeleri (`docs/audit/`) düzenli klasörlere taşındı; geçici döküm/çöp dosyaları temizlendi.
