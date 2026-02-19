using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI;

public class CoreBankDbContext(DbContextOptions<CoreBankDbContext> options) : DbContext(options)
{
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<MessagingOutboxMessage> MessagingOutboxMessages => Set<MessagingOutboxMessage>();
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            entity.HasIndex(e => new { e.PartitionId, e.Status, e.ReceivedAt }); // Partition-based query index
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ReceivedAt);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FromAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ToAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.TransactionId).HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.TraceParent).HasMaxLength(55);
            entity.Property(e => e.TraceState).HasMaxLength(512);
        });

        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.AccountNumber);
            entity.Property(e => e.AccountNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.AccountHolderName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.HasIndex(e => e.IsActive);
        });

        modelBuilder.Entity<MessagingOutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PartitionId, e.Status, e.CreatedAt }); // Partition-based query index
            entity.HasIndex(e => e.TransactionId).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.TransactionId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EventSource).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TraceParent).HasMaxLength(55);
            entity.Property(e => e.TraceState).HasMaxLength(512);
        });
    }
}