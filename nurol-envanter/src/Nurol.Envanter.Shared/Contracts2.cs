namespace Nurol.Envanter.Shared;

// ---- Transfer (Aşama 2) ----
public enum TransferStatus { Draft = 0, Sent = 1, Received = 2, Cancelled = 3 }

public record TransferStockLineSave(Guid StockItemId, decimal Quantity);
public record TransferCreate(Guid SourceWarehouseId, Guid TargetWarehouseId, IReadOnlyList<Guid> ItemIds, IReadOnlyList<TransferStockLineSave> StockLines, string? Note);
public record TransferReceive(Guid? LocationId, string? Note);
public record TransferListRow(Guid Id, string No, TransferStatus Status, string SourceWarehouseName, string TargetWarehouseName, int ItemCount, int StockLineCount,
    DateTimeOffset CreatedAt, DateTimeOffset? SentAt, DateTimeOffset? ReceivedAt, string CreatedBy, bool CanSend, bool CanReceive);
public record TransferItemLineDto(Guid ItemId, string Tag, string Description, string? Serial, ItemStatus Status);
public record TransferStockLineDto(Guid StockItemId, string Name, string Unit, decimal Quantity);
public record TransferDetail(TransferListRow Header, Guid SourceWarehouseId, Guid TargetWarehouseId, string? Note,
    string? SentBy, string? ReceivedBy, string? CancelledBy, IReadOnlyList<TransferItemLineDto> Items, IReadOnlyList<TransferStockLineDto> StockLines);

public static class TransferLabels
{
    public static string Status(TransferStatus s) => s switch
    {
        TransferStatus.Draft => "Taslak",
        TransferStatus.Sent => "Gönderildi",
        TransferStatus.Received => "Teslim alındı",
        TransferStatus.Cancelled => "İptal edildi",
        _ => s.ToString()
    };
}

// ---- Kayıtlı görünümler (Aşama 2) ----
/// <summary>Name boşsa kullanıcının o sayfadaki varsayılan görünümüdür (yalnızca sütun düzeni).</summary>
public record SavedViewDto(Guid Id, string Page, string Name, string State, DateTimeOffset UpdatedAt);
public record SavedViewSave(string Page, string Name, string State);

// ---- Active Directory (Aşama 2) ----
public record DirectoryStatusDto(bool SyncEnabled, bool LoginEnabled, bool Configured, string? Server, int Port, bool UseSsl, string? BaseDn, string? Filter,
    string? Domain, string IdAttribute, string UserNameAttribute, string? ScheduleNote, IReadOnlyList<DirectorySyncRunDto> RecentRuns);
public record DirectoryTestResult(bool Ok, string Message, int? SampleCount);
public record DirectoryRuleDto(Guid Id, int Priority, string Attribute, string MatchType, string Value, Guid CompanyId, string CompanyName);
public record DirectoryRuleSave(int Priority, string Attribute, string MatchType, string Value, Guid CompanyId);
public record DirectorySyncChange(string Kind, string Name, string? Detail);
public record DirectorySyncRunDto(Guid Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, string Trigger, string UserName,
    int Found, int Created, int Updated, int Deactivated, int Unmatched, string? Message, IReadOnlyList<DirectorySyncChange> Changes);

public static class DirectoryMatch
{
    public const string Equals = "Equals";
    public const string Contains = "Contains";
    public const string DnEndsWith = "DnEndsWith";
    public static readonly string[] All = [Equals, Contains, DnEndsWith];
    public static string Label(string m) => m switch
    {
        Equals => "eşittir",
        Contains => "içerir",
        DnEndsWith => "OU altında (DN ile biter)",
        _ => m
    };
}
