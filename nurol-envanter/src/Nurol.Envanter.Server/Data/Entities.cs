using Microsoft.AspNetCore.Identity;
using Nurol.Envanter.Shared;

namespace Nurol.Envanter.Server.Data;

public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    /// <summary>"Local" (yerel parola). "Directory" Aşama 2'de AD ile gelecek.</summary>
    public string AccountType { get; set; } = "Local";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<UserScope> Scopes { get; set; } = [];
}

public class UserScope
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    /// <summary>Boşsa şirketin tüm depoları.</summary>
    public Guid? WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
}

public class ServerSession
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public byte[] Ticket { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public class Company
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Archived { get; set; }
}

public class Warehouse
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public bool Archived { get; set; }
    public List<Location> Locations { get; set; } = [];
}

public class Location
{
    public Guid Id { get; set; }
    public Guid WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public string Name { get; set; } = "";
    public bool Archived { get; set; }
}

public class Category
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool RequiresSerial { get; set; }
    public bool Archived { get; set; }
}

public class Manufacturer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class AssetModel
{
    public Guid Id { get; set; }
    public Guid CategoryId { get; set; }
    public Category? Category { get; set; }
    public Guid ManufacturerId { get; set; }
    public Manufacturer? Manufacturer { get; set; }
    public string Name { get; set; } = "";
    public bool Archived { get; set; }
}

/// <summary>Özel alan tanımı. CategoryId doluysa kategorinin varsayılan alanı, AssetModelId doluysa modele ek alandır.</summary>
public class FieldDefinition
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? HelpText { get; set; }
    public FieldType Type { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? AssetModelId { get; set; }
    public int SortOrder { get; set; }
    public bool Required { get; set; }
    /// <summary>Grup genelinde benzersiz (şirketten bağımsız).</summary>
    public bool Unique { get; set; }
    public int? MaxLength { get; set; }
    public bool DigitsOnly { get; set; }
    public int? ExactLength { get; set; }
    public List<string> Options { get; set; } = [];
    public bool Archived { get; set; }
}

public class InventoryItem
{
    public Guid Id { get; set; }
    public string Tag { get; set; } = "";
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public Guid CategoryId { get; set; }
    public Category? Category { get; set; }
    public Guid ManufacturerId { get; set; }
    public Guid AssetModelId { get; set; }
    public AssetModel? AssetModel { get; set; }
    public Guid WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public Guid? LocationId { get; set; }
    public Location? Location { get; set; }
    public string? Serial { get; set; }
    public ItemStatus Status { get; set; }
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public Guid? OpenAssignmentId { get; set; }
    public Assignment? OpenAssignment { get; set; }
    public decimal? Cost { get; set; }
    public string? Notes { get; set; }
    public string? MaintenanceNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>PostgreSQL xmin; iyimser eşzamanlılık.</summary>
    public uint Version { get; set; }
    public List<FieldValue> FieldValues { get; set; } = [];
}

public class FieldValue
{
    public Guid ItemId { get; set; }
    public Guid FieldId { get; set; }
    public FieldDefinition? Field { get; set; }
    /// <summary>Tüm tipler için normalize metin; arama ve benzersizlik bunun üzerinden yapılır.</summary>
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public DateOnly? Date { get; set; }
    public bool? Bool { get; set; }
}

/// <summary>Benzersiz alan değerlerinin veritabanı düzeyindeki koruması: (alan, değer) birincil anahtar.</summary>
public class UniqueFieldValue
{
    public Guid FieldId { get; set; }
    public string Value { get; set; } = "";
    public Guid ItemId { get; set; }
}

public class ItemEvent
{
    public Guid Id { get; set; }
    public Guid ItemId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public string UserName { get; set; } = "";
    public string? Reference { get; set; }
}

public class StockItem
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "adet";
    public decimal MinLevel { get; set; }
    public bool Archived { get; set; }
    public List<StockBalance> Balances { get; set; } = [];
}

public class StockBalance
{
    public Guid StockItemId { get; set; }
    public Guid WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public decimal Quantity { get; set; }
}

public class StockMovement
{
    public Guid Id { get; set; }
    public string No { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public StockMovementType Type { get; set; }
    public Guid StockItemId { get; set; }
    public StockItem? StockItem { get; set; }
    public Guid WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    /// <summary>İşaretli miktar: giriş +, çıkış −.</summary>
    public decimal Quantity { get; set; }
    public decimal BalanceAfter { get; set; }
    public Guid? EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public string? Project { get; set; }
    public string? Note { get; set; }
    public string UserName { get; set; } = "";
    public string? IdempotencyKey { get; set; }
}

public class Employee
{
    public Guid Id { get; set; }
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    public string? RegistryNo { get; set; }
    public string FullName { get; set; } = "";
    public string? Title { get; set; }
    public string? Department { get; set; }
    public string? Email { get; set; }
    public EmployeeSource Source { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Assignment
{
    public Guid Id { get; set; }
    public string No { get; set; } = "";
    public AssignmentKind Kind { get; set; }
    public Guid CompanyId { get; set; }
    public Guid WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public Guid EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateTimeOffset At { get; set; }
    public string UserName { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>Belgenin üretildiği andaki verilerin dondurulmuş JSON kopyası.</summary>
    public string Snapshot { get; set; } = "{}";
    public string? IdempotencyKey { get; set; }
    public List<AssignmentLine> Lines { get; set; } = [];
    public List<Document> Documents { get; set; } = [];
}

public class AssignmentLine
{
    public Guid Id { get; set; }
    public Guid AssignmentId { get; set; }
    public Guid ItemId { get; set; }
    public InventoryItem? Item { get; set; }
    public string? Condition { get; set; }
    /// <summary>Teslim satırı hâlâ açıksa true. Kısmi benzersiz indeks bir cihazın tek açık zimmetini garanti eder.</summary>
    public bool IsOpen { get; set; }
    public Guid? ClosedByAssignmentId { get; set; }
}

public class Document
{
    public Guid Id { get; set; }
    public Guid AssignmentId { get; set; }
    public Assignment? Assignment { get; set; }
    public DocumentKind Kind { get; set; }
    public int Version { get; set; }
    public string StorageKey { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public string UserName { get; set; } = "";
}

public class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Entity { get; set; } = "";
    public string? EntityId { get; set; }
    public string? Details { get; set; }
}

public class Counter
{
    public string Name { get; set; } = "";
    public long Value { get; set; }
}
