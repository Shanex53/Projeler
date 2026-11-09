# Nurol Envanter — Kurulum Kılavuzu

Bu kılavuz uygulamanın yerelde denenmesini ve kurumsal Kubernetes ortamına kurulmasını anlatır.
Parolalar, erişim anahtarları ve sunucu adresleri bu belgede veya kaynak kodda tutulmaz; kurulumda
Kubernetes Secret ve Helm values dosyasıyla verilir.

## 1. Bileşenler

| Bileşen | Açıklama |
|---|---|
| Uygulama imajı | ASP.NET Core API + Blazor WebAssembly arayüzü tek imajda. Root olmayan kullanıcı (UID 1654), port 8080. |
| PostgreSQL | Kurumsal PostgreSQL (14 ve üzeri; 17 ile test edildi). Chart veritabanı kurmaz. |
| Nesne deposu | S3 uyumlu özel depo (kurumsal S3, MinIO vb.). İmzalı evraklar ve üretilen tutanaklar burada tutulur. |
| Migration Job | Aynı imaj `migrate` argümanıyla çalışır; şemayı günceller ve ilk yöneticiyi oluşturur. |
| AD eşitleme CronJob'u | Aynı imaj `ad-sync` argümanıyla çalışır (isteğe bağlı). |
| Günlük özet CronJob'u | Aynı imaj `bildirim-ozet` argümanıyla çalışır (isteğe bağlı). |
| SMTP | E-posta bildirimleri için kurum SMTP sunucusu (isteğe bağlı). |
| Ağ yazıcıları | SNMP v1/v2c (UDP 161) ile okunur; pod'ların yazıcı ağlarına erişimi gerekir. |
| Active Directory | LDAPS ile salt okunur servis hesabı. Personel eşitlemesi ve AD ile giriş ayrı ayrı açılır. |

Redis veya başka bir ara katman gerekmez. Oturumlar, cookie şifreleme anahtarları ve giriş kilitleri PostgreSQL'de
ortak tutulur; birden fazla pod çalışırken sticky session gerekmez.

## 2. Yerel geliştirme

Gereksinimler: .NET 10 SDK, Docker.

```bash
docker run -d --name ne-pg -p 5432:5432 \
  -e POSTGRES_DB=nurol_envanter -e POSTGRES_USER=nurol -e POSTGRES_PASSWORD=gelistirme \
  postgres:17-alpine
dotnet tool restore
dotnet run --project src/Nurol.Envanter.Server --launch-profile http
```

`http://localhost:5115` adresini açın. Geliştirme ortamında migration otomatik uygulanır, evraklar
`.yerel/evrak` klasörüne yazılır ve örnek veri yüklenir.

| Kullanıcı | Rol | Kapsam |
|---|---|---|
| `yonetici` | Sistem yöneticisi | Tümü |
| `selin.aydin` | Şirket yöneticisi | Nurol İnşaat |
| `burak.kaya` | Depo sorumlusu | Ankara Merkez, Gölbaşı Fabrika |
| `santiye.ist` | Depo sorumlusu | İstanbul Şantiye |
| `zeynep.celik` | Denetçi | Nurol İnşaat (salt okunur) |

Örnek hesapların parolası `Gelistirme123`'tür ve **yalnızca geliştirme ortamında** oluşturulur.
Üretim ortamında örnek veri ve varsayılan parola yoktur.

Şema değişikliğinden sonra yeni migration:

```bash
cd src/Nurol.Envanter.Server
dotnet ef migrations add <Ad> -o Data/Migrations
```

## 3. Docker Compose ile deneme

PostgreSQL, S3 uyumlu yerel depo (SeaweedFS) ve uygulamayı birlikte başlatır:

```bash
docker compose -f deploy/docker-compose.yml up --build
```

`http://localhost:8080` adresini açın. Örnek hesaplar yukarıdaki tablodakilerle aynıdır.
Bu ortam yalnızca deneme içindir.

> MinIO'nun Docker Hub imajı artık yayımlanmadığı için yerel denemede SeaweedFS kullanılır. Üretimde
> kurumun S3 uyumlu servisi (kurumsal MinIO dahil) kullanılır; uygulama standart S3 API'siyle çalışır.

## 4. İmajın derlenmesi

```bash
docker build -t registry.kurum.local/nurol/nurol-envanter:1.0.0 .
docker push registry.kurum.local/nurol/nurol-envanter:1.0.0
```

Derleme NuGet'e (api.nuget.org) ve Debian paket deposuna erişim ister. Kurum ağında bir proxy veya
iç paket deposu varsa derleme makinesine tanımlanmalıdır.

## 5. Kubernetes kurulumu

### 5.1 Ön hazırlık (kurum tarafından)

