namespace Nurol.Envanter.Shared;

// ---- Yazıcı izleme (SNMP) ----
/// <summary>State: ok, low, empty, unknown. Key: yuvanın kalıcı anahtarı (renk veya açıklama); toner eşlemesi bu anahtara yapılır.</summary>
public record PrinterSupplyDto(string Key, int Index, string Description, string? Color, string Type, int? Percent, string Level, string State, bool Monitored,
    Guid? StockItemId, string? StockItemName, string? StockItemCode, decimal? StockInWarehouse, decimal? StockTotal, bool PendingReplacement);

/// <summary>Severity: Kritik (bitti veya çok az) / Uyarı (eşik altında).</summary>
public record PrinterWarning(Guid PrinterId, string PrinterName, string? Location, string SupplyKey, string Supply, int? Percent, string Severity, string Message,
    Guid? StockItemId, string? Toner, decimal? StockInWarehouse, decimal? StockTotal);

public record PrinterListRow(Guid Id, string Name, string Host, string? Location, Guid WarehouseId, string WarehouseName, string? Model, bool IsActive,
    bool? Online, DateTimeOffset? LastPolledAt, DateTimeOffset? LastSuccessAt, string? LastError, long? PageCount,
    IReadOnlyList<PrinterSupplyDto> Supplies, IReadOnlyList<PrinterWarning> Warnings, bool CanManage);

public record PrinterEventDto(DateTimeOffset At, string Type, string Text, string UserName);
public record PrinterCounterDay(DateOnly Day, long PageCount, long? Pages);
public record PrinterDetail(PrinterListRow Printer, string? SerialNumber, string? SysName, int Port, string SnmpVersion, int LowThreshold, bool HasCommunity,
    IReadOnlyList<PrinterEventDto> Events, IReadOnlyList<PrinterCounterDay> Counters, long? PagesLast30Days);

/// <summary>Community boş bırakılırsa (düzenlemede) kayıtlı değer korunur.</summary>
public record PrinterSave(Guid WarehouseId, string Name, string Host, int Port, string SnmpVersion, string? Community, string? Location, int LowThreshold, bool IsActive);
public record PrinterProbeRequest(string Host, int Port, string SnmpVersion, string? Community, Guid? PrinterId);
public record PrinterProbeResult(bool Ok, string Message, string? Model, string? SysName, string? SerialNumber, long? PageCount, IReadOnlyList<PrinterSupplyDto> Supplies);
public record PrinterTonerMap(Guid? StockItemId, bool ApplyToSameModel);
public record PrinterReplace(bool ConsumeFromStock, string? Note);
public record TonerNeedRow(Guid StockItemId, string Code, string Name, string Unit, decimal MinLevel, int PrinterCount, int LowCount, decimal StockTotal,
    IReadOnlyList<StockBalanceDto> Balances, string Status, IReadOnlyList<string> Printers);
