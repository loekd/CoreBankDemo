using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Outbox;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.MessageId).IsUnique(); // Unique index for deduplication
            entity.HasIndex(e => new { e.PartitionId, e.Status, e.CreatedAt }); // Partition-based query index
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.MessageId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TransactionId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FromAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ToAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.TraceParent).HasMaxLength(55);
            entity.Property(e => e.TraceState).HasMaxLength(512);
        });
    }
}