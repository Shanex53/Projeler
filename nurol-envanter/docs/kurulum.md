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
4. PostgreSQL veya S3 iç CA ile imzalı sertifika kullanıyorsa kurum CA sertifikası (PEM).

### 5.2 Namespace, Secret ve CA

```bash
kubectl create namespace envanter

# Parolaları komut geçmişine yazmamak için dosyadan okuyun.
kubectl -n envanter create secret generic nurol-envanter-gizli \
  --from-file=ConnectionStrings__Default=./pg-baglanti.txt \
  --from-file=Storage__AccessKey=./s3-erisim.txt \
  --from-file=Storage__SecretKey=./s3-gizli.txt \
  --from-file=Bootstrap__AdminPassword=./ilk-yonetici-parolasi.txt

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

AD personel eşitleme CronJob'u 2. aşamada chart'a eklenecektir.

### 5.5 Yükseltme ve geri alma

```bash
helm upgrade nurol-envanter deploy/helm/nurol-envanter -n envanter -f values-kurum.yaml --set image.tag=1.1.0
helm rollback nurol-envanter <revizyon> -n envanter
```

Geri alma uygulama sürümünü döndürür ama şemayı geri almaz. Şema değişikliği içeren bir sürümden önce
veritabanı yedeği alınmalıdır.

## 6. Yedekleme ve geri yükleme

Veritabanı ve nesne deposu **birlikte** yedeklenmelidir. Tutanak kayıtları veritabanında, dosyaları nesne deposundadır.

Önerilen sıra:

1. `pg_dump -Fc -d nurol_envanter -f envanter-YYYYMMDD.dump`
2. Ardından kovanın eşlemesi (örn. `mc mirror` veya `aws s3 sync`) yedek konumuna.

Nesne deposu veritabanından sonra yedeklendiği için, veritabanında kaydı olan her dosya yedekte bulunur.
Geri yüklemede önce kova, sonra veritabanı geri yüklenir (`pg_restore -c -d nurol_envanter envanter-YYYYMMDD.dump`).
Evrak dosyaları yüklendikten sonra değiştirilmez; her sürüm ayrı bir nesnedir ve SHA-256 değeri veritabanında saklanır.

Gerçek yedekleme sıklığı, saklama süresi ve felaket kurtarma planı kurumun yedekleme altyapısına göre belirlenmelidir.

## 7. Güvenlik notları

- Oturum cookie'si HttpOnly, SameSite=Strict ve üretimde Secure'dür. Uygulama HTTPS arkasında (ingress TLS) çalışmalıdır.
- Durum değiştiren tüm API isteklerinde özel bir başlık zorunludur (CSRF koruması); uygulama CORS açmaz.
- Giriş: 5 hatalı denemede hesap 15 dakika kilitlenir. Ayrıca IP başına dakikada 10 giriş denemesi sınırı vardır (`Security__LoginAttemptsPerMinute`).
- Cookie şifreleme anahtarları PostgreSQL'de (`data_protection_keys`) şifrelenmeden saklanır. Veritabanı erişimi yalnızca uygulama hesabıyla sınırlandırılmalıdır.
- Kullanıcı devre dışı bırakıldığında, rolü veya parolası değiştiğinde açık oturumları sunucudan hemen iptal edilir.
- Yüklenen dosyaların türü uzantıdan değil içerikten belirlenir (PDF/JPEG/PNG); en fazla 20 MB.

## 8. Sorun giderme

| Belirti | Olası neden |
|---|---|
| `/healthz/ready` → `"schema": false` | Migration Job çalışmadı veya başarısız. Job loglarına bakın. |
| `/healthz/ready` → `"storage": false` | S3 adresi, kova adı veya erişim anahtarı hatalı; ya da kurum CA tanımlı değil. |
| `/healthz/ready` → `"db": false` | Bağlantı cümlesi, ağ erişimi veya PostgreSQL TLS/CA ayarı. |
| İlk yönetici oluşmadı | `Bootstrap__AdminPassword` parola kuralına uymuyor (Job loglarında yazar) veya zaten bir sistem yöneticisi var. |
| Evrak yüklemesi 413 | Ingress gövde sınırı 20 MB'tan küçük. |

## 9. Doğrulama durumu

**Bu teslimde test edilenler:**

- 19 otomatik entegrasyon testi, gerçek PostgreSQL 17 üzerinde (Testcontainers). Kapsamları: rol ve depo kapsamı (liste, detay, CSV dışa aktarım, belge indirme), şirket bazlı zimmet kuralı, eşzamanlı çift zimmet engeli, tekrarlanan istek (idempotency), eşzamanlı stok çıkışlarında negatif bakiye engeli, IMEI kuralları (15 hane, baştaki sıfır, grup geneli benzersizlik), alan arşivleme ve değerlerin korunması, kural çakışma raporu, iyimser eşzamanlılık, PDF belgesinin sonradan değişmemesi, imzalı evrak sürümleme ve içerik türü denetimi, iki ayrı uygulama örneği arasında oturum paylaşımı ve hesap kapatılınca oturum iptali.
- S3 uyumlu depoya (SeaweedFS) bayt bayt doğru yazma/okuma testi.
- Tarayıcı akış testi (`tests/e2e/akis.cjs`): giriş, alan ekleme, sütun tercihi, zimmet, PDF indirme, imzalı evrak yükleme, stok sınırı.
- İmajın salt okunur kök dosya sistemiyle ve root olmayan kullanıcıyla çalışması, `migrate` işinin şemayı kurup ilk yöneticiyi oluşturması, şema hazır olmadan hazırlık kontrolünün 503 dönmesi.
- Docker Compose ortamının uçtan uca çalışması.
- `helm lint` ve `helm template`; üretilen manifestoların Kubernetes 1.31 şemasına göre doğrulanması (kubeconform, katı mod).

**Kurum ortamında doğrulanması gerekenler:**

- Helm chart'ın gerçek kümede kurulumu. Geliştirme ortamında yerel Kubernetes (kind) çalıştırılamadı; chart yalnızca şablon ve şema düzeyinde doğrulandı.
- Kurumsal PostgreSQL'e TLS ile bağlantı (`SSL Mode=VerifyFull` ve kurum CA).
- Kurumsal S3/MinIO servisiyle çalışma (adres, path-style erişim, CA).
- Ingress denetleyicisinin gövde sınırı ve TLS ayarları.
- Yedekleme ve geri yükleme prosedürünün kurum altyapısında denenmesi.
