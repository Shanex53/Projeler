# Nurol Envanter Yönetim Sistemi — Tasarım (v2)

Tarih: 8 Kasım 2026 · Durum: Onaylandı (8 Kasım 2026)

## 1. Amaç

Nurol grup şirketleri için Türkçe arayüzlü; depolardaki tekil demirbaşları ve miktarla takip edilen malzemeleri, depolar arası transferleri ve personele yapılan zimmetleri izleyen bir web uygulaması. Islak imzalı zimmet evrakı uygulamadan PDF olarak üretilir, imzalı taranmış kopyası ilgili zimmete yüklenir. Satın alma modülü yoktur.

Proje sıfırdan geliştirilir; daha önceki projelerden kod, veri modeli veya yapılandırma devralınmaz. Mevcut veri bulunmadığından içe aktarma gerekmez; sistem boş başlar.

**Başarı ölçütleri:** yetkili hesaplarla giriş; kategori alanlarını ve liste sütunlarını kod değiştirmeden düzenleme; bir cihazı aynı anda iki kişiye zimmetleyememe; miktarlı stokta negatif bakiye oluşmaması; zimmet PDF'i üretip imzalı kopyasını yükleme; transferi gönderme/teslim alma aşamalarıyla izleme; personeli AD'den alma; uygulamayı PostgreSQL kullanan bir Kubernetes ortamında çalıştırma.

## 2. Kararlar

