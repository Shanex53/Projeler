using System.Net;
using System.Security.Cryptography;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Tests;

[Collection("app")]
public class TransferTests(TestApp app)
{
    Guid Wh(string name) => app.Db(db => db.Warehouses.Where(w => w.Name == name).Select(w => w.Id).First());
    Guid Company(string code) => app.Db(db => db.Companies.Where(c => c.Code == code).Select(c => c.Id).First());
    record IdOnly(Guid Id);

    async Task<Guid> NewItem(HttpClient admin, string warehouse)
    {
        var model = app.Db(db => db.AssetModels.First(m => m.Name == "27BR650B").Id);
        var res = await admin.PostJson("/api/items", new ItemSave(model, Wh(warehouse), null, "MN" + RandomNumberGenerator.GetHexString(10), null, null, [], null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Read<IdOnly>()).Id;
    }

    async Task<Guid> NewStock(HttpClient admin, string warehouse, decimal qty)
    {
        var sid = (await (await admin.PostJson("/api/stock", new StockItemSave(Company("NRL"), "TRF-" + RandomNumberGenerator.GetHexString(6), "Transfer testi", "adet", 0))).Read<IdOnly>()).Id;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJson("/api/stock/movements", new StockMovementRequest(sid, Wh(warehouse), StockMovementType.In, qty, null, null, null))).StatusCode);
        return sid;
    }

    decimal Balance(Guid sid, string wh) => app.Db(db => db.StockBalances.Where(b => b.StockItemId == sid && b.WarehouseId == Wh(wh)).Select(b => (decimal?)b.Quantity).FirstOrDefault() ?? 0);

    [Fact]
    public async Task Send_and_receive_moves_items_and_stock_with_warehouse_permissions()
    {
        var admin = await app.Login("yonetici");
        var item = await NewItem(admin, "Ankara Merkez Depo");
        var sid = await NewStock(admin, "Ankara Merkez Depo", 10);

        var burak = await app.Login("burak.kaya"); // Ankara + Gölbaşı
        var created = await burak.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("İstanbul Şantiye Deposu"), [item], [new(sid, 4)], "Test"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var t = await created.Read<TransferDetail>();
        Assert.Equal(TransferStatus.Draft, t.Header.Status);
        Assert.Equal(10, Balance(sid, "Ankara Merkez Depo")); // taslak stoku etkilemez

        var sent = await (await burak.PostAsync($"/api/transfers/{t.Header.Id}/send", null)).Read<TransferDetail>();
        Assert.Equal(TransferStatus.Sent, sent.Header.Status);
        Assert.Equal(ItemStatus.InTransit, (await admin.GetJson<ItemDetail>($"/api/items/{item}")).Status);
        Assert.Equal(6, Balance(sid, "Ankara Merkez Depo"));
        Assert.Equal(0, Balance(sid, "İstanbul Şantiye Deposu")); // teslimden önce hedefe geçmez

        // Transferdeki cihaz zimmetlenemez.
        var kemal = app.Db(db => db.Employees.First(e => e.FullName == "Kemal Arslan").Id);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson("/api/assignments", new AssignmentCreate(kemal, [item], null, null))).StatusCode);

        // Kaynak depo sorumlusu teslim alamaz; hedef depo yetkisi gerekir.
        Assert.Equal(HttpStatusCode.Forbidden, (await burak.PostJson($"/api/transfers/{t.Header.Id}/receive", new TransferReceive(null, null))).StatusCode);

        var ist = await app.Login("santiye.ist");
        var waiting = await ist.GetJson<PagedResult<TransferListRow>>("/api/transfers?waitingForMe=true");
        Assert.Contains(waiting.Items, x => x.Id == t.Header.Id && x.CanReceive);
        var loc = app.Db(db => db.Locations.First(l => l.Name == "Konteyner 1").Id);
        var received = await (await ist.PostJson($"/api/transfers/{t.Header.Id}/receive", new TransferReceive(loc, "Eksiksiz geldi"))).Read<TransferDetail>();
        Assert.Equal(TransferStatus.Received, received.Header.Status);

        var d = await ist.GetJson<ItemDetail>($"/api/items/{item}");
        Assert.Equal(ItemStatus.InStock, d.Status);
        Assert.Equal("İstanbul Şantiye Deposu", d.WarehouseName);
        Assert.Equal("Konteyner 1", d.LocationName);
        Assert.Equal(6, Balance(sid, "Ankara Merkez Depo"));
        Assert.Equal(4, Balance(sid, "İstanbul Şantiye Deposu"));
        Assert.Equal(2, app.Db(db => db.StockMovements.Count(m => m.StockItemId == sid && (m.Type == StockMovementType.TransferOut || m.Type == StockMovementType.TransferIn))));
        // Cihaz artık Burak'ın kapsamında değil.
        Assert.Equal(HttpStatusCode.NotFound, (await burak.GetAsync($"/api/items/{item}")).StatusCode);
    }

    [Fact]
    public async Task Send_fails_atomically_when_stock_is_insufficient()
    {
        var admin = await app.Login("yonetici");
        var item = await NewItem(admin, "Ankara Merkez Depo");
        var sid = await NewStock(admin, "Ankara Merkez Depo", 3);
        var t = await (await admin.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("Gölbaşı Fabrika Deposu"), [item], [new(sid, 2)], null))).Read<TransferDetail>();
        // Taslaktan sonra stok azalır.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostJson("/api/stock/movements", new StockMovementRequest(sid, Wh("Ankara Merkez Depo"), StockMovementType.Out, 2, null, "Proje", null))).StatusCode);

        var res = await admin.PostAsync($"/api/transfers/{t.Header.Id}/send", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("kaynak depoda 1 adet", await res.Content.ReadAsStringAsync());
        // Hiçbir şey değişmemiş olmalı.
        Assert.Equal(ItemStatus.InStock, (await admin.GetJson<ItemDetail>($"/api/items/{item}")).Status);
        Assert.Equal(1, Balance(sid, "Ankara Merkez Depo"));
        Assert.Equal(TransferStatus.Draft, (await admin.GetJson<TransferDetail>($"/api/transfers/{t.Header.Id}")).Header.Status);
    }

    [Fact]
    public async Task Concurrent_send_requests_move_stock_once()
    {
        var admin = await app.Login("yonetici");
        var sid = await NewStock(admin, "Ankara Merkez Depo", 10);
        var t = await (await admin.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("Gölbaşı Fabrika Deposu"), [], [new(sid, 5)], null))).Read<TransferDetail>();
        var r = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => admin.PostAsync($"/api/transfers/{t.Header.Id}/send", null)));
        Assert.All(r, x => Assert.Equal(HttpStatusCode.OK, x.StatusCode));
        Assert.Equal(5, Balance(sid, "Ankara Merkez Depo"));
        Assert.Equal(1, app.Db(db => db.StockMovements.Count(m => m.StockItemId == sid && m.Type == StockMovementType.TransferOut)));
    }

    [Fact]
    public async Task Draft_can_be_cancelled_but_sent_cannot_and_rules_are_enforced()
    {
        var admin = await app.Login("yonetici");
        var item = await NewItem(admin, "Ankara Merkez Depo");

        // Şirketler arası transfer yok.
        var cross = await admin.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("Makina Merkez Depo"), [item], [], null));
        Assert.Equal(HttpStatusCode.Conflict, cross.StatusCode);

        var t = await (await admin.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("Gölbaşı Fabrika Deposu"), [item], [], null))).Read<TransferDetail>();
        // Aynı cihaz ikinci bir taslağa eklenemez.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("İstanbul Şantiye Deposu"), [item], [], null))).StatusCode);
        Assert.Equal(TransferStatus.Cancelled, (await (await admin.PostAsync($"/api/transfers/{t.Header.Id}/cancel", null)).Read<TransferDetail>()).Header.Status);

        var t2 = await (await admin.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("Gölbaşı Fabrika Deposu"), [item], [], null))).Read<TransferDetail>();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/transfers/{t2.Header.Id}/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/transfers/{t2.Header.Id}/cancel", null)).StatusCode);

        // Zimmetli cihaz önce iade alınmadan transfere eklenemez.
        var assigned = app.Db(db => db.Items.First(i => i.Status == ItemStatus.Assigned && i.Warehouse!.Name == "Ankara Merkez Depo").Id);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("Gölbaşı Fabrika Deposu"), [assigned], [], null))).StatusCode);
    }

    [Fact]
    public async Task Auditor_and_unrelated_manager_cannot_operate_transfers()
    {
        var admin = await app.Login("yonetici");
        var item = await NewItem(admin, "Ankara Merkez Depo");
        var t = await (await admin.PostJson("/api/transfers", new TransferCreate(Wh("Ankara Merkez Depo"), Wh("Gölbaşı Fabrika Deposu"), [item], [], null))).Read<TransferDetail>();

        var auditor = await app.Login("zeynep.celik");
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsync($"/api/transfers/{t.Header.Id}/send", null)).StatusCode);
        var ist = await app.Login("santiye.ist");
        Assert.Equal(HttpStatusCode.NotFound, (await ist.GetAsync($"/api/transfers/{t.Header.Id}")).StatusCode);
    }
}

