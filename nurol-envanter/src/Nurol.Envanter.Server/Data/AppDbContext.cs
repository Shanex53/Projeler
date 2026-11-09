using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Nurol.Envanter.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser>(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<ServerSession> Sessions => Set<ServerSession>();
    public DbSet<UserScope> UserScopes => Set<UserScope>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Manufacturer> Manufacturers => Set<Manufacturer>();
    public DbSet<AssetModel> AssetModels => Set<AssetModel>();
    public DbSet<FieldDefinition> FieldDefinitions => Set<FieldDefinition>();
    public DbSet<InventoryItem> Items => Set<InventoryItem>();
    public DbSet<FieldValue> FieldValues => Set<FieldValue>();
    public DbSet<UniqueFieldValue> UniqueFieldValues => Set<UniqueFieldValue>();
    public DbSet<ItemEvent> ItemEvents => Set<ItemEvent>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<AssignmentLine> AssignmentLines => Set<AssignmentLine>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Counter> Counters => Set<Counter>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<TransferLine> TransferLines => Set<TransferLine>();
    public DbSet<SavedView> SavedViews => Set<SavedView>();
    public DbSet<DirectoryCompanyRule> DirectoryRules => Set<DirectoryCompanyRule>();
    public DbSet<DirectorySyncRun> DirectorySyncRuns => Set<DirectorySyncRun>();
    public DbSet<OutboxMail> OutboxMails => Set<OutboxMail>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>().HasMany(u => u.Scopes).WithOne().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ServerSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);
        });

        b.Entity<Company>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(10);
            e.Property(x => x.Name).HasMaxLength(200);
        });
        b.Entity<Warehouse>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.Company).WithMany().OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Location>(e =>
        {
            e.HasIndex(x => new { x.WarehouseId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.HasOne(x => x.Warehouse).WithMany(w => w.Locations).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Category>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Code).HasMaxLength(10);
        });
        b.Entity<Manufacturer>().HasIndex(x => x.Name).IsUnique();
        b.Entity<AssetModel>(e =>
        {
            e.HasIndex(x => new { x.ManufacturerId, x.Name }).IsUnique();
            e.HasOne(x => x.Category).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Manufacturer).WithMany().OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<FieldDefinition>(e =>
        {
            e.HasIndex(x => x.Key).IsUnique();
            e.HasIndex(x => x.CategoryId);
            e.HasIndex(x => x.AssetModelId);
            e.ToTable(t => t.HasCheckConstraint("ck_field_scope", "(category_id IS NULL) <> (asset_model_id IS NULL)"));
        });

        b.Entity<InventoryItem>(e =>
        {
            e.HasIndex(x => x.Tag).IsUnique();
            // Seri numarası aynı üretici içinde grup genelinde benzersiz.
            e.HasIndex(x => new { x.ManufacturerId, x.Serial }).IsUnique().HasFilter("serial IS NOT NULL");
            e.HasIndex(x => new { x.WarehouseId, x.Status });
            e.HasIndex(x => x.EmployeeId);
            e.Property(x => x.Version).IsRowVersion();
            e.Property(x => x.Cost).HasPrecision(14, 2);
            e.HasOne(x => x.Company).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Category).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.AssetModel).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Location).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Employee).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.OpenAssignment).WithMany().HasForeignKey(x => x.OpenAssignmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.FieldValues).WithOne().HasForeignKey(v => v.ItemId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<FieldValue>(e =>
        {
            e.HasKey(x => new { x.ItemId, x.FieldId });
            e.HasIndex(x => new { x.FieldId, x.Text });
            e.Property(x => x.Number).HasPrecision(20, 6);
            e.HasOne(x => x.Field).WithMany().HasForeignKey(x => x.FieldId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<UniqueFieldValue>(e =>
        {
            e.HasKey(x => new { x.FieldId, x.Value });
            e.HasIndex(x => x.ItemId);
        });
        b.Entity<ItemEvent>().HasIndex(x => new { x.ItemId, x.At });

        b.Entity<StockItem>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
            e.Property(x => x.MinLevel).HasPrecision(14, 3);
            e.HasMany(x => x.Balances).WithOne().HasForeignKey(x => x.StockItemId);
        });
        b.Entity<StockBalance>(e =>
        {
            e.HasKey(x => new { x.StockItemId, x.WarehouseId });
            e.Property(x => x.Quantity).HasPrecision(14, 3);
            e.ToTable(t => t.HasCheckConstraint("ck_stock_balance_non_negative", "quantity >= 0"));
            e.HasOne(x => x.Warehouse).WithMany().OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<StockMovement>(e =>
        {
            e.HasIndex(x => x.No).IsUnique();
            e.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            e.HasIndex(x => new { x.StockItemId, x.At });
            e.HasIndex(x => x.EmployeeId);
            e.Property(x => x.Quantity).HasPrecision(14, 3);
            e.Property(x => x.BalanceAfter).HasPrecision(14, 3);
        });

        b.Entity<Employee>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.RegistryNo }).IsUnique().HasFilter("registry_no IS NOT NULL");
            e.HasIndex(x => x.FullName);
            e.HasOne(x => x.Company).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Assignment>(e =>
        {
            e.HasIndex(x => x.No).IsUnique();
            e.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            e.HasIndex(x => new { x.EmployeeId, x.At });
            e.Property(x => x.Snapshot).HasColumnType("jsonb");
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Documents).WithOne(d => d.Assignment).HasForeignKey(d => d.AssignmentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Employee).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AssignmentLine>(e =>
        {
            // Bir cihazın aynı anda yalnızca bir açık zimmet satırı olabilir.
            e.HasIndex(x => x.ItemId, "ux_assignment_lines_open_item").IsUnique().HasFilter("is_open").HasDatabaseName("ux_assignment_lines_open_item");
            e.HasIndex(x => x.ItemId, "ix_assignment_lines_item_id").HasDatabaseName("ix_assignment_lines_item_id");
            e.HasOne(x => x.Item).WithMany().OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Document>(e => e.HasIndex(x => new { x.AssignmentId, x.Kind, x.Version }).IsUnique());
        b.Entity<AuditEvent>().HasIndex(x => x.At);
        b.Entity<Counter>().HasKey(x => x.Name);

        b.Entity<AppUser>().HasIndex(u => u.DirectoryId).IsUnique().HasFilter("directory_id IS NOT NULL");
        b.Entity<Employee>().HasIndex(e => e.DirectoryId).IsUnique().HasFilter("directory_id IS NOT NULL");

        b.Entity<Transfer>(e =>
        {
            e.HasIndex(x => x.No).IsUnique();
            e.HasIndex(x => new { x.Status, x.TargetWarehouseId });
            e.Property(x => x.Version).IsRowVersion();
            e.HasOne(x => x.SourceWarehouse).WithMany().HasForeignKey(x => x.SourceWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.TargetWarehouse).WithMany().HasForeignKey(x => x.TargetWarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.TransferId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("ck_transfer_distinct_warehouses", "source_warehouse_id <> target_warehouse_id"));
        });
        b.Entity<TransferLine>(e =>
        {
            e.HasIndex(x => x.ItemId);
            e.Property(x => x.Quantity).HasPrecision(14, 3);
            e.HasOne(x => x.Item).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.StockItem).WithMany().OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_transfer_line_kind",
                "(item_id IS NOT NULL AND stock_item_id IS NULL AND quantity IS NULL) OR (item_id IS NULL AND stock_item_id IS NOT NULL AND quantity > 0)"));
        });
        b.Entity<SavedView>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Page, x.Name }).IsUnique();
            e.Property(x => x.State).HasColumnType("jsonb");
            e.Property(x => x.Name).HasMaxLength(80);
        });
        b.Entity<OutboxMail>(e =>
        {
            e.HasIndex(x => x.DedupKey).IsUnique().HasFilter("dedup_key IS NOT NULL");
            e.HasIndex(x => new { x.SentAt, x.SendAfter });
        });
        b.Entity<DirectoryCompanyRule>().HasOne(x => x.Company).WithMany().OnDelete(DeleteBehavior.Restrict);
        b.Entity<DirectorySyncRun>(e =>
        {
            e.HasIndex(x => x.StartedAt);
            e.Property(x => x.Changes).HasColumnType("jsonb");
        });
    }
}
