# Aşama 1 Uygulama Planı — Çekirdek

Durum: **tamamlandı** (9 Kasım 2026). Plandan sapmalar ve doğrulama sonuçları en alttadır.

Kaynak: [tasarim.md](tasarim.md). Görsel referans: [prototip/index.html](prototip/index.html).

## Çözüm yapısı

```
nurol-envanter/
  src/
    Nurol.Envanter.Domain/          varlıklar, durum geçişleri, iş kuralları (bağımlılıksız)
    Nurol.Envanter.Application/     kullanım senaryoları, yetki/kapsam kontrolü, doğrulama
    Nurol.Envanter.Infrastructure/  EF Core + PostgreSQL, S3 deposu, PDF üretimi, Identity
    Nurol.Envanter.Api/             ASP.NET Core minimal API, cookie oturumu, Blazor WASM barındırma
    Nurol.Envanter.Web/             Blazor WebAssembly arayüzü
    Nurol.Envanter.Migrator/        migration Job'u için konsol uygulaması
  tests/
    Nurol.Envanter.Domain.Tests/
    Nurol.Envanter.Api.Tests/       Testcontainers ile gerçek PostgreSQL
    Nurol.Envanter.E2E/             Playwright tarayıcı testleri
  deploy/
    docker-compose.yml              PostgreSQL + MinIO + uygulama (geliştirme)
    Dockerfile                      tek imaj, root olmayan kullanıcı
    helm/nurol-envanter/            Deployment, Service, Ingress, migration Job, values örnekleri
  docs/
```

## Adımlar

1. **İskelet ve altyapı:** çözüm, docker compose, sağlık uç noktaları, yapılandırılmış log, Dockerfile. Testcontainers ile boş entegrasyon testi.
2. **Kimlik ve yetki:** yerel hesaplar (ASP.NET Core Identity), PostgreSQL'de oturum ve Data Protection anahtarları, giriş deneme sınırı, CSRF. Roller ve şirket/depo kapsamı; her sorguya kapsam filtresi. İlk yönetici Secret'tan oluşturulur.
3. **Tanımlar:** şirket, depo, lokasyon, kategori, marka, model. Arşivleme kuralı.
4. **Özel alanlar:** alan tanımı, alan grubu, kategori varsayılanı + model ek alanları, tipli değer tablosu. Doğrulama (tip, zorunluluk, uzunluk, 15 hane), grup geneli benzersizlik için ayrı benzersiz anahtar tablosu. Kural çakışma raporu.
5. **Tekil envanter:** otomatik etiket numaralandırma, liste (arama, filtre, sıralama, özel alan sütunları), detay, hareket geçmişi, bakım giriş/çıkış.
6. **Miktarlı stok:** ürün, depo bakiyesi, giriş/sarf çıkışı/düzeltme hareketleri; satır kilidiyle negatif bakiye engeli; idempotency anahtarı.
7. **Personel:** elle kayıt, şirket bağı, açık zimmet ve sarf geçmişi.
8. **Zimmet ve evrak:** çoklu cihaz zimmeti, iade, devir; eşzamanlı çift zimmet engeli. PdfSharp/MigraDoc ile A4 tutanak (gömülü Türkçe font), dondurulmuş belge sürümü. İmzalı kopya yükleme (tip/içerik/20 MB kontrolü, SHA-256, sürümleme) ve yetkili indirme.
9. **Arayüz:** prototipteki ekranların Blazor WebAssembly karşılıkları; boş/yükleniyor/hata durumları.
10. **Dashboard:** gerçek verilerle göstergeler, kritik stok, eksik evrak, son hareketler.
11. **Kubernetes:** Helm chart (probe'lar, kaynak sınırları, salt okunur kök dosya sistemi, Secret/ConfigMap referansları, migration Job), `helm lint`/`template` ve kind üzerinde smoke testi.
12. **Teslim:** Türkçe kurulum kılavuzu, yedekleme ve geri yükleme notları, test edilen / kurumda doğrulanacak maddeler listesi.

## Kabul testleri

- Kapsam dışı depoya ait kayıt listede, detayda, dışa aktarımda ve dosya indirmede görünmez.
- Farklı şirketin personeline zimmet reddedilir.
- Aynı cihaza eşzamanlı iki zimmet isteğinden yalnızca biri başarılı olur.
- Aynı idempotency anahtarıyla gelen ikinci istek yeni hareket oluşturmaz.
- Bakiyeden fazla sarf çıkışı reddedilir; bakiye değişmez.
- Grup genelinde aynı IMEI ikinci kez girilemez; baştaki sıfırlar korunur.
- Personel adı değiştirildikten sonra eski tutanak PDF'i aynı kalır.
- İkinci imzalı kopya yüklemesi birinciyi silmez; ikisi de indirilebilir.
- İki API pod'u arasında istek dağıldığında oturum düşmez.

## Uygulama notları ve plandan sapmalar

- **Proje yapısı sadeleştirildi:** Domain/Application/Infrastructure katmanları tek `Nurol.Envanter.Server` projesinde klasörlerle ayrıldı (`Data`, `Services`, `Infrastructure`, `Endpoints`). Ayrı `Migrator` projesi yerine aynı imaj `migrate` argümanıyla çalışır; Helm migration Job'u bunu kullanır.
- **PDF kütüphanesi:** PDFsharp-MigraDoc 6.5 AGPL-3.0 lisansına geçtiği için MIT lisanslı 6.2.4 sürümüne sabitlendi.
- **Sütun tercihleri:** Aşama 1'de tarayıcıda saklanır. Kullanıcıya özel, sunucuda kayıtlı görünümler tasarımdaki gibi Aşama 2'dedir.
- **Devir:** iade ve yeni zimmet tek işlemde yapılır; iki ayrı tutanak üretilir.
- **Yerel S3:** MinIO'nun Docker Hub imajı artık yayımlanmadığından compose ortamında SeaweedFS kullanıldı. Bu sırada güncel AWS SDK'nın varsayılan parçalı imza ve sağlama toplamı davranışının S3 uyumlu servislerde dosyayı bozabildiği görüldü; istemci bu özellikleri yalnızca gerektiğinde kullanacak şekilde ayarlandı ve bayt bayt doğrulayan bir test eklendi.
- **Tarayıcı testleri:** .NET projesi yerine Node + Playwright betiği (`tests/e2e/akis.cjs`).
- **Kubernetes doğrulaması:** Geliştirme ortamında kind çalıştırılamadı (iç içe konteyner desteği yok). Chart `helm lint`, `helm template` ve kubeconform (Kubernetes 1.31, katı mod) ile doğrulandı; konteyner salt okunur dosya sistemi ve root olmayan kullanıcıyla ayrıca denendi. Gerçek küme kurulumu kurum ortamında doğrulanmalıdır.

Ayrıntılı test listesi ve kurumda doğrulanacak maddeler: [kurulum.md § 9](kurulum.md#9-doğrulama-durumu).
