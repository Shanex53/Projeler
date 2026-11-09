using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Infrastructure;

/// <summary>
/// İstek sahibinin rolü ve şirket/depo kapsamı. Tüm liste, detay, dışa aktarım ve dosya indirme
/// sorguları bu sınıfın filtrelerinden geçer.
/// </summary>
public class Access
{
    public string UserId { get; private init; } = "";
    public string UserName { get; private init; } = "";
    public string DisplayName { get; private init; } = "";
    public string Role { get; private init; } = "";
    public bool IsSystemAdmin => Role == Roles.SystemAdmin;
    public bool IsCompanyAdmin => Role == Roles.CompanyAdmin;
    public bool CanWrite => Role is Roles.SystemAdmin or Roles.CompanyAdmin or Roles.WarehouseManager;
    public bool CanManageDefinitions => Role is Roles.SystemAdmin or Roles.CompanyAdmin;

    /// <summary>Okuma kapsamındaki şirketler (sistem yöneticisi için boş = hepsi).</summary>
    public HashSet<Guid> Companies { get; private init; } = [];
    /// <summary>Okuma/yazma kapsamındaki depolar (sistem yöneticisi için boş = hepsi).</summary>
    public HashSet<Guid> Warehouses { get; private init; } = [];
    /// <summary>Şirket düzeyinde yetkili olunan şirketler (depo sınırı olmayan atamalar).</summary>
    public HashSet<Guid> CompanyWide { get; private init; } = [];

    public static Task<Access> Load(ClaimsPrincipal principal, AppDbContext db, CancellationToken ct = default) =>
        ForUser(db, principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new AppException(401, "Oturum bulunamadı."), ct);

    public static async Task<Access> ForUser(AppDbContext db, string userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Scopes).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive) throw new AppException(401, "Hesabınız devre dışı.");
        var role = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == userId select r.Name).FirstOrDefaultAsync(ct) ?? "";

        var companies = user.Scopes.Select(s => s.CompanyId).ToHashSet();
        var companyWide = user.Scopes.Where(s => s.WarehouseId == null).Select(s => s.CompanyId).ToHashSet();
        var explicitWh = user.Scopes.Where(s => s.WarehouseId != null).Select(s => s.WarehouseId!.Value);
        var whOfCompanies = companyWide.Count == 0 ? [] :
            await db.Warehouses.Where(w => companyWide.Contains(w.CompanyId)).Select(w => w.Id).ToListAsync(ct);

        return new Access
        {
            UserId = userId,
            UserName = user.UserName ?? "",
            DisplayName = user.DisplayName,
            Role = role,
            Companies = companies,
            CompanyWide = companyWide,
            Warehouses = explicitWh.Concat(whOfCompanies).ToHashSet()
        };
    }

    /// <summary>Örnek veri yükleme gibi sistem işlemleri için tam yetkili bağlam.</summary>
    internal static Access System(string userName) => new() { UserId = "system", UserName = userName, DisplayName = "Örnek Veri", Role = Roles.SystemAdmin };

    public bool CanReadCompany(Guid companyId) => IsSystemAdmin || Companies.Contains(companyId);
    public bool CanReadWarehouse(Guid warehouseId) => IsSystemAdmin || Warehouses.Contains(warehouseId);
    public bool CanWriteWarehouse(Guid warehouseId) => CanWrite && CanReadWarehouse(warehouseId);
    /// <summary>Personel kaydı yazma: şirkette en az bir depo veya şirket yetkisi olan yazma rolü.</summary>
    public bool CanWriteCompany(Guid companyId) => CanWrite && CanReadCompany(companyId);
    public bool CanAdministerCompany(Guid companyId) => IsSystemAdmin || (IsCompanyAdmin && CompanyWide.Contains(companyId));

    public void RequireWrite()
    {
        if (!CanWrite) throw AppException.Forbidden("Salt okunur hesapla değişiklik yapılamaz.");
    }
    public void RequireWriteWarehouse(Guid warehouseId)
    {
        RequireWrite();
        if (!CanReadWarehouse(warehouseId)) throw AppException.Forbidden("Bu depoda işlem yetkiniz yok.");
    }
    public void RequireDefinitions()
    {
        if (!CanManageDefinitions) throw AppException.Forbidden("Tanımları yalnızca sistem veya şirket yöneticisi değiştirebilir.");
    }
    public void RequireSystemAdmin()
    {
        if (!IsSystemAdmin) throw AppException.Forbidden("Bu işlem yalnızca sistem yöneticisine açıktır.");
    }

    public IQueryable<InventoryItem> Items(AppDbContext db) =>
        IsSystemAdmin ? db.Items : db.Items.Where(i => Warehouses.Contains(i.WarehouseId));
    public IQueryable<Warehouse> WarehousesQuery(AppDbContext db) =>
        IsSystemAdmin ? db.Warehouses : db.Warehouses.Where(w => Warehouses.Contains(w.Id));
    public IQueryable<Company> CompaniesQuery(AppDbContext db) =>
        IsSystemAdmin ? db.Companies : db.Companies.Where(c => Companies.Contains(c.Id));
    public IQueryable<Employee> Employees(AppDbContext db) =>
        IsSystemAdmin ? db.Employees : db.Employees.Where(e => Companies.Contains(e.CompanyId));
    public IQueryable<Assignment> Assignments(AppDbContext db) =>
        IsSystemAdmin ? db.Assignments : db.Assignments.Where(a => Warehouses.Contains(a.WarehouseId));
    public IQueryable<StockItem> StockItems(AppDbContext db) =>
        IsSystemAdmin ? db.StockItems : db.StockItems.Where(s => Companies.Contains(s.CompanyId));
    public IQueryable<StockMovement> StockMovements(AppDbContext db) =>
        IsSystemAdmin ? db.StockMovements : db.StockMovements.Where(m => Warehouses.Contains(m.WarehouseId));
}

public static class AccessExtensions
{
    public static async Task<Access> Access(this HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(typeof(Access), out var cached) && cached is Access a) return a;
        var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
        var access = await Infrastructure.Access.Load(ctx.User, db, ctx.RequestAborted);
        ctx.Items[typeof(Access)] = access;
        return access;
    }
}
