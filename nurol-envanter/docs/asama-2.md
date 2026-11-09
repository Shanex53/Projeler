# Aşama 2 — Entegrasyon ve akış

Durum: **tamamlandı** (9 Kasım 2026). Kaynak: [tasarim.md](tasarim.md) §3–§6.

## Kapsam

| Konu | Uygulama |
|---|---|
| Transfer | `Transfer` / `TransferLine` (cihaz veya malzeme + miktar). Taslak → gönderildi → teslim alındı; taslak iptal edilebilir. Gönderimde cihazlar satır kilidiyle “transferde” olur, malzeme koşullu UPDATE ile kaynaktan düşer; yetersizse işlem tamamen geri alınır. Teslimde cihazlar hedef depoya ve seçilen lokasyona geçer, malzeme hedefe eklenir. Stok hareketleri “Transfer çıkışı / girişi” türüyle ve transfer numarasıyla kaydedilir. |
| Yetki | Gönderme ve iptal: kaynak depo yazma yetkisi. Teslim alma: hedef depo yazma yetkisi. Transfer, kaynak veya hedef depoda yetkisi olanlara görünür. Hedef depo seçimi için kullanıcının şirketlerindeki depo adları (`/api/warehouses/targets`) listelenir; içerik gösterilmez. |
| Kurallar | Yalnızca aynı şirket depoları; zimmetli/bakımdaki cihaz eklenemez; bir cihaz aynı anda tek taslakta olabilir; tekrarlanan gönderme/teslim isteği mükerrer hareket üretmez; kısmi teslim yok. |
| Kayıtlı görünümler | `SavedView` (kullanıcı + sayfa + ad + JSON durum). Adı boş kayıt, sayfanın varsayılan sütun düzenidir; artık tarayıcıda değil hesapta saklanır. Görünümler yalnızca sahibine görünür. |
| AD eşitleme | `DirectorySync`: LDAPS sayfalı arama, `domain + objectGUID` kalıcı kimlik, kurallarla şirket eşleme, sicil no ile elle girilmiş kayda bağlama, önizleme, advisory lock, başarısız/şüpheli aramada toplu pasife almama, pasife alınan personelin hesabını kapatma ve oturumlarını iptal etme. Her çalışma `DirectorySyncRun` olarak saklanır. |
| AD girişi | Hesap türü seçilerek giriş. AD hesabı her girişte AD'de aranır, devre dışılık ve kimlik eşleşmesi kontrol edilir, parola AD'ye bağlanarak doğrulanır. Yerel parolaya düşmez. |
| Dağıtım | Helm: `directory.*` değerleri, `Directory__BindPassword` Secret anahtarı, `LDAPTLS_CACERT`, isteğe bağlı CronJob (`ad-sync`). İmaja `libldap2` eklendi. |

## Notlar

- AD kodu Windows AD öznitelikleri (`objectGUID`, `sAMAccountName`, `userAccountControl`) için varsayılanlarla gelir; öznitelik adları ayarlanabilir olduğundan testler OpenLDAP üzerinde (`entryUUID`, `uid`) yapıldı. Gerçek AD ile ilk bağlantı kurum ağında **Bağlantıyı test et** ve **Önizleme** ile doğrulanmalıdır.
- Eşitleme kodu, her alan adının kayıtlarını ayrı değerlendirir; birden fazla alan adı eşitlense de bir alan adındaki eksiklik diğerinin personelini etkilemez.
- Windows oturumuyla otomatik giriş (Kerberos/SSO) tasarımdaki gibi kapsam dışıdır.
