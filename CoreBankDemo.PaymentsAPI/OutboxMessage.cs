using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI;

public class OutboxMessage
{
    public Guid Id { get; set; }
    public required string MessageId { get; set; } // Unique message ID for deduplication
    public required string PaymentId { get; set; }
    public required string FromAccount { get; set; }
    public required string ToAccount { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Completed, Failed
}

public class PaymentsDbContext : DbContext
{
    public PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : base(options)
    {
    }

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MessageId).IsUnique(); // Unique index for deduplication
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.MessageId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.PaymentId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FromAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ToAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
        });
    }
}
