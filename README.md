# Nurol Envanter Yönetim Sistemi

Kurumsal demirbaş, zimmet, lisans, bakım ve fiziksel sayım yönetimi; 18 hazır yönetim raporu (ekran + Excel + PDF).

**Teknoloji:** .NET 10 · ASP.NET Core Blazor (Interactive Server) · MudBlazor 8 · EF Core 10 / SQL Server · ASP.NET Identity ·
ApexCharts · ClosedXML (Excel) · PDFsharp-MigraDoc (PDF, MIT lisanslı) · QRCoder · Serilog

---

## Tek tıkla çalıştırma (yerel)

Masaüstündeki **Nurol Envanter** kısayoluna (veya repo kökündeki `NurolEnvanter-Baslat.cmd`) çift tıklayın.
Başlatıcı LocalDB'yi açar, kod değiştiyse uygulamayı `%LOCALAPPDATA%\NurolEnvanter\app` klasörüne yeniden derler,
arka planda başlatır ve tarayıcıyı `http://localhost:5161` adresinde açar. Uygulama zaten çalışıyorsa yalnızca tarayıcıyı açar.
Durdurmak için **Nurol Envanter - Durdur** kısayolu. Kısayollar silinirse: `powershell -ExecutionPolicy Bypass -File tools\kisayol-olustur.ps1`.

## Hızlı başlangıç (geliştirme)

Gereksinimler: .NET 10 SDK, SQL Server veya LocalDB (Visual Studio ile gelir).

```powershell
dotnet tool restore
dotnet run --project src/NurolEnvanter.Web --launch-profile http
```

`http://localhost:5161` adresini açın. Geliştirme ortamında:

