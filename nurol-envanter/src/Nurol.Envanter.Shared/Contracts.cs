namespace Nurol.Envanter.Shared;

public enum ItemStatus { InStock = 0, Assigned = 1, InTransit = 2, InMaintenance = 3, Retired = 4 }
public enum FieldType { Text = 0, Number = 1, Date = 2, Select = 3, Boolean = 4 }
public enum StockMovementType { In = 0, Out = 1, Adjust = 2, TransferOut = 3, TransferIn = 4 }
public enum AssignmentKind { Issue = 0, Return = 1 }
public enum DocumentKind { Generated = 0, Signed = 1 }
public enum EmployeeSource { Manual = 0, Directory = 1 }

public static class Roles
{
    public const string SystemAdmin = "SystemAdmin";
    public const string CompanyAdmin = "CompanyAdmin";
    public const string WarehouseManager = "WarehouseManager";
    public const string Auditor = "Auditor";
    public static readonly string[] All = [SystemAdmin, CompanyAdmin, WarehouseManager, Auditor];

    public static string Label(string role) => role switch
    {
        SystemAdmin => "Sistem yöneticisi",
        CompanyAdmin => "Şirket yöneticisi",
        WarehouseManager => "Depo sorumlusu",
        Auditor => "Denetçi",
        _ => role
    };
}

public static class Labels
{
    public static string Status(ItemStatus s) => s switch
    {
        ItemStatus.InStock => "Depoda",
        ItemStatus.Assigned => "Zimmetli",
        ItemStatus.InTransit => "Transferde",
        ItemStatus.InMaintenance => "Bakımda",
        ItemStatus.Retired => "Kullanım dışı",
        _ => s.ToString()
    };

    public static string FieldType(FieldType t) => t switch
    {
        Shared.FieldType.Text => "Metin",
        Shared.FieldType.Number => "Sayı",
        Shared.FieldType.Date => "Tarih",
        Shared.FieldType.Select => "Seçim listesi",
        Shared.FieldType.Boolean => "Evet/Hayır",
        _ => t.ToString()
    };

    public static string Movement(StockMovementType t) => t switch
    {
        StockMovementType.In => "Giriş",
        StockMovementType.Out => "Sarf çıkışı",
        StockMovementType.Adjust => "Düzeltme",
        StockMovementType.TransferOut => "Transfer çıkışı",
        StockMovementType.TransferIn => "Transfer girişi",
        _ => t.ToString()
    };
}

/// <summary>API hata gövdesi. Message kullanıcıya gösterilir; Errors alan bazında hatalardır.</summary>
public record ApiError(string Message, Dictionary<string, string>? Errors = null);

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

// ---- Kimlik ----
public record LoginRequest(string UserName, string Password, string? AccountType = null);
public record ScopeDto(Guid CompanyId, string CompanyName, Guid? WarehouseId, string? WarehouseName);
public record MeDto(string UserName, string DisplayName, string AccountType, IReadOnlyList<string> Roles, IReadOnlyList<ScopeDto> Scopes, bool CanWrite, bool IsSystemAdmin);

// ---- Organizasyon ----
public record CompanyDto(Guid Id, string Code, string Name, bool Archived);
public record CompanySave(string Code, string Name);
public record WarehouseDto(Guid Id, Guid CompanyId, string CompanyName, string Name, string? Address, bool Archived, IReadOnlyList<LocationDto> Locations, int ItemCount);
public record WarehouseSave(Guid CompanyId, string Name, string? Address);
public record LocationDto(Guid Id, Guid WarehouseId, string Name, bool Archived);
public record LocationSave(Guid WarehouseId, string Name);

// ---- Kategori, model, alan ----
public record CategoryDto(Guid Id, string Code, string Name, bool RequiresSerial, bool Archived, int FieldCount);
public record CategorySave(string Code, string Name, bool RequiresSerial);
public record ManufacturerDto(Guid Id, string Name);
public record ManufacturerSave(string Name);
public record AssetModelDto(Guid Id, Guid CategoryId, string CategoryName, Guid ManufacturerId, string ManufacturerName, string Name, bool Archived);
public record AssetModelSave(Guid CategoryId, Guid ManufacturerId, string Name);

public record FieldDefinitionDto(
    Guid Id, string Key, string Label, string? HelpText, FieldType Type,
    Guid? CategoryId, Guid? AssetModelId, int SortOrder, bool Required, bool Unique,
    int? MaxLength, bool DigitsOnly, int? ExactLength, IReadOnlyList<string> Options,
    bool Archived, int ValueCount);

public record FieldDefinitionSave(
    string Label, string? HelpText, FieldType Type, Guid? CategoryId, Guid? AssetModelId,
    int SortOrder, bool Required, bool Unique, int? MaxLength, bool DigitsOnly, int? ExactLength,
    IReadOnlyList<string>? Options);

/// <summary>Yeni kural mevcut verilerle çelişiyorsa çelişen kayıtlar.</summary>
public record FieldConflictDto(string Rule, IReadOnlyList<string> ItemTags);

// ---- Envanter ----
public record FieldValueDto(Guid FieldId, string Key, string Label, FieldType Type, string? Value, bool Archived);

public record ItemListRow(
    Guid Id, string Tag, string CategoryName, string ManufacturerName, string ModelName, string? Serial,
    ItemStatus Status, Guid WarehouseId, string WarehouseName, string? LocationName,
    string? EmployeeName, bool? SignedDocumentMissing, decimal? Cost, Dictionary<string, string?> Fields);