1. PostgreSQL'de boş bir veritabanı ve bu veritabanında tablo oluşturma yetkisi olan bir kullanıcı.
2. S3 uyumlu depoda özel (public olmayan) bir kova, örn. `nurol-envanter`, ve bu kovaya okuma/yazma yetkisi olan erişim anahtarı.
3. Ingress için alan adı ve TLS sertifikası (Secret olarak).
4. PostgreSQL, S3 veya AD (LDAPS) iç CA ile imzalı sertifika kullanıyorsa kurum CA sertifikası (PEM).
5. AD kullanılacaksa: salt okunur bir servis hesabı, personelin bulunduğu OU (arama kökü) ve kümeden etki alanı denetleyicisine 636 numaralı porta erişim.

### 5.2 Namespace, Secret ve CA

```bash
kubectl create namespace envanter

# Parolaları komut geçmişine yazmamak için dosyadan okuyun.
kubectl -n envanter create secret generic nurol-envanter-gizli \
  --from-file=ConnectionStrings__Default=./pg-baglanti.txt \
  --from-file=Storage__AccessKey=./s3-erisim.txt \
  --from-file=Storage__SecretKey=./s3-gizli.txt \
  --from-file=Bootstrap__AdminPassword=./ilk-yonetici-parolasi.txt \
  --from-file=Directory__BindPassword=./ad-servis-parolasi.txt \  # AD kullanılmayacaksa bu satırı çıkarın
  --from-file=Smtp__Password=./smtp-parolasi.txt                 # kimlik doğrulamasız SMTP'de bu satırı çıkarın

# İsteğe bağlı: kurum CA sertifikası
kubectl -n envanter create configmap kurum-ca --from-file=ca.crt=./kurum-ca.pem
```

`pg-baglanti.txt` içeriği örneği (tek satır):

```
Host=pg.kurum.local;Port=5432;Database=nurol_envanter;Username=nurol_envanter;Password=...;SSL Mode=VerifyFull
```

İlk yönetici parolası en az 10 karakter olmalı; büyük harf, küçük harf ve rakam içermelidir. Bu parola
yalnızca hiç sistem yöneticisi yokken kullanılır; ilk girişten sonra uygulamadaki Yönetim ekranından değiştirilebilir.

### 5.3 Değerler ve kurulum

`deploy/helm/nurol-envanter/values-ornek.yaml` dosyasını kopyalayıp düzenleyin (imaj adresi, S3 adresi, alan adı,
ingress sınıfı, CA). Ardından:

```bash
helm upgrade --install nurol-envanter deploy/helm/nurol-envanter \
  -n envanter -f values-kurum.yaml
```

Kurulum sırası:

1. Ayarlar ConfigMap'i ve migration Job'u (Helm hook) çalışır. Job şemayı günceller ve ilk yöneticiyi oluşturur.
2. Job başarıyla bitince Deployment, Service, Ingress ve PodDisruptionBudget oluşturulur.
3. Pod'lar `/healthz/ready` kontrolünden geçince trafik alır. Bu kontrol veritabanı bağlantısını, şemanın güncelliğini ve nesne deposunu doğrular.

Kontrol:

```bash
kubectl -n envanter get pods
kubectl -n envanter logs job/nurol-envanter-migration   # hook başarılıysa silinmiş olabilir
kubectl -n envanter port-forward svc/nurol-envanter 8080:80
curl http://localhost:8080/healthz/ready
```

### 5.4 Chart'ın sağladıkları

- Root olmayan, salt okunur kök dosya sistemli konteyner; tüm Linux yetenekleri kapalı; `/tmp` için emptyDir.
- startup/readiness/liveness kontrolleri, kaynak istek ve sınırları.
- Kesintisiz güncelleme (maxUnavailable 0) ve PodDisruptionBudget.
- Evrak yüklemeleri için ingress gövde sınırı 25 MB (nginx ingress açıklaması; başka ingress denetleyicilerinde eşdeğeri ayarlanmalıdır).
- Migration'lar PostgreSQL advisory lock ile korunur; aynı anda iki Job çalışsa da tek kez uygulanır.

- `directory.schedule.enabled` açıksa AD eşitleme CronJob'u (`concurrencyPolicy: Forbid`; ayrıca veritabanı kilidi).
- `smtp.digest.enabled` açıksa günlük özet CronJob'u.

### 5.5 Yükseltme ve geri alma

```bash
helm upgrade nurol-envanter deploy/helm/nurol-envanter -n envanter -f values-kurum.yaml --set image.tag=1.1.0
helm rollback nurol-envanter <revizyon> -n envanter
```

Geri alma uygulama sürümünü döndürür ama şemayı geri almaz. Şema değişikliği içeren bir sürümden önce
veritabanı yedeği alınmalıdır.

## 6. Active Directory

### 6.1 Ayarlar

`values` dosyasındaki `directory` bölümü (örnek: `values-ornek.yaml`):

