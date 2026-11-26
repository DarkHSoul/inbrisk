# Inbrisk — Dokümantasyon Dizini

Bu klasör, Inbrisk projesinin mimari planlarını, denetim kayıtlarını ve teknik yol haritalarını içerir.

## Planlar (`docs/plans/`)

| Belge | Kapsam | Durum |
|---|---|---|
| [modularization.md](plans/modularization.md) | 1.000+ satırlık "god-file" dosyalarının (başta 11.485 satırlık `InbriskTools.cs`) sıfır regresyonla modülerleştirme planı | Hazır |
| [full-test-pipeline-creation.md](plans/full-test-pipeline-creation.md) | İki katmanlı otomatik CI/CD test pipeline'ı, mevcut testlerin "yalandan test" denetimi, 18 kırık testin onarımı ve eksik testlerin eklenmesi | Hazır |
| [human-intervention-mode.md](plans/human-intervention-mode.md) | Sıradaki TODO: `HumanInterventionMode` (fiziksel klavye/fare müdahalesinde otomatik duraklatma) tasarım ve uygulama planı | Sıradaki TODO |

## Denetim & Analizler (`docs/audit/`)

| Belge | Kapsam |
|---|---|
| [REVIEW.md](audit/REVIEW.md) | Principal Systems Review — güvenlik, güvenilirlik ve performans bulguları |
| [PHASE0_TRIAGE.md](audit/PHASE0_TRIAGE.md) | Faz 0 triyaj ve önceliklendirme kaydı |