| | |
|---|---|
| Kullanıcı | ` |
| Parola |  (`appsettings.Development.json`) |
| Demo veri | 4 şirket, 8 lokasyon, 180 personel, ~730 varlık, 9 lisans, 1 sayım (kurgusal) |

Veritabanı ilk çalıştırmada otomatik oluşturulur ve migration'lar uygulanır. Demo veriyi sıfırlamak için
`NurolEnvanter` veritabanını silip uygulamayı yeniden başlatın.

Visual Studio: `NurolEnvanter.sln` → başlangıç projesi `NurolEnvanter.Web` → F5.

## Modüller

| Modül | Özellikler |
|---|---|
| **Gösterge paneli** | KPI kartları, zimmet hareketleri, durum dağılımı, kategori ve şirket değerleri, uyarı merkezi, son hareketler |
| **Varlıklar** | Sunucu taraflı sayfalama/filtre, durum sekmeleri, toplu QR etiket, değer/amortisman eğrisi, değişiklik geçmişi |
| **Zimmet** | Barkod okuyucu ile zimmet sihirbazı, tek tutanakta çoklu varlık, iade, devir, PDF zimmet tutanağı ve personel zimmet dökümü |
| **Lisanslar** | Koltuk ataması (personel/cihaz), aşım kontrolü, maskelenmiş lisans anahtarı (ayrı izin) |
| **Bakım ve arıza** | Kayıt açıldığında varlık "Bakımda", kapanınca önceki durumuna döner; maliyet takibi |
| **Fiziksel sayım** | Lokasyon bazlı; USB/Bluetooth barkod okuyucu veya telefon kamerası (BarcodeDetector) ile okutma; eksik/fazla/yanlış lokasyon tespiti |
| **Rapor merkezi** | 14 rapor; filtreler, KPI, grafik + tablo görünümü, sonuçlarda arama/sıralama, toplam satırı, paylaşılabilir bağlantı, Excel ve PDF |
| **Yönetim** | Kullanıcı, rol/izin matrisi, şirket bazlı veri erişimi, değiştirilemez denetim kaydı, AD senkronizasyonu, Excel ile toplu aktarım |

### Raporlar

Envanter: Demirbaş Envanter Listesi · Şirket ve Lokasyon Dağılımı · Kategori Bazlı Özet · Yaşlanma ve Yenileme Planı · Depolar Arası Transferler
Zimmet: Personel Zimmet · Zimmet Hareketleri · Ayrılan Personelde Kalan Zimmetler · İade Tarihi Geçen Zimmetler
Finans: Amortisman ve Defter Değeri · Bakım ve Arıza Maliyetleri · Garanti Bitiş Takvimi
Uyum/Denetim: Lisans Uyum · Sayım Fark · Değişiklik Denetim

Tüm raporlar tek bir modelden (kolon + satır + KPI + grafik) üretilir; ekran, Excel ve PDF birebir aynı veriyi gösterir.
Excel çıktısı: özet sayfası (filtreler, KPI'lar, grafik verisi) + filtrelenebilir Excel tablosu, toplam satırı, sayı biçimleri.
PDF çıktısı: kurumsal başlık/altlık, KPI kartları, grafikler, sayfa numarası, oluşturan kullanıcı.
Her dışa aktarma denetim kaydına yazılır.

> Amortisman doğrusal yöntemle, kalıntı değer 0 kabul edilerek kategori faydalı ömrüne göre hesaplanır.
> Envanter değerlemesi ve yenileme planlaması içindir; muhasebe/VUK kayıtlarının yerine geçmez.

## Güvenlik

- ASP.NET Identity: parola politikası (10+ karakter, büyük harf, rakam, özel karakter), 5 hatalı denemede 15 dk kilit.
- İzin tabanlı yetki: 18 izin, 5 hazır rol; rol–izin matrisi arayüzden düzenlenir. Her sayfa ve uç nokta izinle korunur.
- **Şirket bazlı veri erişimi**: EF Core global filtreleri ile tüm sorgulara uygulanır (varlık, personel, zimmet, lisans, sayım, raporlar).
- Değiştirilemez denetim kaydı: her oluşturma/güncelleme/silme alan bazında eski → yeni değeriyle, kullanıcı ve IP ile yazılır.
- Pasife alınan kullanıcı veya rol değişikliği açık oturumlara en geç 5 dakikada yansır.
- Güvenlik başlıkları (nosniff, frame, referrer), antiforgery, yalnızca uygulama içi yönlendirme, HttpOnly çerez.
- Opsiyonel **Microsoft Entra ID** (kurumsal hesap) ile giriş.

## İmzalı zimmet ve iade tutanakları

1. Zimmet verildiğinde **ZT-yyyy-nnnnnn**, iade alındığında **IT-yyyy-nnnnnn** numaralı tutanak "İmza bekliyor" olarak açılır.
   Devir işlemi tek transaction'da hem iade hem zimmet tutanağı üretir.
2. Tutanak PDF'i otomatik açılır; üzerinde belge numarasını taşıyan QR kod vardır. Personele (iadede BT'ye) imzalatılır.
3. Taranan nüsha (PDF/JPG/PNG, ≤15 MB) zimmet ekranından, personel kartından veya **Zimmet → Tutanaklar** ekranından yüklenir.
   Toplu yüklemede dosya adındaki tutanak numarası ile otomatik eşleştirilir (ör. `ZT-2026-000123.pdf`, `tarama_IT-2026-000004.jpg`).
4. Dosya türü içerikten doğrulanır, SHA-256 özeti saklanır; görüntüleme yalnızca zimmet yetkililerine açıktır ve her açılış denetim kaydına yazılır.
5. **Sistem Ayarları → İmzalı tutanak zorunlu** açılırsa, belirtilen günden uzun süredir imzasız tutanağı olan personele yeni zimmet verilmez.
   Gerekirse gerekçe yazılarak "İmzadan muaf tut" yapılabilir.
6. Gösterge panelinde "İmzasız tutanak" uyarısı, Rapor Merkezi'nde **Tutanak İmza Durumu** raporu vardır.

Tutanak takibinden önce oluşmuş zimmetler ve Excel aktarımları "İmza gerekmez" olarak işaretlenir.

## Depolar arası transfer

1. **Depo Transferleri → Yeni transfer talebi:** malzemenin geleceği depo ve kaynak depo seçilir. Kaynak deponun kullanılabilir
   stoğu model bazında listelenir (diğer taleplerde bekleyen adet ayrıca gösterilir), adetler ve gerekçe girilir → **DT-yyyy-nnnnnn**.
2. Kaynak depo sorumlusuna bildirim gider (zil simgesi; SMTP tanımlıysa e-posta). Sorumlu etiket okutarak veya "Otomatik seç" ile
   varlıkları seçer, sevk bilgisini yazar ve gönderir. Kısmi gönderim mümkündür; talep reddedilebilir veya talep eden iptal edebilir.
3. Gönderimle varlıklar **hedef depo envanterine "Transferde"** olarak geçer (zimmetlenemez, sayımda beklenmez). Hedef depo
   sorumlusu teslim alınca "Depoda" olur; gelmeyen varlıklar açıklamayla "Kayıp" işaretlenir ve kaynak depoya bildirilir.
4. Toplama listesi / sevk formu PDF'i (QR'lı, gönderen-taşıyan-teslim alan imzalı), **Depolar Arası Transferler** raporu ve
   gösterge uyarıları (gönderilmeyen / teslim alınmayan) vardır.

Depo sorumluları **Kullanıcılar → Sorumlu olduğu depolar** alanından atanır. Sorumlusu olmayan deponun bildirimi
"Tüm depo transferleri" yetkili kullanıcılara gider. **Sistem Ayarları:** teslim alma onayı (kapalıysa gönderimle doğrudan
"Depoda"), şirketler arası transfer izni (açıksa varlığın sahibi şirket değişir) ve SMTP ayarları.

## Yazıcılar ve toner

- **Canlı durum:** Yazıcılar SNMP (standart Printer-MIB, v1/v2c, UDP 161) ile okunur: toner/drum/atık kutusu seviyesi, toplam sayaç,
  panel mesajı, yazıcı uyarıları. Varsayılan 15 dakikada bir otomatik, istenirse anında. HP ve Kyocera dahil ağ yazıcılarının
  çoğu bu MIB'i destekler; yazıcıda SNMP açık olmalı ve uygulamanın çalıştığı bilgisayar yazıcı ağına erişebilmelidir.
- **Yazıcı ekleme:** Yazıcılar → Yazıcı ekle → envanterdeki yazıcı + IP + community → "Bağlantıyı test et". Renkli/siyah-beyaz ayrı
  sayaç için üreticinin OID'si "Gelişmiş" bölümüne girilebilir (standart MIB yalnızca toplamı verir).
- **Toner kataloğu:** Toner modeli (parça no, renk, verim, minimum stok, birim fiyat) ve **uyumlu yazıcı modelleri**. Kurulumda yaygın
  Kyocera (TK-1170, TK-1160, TK-3060, TK-5240, TK-5270, TK-8345) ve HP (59A/X, 89A/X, 415A, 207A) parçaları yüklenir; kendi
  cihazlarınıza göre kontrol edip düzenleyin.
- **Stok:** Depo bazında giriş, sayım düzeltmesi, fire/iade ve **yazıcıya takma** (stoktan düşer, o anki sayaç kaydedilir; ardışık
  iki takma arası fark tonerin ölçülen gerçek verimidir).
- **Uyarılar:** Toner eşiğin (varsayılan %15) altına indiğinde yazıcının deposundaki sorumlulara, uyumlu tonerin stok durumuyla
  birlikte bildirim gider. Gösterge panelinde toneri azalan / erişilemeyen yazıcı ve kritik toner stoğu uyarıları vardır.
- **Raporlar:** Yazıcı Toner ve Sayaç Durumu (aylık sayfa grafiği), Toner Stok ve Tüketim (yeterlilik süresi, maliyet, ölçülen verim).

> Demo veritabanındaki yazıcılar adres olarak `simulator` kullanır ve ekranda **Simülasyon** etiketiyle gösterilir. Gerçek yazıcı
> için adres alanına IP girilmesi yeterlidir. Bu bilgisayarın ağında yazıcı olmadığından SNMP okuyucu, gerçek UDP üzerinden
> cevap veren sahte bir SNMP ajanıyla test edilmiştir.
## Entegrasyonlar

**Active Directory (LDAP/LDAPS):** Yönetim → Entegrasyon → **Active Directory** ekranından yapılandırılır (appsettings yalnızca varsayılandır).
Sunucu, Base DN, salt okunur servis hesabı (parola şifreli saklanır), sicil özniteliği (`employeeID` vb.) ve şirket eşleştirmesi
(`company` alanı veya OU yapısı; "AD değeri = ŞİRKET KODU" satırları) girilir.
- **Bağlantıyı test et:** bağlanır, ilk 20 kullanıcıyı ve hangi şirkete eşleşeceklerini gösterir; hiçbir şey yazmaz.
- **Önizle:** tüm senkronizasyonu çalıştırır, yeni/güncellenen/pasife alınacak/atlanan personeli listeler, sonra geri alır.
- **Şimdi senkronize et** veya ayarlanan saatte her gün otomatik.
Devre dışı hesaplar pasife alınır ve açık zimmetleri "Ayrılan Personelde Kalan Zimmetler" raporuna düşer.
> Bu bilgisayar domain'e bağlı olmadığı için AD gerçek bir dizinle denenemedi; eşleştirme kuralları sahte AD verisiyle test edilmiştir.

**SAP:** Henüz bağlı değil. Varlıklarda SAP sabit kıymet no alanı mevcut; yöntem SAP sürümüne göre seçilecek (OData / RFC / dosya aktarımı).

**Excel ile toplu aktarım** (canlıya geçiş): Yönetim → Entegrasyon ve Aktarım. Şablonlar açılır listeler içerir.
Önce personel, sonra varlık aktarılır. "Doğrula" hiçbir şeyi yazmaz; hatalı dosyada tüm aktarım geri alınır.
`Zimmetli Sicil No` sütunu mevcut zimmetleri de taşır.

## Kubernetes

İmaj: depo kökündeki `Dockerfile` (Linux, .NET 10, root olmayan kullanıcı, Türkiye saati, PDF için DejaVu yazı tipleri).
Manifestler: `deploy/k8s` (Kustomize; `base` + `overlays/demo` + `overlays/production`).

| Bileşen | Kopya | Görev |
|---|---|---|
| `nurol-envanter-web` | 2 | Kullanıcı trafiği. Zamanlanmış iş ve migration çalıştırmaz. |
| `nurol-envanter-worker` | 1 (Recreate) | Init container'da migration + başlangıç verisi (`--migrate`), yazıcı SNMP okuma, AD senkronizasyonu. |
| Ingress (nginx) | | Yapışkan oturum (Blazor devresi), 1 saatlik WebSocket zaman aşımı, 20 MB yükleme sınırı, TLS. |

- **Sağlık:** `/saglik` canlılık (süreç ayakta), `/saglik/hazir` hazırlık (veritabanına erişim + şema güncel).
  Web pod'ları migration bitene kadar trafik almaz.
- **Şifreleme anahtarları** (oturum çerezi, SMTP/AD parolaları) veritabanında paylaşılır: pod yeniden başlasa veya
  kopya sayısı artsa da oturumlar ve kayıtlı parolalar korunur. İstenirse `DataProtection__CertificatePath` ile
  sertifikayla ayrıca şifrelenir (Windows'ta varsayılan DPAPI).
- **Günlükler** stdout'a yazılır (`kubectl logs`); dosya günlüğü konteynerde kapalıdır (`Logging__File=false`).
- **Ağ:** Pod'ların SQL Server (1433), yazıcılar (UDP 161), AD (389/636) ve SMTP sunucusuna çıkışı olmalıdır.
  NetworkPolicy kullanılıyorsa bu çıkışlara izin verin.

### Yerel deneme (Rancher Desktop)

Rancher Desktop'ta Kubernetes açık ve kapsayıcı motoru `dockerd (moby)` iken:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\k8s\demo-kur.ps1          # kur / güncelle
powershell -ExecutionPolicy Bypass -File deploy\k8s\demo-kur.ps1 -Kaldir  # sil (veritabanı dahil)
```

