using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Endpoints;
using Nurol.Envanter.Server.Services;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>
/// Yalnızca geliştirme ortamı için örnek veri. Üretimde çalışmaz (Program.cs'te ortam kontrolü).
/// Örnek hesapların parolası: Gelistirme123
/// </summary>
public static class DevSeeder
{
    public const string DevPassword = "Gelistirme123";

    public static async Task RunAsync(IServiceProvider sp)
    {
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Companies.AnyAsync()) return;
        var files = scope.ServiceProvider.GetRequiredService<IFileStore>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        Company Co(string code, string name) { var c = new Company { Id = Guid.NewGuid(), Code = code, Name = name }; db.Companies.Add(c); return c; }
        var ins = Co("NRL", "Nurol İnşaat");
        var mak = Co("NMK", "Nurol Makina");
        Co("NTK", "Nurol Teknoloji");

        Warehouse Wh(Company c, string name, string addr, params string[] locs)
        {
            var w = new Warehouse { Id = Guid.NewGuid(), CompanyId = c.Id, Name = name, Address = addr };
            foreach (var l in locs) w.Locations.Add(new Location { Id = Guid.NewGuid(), Name = l });
            db.Warehouses.Add(w);
            return w;
        }
        var ank = Wh(ins, "Ankara Merkez Depo", "Çankaya, Ankara", "Raf A-01", "Raf B-04", "BT Odası", "Arşiv");
        var ist = Wh(ins, "İstanbul Şantiye Deposu", "Başakşehir, İstanbul", "Konteyner 1", "Konteyner 2", "Şantiye Ofisi");
        var glb = Wh(ins, "Gölbaşı Fabrika Deposu", "Gölbaşı, Ankara", "Sistem Odası", "İdari Bina", "Güvenlik");
        var mkd = Wh(mak, "Makina Merkez Depo", "Gölbaşı, Ankara", "Ana Depo");

        Category Cat(string code, string name, bool serial) { var c = new Category { Id = Guid.NewGuid(), Code = code, Name = name, RequiresSerial = serial }; db.Categories.Add(c); return c; }
        var tlf = Cat("TLF", "Cep Telefonu", true);
        var blg = Cat("BLG", "Dizüstü Bilgisayar", true);
        var mnt = Cat("MNT", "Monitör", true);
        var agc = Cat("AGC", "Ağ Cihazı", true);
        var tbl = Cat("TBL", "Tablet", true);
        Cat("YZC", "Yazıcı", false);
        Cat("TLS", "Telsiz", false);

        FieldDefinition F(Category? c, AssetModel? m, string key, string label, FieldType t, int order, bool req = false, bool uniq = false, int? exact = null, bool digits = false, params string[] opts)
        {
            var f = new FieldDefinition { Id = Guid.NewGuid(), Key = key, Label = label, Type = t, CategoryId = c?.Id, AssetModelId = m?.Id, SortOrder = order, Required = req, Unique = uniq, ExactLength = exact, DigitsOnly = digits, Options = [.. opts] };
            db.FieldDefinitions.Add(f);
            return f;
        }
        var imei1 = F(tlf, null, "imei_1", "IMEI 1", FieldType.Text, 1, req: true, uniq: true, exact: 15, digits: true);
        var imei2 = F(tlf, null, "imei_2", "IMEI 2", FieldType.Text, 2, uniq: true, exact: 15, digits: true);
        var hat = F(tlf, null, "hat_numarasi", "Hat numarası", FieldType.Text, 3);
        var renk = F(tlf, null, "renk", "Renk", FieldType.Select, 4, opts: ["Siyah", "Beyaz", "Koyu gri", "Mavi"]);
        var ram = F(blg, null, "ram", "RAM", FieldType.Select, 1, opts: ["8 GB", "16 GB", "32 GB", "64 GB"]);
        var cpu = F(blg, null, "islemci", "İşlemci", FieldType.Text, 2);
        var disk = F(blg, null, "disk", "Disk", FieldType.Text, 3);
        var garanti = F(blg, null, "garanti_bitisi", "Garanti bitişi", FieldType.Date, 4);
        var ip = F(agc, null, "ip_adresi", "IP adresi", FieldType.Text, 1);
        var mac = F(agc, null, "mac_adresi", "MAC adresi", FieldType.Text, 2, uniq: true);
        var boyut = F(mnt, null, "ekran_boyutu", "Ekran boyutu (inç)", FieldType.Number, 1);
        var timei = F(tbl, null, "imei_tablet", "IMEI", FieldType.Text, 1, uniq: true, exact: 15, digits: true);

