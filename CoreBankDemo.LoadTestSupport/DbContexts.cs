using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.CoreBankAPI.Outbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.EntityFrameworkCore;
using CoreBankInboxMessage = CoreBankDemo.CoreBankAPI.Inbox.InboxMessage;
using PaymentsInboxMessage = CoreBankDemo.PaymentsAPI.Inbox.InboxMessage;

namespace CoreBankDemo.LoadTestSupport;

// Read-only view of the CoreBank database for assertion queries
public class CoreBankReadDbContext(DbContextOptions<CoreBankReadDbContext> options) : DbContext(options)
{
    public DbSet<CoreBankInboxMessage> InboxMessages => Set<CoreBankInboxMessage>();
    public DbSet<MessagingOutboxMessage> OutboxMessages => Set<MessagingOutboxMessage>();
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Account uses a non-conventional primary key
        modelBuilder.Entity<Account>().HasKey(e => e.AccountNumber);
    }
}

public class PaymentsReadDbContext(DbContextOptions<PaymentsReadDbContext> options) : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<PaymentsInboxMessage> InboxMessages => Set<PaymentsInboxMessage>();
}