Betik rastgele parolalar üretir, imajı derler, küme içi SQL Server (yalnızca deneme; Developer sürümü) ile
demo verisini kurar ve `http://nurol-envanter.localhost` adresini verir.

### Üretim

```powershell
docker build -t registry.nurol.local/nurol-envanter:1.0.0 .
docker push registry.nurol.local/nurol-envanter:1.0.0
copy deploy\k8s\overlays\production\secrets.env.example deploy\k8s\overlays\production\secrets.env   # doldurun
kubectl create namespace nurol-envanter
kubectl -n nurol-envanter create secret tls nurol-envanter-tls --cert=sertifika.crt --key=sertifika.key
kubectl apply -k deploy\k8s\overlays\production
```

Yeni sürüm: imaj etiketini `overlays/production/kustomization.yaml` içinde değiştirip yeniden `apply`;
worker önce migration'ı uygular, web pod'ları sırayla güncellenir.

## Üretime alma

1. SQL Server'da boş `NurolEnvanter` veritabanı ve `db_owner` (ilk kurulum) yetkili uygulama kullanıcısı oluşturun.
2. Yayın: `dotnet publish src/NurolEnvanter.Web -c Release -o publish`
3. IIS: ASP.NET Core Hosting Bundle (.NET 10) kurun, site → `publish` klasörü, uygulama havuzu "No Managed Code", HTTPS bağlaması.
4. `appsettings.Production.json.example` dosyasını `appsettings.Production.json` olarak kopyalayıp düzenleyin.
   Gizli bilgileri dosyaya değil **ortam değişkenine** yazın, ör. `ConnectionStrings__Default`,
   `Seed__AdminPassword` (yalnızca ilk kurulumda), `Authentication__EntraId__ClientSecret`, `Integrations__ActiveDirectory__BindPassword`.