| Değer | Açıklama |
|---|---|
| `syncEnabled` | Personel eşitlemesini açar (elle ve zamanlanmış). |
| `loginEnabled` | AD hesaplarıyla girişi açar. Eşitlemeden bağımsızdır. |
| `server`, `port`, `useSsl` | Etki alanı denetleyicisi. LDAPS (636) önerilir; iç CA için `corporateCa` açılmalıdır. |
| `baseDn`, `filter` | Personelin arandığı OU ve filtre. Varsayılan filtre kullanıcı nesneleridir. |
| `bindDn` | Salt okunur servis hesabının DN'i. Parolası Secret'ta (`Directory__BindPassword`). |
| `domain` | Kalıcı kimliğin öneki. Personel `domain + objectGUID` ile tanınır; ad veya kullanıcı adı değişse de aynı kayıt kalır. |
| `attributes.*` | AD öznitelik eşlemesi (sAMAccountName, displayName, mail, title, department, company, employeeID). |
| `schedule.*` | Zamanlanmış eşitleme (varsayılan kapalı). |

AD parolaları veritabanına yazılmaz ve yerel parolaya dönüştürülmez.

### 6.2 İlk eşitleme

1. Uygulamada **Yönetim → Active Directory** sekmesinde **Bağlantıyı test et**. Servis hesabı reddedilirse veya sunucuya ulaşılamazsa anlaşılır bir hata gösterilir.
2. **Şirket eşleme kuralları** ekleyin: AD'deki `company`, `department` vb. değerine göre ya da personelin bulunduğu OU'ya göre (`DN ile biter`) şirket atanır. AD'deki serbest metin şirket adı tek başına yetki vermez; kurala uymayan kişi oluşturulmaz, raporda “Eşleşmeyen” olarak listelenir.
3. **Önizleme ile eşitle**. Önizleme hiçbir kaydı değiştirmez; yeni, güncellenen, pasife alınacak ve eşleşmeyen kişileri listeler.
4. Sonuç uygunsa **Bu değişiklikleri uygula**.

Elle girilmiş bir personel, aynı şirkette sicil numarası (`employeeID`) eşleşirse yeni kayıt açılmadan AD hesabına bağlanır.

### 6.3 Güvenlik kuralları

- Arama tamamlanamazsa (ağ, TLS, servis hesabı) hiçbir personel kaydı değişmez.
- Dönen kayıt sayısı mevcut aktif AD personelinin `minFoundRatio` oranının altındaysa (ör. yanlış OU) kimse topluca pasife alınmaz; raporda uyarı görünür.
- AD'de devre dışı bırakılan veya artık bulunmayan personel pasife alınır, silinmez; zimmet geçmişi korunur. Açık zimmeti varsa raporda belirtilir.
- Pasife alınan personele bağlı uygulama hesabı kapatılır ve açık oturumları hemen sonlandırılır.
- Aynı anda tek eşitleme çalışır.

### 6.4 AD hesaplarına giriş yetkisi

AD'den gelen personel otomatik giriş hakkı kazanmaz. **Yönetim → Kullanıcılar → Hesap aç → Kurum hesabı (AD)** ile personel seçilip rol ve şirket/depo kapsamı verilir.

Girişte kullanıcı adı `ad.soyad`, `NUROL\ad.soyad` veya `ad.soyad@nurol.local` biçiminde yazılabilir. Her girişte:

- Hesap AD'de aranır; devre dışıysa giriş reddedilir.
- AD kimliği, yetkinin verildiği kimlikle aynı olmalıdır. Aynı kullanıcı adıyla sonradan açılan başka bir AD hesabı yetkiyi devralamaz.
- Parola AD'de doğrulanır. Başarısız doğrulama yerel parolaya düşmez. AD'ye ulaşılamazsa giriş yapılamaz (yerel yönetici hesabı her zaman kullanılabilir).

## 7. E-posta bildirimleri

`values` dosyasındaki `smtp` bölümüyle açılır; parola Secret'taki `Smtp__Password` anahtarındadır.

| Bildirim | Alıcı | Zaman |
|---|---|---|
| Transfer yolda | Hedef depoda yazma yetkisi olan depo sorumluları ve şirket yöneticileri | Transfer gönderildiğinde |
| Günlük özet | Her kullanıcıya kendi kapsamı için: teslim almasını bekleyen transferler, imzalı evrakı `missingDocDays` günden uzun süredir eksik tutanaklar, kritik stok, açık zimmeti kalan pasif personel. Bildirecek kayıt yoksa gönderilmez. | `smtp.digest.cron` (varsayılan hafta içi 07:30) |
| AD'den ayrılan | Sistem yöneticileri | AD eşitlemesi açık zimmetli personeli pasife aldığında |

İşleyiş:

- Bildirimler, onları doğuran işlemle aynı veritabanı işleminde bir gönderim kuyruğuna (`outbox_mails`) yazılır. İşlem geri alınırsa e-posta da oluşmaz.
- Uygulama pod'ları kuyruğu `smtp.enabled` açıkken gönderir. Birden fazla pod aynı e-postayı göndermez. Başarısız gönderimler artan aralıklarla 8 kez denenir; durum **Yönetim → Bildirimler** sekmesinde görünür ve elle tekrar denenebilir.
- Günlük özet aynı gün ikinci kez çalışsa da kişi başına tek e-posta üretilir.
- Kullanıcılar e-posta adreslerini ve bildirim tercihlerini **Hesabım** sayfasından yönetir. AD hesabı açılırken e-posta adresi AD'deki `mail` değerinden alınır.
- E-postalardaki bağlantılar `smtp.baseUrl` (boşsa ingress adresi) ile oluşturulur.

