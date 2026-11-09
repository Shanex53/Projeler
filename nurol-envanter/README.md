# Nurol Envanter

Nurol grup şirketleri için demirbaş, miktarlı stok ve zimmet yönetimi. Türkçe arayüz; C# / .NET 10,
Blazor WebAssembly, PostgreSQL; Kubernetes'e Helm ile kurulur.

Bu klasör sıfırdan başlatılan yeni projedir; depodaki diğer dosyalarla bağlantısı yoktur.

## Aşama 1'de neler var

- Yerel hesaplarla giriş; dört rol (sistem yöneticisi, şirket yöneticisi, depo sorumlusu, denetçi) ve şirket/depo kapsamı
- Şirket → depo → lokasyon tanımları
- Kategori, marka, model; kod değiştirmeden eklenen özel alanlar (metin, sayı, tarih, seçim listesi, evet/hayır) ve kuralları
- Tekil envanter: otomatik etiket (`NRL-TLF-000001`), arama, filtre, sıralama, sütun seçimi, CSV dışa aktarım, bakım ve kullanım dışı durumları
- Miktarlı stok: giriş, sarf çıkışı, düzeltme; eksiye düşmeyen bakiye; kritik seviye
- Personel kaydı (şirkete bağlı), açık zimmetler ve sarf geçmişi
- Zimmet, iade ve devir; A4 PDF tutanak (işlem anındaki verilerle dondurulur); ıslak imzalı kopya yükleme ve sürümleme
- Genel bakış: gerçek verilerle göstergeler, depo dağılımı, kritik stok, eksik imzalı evrak, son hareketler

## Aşama 2'de eklenenler

- Depolar arası transfer (tekil cihaz ve miktarlı malzeme): taslak → gönderildi → teslim alındı. Gönderme kaynak, teslim alma hedef depo yetkisi ister; stok teslimde hedefe geçer.
- Kullanıcıya özel kayıtlı görünümler (filtre, sıralama ve sütun düzeni hesapta saklanır)
- Active Directory: LDAPS ile personel eşitleme (önizlemeli, şirket eşleme kurallı, zamanlanabilir) ve AD hesaplarıyla giriş

## Aşama 3'te eklenenler

- Raporlar: personel bazlı zimmet, eksik imzalı evrak, sarf tüketimi, envanter değeri, ayrılan personel, hareket dökümü; hepsi Excel olarak indirilebilir
- Excel ile toplu ekleme (envanter ve personel): şablon, satır bazlı kontrol, önizleme, hepsi-ya-hiçbiri kayıt
- QR etiket basımı (A4 etiket kâğıtları) ve kod okutma (el tipi okuyucu, kamera, elle giriş; etiket, seri no veya IMEI)
- E-posta bildirimleri: transfer yolda, günlük özet, AD'den ayrılan personel; kullanıcı bazında aç/kapa

## Yazıcı izleme

- Ağ yazıcıları IP adresiyle eklenir (envanterden bağımsız); SNMP ile toner seviyeleri ve sayfa sayacı canlı okunur
- Her toner yuvasına stoktaki tonerin bağlanması; azalan/biten tonerde “şu toneri kullanın, şu depoda bu kadar var” uyarısı ve e-posta
- Toner değişimi algılama ve tek tıkla stoktan düşme, toner ihtiyacı tablosu, günlük baskı ve yazıcı sayaçları raporu
- Kyocera ve HP yazıcıların bildirdiği parça numarasından (TK-1170, CF259A…) stoktaki tonerin otomatik önerilmesi ve toplu atanması

Ayrıntılar: [tasarım](docs/tasarim.md), [Aşama 1 planı](docs/plan-asama-1.md), [Aşama 2 notları](docs/asama-2.md), [Aşama 3 notları](docs/asama-3.md).

## Hızlı başlangıç

```bash
docker compose -f deploy/docker-compose.yml up --build
# http://localhost:8080 — kullanıcı: yonetici, parola: Gelistirme123 (yalnızca deneme ortamı)
```

Geliştirme, Kubernetes kurulumu, yedekleme ve güvenlik notları için: **[Kurulum kılavuzu](docs/kurulum.md)**.

## Klasör yapısı

```
src/
  Nurol.Envanter.Shared/   API sözleşmeleri (sunucu ve arayüz ortak)
  Nurol.Envanter.Server/   ASP.NET Core API, veri modeli, migration'lar, PDF, oturum, dosya deposu
  Nurol.Envanter.Web/      Blazor WebAssembly arayüzü (sunucu tarafından aynı imajda yayımlanır)
tests/
  Nurol.Envanter.Tests/    Gerçek PostgreSQL ve S3 uyumlu depo ile entegrasyon testleri
  e2e/akis.cjs             Playwright tarayıcı akış testi
deploy/
  docker-compose.yml       Yerel deneme ortamı
  helm/nurol-envanter/     Kubernetes Helm chart'ı ve örnek değerler
docs/                      Tasarım, plan, kurulum kılavuzu, arayüz prototipi
Dockerfile
```

## Testler

```bash
dotnet test                                  # Docker gerekir (Testcontainers ile PostgreSQL, SeaweedFS, OpenLDAP ve Mailpit)
NE_TEST_DB="Host=...;Database=...;Username=...;Password=..." dotnet test   # var olan boş bir veritabanıyla
NE_URL=http://localhost:8080 node tests/e2e/akis.cjs                       # compose ortamı açıkken
```

## Kullanılan açık kaynak bileşenler

ASP.NET Core, Entity Framework Core, Npgsql (PostgreSQL), System.DirectoryServices.Protocols (LDAP), PDFsharp-MigraDoc 6.2.4 (MIT), AWS SDK for .NET (S3),
ClosedXML (Excel, MIT), QRCoder (QR, MIT), MailKit (SMTP, MIT), Lextm.SharpSnmpLib (SNMP, MIT),
DejaVu Sans yazı tipi (PDF'te Türkçe karakterler için; lisansı `src/Nurol.Envanter.Server/Fonts/LICENSE-DejaVu.txt`).

PDFsharp-MigraDoc 6.5 ve sonrası AGPL-3.0 lisanslıdır; bu nedenle MIT lisanslı 6.2.4 sürümüne sabitlenmiştir.
Sürüm yükseltirken lisans kontrol edilmelidir.