| Konu | Karar |
|---|---|
| Dil / platform | C#, .NET 10 LTS, ASP.NET Core |
| Arayüz | Blazor WebAssembly (C#) — tek dil; Kubernetes'te sticky session gerektirmez. Blazor Server kullanılmaz. |
| Veritabanı | PostgreSQL, Entity Framework Core |
| Dosya deposu | S3 uyumlu özel nesne deposu (kurumsal servis veya MinIO) |
| Dağıtım | Linux konteynerleri, Kubernetes, Helm |
| PDF | MIT lisanslı kütüphane (PdfSharp/MigraDoc); ücretli lisans gerektiren kütüphane kullanılmaz |
| Giriş | Hibrit: yerel hesaplar + yetki verilmiş AD hesapları |
| Zimmet | Şirket bazlı: personel yalnızca kendi şirketinin depolarındaki envanteri zimmetlenebilir |
| Transfer | Aynı şirket depoları arasında, tam teslim |
| Takip biçimi | Tekil demirbaş + miktarlı stok, ilk sürümde birlikte |
| Mevcut veri | Yok; içe aktarma ilk sürümde yer almaz |

## 3. Aşamalar

1. **Aşama 1 — Çekirdek:** yerel giriş, roller ve şirket/depo kapsamı; şirket → depo → lokasyon; kategori, marka/model ve özel alanlar; tekil envanter; miktarlı stok (giriş, çıkış, sarf, düzeltme); personel (elle kayıt); zimmet, iade, PDF ve imzalı evrak yükleme; dashboard; Dockerfile ve Helm chart.
2. **Aşama 2 — Entegrasyon ve akış:** AD personel senkronizasyonu ve AD ile giriş; depolar arası transfer (tekil + miktarlı); kullanıcıya özel kaydedilmiş görünümler ve sütun tercihleri.
3. **Aşama 3 — Geliştirmeler:** raporlar, e-posta bildirimleri, barkod/QR etiket basımı ve okutma, Excel ile toplu ekleme.

Her aşama kendi başına kurulabilir ve kullanılabilir olarak teslim edilir.

## 4. Personel, hesap ve yetki

`Personel` ile `UygulamaHesabı` ayrı kayıtlardır. Giriş hesabı olmayan personele de zimmet yapılabilir. AD'den gelen personel otomatik giriş hakkı kazanmaz; yönetici seçtiği kişiye rol ve kapsam verir. Açık kayıt ekranı yoktur.

**Roller:** sistem yöneticisi, şirket yöneticisi, depo sorumlusu, salt okunur denetçi. İzinler API'de zorunlu kontrol edilir. Sistem yöneticisi dışındaki kullanıcılar şirket/depo atamalarıyla sınırlıdır; liste, detay, dışa aktarım ve dosya indirme aynı kapsam kontrolünden geçer. Denetçi evrakları yetkisi dahilinde görür, stok değiştiremez.

**Şirket bağı:** her personel tek bir şirkete bağlıdır. Şirket eşlemesi kural veya yönetici onayıyla yapılır; AD'deki serbest metin şirket adı tek başına yetki vermez. Personel şirket değiştirirse açık zimmetleri işaretlenir; önce iade, ardından yeni şirkette yeni zimmet yapılır.

**AD (Aşama 2):** LDAPS üzerinden salt okunur servis hesabı; yapılandırılabilir arama kökü/OU filtresi ve alan eşlemesi; önizlemeli elle veya planlı senkronizasyon. Kalıcı kimlik domain + objectGUID'dir; ad değişikliği mükerrer kayıt oluşturmaz. Yarım kalan senkronizasyon personeli topluca pasife almaz. Pasifleşen personelin geçmişi korunur, açık zimmetleri işaretlenir; AD'de devre dışı bırakılan hesabın oturumları iptal edilir. Giriş ve senkronizasyon ayrı ayarlardır. Windows oturumuyla otomatik giriş (Kerberos/SSO) kapsam dışıdır.

**Güvenlik:** AD parolaları saklanmaz; AD hesabında başarısız doğrulama yerel parolaya düşmez. Yerel parolalar ASP.NET Core Identity ile hashlenir. Oturum güvenli HttpOnly cookie ile tutulur; oturumlar, cookie şifreleme anahtarları (Data Protection), giriş deneme sayaçları ve hesap kilitleri PostgreSQL'de ortak saklanır — pod değişimi kullanıcıyı düşürmez. CSRF koruması ve süreli oturum uygulanır; tarayıcıda uzun ömürlü token saklanmaz.

## 5. Envanter, kategori ve özel alanlar

Şirket → depo → lokasyon hiyerarşisi. İki takip biçimi:

- **Tekil demirbaş:** cihaz başına bir kayıt; benzersiz envanter etiketi (şirket ve kategori önekli otomatik numara, örn. `NRL-BLG-000123`; biçim yapılandırılabilir), marka, model, kategori, seri numarası (kategoriye göre zorunlu/opsiyonel).
- **Miktarlı malzeme:** ürün tanımı + depo bazında bakiye; hareketler giriş, çıkış (personel/proje sarfı), düzeltme ve transferdir. Sarf çıkışı tekil zimmetle karıştırılmaz.

Yönetici kategorileri ve alan gruplarını düzenler; kategori varsayılan alan grubunu kullanır, modelde ek alan tanımlanabilir. Aynı alan iki kez eklenmez; kararlı alan kimlikleri kullanılır. Örnek: telefon için IMEI 1/2 ve hat numarası; bilgisayar için RAM/işlemci/disk; ağ cihazı için IP/MAC.

**Alan tipleri:** metin, sayı, tarih, seçim listesi, evet/hayır. Etiket, yardım metni, sıra, zorunluluk, izin verilen değerler, uzunluk ve benzersizlik kuralı yönetilir. IMEI metin olarak saklanır (baştaki sıfırlar korunur, 15 hane kuralı seçilebilir). Benzersizlik kuralı grup genelinde uygulanır (aynı fiziksel cihaz iki şirkette kayıtlı olamaz) ve veritabanında da korunur. Yeni kural mevcut verilerle çelişirse yöneticiye çelişen kayıtlar gösterilir; sessiz dönüşüm yapılmaz.

Özel alanlar tanım + tipli değer tablolarında saklanır; alan eklemek deploy gerektirmez. Değerler API'de doğrulanır; arama, filtre, sıralama ve dışa aktarıma katılır. İndeksleme stratejisi on binlerce kayıtla yük testiyle belirlenir.

**Görünüm ≠ şema:** kullanıcı sütunları gösterir/gizler, sıralar, genişletir; kategoriye göre filtre ve görünüm kaydeder (Aşama 2). Gizlemek yetki kısıtlaması değildir. Kullanımdaki özel alan silinmez, arşivlenir; hiç kullanılmamış alan silinebilir.

## 6. Hareketler, transfer ve zimmet

**Tekil durumlar:** depoda, zimmetli, transferde, bakımda, kullanım dışı. Her varlığın tek aktif konum/durumu vardır. Bakıma alma ve dönüş; açıklama, servis bilgisi ve tarihle kayıt altına alınır.

**Kurallar:** geçmiş kalıcıdır; düzeltmeler karşı hareketle yapılır. Miktarlı stok negatif bakiyeye düşemez. Durum geçişleri transaction içinde satır kilidi/iyimser eşzamanlılıkla çalışır; bir varlığın iki aktif zimmeti olamaz; tekrarlanan istek mükerrer hareket üretmez (idempotency anahtarı). Zamanlar UTC saklanır, Europe/Istanbul gösterilir. Dashboard'da tekil adetler ile miktar birimleri toplanmaz.

**Transfer (Aşama 2):** taslak → gönderildi → teslim alındı. Gönderme kaynak, teslim hedef depo yetkisi ister. Gönderilen varlık başka işleme konu olamaz; miktarlı stok teslime kadar transfer ara stokunda bekler. Tam teslim esastır; taslak iptal edilebilir, sevk sonrası dönüş ayrı iade hareketidir. Zimmetli varlık iade edilmeden sevk edilemez.

**Zimmet:** operatör aynı şirketteki personele bir veya birden çok tekil envanter zimmetler. İade ve devir yeni olaydır ve yeni evrak üretir.

## 7. Evrak

A4 PDF: zimmet numarası, teslim eden/alan, şirket/depo, envanter etiketleri, seri ve özel alanlar, durum açıklaması, tarih ve imza alanları; Türkçe karakter ve çok sayfa desteği. Belge, işlem anındaki verilerle sabit sürüm olarak saklanır; sonraki değişiklikler eski belgeyi etkilemez.

İmzalı kopya PDF/JPEG/PNG, en fazla 20 MB; tip/içerik kontrolünden geçer. Dosyalar özel nesne deposunda tutulur, yetkili uç noktadan indirilir; yükleme zamanı, yükleyen, hash ve sürüm kaydedilir; önceki kopya üzerine yazılmaz. Zimmet durumu ile evrak durumu ayrıdır; eksik imzalı evrak dashboard ve listede görünür. Islak imza yüklemesi elektronik imza değildir; OCR ve otomatik imza doğrulama yoktur.

## 8. Arayüz

Türkçe, modern, masaüstü odaklı ve mobil uyumlu; açık zemin, mavi/lacivert, yuvarlatılmış kartlar. Menü: Genel Bakış, Envanter, Stok, Kategoriler ve Alanlar, Depolar, Transferler, Personeller, Zimmetler, Raporlar, Yönetim.

Giriş ekranı yerel/AD hesap türünü açıkça gösterir. Dashboard: toplam tekil envanter, depodaki/zimmetli cihazlar, kritik stok seviyeleri, bekleyen transferler, eksik imzalı evraklar, son hareketler. Envanter listesinde arama, kategori/depo/durum filtreleri ve sütun seçici. Personel detayında açık/geçmiş zimmetler ve sarf çıkışları; varlık detayında hareketler ve evraklar. Boş, yükleniyor ve hata durumları tasarlanır; hatalar form üzerinde anlaşılır Türkçe metinle gösterilir.

## 9. Veri ve dağıtım

**Temel kayıtlar:** Company, Warehouse, Location, Category, Manufacturer, AssetModel, InventoryItem, StockItem, StockBalance, StockMovement, Transfer/TransferLine, Employee, ApplicationAccount/Role/Scope, DirectoryConnection/SyncRun, FieldDefinition/FieldSet/FieldValue, Assignment/AssignmentLine, DocumentVersion/Attachment, SavedView, AuditEvent. Şirket ilişkileri kayıt zinciri boyunca doğrulanır; kullanımdaki kayıtlar silinmez, arşivlenir.

**Mimari:** tek modüler ASP.NET Core uygulaması; Blazor WebAssembly arayüzü aynı imajdan statik olarak sunulur. PostgreSQL ve nesne deposu dış servistir. Mikroservis ve Redis bağımlılığı yoktur.

**Kubernetes:** Deployment + Service, Ingress/TLS, Secret/ConfigMap referansları, startup/readiness/liveness kontrolleri, kaynak istek/sınırları, root olmayan ve salt okunur kök dosya sistemli konteyner, kontrollü migration Job, AD senkronizasyon CronJob'u (`concurrencyPolicy: Forbid` + veritabanı kilidi). Çoklu pod'da oturum ve dosya depolama ortaktır; sticky session gerekmez. Üretim chart'ı varsayılan olarak veritabanı kurmaz; bağlantı bilgileri values/Secret ile verilir. Geliştirme için docker compose (PostgreSQL + MinIO) sağlanır.

**Sırlar:** AD, PostgreSQL, nesne deposu ve ilk yönetici bilgileri yalnızca Secret ile verilir; kaynak koda girmez. Örnek hesaplar yalnızca geliştirme ortamındadır.

**Yedekleme:** veritabanı ve evrak deposu birlikte yedeklenmelidir; geri yükleme adımları belgelenir. Gerçek yedekleme planı kurum altyapısının kararıdır.

**Teslim:** kaynak kod, docker compose, Dockerfile, Helm chart, örnek values, migration'lar, geliştirme örnek verisi, Türkçe kurulum kılavuzu.

## 10. Kapsam dışı

Satın alma, sipariş, tedarikçi, ERP entegrasyonu, lisans yönetimi, elektronik imza, OCR, otomatik cihaz keşfi, AD'ye yazma, şirketler arası transfer/zimmet, kısmi transfer teslimi, Windows SSO.

## 11. Doğrulama

Otomatik testler: kimlik/rol/kapsam, şirket bazlı zimmet kuralı, negatif stok engeli, eşzamanlı çift zimmet/çift teslim, mükerrer istek, özel alan tip/benzersizlik kuralları, alan arşivleme. API entegrasyon testleri gerçek PostgreSQL ile (Testcontainers). Tarayıcı testleri: giriş, alan ekleme, zimmet, PDF indirme, belge yükleme; Aşama 2'de transfer ve sütun tercihi. PDF'de Türkçe metin ve belge sürümünün değişmezliği kontrol edilir.

LDAP testleri test dizini ile yapılır; gerçek AD bağlantısı kurum ağında doğrulanır. Helm lint/render ve yerel Kubernetes (kind) smoke testi yapılır. Kurum kümesinde doğrulanmamış davranışlar teslim kılavuzunda ayrıca listelenir.
