using Microsoft.EntityFrameworkCore;

namespace ReplayProbe;

// The seed database (docker/seed.sql) as EF Core sees it - enough of it for the queries and
// SaveChanges batches an EF application typically sends.

internal sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string Region { get; set; } = "NA";
    public decimal Balance { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Notes { get; set; }
    public Guid RowGuid { get; set; }
    public List<Order> Orders { get; set; } = [];
}

internal sealed class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public float? Weight { get; set; }
    public string? Attributes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] Version { get; set; } = [];
}

internal sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateTime OrderedAt { get; set; }
    public string Status { get; set; } = "new";
    public decimal Total { get; set; }
    public string? Note { get; set; }
    public List<OrderLine> Lines { get; set; } = [];
}

internal sealed class OrderLine
{
    public int OrderId { get; set; }
    public int LineNumber { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int Qty { get; set; }
    public decimal UnitPrice { get; set; }
}

internal sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Who { get; set; } = "";
    public string What { get; set; } = "";
}

internal sealed class ShopContext(string connectionString) : DbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlServer(connectionString);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Customer>(e =>
        {
            e.ToTable("Customers");
            e.Property(c => c.Name).HasMaxLength(100);
            e.Property(c => c.Email).HasMaxLength(200).IsUnicode(false);
            e.Property(c => c.Region).HasMaxLength(2).IsFixedLength().IsUnicode(false).HasDefaultValue("NA");
            e.Property(c => c.Balance).HasColumnType("decimal(18,2)");
            e.Property(c => c.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(c => c.RowGuid).HasDefaultValueSql("NEWID()");
        });

        model.Entity<Product>(e =>
        {
            e.ToTable("Products");
            e.Property(p => p.Id).ValueGeneratedNever();
            e.Property(p => p.Sku).HasMaxLength(20).IsUnicode(false);
            e.Property(p => p.Price).HasColumnType("money");
            e.Property(p => p.Attributes).HasColumnType("xml");
            e.Property(p => p.UpdatedAt).HasDefaultValueSql("SYSDATETIMEOFFSET()");
            e.Property(p => p.Version).IsRowVersion();
        });

        model.Entity<Order>(e =>
        {
            e.ToTable("Orders");
            e.Property(o => o.Status).HasMaxLength(20).IsUnicode(false);
            e.Property(o => o.Total).HasColumnType("decimal(18,2)");
            e.Property(o => o.OrderedAt).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId);
        });

        model.Entity<OrderLine>(e =>
        {
            e.ToTable("OrderLines");
            e.HasKey(l => new { l.OrderId, l.LineNumber });
            e.Property(l => l.LineNumber).ValueGeneratedNever();
            e.Property(l => l.UnitPrice).HasColumnType("money");
        });

        model.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.Property(a => a.At).HasColumnType("datetime2(3)").HasDefaultValueSql("SYSUTCDATETIME()");
            e.Property(a => a.Who).HasDefaultValueSql("SUSER_SNAME()");
        });
    }
}
