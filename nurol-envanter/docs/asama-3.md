# Aşama 3 — Geliştirmeler

Durum: **tamamlandı** (9 Kasım 2026). Kaynak: [tasarim.md](tasarim.md) §3 Aşama 3.

| Konu | Uygulama |
|---|---|
| Raporlar | `/api/reports/{anahtar}` altı rapor; `?format=xlsx` ile ClosedXML üzerinden tipli hücrelerle (tarih, tutar, sayı) Excel. Tüm sorgular `Access` kapsam filtrelerinden geçer. Arayüzde tek bir rapor sayfası; ilk 500 satır ekranda, tamamı Excel'de. |
| Toplu ekleme | Kategoriye özel şablon (metin biçimli sütunlar, açılır listeler, açıklama sayfası). Yükleme önce doğrulanır; onay ayrı istektir ve tüm satırlar tek transaction'da, Aşama 1'deki kayıt koduyla (`ItemEndpoints.CreateAsync`) eklenir. Personel için ayrı şablon. |
| QR etiket | QRCoder ile QR (içerik: yalnızca envanter etiketi), MigraDoc ile A4 etiket kâğıdı PDF'i; 3×8 ve 2×7 düzen, başlangıç konumu. |
| Kod okutma | `/api/items/lookup`: etiket → seri no → benzersiz özel alan. Arayüzde “Kod okut” sayfası (el tipi okuyucu, kamera: tarayıcının `BarcodeDetector` API'si) ve üst arama kutusunda doğrudan cihaza gitme. |
| Bildirimler | Outbox deseni (`outbox_mails`, `DedupKey`), MailKit ile gönderen arka plan işi (`FOR UPDATE SKIP LOCKED`, artan bekleme), `bildirim-ozet` komutu ve CronJob'u, Hesabım sayfası, yönetimde Bildirimler sekmesi. |

## Yol üstünde bulunan ve düzeltilen

- QR kodları ilk sürümde PDF'e basılmıyordu: PDFsharp'ın platformdan bağımsız sürümü QRCoder'ın PNG çıktısını okuyamıyor ve yerine “Image has no valid type” yazıyor. QR artık BMP olarak gömülüyor; test, PDF'te görsel nesnesi olduğunu ve yer tutucu metnin olmadığını kontrol ediyor. Basılan etiketler bir QR çözücüyle okunarak doğrulandı.
- Envanter sayfası açıkken üst arama kutusundan yapılan arama sayfayı güncellemiyordu; sayfa artık adres parametresindeki değişikliğe tepki veriyor.

## Notlar

- Kamerayla okutma tarayıcının yerleşik barkod okuyucusuna dayanır (Android'de Chrome/Edge). iOS Safari gibi desteklemeyen tarayıcılarda sayfa bunu belirtir; el tipi okuyucu ve elle giriş her yerde çalışır. Kamera erişimi için uygulama HTTPS üzerinden açılmalıdır.
- Excel şablonundaki açılır listeler Excel'in standart veri doğrulamasıdır; LibreOffice ve Excel'de çalışır, bazı üçüncü parti araçlar bu listeleri göstermeyebilir (değerler yine doğrulanır).
