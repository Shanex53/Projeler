namespace Nurol.Envanter.Shared;

// ---- Raporlar (Aşama 3) ----
/// <summary>Kolon tipleri: text, number, money, date, datetime. Satır değerleri tipe uygun JSON değerleridir.</summary>
public record ReportColumn(string Key, string Label, string Type);
public record ReportTable(string Title, string? Note, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<object?[]> Rows, IReadOnlyList<ReportSummary> Summary);
public record ReportSummary(string Label, string Value);
public record ReportInfo(string Key, string Title, string Description, bool DateRange, bool GroupBy, IReadOnlyList<string> GroupOptions);

// ---- Excel ile toplu ekleme ----
public record ImportError(int Row, string? Column, string Message);
public record ImportResult(int Total, int Valid, bool Committed, IReadOnlyList<ImportError> Errors, IReadOnlyList<string> Created, IReadOnlyList<string> PreviewHeaders, IReadOnlyList<string?[]> Preview);

// ---- Kod okutma ----
public record LookupResult(Guid ItemId, string Tag, string MatchedBy);

// ---- Bildirimler ----
public record NotificationStatusDto(bool Enabled, string? Host, int Port, string? From, string? BaseUrl, int Pending, int Failed, IReadOnlyList<NotificationRow> Recent);
public record NotificationRow(Guid Id, string Kind, string To, string Subject, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, int Attempts, string? LastError);
public record MyAccountDto(string UserName, string DisplayName, string AccountType, string? Email, bool NotifyByEmail);
public record MyAccountSave(string? Email, bool NotifyByEmail, string? CurrentPassword, string? NewPassword);