5. İlk açılışta migration'lar uygulanır ve yönetici hesabı oluşturulur. Ardından `Seed__AdminPassword` değişkenini kaldırın.
6. Sağlık kontrolü: `GET /saglik`. Loglar: `logs/nurolenvanter-YYYYMMDD.log` (30 gün saklanır).
7. Yedekleme: SQL Server bakım planı ile tam + fark + işlem günlüğü yedeği.

PDF yazı tipi: Windows'ta Segoe UI kullanılır; Linux/konteynerde DejaVu Sans'a düşer (`NUROL_FONT_DIR` ile klasör verilebilir).

## Testler

```powershell
dotnet test
```

72 test: iş kuralları (zimmet, iade, lisans, sayım, amortisman), şirket bazlı erişim filtresi, denetim kaydı,
**SQL Server (LocalDB) üzerinde** 18 raporun çalışması ve Excel/PDF üretimi, eşzamanlı numara üretimi, toplu aktarımın geri alınması.
SQL Server testleri geçici bir veritabanı oluşturup sonunda siler.

## Proje yapısı

```
src/NurolEnvanter.Domain          Varlıklar, enum'lar, izin kataloğu
src/NurolEnvanter.Infrastructure  EF Core, Identity, iş servisleri, raporlama (Excel/PDF), AD, aktarım, seed
src/NurolEnvanter.Web             Blazor arayüzü, kimlik doğrulama, dışa aktarma uç noktaları
tests/NurolEnvanter.Tests         Birim + SQL Server entegrasyon testleri
```

Yeni migration: `dotnet ef migrations add <Ad> -p src/NurolEnvanter.Infrastructure -s src/NurolEnvanter.Infrastructure -o Data/Migrations`

## Bilinen sınırlar / sonraki adımlar

- AD ve Entra ID entegrasyonları kodlandı ancak gerçek dizine karşı **test edilmedi**; pilot öncesi BT ile birlikte doğrulanmalı.
- SAP sabit kıymet ve Intune/SCCM cihaz keşfi henüz yok (alanlar hazır: `SapAssetNumber`, `Hostname`).
- E-posta bildirimleri (garanti/lisans bitişi, geciken iade) ve zamanlanmış rapor gönderimi yok; uyarılar panelde.
- Zimmet tutanağı ıslak imza içindir; e-imza/dijital onay entegrasyonu yok.
- Service desk (talep/arıza bildirimi) bu sürümün kapsamında değil.

