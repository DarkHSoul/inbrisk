using Xunit;

// Testler GERCEK masaustunu suruyor: pencere odagi aliyor, SendInput ile
// tus gonderiyor, koordinata tikliyor, TestApp'i baslatiyor. xUnit varsayilan
// olarak her test sinifini ayri bir koleksiyon sayip PARALEL kosturuyor.
//
// Sonuc: ayni anda birden fazla test odak/TIKLAMA savasina giriyor,
// birbirlerinin cikti dosyasini bozuyor, TestApp pencereleri ortada kaliyor
// ve bazi testler sonsuza kadar asiliyor.
//
// Bu dosya butun test kosusunu TEK ISCE sifirla. GUI otomasyonu testinde
// paralellik dogru bir hedef degil; siralama zorunluluktur.
//
// Not: xunit.runner.json da ayni ayari yapar; ikisi birlikte verilir ki
// biri yuklenmezse digeri gecerli olsun.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