public record ItemDetail(
    Guid Id, string Tag, Guid CompanyId, string CompanyName, Guid CategoryId, string CategoryName, bool RequiresSerial,
    Guid AssetModelId, string ManufacturerName, string ModelName, string? Serial, ItemStatus Status,
    Guid WarehouseId, string WarehouseName, Guid? LocationId, string? LocationName,
    Guid? EmployeeId, string? EmployeeName, Guid? OpenAssignmentId, string? OpenAssignmentNo,
    decimal? Cost, string? Notes, string? MaintenanceNote, uint Version,
    IReadOnlyList<FieldValueDto> Fields, IReadOnlyList<HistoryDto> History, IReadOnlyList<DocumentDto> Documents);

public record ItemSave(
    Guid AssetModelId, Guid WarehouseId, Guid? LocationId, string? Serial, decimal? Cost, string? Notes,
    Dictionary<Guid, string?> Fields, uint? Version);

public record MaintenanceRequest(string Note, uint Version);
public record HistoryDto(DateTimeOffset At, string Type, string Text, string UserName, string? Reference);

// ---- Stok ----
public record StockItemDto(Guid Id, Guid CompanyId, string Code, string Name, string Unit, decimal MinLevel, bool Archived, IReadOnlyList<StockBalanceDto> Balances, decimal Total);
public record StockItemSave(Guid CompanyId, string Code, string Name, string Unit, decimal MinLevel);
public record StockBalanceDto(Guid WarehouseId, string WarehouseName, decimal Quantity);
public record StockMovementRequest(Guid StockItemId, Guid WarehouseId, StockMovementType Type, decimal Quantity, Guid? EmployeeId, string? Project, string? Note);
public record StockMovementDto(Guid Id, string No, DateTimeOffset At, StockMovementType Type, string StockItemName, string Unit, string WarehouseName, decimal Quantity, decimal BalanceAfter, string? EmployeeName, string? Project, string? Note, string UserName);

// ---- Personel ----
public record EmployeeDto(Guid Id, Guid CompanyId, string CompanyName, string? RegistryNo, string FullName, string? Title, string? Department, string? Email, EmployeeSource Source, bool IsActive, int OpenItemCount, string? DirectoryUserName = null, bool HasAccount = false);
public record EmployeeSave(Guid CompanyId, string? RegistryNo, string FullName, string? Title, string? Department, string? Email, bool IsActive);
public record EmployeeDetail(EmployeeDto Employee, IReadOnlyList<ItemListRow> OpenItems, IReadOnlyList<AssignmentListRow> Assignments, IReadOnlyList<StockMovementDto> Consumption);

// ---- Zimmet ----
public record AssignmentCreate(Guid EmployeeId, IReadOnlyList<Guid> ItemIds, IReadOnlyDictionary<Guid, string>? ConditionNotes, string? Note);
public record AssignmentReturn(Guid EmployeeId, IReadOnlyList<Guid> ItemIds, Guid? WarehouseId, Guid? LocationId, IReadOnlyDictionary<Guid, string>? ConditionNotes, string? Note);
public record AssignmentListRow(Guid Id, string No, AssignmentKind Kind, DateTimeOffset At, Guid EmployeeId, string EmployeeName, string? Department, string WarehouseName, int ItemCount, bool SignedUploaded, string UserName);
public record AssignmentLineDto(Guid ItemId, string Tag, string Description, string? Serial, string? Extra, string? Condition, bool Open);
public record AssignmentDetail(AssignmentListRow Header, string? Note, IReadOnlyList<AssignmentLineDto> Lines, IReadOnlyList<DocumentDto> Documents);
public record DocumentDto(Guid Id, Guid AssignmentId, string AssignmentNo, DocumentKind Kind, int Version, string FileName, string ContentType, long Size, string Sha256, DateTimeOffset At, string UserName);

// ---- Kullanıcılar ----
public record UserDto(string Id, string UserName, string DisplayName, string AccountType, bool IsActive, IReadOnlyList<string> Roles, IReadOnlyList<ScopeDto> Scopes, DateTimeOffset? LastLoginAt, Guid? EmployeeId = null);
/// <summary>AccountType "Local" için parola gerekir; "Directory" için AD'den gelmiş personel (EmployeeId) seçilir, parola AD'dedir.</summary>
public record UserCreate(string UserName, string DisplayName, string? Password, string Role, IReadOnlyList<ScopeSave> Scopes, string AccountType = "Local", Guid? EmployeeId = null);
public record UserUpdate(string DisplayName, bool IsActive, string Role, IReadOnlyList<ScopeSave> Scopes, string? NewPassword);
public record ScopeSave(Guid CompanyId, Guid? WarehouseId);

// ---- Genel bakış ----
public record DashboardDto(
    int TotalItems, int InStock, int Assigned, int InMaintenance, int InTransit, int Retired,
    int MissingSignedDocuments, DateTimeOffset? OldestMissingSince, int PendingTransfers,
    IReadOnlyList<WarehouseBreakdown> Warehouses, IReadOnlyList<LowStockRow> LowStock, IReadOnlyList<ActivityRow> Activity,
    IReadOnlyList<PrinterWarning>? PrinterWarnings = null, int OpenOffboarding = 0, int ReadyOffboarding = 0);
public record WarehouseBreakdown(Guid WarehouseId, string Name, int InStock, int Assigned, int InMaintenance, int InTransit);
public record LowStockRow(Guid StockItemId, string Name, string Unit, string WarehouseName, decimal Quantity, decimal MinLevel);
public record ActivityRow(DateTimeOffset At, string Type, string Reference, string Text, string UserName);
