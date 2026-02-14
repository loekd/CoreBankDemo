using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI;

public class InboxMessage
{
    public Guid Id { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string FromAccount { get; set; }
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public string? TransactionId { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, Failed
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string? ResponsePayload { get; set; }
}

public class CoreBankDbContext : DbContext
{
    public CoreBankDbContext(DbContextOptions<CoreBankDbContext> options) : base(options)
    {
    }

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ReceivedAt);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FromAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ToAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.TransactionId).HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.AccountNumber);
            entity.Property(e => e.AccountNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.AccountHolderName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.HasIndex(e => e.IsActive);
        });
    }
}