İlk kurulumdan sonra **Yönetim → Bildirimler → Kendime test e-postası gönder** ile SMTP ayarlarını doğrulayın.

## 8. Raporlar, etiketler ve toplu ekleme

- **Raporlar:** personel bazlı zimmet, eksik imzalı evrak, sarf tüketimi, envanter değeri, ayrılan personel, hareket dökümü. Hepsi kullanıcının kapsamıyla süzülür ve Excel (.xlsx) olarak indirilebilir. Tarih aralığı en fazla bir yıldır.
- **QR etiketler:** envanter listesinden (filtreye uyan en fazla 1000 kayıt) veya cihaz sayfasından A4 etiket kâğıdına PDF. İki düzen vardır: 3×8 (70×37 mm) ve 2×7 (99×38 mm). Kısmen kullanılmış kâğıt için başlangıç konumu seçilebilir. QR yalnızca envanter etiketini içerir; sunucu adresi değişse de etiketler geçerli kalır. Yazdırırken ölçek %100 olmalıdır.
- **Kod okutma:** **Kod okut** sayfası ve üst arama kutusu etiket, seri no veya benzersiz özel alan (ör. IMEI) ile cihazı bulur. USB/Bluetooth el tipi okuyucular klavye gibi çalışır. Kamerayla okuma, tarayıcının yerleşik barkod okuyucusunu destekleyen tarayıcılarda (Android'de Chrome/Edge) çalışır; kamera için sayfa HTTPS üzerinden açılmalıdır.
- **Excel ile toplu ekleme:** kategoriye özel şablon (açılır model, depo ve seçenek listeleriyle) indirilir, doldurulup yüklenir. Dosya önce satır satır kontrol edilir (model, depo yetkisi, lokasyon, seri no ve benzersiz alanların hem veritabanında hem dosya içinde tekrarı, alan kuralları). Hata varsa hiçbir satır kaydedilmez. Hatasızsa önizleme gösterilir ve onayla tüm satırlar tek işlemde eklenir. Personel için de aynı akış vardır (AD kullanılmayan personel için).

## 9. Yazıcı izleme

Yazıcılar envanterden bağımsızdır; **Yazıcılar → Yazıcı ekle** ile IP adresi (veya DNS adı), SNMP topluluk adı ve bağlı olduğu depo girilir. **Bağlantıyı test et** kaydetmeden önce model, sayaç ve toner bilgisini okur.

**Okunanlar (standart Printer-MIB, RFC 3805):** model, seri no, ağ adı, toplam sayfa sayacı ve her sarf yuvası (toner, mürekkep, drum, atık toner) için açıklama, renk ve seviye. Yazıcı yüzde yerine yalnızca “var” bildiriyorsa (bazı modeller) seviye “Var” olarak gösterilir.

**Toner eşleme ve uyarılar:**

- Her yuva için **kullanılacak toner** stok malzemelerinden seçilir (önce Stok sayfasında toner malzemesi tanımlanmalıdır). “Aynı modeldeki diğer yazıcılara da uygula” ile aynı modeldeki tüm yazıcılara tek seferde atanır.
- Seviye uyarı eşiğinin (varsayılan %15) altına inince ya da biterse yazıcı sayfasında, Yazıcılar listesinde ve Genel Bakış'ta uyarı görünür. Örnek: “Black Cartridge %8. Kullanılacak toner: HP 151A (SRF-TNR-014) — Ankara Merkez Depo: 4 adet.” Toner seçilmemişse bunu belirtir. Yazıcının deposunda stok yoksa diğer depolardaki toplamı gösterir.
- Uyarı başına bir kez, yazıcının deposundaki depo sorumlularına ve şirket yöneticilerine e-posta gider; seviye yükselene kadar tekrarlanmaz. Günlük özette de “toneri azalan yazıcılar” listelenir.
- Seviye birden en az 30 puan artıp %70'in üzerine çıkarsa **toner değişimi algılanır**. Yazıcı sayfasında “Değişimi onayla” ile kayıt yapılır ve istenirse 1 adet toner yazıcının deposundan stoktan düşülür. Elle değişim için “Değiştirildi” düğmesi kullanılır.
- **Toner ihtiyacı** sekmesi her toner için kullanan yazıcı sayısını, azalan yazıcı sayısını ve depolardaki stoku gösterir.

**Parça numarasından toner önerisi (Kyocera ve HP):** Kyocera yazıcılar sarf açıklamasında doğrudan toner kodunu bildirir (ör. `TK-1170`, renklilerde `TK-5240K/C/M/Y`). HP yazıcılar parça numarasını açıklamaya yazar (ör. `Black Cartridge HP CF259A`). Uygulama bu numarayı stoktaki malzemelerin adı veya koduyla eşleştirir ve yuvaya **önerilen toneri** gösterir. Uyarılarda öneri “Önerilen toner: …” olarak geçer. **Toner önerilerini uygula** düğmesi tonersiz yuvalara önerileri toplu atar. Öneri yalnızca tek bir stok kalemi eşleşiyorsa yapılır; bu yüzden toner malzemelerini Stok sayfasında parça numarasıyla adlandırın (ad veya kodda `TK-1170`, `CF259A` geçsin). Renk harfi olmayan bir kalem (ör. “TK-5240 toner seti”) tek adaysa her renge önerilir.

**Marka notları:**

- **Kyocera (ECOSYS / TASKalfa):** SNMP genellikle açık gelir. Kapalıysa yazıcının web arayüzünde (Command Center RX) ağ ayarlarının protokol bölümünden **SNMPv1/v2c** açılır; okuma topluluk adı varsayılan olarak `public`'tir. Kyocera seviyeyi baskı birimiyle bildirir; uygulama bunu yüzdeye çevirir. Atık toner kutusu çoğunlukla yalnızca “var/dolu” bildirir ve “Var” görünür.
- **HP (LaserJet / Color LaserJet):** Yeni HP ürün yazılımlarında SNMPv1/v2 kapalı gelebilir. Yazıcının web arayüzünde (EWS) Ağ/Güvenlik altındaki yönetim protokolleri ya da SNMP ayarından **SNMPv1/v2 salt okunur erişim** açılır. Orijinal olmayan veya yeniden doldurulmuş kartuşlarda HP yüzde yerine yalnızca “var” bildirebilir; bu durumda seviye “Var” görünür ve yüzde eşiği uyarısı verilemez. Kartuş bittiğinde yine uyarı çıkar.
- Menü adları ürün yazılımı sürümüne göre değişebilir. Her model için bir yazıcıyı ekleyip **Bağlantıyı test et** ile kontrol edin.
- Renkli/siyah-beyaz ayrı sayaçlar ve kopya/baskı ayrımı standart MIB'de yoktur; uygulama yalnızca toplam sayfa sayacını okur. Ayrı sayaç gerekiyorsa üreticiye özel MIB değerleri ek geliştirmeyle eklenebilir.

**Sayaç:** her okumada gün sonu sayacı saklanır. Yazıcı sayfasında günlük baskı ve son 30 gün toplamı, **Raporlar → Yazıcı sayaçları**'nda tarih aralığına göre yazıcı başına basılan sayfa (Excel'e aktarılabilir) görünür. Sayaç sıfırlanmışsa o dönem boş bırakılır.

