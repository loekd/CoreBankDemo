using CoreBankDemo.PaymentsAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using CoreBankDemo.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI;

public class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            MessageRepositoryBase<OutboxMessage, PaymentsDbContext>.ConfigureDedupeIndex(
                entity, nameof(OutboxMessage.IdempotencyKey));
            MessageRepositoryBase<OutboxMessage, PaymentsDbContext>.ConfigureConcurrencyToken(entity);
            entity.HasIndex(e => new { e.PartitionId, e.Status, e.Priority, e.CreatedAt });
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CreatedAt);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TransactionId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FromAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ToAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Amount).HasPrecision(18, 2);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.TraceParent).HasMaxLength(55);
            entity.Property(e => e.TraceState).HasMaxLength(512);
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            MessageRepositoryBase<InboxMessage, PaymentsDbContext>.ConfigureDedupeIndex(
                entity,
                nameof(InboxMessage.TransactionId),
                nameof(InboxMessage.EventType),
                nameof(InboxMessage.AccountNumber));
            MessageRepositoryBase<InboxMessage, PaymentsDbContext>.ConfigureConcurrencyToken(entity);
            entity.HasIndex(e => new { e.PartitionId, e.Status, e.Priority, e.ReceivedAt });
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ReceivedAt);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TransactionId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.AccountNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.TraceParent).HasMaxLength(55);
            entity.Property(e => e.TraceState).HasMaxLength(512);
        });
    }
}
