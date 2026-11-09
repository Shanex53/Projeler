using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nurol.Envanter.Server.Data;
using Nurol.Envanter.Server.Infrastructure;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Endpoints;

public static class PeopleEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        // ---- Personel ----
        api.MapGet("/employees", async (HttpContext ctx, AppDbContext db, string? q, Guid? companyId, bool? includeInactive, bool? withOpenItems, int? page, int? pageSize) =>
        {
            var a = await ctx.Access();
            var query = a.Employees(db).AsNoTracking();
            if (companyId is Guid c) query = query.Where(e => e.CompanyId == c);
            if (includeInactive != true) query = query.Where(e => e.IsActive || db.Items.Any(i => i.EmployeeId == e.Id));
            if (withOpenItems == true) query = query.Where(e => db.Items.Any(i => i.EmployeeId == e.Id));
            if (!string.IsNullOrWhiteSpace(q))
            {
                var t = $"%{q.Trim()}%";
                query = query.Where(e => EF.Functions.ILike(e.FullName, t) || EF.Functions.ILike(e.RegistryNo ?? "", t) ||
                    EF.Functions.ILike(e.Department ?? "", t) || EF.Functions.ILike(e.Email ?? "", t));
            }
            var p = Math.Max(1, page ?? 1); var size = Math.Clamp(pageSize ?? 100, 1, 500);
            var total = await query.CountAsync();
            var rows = await Project(db, query.OrderBy(e => e.FullName).Skip((p - 1) * size).Take(size)).ToListAsync();
            return new PagedResult<EmployeeDto>(rows, total, p, size);
        });

        api.MapGet("/employees/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var emp = await Project(db, a.Employees(db).Where(e => e.Id == id)).FirstOrDefaultAsync() ?? throw AppException.NotFound("Personel");
            var (openRows, _) = await ItemEndpoints.ListAsync(db, a, new ItemQuery(null, null, null, null, null, null, null, "tag", false, 1, 500, id));
            var assignments = await a.Assignments(db).Where(x => x.EmployeeId == id).OrderByDescending(x => x.At)
                .Select(x => new AssignmentListRow(x.Id, x.No, x.Kind, x.At, x.EmployeeId, x.Employee!.FullName, x.Employee.Department, x.Warehouse!.Name, x.Lines.Count,
                    x.Documents.Any(d => d.Kind == DocumentKind.Signed), x.UserName)).Take(200).ToListAsync();
            var consumption = await StockEndpoints.Project(a.StockMovements(db).Where(m => m.EmployeeId == id).OrderByDescending(m => m.At).Take(200)).ToListAsync();
            return new EmployeeDetail(emp, openRows, assignments, consumption);
        });

        api.MapPost("/employees", async (EmployeeSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            if (!a.CanWriteCompany(req.CompanyId)) throw AppException.Forbidden("Bu şirkete personel ekleme yetkiniz yok.");
            var e = new Employee { Id = Guid.NewGuid(), CompanyId = req.CompanyId, Source = EmployeeSource.Manual };
            Apply(e, req);
            db.Employees.Add(e);
            Audit.Add(db, a, "Oluşturma", "Personel", e.Id, e.FullName);
            await db.SaveChangesAsync();
            return Results.Ok(new { e.Id });
        });

        api.MapPut("/employees/{id:guid}", async (Guid id, EmployeeSave req, HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access();
            var e = await a.Employees(db).FirstOrDefaultAsync(x => x.Id == id) ?? throw AppException.NotFound("Personel");
            if (!a.CanWriteCompany(e.CompanyId)) throw AppException.Forbidden();
            var openCount = await db.Items.CountAsync(i => i.EmployeeId == id);
            if (req.CompanyId != e.CompanyId)
            {
                if (!a.CanWriteCompany(req.CompanyId)) throw AppException.Forbidden("Hedef şirkette yetkiniz yok.");
                if (openCount > 0) throw AppException.Conflict($"Personelin {openCount} açık zimmeti var. Şirket değişikliğinden önce cihazları iade alın.");
                e.CompanyId = req.CompanyId;
            }
            if (!req.IsActive && e.IsActive && openCount > 0)
            {
                // Pasife alınabilir ama açık zimmetler listede işaretli kalır.
                Audit.Add(db, a, "Pasife alma (açık zimmetli)", "Personel", e.Id, $"{e.FullName} · {openCount} açık zimmet");
            }
            Apply(e, req);
            Audit.Add(db, a, "Güncelleme", "Personel", e.Id, e.FullName);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- Uygulama kullanıcıları (yalnızca sistem yöneticisi) ----
        api.MapGet("/users", async (HttpContext ctx, AppDbContext db) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            var users = await db.Users.AsNoTracking().Include(u => u.Scopes).ThenInclude(s => s.Company).Include(u => u.Scopes).ThenInclude(s => s.Warehouse)
                .OrderBy(u => u.UserName).ToListAsync();
            var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id select new { ur.UserId, r.Name }).ToListAsync();
            return users.Select(u => new UserDto(u.Id, u.UserName!, u.DisplayName, u.AccountType, u.IsActive,
                roles.Where(r => r.UserId == u.Id).Select(r => r.Name!).ToList(),
                u.Scopes.Select(s => new ScopeDto(s.CompanyId, s.Company!.Name, s.WarehouseId, s.Warehouse?.Name)).ToList(), u.LastLoginAt, u.EmployeeId)).ToList();
        });

        api.MapPost("/users", async (UserCreate req, HttpContext ctx, AppDbContext db, UserManager<AppUser> um) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            ValidateRole(req.Role, req.Scopes);
            AppUser user;
            IdentityResult res;
            if (req.AccountType == "Directory")
            {
                // AD'den aktarılan personel otomatik giriş hakkı kazanmaz; yönetici burada yetki verir. Parola AD'dedir.
                var emp = await db.Employees.FirstOrDefaultAsync(e => e.Id == req.EmployeeId) ?? throw AppException.Invalid("AD'den gelen bir personel seçin.");
                if (emp.DirectoryId is null || emp.DirectoryUserName is null) throw AppException.Invalid("Seçilen personel AD'den eşitlenmemiş.");
                if (!emp.IsActive) throw AppException.Conflict("Pasif personele hesap açılamaz.");
                if (await db.Users.AnyAsync(u => u.DirectoryId == emp.DirectoryId)) throw AppException.Conflict("Bu personelin zaten bir uygulama hesabı var.");
                user = new AppUser { UserName = emp.DirectoryUserName, DisplayName = emp.FullName, AccountType = "Directory", DirectoryId = emp.DirectoryId, Email = emp.Email,
                    EmployeeId = emp.Id, IsActive = true, LockoutEnabled = true };
                res = await um.CreateAsync(user);
            }
            else
            {
                DefinitionEndpoints.Require(req.UserName, "Kullanıcı adı");
                DefinitionEndpoints.Require(req.DisplayName, "Ad soyad");
                user = new AppUser { UserName = req.UserName.Trim().ToLowerInvariant(), DisplayName = req.DisplayName.Trim(), AccountType = "Local", IsActive = true, LockoutEnabled = true };
                res = await um.CreateAsync(user, req.Password ?? "");
            }
            Check(res);
            await um.AddToRoleAsync(user, req.Role);
            await SetScopes(db, user.Id, req.Scopes);
            Audit.Add(db, a, "Oluşturma", "Kullanıcı", user.Id, $"{user.UserName} · {(user.AccountType == "Directory" ? "AD" : "yerel")} · {Roles.Label(req.Role)}");
            await db.SaveChangesAsync();
            return Results.Ok(new { user.Id });
        });

        api.MapPut("/users/{id}", async (string id, UserUpdate req, HttpContext ctx, AppDbContext db, UserManager<AppUser> um) =>
        {
            var a = await ctx.Access(); a.RequireSystemAdmin();
            var user = await um.FindByIdAsync(id) ?? throw AppException.NotFound("Kullanıcı");
            ValidateRole(req.Role, req.Scopes);
            if (id == a.UserId && (!req.IsActive || req.Role != Roles.SystemAdmin))
                throw AppException.Conflict("Kendi yönetici yetkinizi kaldıramaz veya hesabınızı kapatamazsınız.");
            var revoke = false;
            user.DisplayName = req.DisplayName.Trim();
            if (user.IsActive && !req.IsActive) revoke = true;
            user.IsActive = req.IsActive;
            Check(await um.UpdateAsync(user));
            var current = await um.GetRolesAsync(user);
            if (!current.SequenceEqual([req.Role]))
            {
                await um.RemoveFromRolesAsync(user, current);
                await um.AddToRoleAsync(user, req.Role);
                revoke = true;
            }
            if (!string.IsNullOrEmpty(req.NewPassword))
            {
                if (user.AccountType == "Directory") throw AppException.Invalid("AD hesabının parolası AD'de yönetilir; buradan değiştirilemez.");
                var token = await um.GeneratePasswordResetTokenAsync(user);
                Check(await um.ResetPasswordAsync(user, token, req.NewPassword));
                revoke = true;
            }
            await SetScopes(db, user.Id, req.Scopes);
            Audit.Add(db, a, "Güncelleme", "Kullanıcı", user.Id, user.UserName);
            await db.SaveChangesAsync();
            // Yetki veya parola değişince açık oturumlar sunucudan iptal edilir.
            if (revoke) await DbTicketStore.RevokeUser(db, user.Id);
            return Results.NoContent();
        });
    }

    static IQueryable<EmployeeDto> Project(AppDbContext db, IQueryable<Employee> q) => q.Select(e => new EmployeeDto(
        e.Id, e.CompanyId, e.Company!.Name, e.RegistryNo, e.FullName, e.Title, e.Department, e.Email, e.Source, e.IsActive,
        db.Items.Count(i => i.EmployeeId == e.Id), e.DirectoryUserName, db.Users.Any(u => u.EmployeeId == e.Id)));

    static void Apply(Employee e, EmployeeSave r)
    {
        DefinitionEndpoints.Require(r.FullName, "Ad soyad");
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        e.FullName = r.FullName.Trim(); e.RegistryNo = T(r.RegistryNo); e.Title = T(r.Title);
        e.Department = T(r.Department); e.Email = T(r.Email)?.ToLowerInvariant(); e.IsActive = r.IsActive;
    }

    static void ValidateRole(string role, IReadOnlyList<ScopeSave> scopes)
    {
        if (!Roles.All.Contains(role)) throw AppException.Invalid("Geçerli bir rol seçin.");
        if (role != Roles.SystemAdmin && (scopes is null || scopes.Count == 0))
            throw AppException.Invalid("Sistem yöneticisi dışındaki kullanıcılar için en az bir şirket veya depo kapsamı seçin.");
        if (role == Roles.WarehouseManager && scopes.Any(s => s.WarehouseId is null))
            throw AppException.Invalid("Depo sorumlusu için şirketin tamamı değil, belirli depolar seçilmelidir.");
    }

    static async Task SetScopes(AppDbContext db, string userId, IReadOnlyList<ScopeSave> scopes)
    {
        await db.UserScopes.Where(s => s.UserId == userId).ExecuteDeleteAsync();
        foreach (var s in (scopes ?? []).Distinct())
        {
            if (s.WarehouseId is Guid w && !await db.Warehouses.AnyAsync(x => x.Id == w && x.CompanyId == s.CompanyId))
                throw AppException.Invalid("Seçilen depo şirkete ait değil.");
            db.UserScopes.Add(new UserScope { Id = Guid.NewGuid(), UserId = userId, CompanyId = s.CompanyId, WarehouseId = s.WarehouseId });
        }
    }

    static void Check(IdentityResult r)
    {
        if (r.Succeeded) return;
        var msg = string.Join(" ", r.Errors.Select(e => e.Code switch
        {
            "DuplicateUserName" => "Bu kullanıcı adı kullanılıyor.",
            "PasswordTooShort" => "Parola en az 10 karakter olmalıdır.",
            "PasswordRequiresDigit" => "Parola en az bir rakam içermelidir.",
            "PasswordRequiresUpper" => "Parola en az bir büyük harf içermelidir.",
            "PasswordRequiresLower" => "Parola en az bir küçük harf içermelidir.",
            "InvalidUserName" => "Kullanıcı adı yalnızca harf, rakam ve . _ - içerebilir.",
            _ => e.Description
        }).Distinct());
        throw AppException.Invalid(msg);
    }
}
