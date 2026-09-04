using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.Messaging;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI;

/// <summary>
/// EF Core context for the ledger service. Schema/constraints are the frozen
/// shape from spec-4-1's Boundaries section — keys, indexes, and MaxLengths
/// here are load-bearing for later stories' repository uniqueness guarantees
/// (story 2.2's <c>StoreIfNewAsync</c> dedupes on the unique indexes declared
/// here). No EF migrations — <c>EnsureCreated()</c> only (this repo's
/// convention; Aspire recreates the database from scratch when needed).
/// </summary>
public class CoreBankDbContext(DbContextOptions<CoreBankDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<MessagingOutboxMessage> MessagingOutboxMessages => Set<MessagingOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.HasKey(e => e.AccountNumber);
            entity.Property(e => e.AccountNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.AccountHolderName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.HasIndex(e => e.IsActive);
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();
            // Spec: add-instant-payment-rail, review loop 1. Was missing here
            // (PaymentsDbContext's entities already had it) -- without it, a
            // concurrent inline claim and a background batch claim could both
            // "win" the same row, since EF would never detect the race via
            // optimistic concurrency on SaveChanges.
            MessageRepositoryBase<InboxMessage, CoreBankDbContext>.ConfigureConcurrencyToken(entity);
            entity.HasIndex(e => new { e.PartitionId, e.Status, e.Priority, e.ReceivedAt }); // Partition-based query index
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ReceivedAt);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FromAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ToAccount).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.TransactionId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.TraceParent).HasMaxLength(55);
            entity.Property(e => e.TraceState).HasMaxLength(512);
        });

        modelBuilder.Entity<MessagingOutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.PartitionId, e.Status, e.CreatedAt }); // Partition-based query index
            entity.HasIndex(e => new { e.TransactionId, e.EventType, e.AccountNumber }).IsUnique();
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.TransactionId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.EventSource).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EventOccurredAt).IsRequired();
            entity.Property(e => e.TraceParent).HasMaxLength(55);
            entity.Property(e => e.TraceState).HasMaxLength(512);
        });
    }
}