        Manufacturer Mf(string n) { var m = new Manufacturer { Id = Guid.NewGuid(), Name = n }; db.Manufacturers.Add(m); return m; }
        var samsung = Mf("Samsung"); var apple = Mf("Apple"); var lenovo = Mf("Lenovo"); var dell = Mf("Dell");
        var lg = Mf("LG"); var cisco = Mf("Cisco"); var xiaomi = Mf("Xiaomi"); Mf("HP");

        AssetModel Md(Category c, Manufacturer m, string n) { var x = new AssetModel { Id = Guid.NewGuid(), CategoryId = c.Id, ManufacturerId = m.Id, Name = n }; db.AssetModels.Add(x); return x; }
        var a55 = Md(tlf, samsung, "Galaxy A55"); var s24 = Md(tlf, samsung, "Galaxy S24"); var ip15 = Md(tlf, apple, "iPhone 15"); var rn13 = Md(tlf, xiaomi, "Redmi Note 13");
        var t14 = Md(blg, lenovo, "ThinkPad T14 Gen 5"); var l5450 = Md(blg, dell, "Latitude 5450"); var l7450 = Md(blg, dell, "Latitude 7450");
        var lg27 = Md(mnt, lg, "27BR650B"); var c1000 = Md(agc, cisco, "Catalyst 1000-24T"); var tab5 = Md(tbl, samsung, "Galaxy Tab Active5");
        var esim = F(null, ip15, "esim", "eSIM", FieldType.Boolean, 10);
        F(null, tab5, "kalem_seri_no", "Kalem seri no", FieldType.Text, 10);

        Employee Emp(Company c, string reg, string name, string title, string dept)
        {
            var e = new Employee { Id = Guid.NewGuid(), CompanyId = c.Id, RegistryNo = reg, FullName = name, Title = title, Department = dept, Email = Slug(name) + "@nurol.com.tr", Source = EmployeeSource.Manual };
            db.Employees.Add(e);
            return e;
        }
        var ayse = Emp(ins, "10482", "Ayşe Korkmaz", "Satın Alma Uzmanı", "Satın Alma Müdürlüğü");
        var mehmet = Emp(ins, "11207", "Mehmet Yıldırım", "Proje Mühendisi", "Altyapı Projeleri");
        var can = Emp(ins, "09931", "Can Demir", "Şantiye Şefi", "İstanbul Şantiyesi");
        var elif = Emp(ins, "11530", "Elif Şahin", "Mimar", "Tasarım Ofisi");
        var hasan = Emp(ins, "08714", "Hasan Öztürk", "Güvenlik Amiri", "İdari İşler");
        Emp(ins, "10066", "Kemal Arslan", "Saha Mühendisi", "Altyapı Projeleri");
        Emp(ins, "12011", "Zeynep Çelik", "İç Denetçi", "İç Denetim");
        Emp(ins, "12140", "Burak Kaya", "BT Uzmanı", "Bilgi Teknolojileri");
        Emp(mak, "20031", "Murat Aydemir", "Üretim Şefi", "Üretim");
        await db.SaveChangesAsync();