**Okuma sıklığı ve çalışma şekli:** uygulama yazıcıları `printers.pollMinutes` (varsayılan 5 dk) aralıkla okur; birden fazla pod olsa da okumayı aynı anda tek pod yapar. Yazıcı sayfasındaki **Şimdi oku** anında okur. Okuma başarısız olursa yazıcı “Ulaşılamıyor” görünür ve son bilinen seviyeler korunur.

**Ağ ve güvenlik:**

- Pod'lardan yazıcı ağlarına **UDP 161** çıkışına izin verin (güvenlik duvarı, NetworkPolicy egress).
- Uygulama yalnızca `printers.allowedNetworks` içindeki adreslere bağlanır (varsayılan özel ağlar 10/8, 172.16/12, 192.168/16). Kurumun yazıcı VLAN'ıyla daraltılması önerilir; liste verildiğinde yalnızca o ağlar geçerlidir.
- Yazıcılarda salt okunur bir SNMP topluluk adı kullanın. Topluluk adı veritabanında saklanır, arayüze geri gönderilmez. SNMP v3 bu sürümde desteklenmez.

## 10. Telefon hatları ve ilişik kesme

**Hatlar** sayfasında her hat için numara, operatör, hat tipi, tarife, aylık ücret, SIM seri numarası (ICCID), masraf yeri ve sözleşme bitişi tutulur. Numara hangi biçimde yazılırsa yazılsın (`+90 532…`, `532…`, boşluklu) `0532 418 27 60` biçimine çevrilir; numara ve ICCID tekildir.