[Collection("app")]
public class SavedViewTests(TestApp app)
{
    [Fact]
    public async Task Views_are_private_to_their_owner()
    {
        var selin = await app.Login("selin.aydin");
        var name = "Bakımdakiler " + RandomNumberGenerator.GetHexString(4);
        var created = await selin.PostJson("/api/views", new SavedViewSave("envanter", name, "{\"status\":\"InMaintenance\"}"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var v = await created.Read<SavedViewDto>();
        Assert.Equal(HttpStatusCode.Conflict, (await selin.PostJson("/api/views", new SavedViewSave("envanter", name, "{}"))).StatusCode);

        var burak = await app.Login("burak.kaya");
        Assert.DoesNotContain(await burak.GetJson<List<SavedViewDto>>("/api/views?page=envanter"), x => x.Id == v.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await burak.DeleteAsync($"/api/views/{v.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await burak.PutJson($"/api/views/{v.Id}", new SavedViewSave("envanter", "x", "{}"))).StatusCode);

        // Varsayılan düzen (boş ad) tekrar kaydedilince güncellenir.
        var d1 = await (await selin.PostJson("/api/views", new SavedViewSave("envanter", "", "{\"cols\":[]}"))).Read<SavedViewDto>();
        var d2 = await (await selin.PostJson("/api/views", new SavedViewSave("envanter", "", "{\"cols\":[[\"tag\",\"1\"]]}"))).Read<SavedViewDto>();
        Assert.Equal(d1.Id, d2.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await selin.PostJson("/api/views", new SavedViewSave("envanter", "bozuk", "{bozuk"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await selin.DeleteAsync($"/api/views/{v.Id}")).StatusCode);
    }
}