        var sys = Access.System("ornek-veri");
        var n = 0;
        async Task<InventoryItem> Item(Warehouse w, AssetModel m, Category c, string serial, string? loc, decimal? cost, Dictionary<Guid, string?> fields)
        {
            var company = await db.Companies.FindAsync(w.CompanyId);
            var no = await Counters.NextAsync(db, $"TAG-{company!.Code}-{c.Code}");
            var item = new InventoryItem
            {
                Id = Guid.NewGuid(), Tag = $"{company.Code}-{c.Code}-{no:000000}", CompanyId = w.CompanyId, CategoryId = c.Id, ManufacturerId = m.ManufacturerId,
                AssetModelId = m.Id, WarehouseId = w.Id, LocationId = w.Locations.FirstOrDefault(l => l.Name == loc)?.Id, Serial = serial,
                Status = ItemStatus.InStock, Cost = cost, CreatedAt = DateTimeOffset.UtcNow.AddDays(-30 + n++)
            };
            db.Items.Add(item);
            await FieldRules.ApplyAsync(db, item, await FieldRules.ActiveFieldsAsync(db, c.Id, m.Id), fields);
            ItemEndpoints.Event(db, item.Id, "Kayıt", $"Envantere eklendi: {w.Name}", sys.UserName);
            await db.SaveChangesAsync();
            return item;
        }
        var i1 = await Item(ank, a55, tlf, "R58X21KQ9PL", "Raf B-04", 18450, new() { [imei1.Id] = "352093114857261", [imei2.Id] = "352093114857279", [hat.Id] = "0532 418 27 60", [renk.Id] = "Koyu gri" });
        var i2 = await Item(ank, t14, blg, "PF4K92LX", "BT Odası", 64900, new() { [ram.Id] = "32 GB", [cpu.Id] = "Intel Core Ultra 7 155U", [disk.Id] = "1 TB NVMe", [garanti.Id] = "2029-10-14" });
        await Item(ist, ip15, tlf, "G6TZ81PQ0D", "Konteyner 2", 52000, new() { [imei1.Id] = "013847002816540", [hat.Id] = "0533 902 11 45", [renk.Id] = "Siyah", [esim.Id] = "true" });
        var i4 = await Item(ank, l5450, blg, "8KJ4N03", "BT Odası", 48300, new() { [ram.Id] = "16 GB", [cpu.Id] = "Intel Core Ultra 5 135U", [disk.Id] = "512 GB NVMe" });
        await Item(glb, c1000, agc, "FOC2741Y0QS", "Sistem Odası", 31750, new() { [ip.Id] = "10.20.4.11", [mac.Id] = "6C:13:D5:4A:91:0E" });
        var i6 = await Item(ank, s24, tlf, "R5CX10MMB2E", "Raf B-04", 41200, new() { [imei1.Id] = "354812290034118", [hat.Id] = "0532 650 74 09", [renk.Id] = "Siyah" });
        await Item(ank, lg27, mnt, "402NTPC8L113", "Raf A-01", 9800, new() { [boyut.Id] = "27" });
        var i8 = await Item(ist, t14, blg, "PF4K93MB", "Şantiye Ofisi", 64900, new() { [ram.Id] = "32 GB", [cpu.Id] = "Intel Core Ultra 7 155U", [disk.Id] = "1 TB NVMe" });
        await Item(ist, tab5, tbl, "R52W70A1KJT", "Konteyner 2", 27600, new() { [timei.Id] = "355601472210983" });
        var i10 = await Item(glb, rn13, tlf, "45021AB7Q", "Güvenlik", 11900, new() { [imei1.Id] = "864211063390527", [hat.Id] = "0530 117 52 38", [renk.Id] = "Mavi" });
        await Item(ank, l7450, blg, "3MZ8R14", "BT Odası", 71500, new() { [ram.Id] = "16 GB", [cpu.Id] = "Intel Core Ultra 7 165U" });
        await Item(mkd, a55, tlf, "R58X21KQ9ZZ", "Ana Depo", 18450, new() { [imei1.Id] = "352093114857550" });

        await AssignmentEndpoints.Issue(db, files, sys, ayse.Id, [i1.Id], null, null, null);
        await AssignmentEndpoints.Issue(db, files, sys, mehmet.Id, [i2.Id], new Dictionary<Guid, string> { [i2.Id] = "Sağlam, çantalı" }, null, null);
        await AssignmentEndpoints.Issue(db, files, sys, can.Id, [i6.Id], null, "Şantiye için", null);
        await AssignmentEndpoints.Issue(db, files, sys, elif.Id, [i8.Id], null, null, null);
        await AssignmentEndpoints.Issue(db, files, sys, hasan.Id, [i10.Id], null, null, null);
        var maint = await db.Items.FirstAsync(x => x.Id == i4.Id);
        maint.Status = ItemStatus.InMaintenance; maint.MaintenanceNote = "Ekran arızası · Dell yetkili servis";
        ItemEndpoints.Event(db, maint.Id, "Bakım", "Bakıma alındı: Ekran arızası · Dell yetkili servis", sys.UserName);
        await db.SaveChangesAsync();

