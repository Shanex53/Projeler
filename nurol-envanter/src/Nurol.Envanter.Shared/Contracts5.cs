namespace Nurol.Envanter.Shared;

// ---- Telefon hattı ve SIM ----
public enum LineStatus { Active = 0, Suspended = 1, Cancelled = 2 }

public static class LineLabels
{
    public static string Status(LineStatus s) => s switch { LineStatus.Active => "Aktif", LineStatus.Suspended => "Askıda", LineStatus.Cancelled => "İptal", _ => s.ToString() };
    public static readonly string[] Operators = ["Turkcell", "Vodafone", "Türk Telekom", "Diğer"];
    public static readonly string[] Types = ["Ses + Data", "Yalnız ses", "Yalnız data", "M2M / IoT"];
    /// <summary>"05324182760" → "0532 418 27 60"</summary>
    public static string Number(string n) => n.Length == 11 && n[0] == '0' ? $"{n[..4]} {n[4..7]} {n[7..9]} {n[9..]}" : n;
}

/// <summary>Hattın kimde olduğu: doğrudan personele verilmişse EmployeeName, cihaz içindeyse cihazın zimmetlisi HolderName olur.</summary>
public record PhoneLineRow(Guid Id, Guid CompanyId, string CompanyName, string Number, string Operator, string LineType, string? Tariff, decimal? MonthlyFee,
    string? Iccid, LineStatus Status, Guid? ItemId, string? ItemTag, string? ItemModel, Guid? EmployeeId, string? EmployeeName,
    Guid? HolderId, string? HolderName, string? HolderDepartment, string? CostCenter, DateOnly? ContractEnd, bool ContractEndingSoon, bool CanManage);
public record PhoneLineEventDto(DateTimeOffset At, string Type, string Text, string UserName);
public record PhoneLineDetail(PhoneLineRow Line, string? Notes, uint Version, IReadOnlyList<PhoneLineEventDto> Events);
public record PhoneLineSave(Guid CompanyId, string Number, string Operator, string LineType, string? Tariff, decimal? MonthlyFee, string? Iccid,
    LineStatus Status, string? CostCenter, DateOnly? ContractEnd, string? Notes, uint? Version);
public record LineAssign(Guid? EmployeeId, string? Note);
public record LineInstall(Guid? ItemId, string? Note);

// ---- İlişik kesme (işten ayrılan personel) ----
public enum OffboardingStatus { Open = 0, Completed = 1, Cancelled = 2 }

public static class OffboardingLabels
{
    public static string Status(OffboardingStatus s) => s switch { OffboardingStatus.Open => "Açık", OffboardingStatus.Completed => "Tamamlandı", OffboardingStatus.Cancelled => "İptal edildi", _ => s.ToString() };
}

public record OffboardingRow(Guid Id, string No, OffboardingStatus Status, Guid EmployeeId, string EmployeeName, string? Department, string CompanyName,
    string Source, DateTimeOffset OpenedAt, DateOnly? LastWorkingDay, int OpenItems, int OpenLines, int PendingTasks, DateTimeOffset? CompletedAt);
public record OffboardingTaskDto(Guid Id, string Title, bool Done, DateTimeOffset? DoneAt, string? DoneBy);
public record OffboardingReturned(string Kind, string Reference, string Text, DateTimeOffset At);
public record OffboardingDetail(OffboardingRow Case, string? Note, string OpenedBy, string? CompletedBy, string? CancelReason,
    IReadOnlyList<ItemListRow> Items, IReadOnlyList<PhoneLineRow> Lines, IReadOnlyList<OffboardingTaskDto> Tasks, IReadOnlyList<OffboardingReturned> Returned,
    bool CanManage, bool CanComplete, IReadOnlyList<string> Blockers, bool HasCertificate);
public record OffboardingStart(Guid EmployeeId, DateOnly? LastWorkingDay, string? Note);
public record OffboardingTaskSave(string Title);
public record OffboardingTaskToggle(bool Done);
public record OffboardingCancel(string Reason);