- **Cihaza takma:** telefon/modem gibi cihazlara en fazla iki hat takılır. Cihaz kime zimmetliyse hat da onda görünür ve zimmet tutanağının “Ek bilgiler” sütununa `Hat: 0532 …` olarak yazılır. Cihaz iade alınınca hat cihazla birlikte döner.
- **Personele doğrudan verme:** kişisel cihazdaki data SIM gibi cihazsız hatlar doğrudan personele verilir ve geri alınır. Hat yalnızca aynı şirketin aktif personeline verilir. İptal edilen hat kişiden düşülür.
- Toplu ekleme: **Excel ile ekleme → Telefon hattı**. Maliyet: **Raporlar → Telefon hatları ve maliyet** (hat listesi ya da operatör, birim, masraf yeri toplamları; Excel'e aktarılabilir). Sözleşmesi 30 gün içinde bitecek hatlar listede işaretlenir ve günlük özette yer alır.

**İlişik kesme** (işten ayrılan personel):

- AD eşitlemesinde devre dışı kalan ya da elle pasife alınan personelin üzerinde cihaz veya hat varsa süreç **kendiliğinden açılır** (`ILS-2026-000001`). Personel kartından veya İlişik kesme sayfasından elle de başlatılabilir. Bir personel için aynı anda tek açık süreç olur. Açılışta şirketteki depo sorumlularına, şirket yöneticilerine ve sistem yöneticilerine e-posta gider.
- Kontrol listesi: üzerindeki cihazlar (iade ekranına bağlantıyla), hatlar (doğrudan verilenler buradan geri alınır) ve elle eklenen görevler (giriş kartı, anahtar, araç vb.).
- Cihazların tümü iade alınmadan, doğrudan verilen hatlar geri alınmadan ve görevler işaretlenmeden süreç **kapatılamaz**. Kullanıcının kapsamı dışındaki depolarda kalan cihazlar da sayılır; bu cihazları o depoların sorumluları iade alır.
- Kapatılınca kapanış anındaki verilerle **ilişik kesme belgesi** (A4 PDF) üretilir, özel depoya yazılır ve SHA-256 özeti saklanır; indirirken özet kontrol edilir. Kapanan süreç değiştirilemez.
- Her yeni sürece otomatik eklenecek görevler ortam değişkenleriyle tanımlanabilir (Helm `extraEnv`):

```yaml
extraEnv:
  Offboarding__DefaultTasks__0: "Giriş kartı teslim alındı"
  Offboarding__DefaultTasks__1: "E-posta hesabı yönlendirildi"
```

## 11. Yedekleme ve geri yükleme

Veritabanı ve nesne deposu **birlikte** yedeklenmelidir. Tutanak kayıtları veritabanında, dosyaları nesne deposundadır.

Önerilen sıra:

1. `pg_dump -Fc -d nurol_envanter -f envanter-YYYYMMDD.dump`
2. Ardından kovanın eşlemesi (örn. `mc mirror` veya `aws s3 sync`) yedek konumuna.

Nesne deposu veritabanından sonra yedeklendiği için, veritabanında kaydı olan her dosya yedekte bulunur.
Geri yüklemede önce kova, sonra veritabanı geri yüklenir (`pg_restore -c -d nurol_envanter envanter-YYYYMMDD.dump`).
Evrak dosyaları yüklendikten sonra değiştirilmez; her sürüm ayrı bir nesnedir ve SHA-256 değeri veritabanında saklanır.

Gerçek yedekleme sıklığı, saklama süresi ve felaket kurtarma planı kurumun yedekleme altyapısına göre belirlenmelidir.

## 12. Güvenlik notları

- Oturum cookie'si HttpOnly, SameSite=Strict ve üretimde Secure'dür. Uygulama HTTPS arkasında (ingress TLS) çalışmalıdır.
- Durum değiştiren tüm API isteklerinde özel bir başlık zorunludur (CSRF koruması); uygulama CORS açmaz.
- Giriş: 5 hatalı denemede hesap 15 dakika kilitlenir. Ayrıca IP başına dakikada 10 giriş denemesi sınırı vardır (`Security__LoginAttemptsPerMinute`).
- Cookie şifreleme anahtarları PostgreSQL'de (`data_protection_keys`) şifrelenmeden saklanır. Veritabanı erişimi yalnızca uygulama hesabıyla sınırlandırılmalıdır.
- Kullanıcı devre dışı bırakıldığında, rolü veya parolası değiştiğinde açık oturumları sunucudan hemen iptal edilir.
- Yüklenen dosyaların türü uzantıdan değil içerikten belirlenir (PDF/JPEG/PNG); en fazla 20 MB.

## 13. Sorun giderme

| Belirti | Olası neden |
|---|---|
| İmaj derlenirken `NU1301 ... UntrustedRoot` | Kurum ağı HTTPS trafiğini kendi sertifikasıyla denetliyor. Windows'ta `powershell -ExecutionPolicy Bypass -File .\sertifika-aktar.ps1` ile bilgisayarın güvendiği sertifikaları `sertifikalar/` klasörüne aktarın (veya kurum kök sertifikasını oraya `.crt` olarak koyun) ve yeniden derleyin. |
| `/healthz/ready` → `"schema": false` | Migration Job çalışmadı veya başarısız. Job loglarına bakın. |
| `/healthz/ready` → `"storage": false` | S3 adresi, kova adı veya erişim anahtarı hatalı; ya da kurum CA tanımlı değil. |
| `/healthz/ready` → `"db": false` | Bağlantı cümlesi, ağ erişimi veya PostgreSQL TLS/CA ayarı. |
| İlk yönetici oluşmadı | `Bootstrap__AdminPassword` parola kuralına uymuyor (Job loglarında yazar) veya zaten bir sistem yöneticisi var. |
| Evrak yüklemesi 413 | Ingress gövde sınırı 20 MB'tan küçük. |
| AD testi: “servis hesabı bilgileri reddedildi” | `bindDn` veya Secret'taki `Directory__BindPassword` hatalı. |
| AD testi: “sunucuya ulaşılamadı” | Ağ erişimi (636), sunucu adı veya kurum CA (`corporateCa`, `LDAPTLS_CACERT`). |
| Eşitlemede çok sayıda “Eşleşmeyen” | Şirket eşleme kuralı eksik; AD'deki şirket/birim değerlerini raporda görüp kural ekleyin. |
| E-postalar “Sırada” kalıyor | `smtp.enabled` kapalı veya SMTP sunucusuna ulaşılamıyor; Bildirimler sekmesindeki hata metnine bakın. |
| E-postalardaki bağlantı yanlış adrese gidiyor | `smtp.baseUrl` değerini uygulamanın dış adresiyle doldurun. |
| Etiketler kâğıda kayık basılıyor | Yazdırmada ölçek %100 (gerçek boyut) değil; ya da kâğıt düzeni seçilen şablona uymuyor. |
| Kamera ile okutma görünmüyor | Tarayıcı yerleşik barkod okuyucuyu desteklemiyor veya sayfa HTTPS değil. El tipi okuyucu veya elle giriş kullanılabilir. |
| Yazıcı “yanıt vermedi” | Yazıcıda SNMP kapalı, topluluk adı yanlış veya pod'lardan UDP 161 engelli. Yazıcının web arayüzünden SNMP v1/v2c'yi açın. |
| Yazıcı “izin verilen ağların dışında” | Adres `printers.allowedNetworks` listesinde değil. |
| Toner seviyesi “Var” veya “Bilinmiyor” | Yazıcı yüzde bildirmiyor; bazı modeller (ör. toner yeniden doldurulmuşsa) yalnızca “var/yok” bildirir. |
| AD kullanıcısı giriş yapamıyor | `loginEnabled` kapalı, uygulamada hesap açılmamış, hesap AD'de devre dışı veya AD kimliği değişmiş (hesabı yeniden açın). |

## 14. Doğrulama durumu

**Bu teslimde test edilenler:**

- 56 otomatik test. Hat ve ilişik kesme için: numara/ICCID biçim düzeltme ve tekillik, şirket kapsamı, cihaza takılı hattın zimmetliye geçmesi ve tutanağa yazılması, cihaz başına iki hat sınırı, iptalde kişiden düşme, eski sürümle kaydetmede çakışma, Excel ile hat eklemede hepsi-ya-hiçbiri; personel pasife alınınca ve AD eşitlemesinde (önizlemede değil) sürecin açılması, üzerinde bir şey olmayan kişide açılmaması, tek açık süreç, iade/geri alma/görev tamamlanmadan kapatılamaması, belge üretimi ve bütünlük özeti, kapanmış sürecin değiştirilememesi, denetçinin yalnızca görmesi, kapsam dışı şirketin görünmemesi. Akış ayrıca tarayıcıda uçtan uca denendi.
- Yazıcı izleme için: Kyocera (birimle bildirilen seviye, sonu NUL baytlı açıklama, atık toner kutusu) ve HP (“var” olarak bildirilen seviye) yanıt biçimleri, parça numarası çıkarma ve yalnızca tek aday varken öneri, önerilerin toplu uygulanması; gerçek UDP üzerinden yanıt veren bir SNMP ajanına karşı Printer-MIB okuması, yanlış topluluk ve izinli ağ dışı adresin reddi, azalan tonerde uyarı ve önerilen toner/stok metni, uyarı e-postasının bir kez gitmesi, aynı modele toner atama, değişim algılama ve stoktan düşme, ulaşılamayan yazıcıda son değerlerin korunması, sayaç raporu, depo kapsamı. Okuyucu ayrıca bağımsız bir SNMP simülatörüne (snmpsim) karşı denendi.
- Aşama 3 için: raporların kapsama göre süzülmesi ve Excel çıktısı, değer raporu toplamı, Excel toplu eklemede hatalı dosyanın hiç kaydedilmemesi (geçersiz depo, dosya içi tekrar seri no ve IMEI, liste dışı seçenek, yetkisiz depo, tanınmayan sütun), IMEI'nin baştaki sıfırı ve sayı olarak girilmesi, personel içe aktarımında sicil kontrolü, etiket PDF'inin sayfa düzeni ve QR görselinin gerçekten gömülmesi, kod ile arama ve kapsam, transfer bildiriminin doğru alıcılara gitmesi, günlük özetin aynı gün tekrar üretilmemesi, gerçek SMTP sunucusuna (Mailpit) gönderim ve hata durumunda yeniden deneme, Hesabım ayarları.
- Aşama 2 için: transfer akışı ve yetkileri (gönderme kaynak, teslim alma hedef depo yetkisi; transferdeki cihazın zimmetlenememesi; stok yetersizse gönderimin tamamen geri alınması; eşzamanlı gönderimde stokun tek kez düşmesi; şirketler arası transfer engeli), görünümlerin kişiye özel olması, AD eşitleme kuralları (yeniden adlandırmada mükerrer kayıt oluşmaması, başarısız veya şüpheli aramada toplu pasife alma yapılmaması, eşleşmeyen kişinin raporlanması) ve AD girişi (yerel parolaya düşmeme, dizine ulaşılamadığında reddetme, devre dışı hesabın oturumunun iptali, aynı kullanıcı adıyla yeni AD hesabının yetkiyi devralamaması).
- LDAP istemcisi gerçek bir dizin sunucusuna (OpenLDAP) karşı test edildi: sayfalı arama, öznitelik eşlemesi, parola doğrulama, boş parolanın reddi, filtre enjeksiyonu. `ad-sync` komutu imaj içinde salt okunur dosya sistemiyle çalıştırıldı.
- Ayrıca Aşama 1'deki 19 entegrasyon testi, gerçek PostgreSQL 17 üzerinde (Testcontainers). Kapsamları: rol ve depo kapsamı (liste, detay, CSV dışa aktarım, belge indirme), şirket bazlı zimmet kuralı, eşzamanlı çift zimmet engeli, tekrarlanan istek (idempotency), eşzamanlı stok çıkışlarında negatif bakiye engeli, IMEI kuralları (15 hane, baştaki sıfır, grup geneli benzersizlik), alan arşivleme ve değerlerin korunması, kural çakışma raporu, iyimser eşzamanlılık, PDF belgesinin sonradan değişmemesi, imzalı evrak sürümleme ve içerik türü denetimi, iki ayrı uygulama örneği arasında oturum paylaşımı ve hesap kapatılınca oturum iptali.
- S3 uyumlu depoya (SeaweedFS) bayt bayt doğru yazma/okuma testi.
- Tarayıcı akış testi (`tests/e2e/akis.cjs`): giriş, alan ekleme, sütun tercihi, kayıtlı görünüm, zimmet, PDF indirme, imzalı evrak yükleme, stok sınırı, transfer gönderme ve teslim alma, rapor Excel indirme, QR etiket PDF'i, kod okutma. Basılan etiketlerdeki QR kodları ayrıca bir QR çözücüyle (OpenCV) okunarak doğrulandı. Excel ile toplu eklemenin hata ekranı ve transfer/günlük özet e-postalarının SMTP üzerinden gelişi tarayıcı ve Mailpit ile denendi. AD eşitleme önizlemesi, AD personeline hesap açma ve AD parolasıyla giriş ayrıca tarayıcıda denendi.
- İmajın salt okunur kök dosya sistemiyle ve root olmayan kullanıcıyla çalışması, `migrate` işinin şemayı kurup ilk yöneticiyi oluşturması, şema hazır olmadan hazırlık kontrolünün 503 dönmesi.
- Docker Compose ortamının uçtan uca çalışması.
- `helm lint` ve `helm template`; üretilen manifestoların Kubernetes 1.31 şemasına göre doğrulanması (kubeconform, katı mod).

**Kurum ortamında doğrulanması gerekenler:**

- Helm chart'ın gerçek kümede kurulumu. Geliştirme ortamında yerel Kubernetes (kind) çalıştırılamadı; chart yalnızca şablon ve şema düzeyinde doğrulandı.
- Kurumsal PostgreSQL'e TLS ile bağlantı (`SSL Mode=VerifyFull` ve kurum CA).
- Kurumsal S3/MinIO servisiyle çalışma (adres, path-style erişim, CA).
- Kurum SMTP sunucusu (port, TLS, kimlik doğrulama) ve e-postaların spam filtrelerinden geçmesi.
- Etiketlerin kurumda kullanılacak etiket kâğıdı ve yazıcıyla hizası; kamerayla okutmanın sahadaki telefonlarda denenmesi.
- Kurumdaki gerçek yazıcı modelleriyle SNMP okuması: Printer-MIB standarttır ama üreticiler sarf açıklamalarını ve seviye bildirimini farklı uygular. Her model için bir yazıcı eklenip **Bağlantıyı test et** ile seviyelerin doğru göründüğü kontrol edilmelidir; pod'lardan UDP 161 erişimi de bu sırada doğrulanır.
- Gerçek Windows Active Directory ile LDAPS bağlantısı: testler OpenLDAP üzerinde yapıldı. AD'ye özgü `objectGUID` (ikili GUID) ve `userAccountControl` (devre dışı biti) okuma kodu birim düzeyinde yazıldı ama gerçek AD'de doğrulanmadı. İlk kurulumda **Bağlantıyı test et** ve **Önizleme** ile kontrol edilmelidir.
- Ingress denetleyicisinin gövde sınırı ve TLS ayarları.
- Yedekleme ve geri yükleme prosedürünün kurum altyapısında denenmesi.