        StockItem St(Company c, string code, string name, string unit, decimal min) { var s = new StockItem { Id = Guid.NewGuid(), CompanyId = c.Id, Code = code, Name = name, Unit = unit, MinLevel = min }; db.StockItems.Add(s); return s; }
        var kbl = St(ins, "SRF-KBL-001", "Cat6 ağ kablosu", "metre", 300);
        var tnr = St(ins, "SRF-TNR-014", "HP 151A toner", "adet", 10);
        var klv = St(ins, "SRF-KLV-003", "USB klavye (TR-Q)", "adet", 20);
        var mou = St(ins, "SRF-MOU-003", "Kablosuz mouse", "adet", 15);
        var sim = St(ins, "SRF-SIM-001", "Data hattı SIM kart", "adet", 10);
        await db.SaveChangesAsync();
        async Task Mv(StockItem s, Warehouse w, StockMovementType t, decimal q, Guid? emp = null, string? project = null, string? note = null) =>
            await StockEndpoints.Move(db, sys, new StockMovementRequest(s.Id, w.Id, t, q, emp, project, note), null);
        await Mv(kbl, ank, StockMovementType.In, 1500, note: "Açılış bakiyesi");
        await Mv(kbl, ist, StockMovementType.In, 310, note: "Açılış bakiyesi");
        await Mv(kbl, glb, StockMovementType.In, 85, note: "Açılış bakiyesi");
        await Mv(kbl, ank, StockMovementType.Out, 260, project: "Altyapı Projeleri", note: "Kat 2 kablolama");
        await Mv(tnr, ank, StockMovementType.In, 6, note: "Açılış bakiyesi");
        await Mv(tnr, ank, StockMovementType.Out, 2, emp: ayse.Id, note: "Kat 3 yazıcısı");
        await Mv(tnr, glb, StockMovementType.In, 2, note: "Açılış bakiyesi");
        await Mv(klv, ank, StockMovementType.In, 38, note: "Açılış bakiyesi");
        await Mv(klv, ist, StockMovementType.In, 12, note: "Açılış bakiyesi");
        await Mv(mou, ank, StockMovementType.In, 22, note: "Açılış bakiyesi");
        await Mv(mou, ist, StockMovementType.In, 6, note: "Açılış bakiyesi");
        await Mv(sim, ank, StockMovementType.In, 14, note: "Açılış bakiyesi");
        await Mv(sim, ist, StockMovementType.In, 3, note: "Açılış bakiyesi");

        async Task U(string user, string name, string role, params (Guid company, Guid? wh)[] scopes)
        {
            if (await users.FindByNameAsync(user) is not null) return;
            var u = new AppUser { UserName = user, DisplayName = name, AccountType = "Local", LockoutEnabled = true };
            var r = await users.CreateAsync(u, DevPassword);
            if (!r.Succeeded) throw new InvalidOperationException(string.Join(", ", r.Errors.Select(e => e.Code)));
            await users.AddToRoleAsync(u, role);
            foreach (var (c, w) in scopes) db.UserScopes.Add(new UserScope { Id = Guid.NewGuid(), UserId = u.Id, CompanyId = c, WarehouseId = w });
            await db.SaveChangesAsync();
        }
        await U("yonetici", "Sistem Yöneticisi", Roles.SystemAdmin);
        await U("selin.aydin", "Selin Aydın", Roles.CompanyAdmin, (ins.Id, null));
        await U("burak.kaya", "Burak Kaya", Roles.WarehouseManager, (ins.Id, ank.Id), (ins.Id, glb.Id));
        await U("santiye.ist", "İstanbul Şantiye", Roles.WarehouseManager, (ins.Id, ist.Id));
        await U("zeynep.celik", "Zeynep Çelik", Roles.Auditor, (ins.Id, null));
    }

    static string Slug(string name) => FieldRules.Slug(name).Replace('_', '.');
}
