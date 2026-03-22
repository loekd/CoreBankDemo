using CoreBankDemo.CoreBankAPI;
using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.LoadTestSupport;

// Read-only view of the CoreBank database for assertion queries
public class CoreBankReadDbContext(DbContextOptions<CoreBankReadDbContext> options) : DbContext(options)
{
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
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
}
