using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CoreBankDemo.Messaging;

/// <summary>
/// Shared storage layer behind <see cref="InboxMessageRepositoryBase{TMessage,TDbContext}"/>
/// and <see cref="OutboxMessageRepositoryBase{TMessage,TDbContext}"/> (story 2.2):
/// race-safe <see cref="StoreIfNewAsync"/> (insert-then-catch, never
/// check-then-insert — AD-4) plus the entity-configuration hook each concrete
/// store uses to declare its dedupe unique index. Claiming, retry/poison
/// handling, and the processor-facing query methods described in the epic-2
/// legacy reference are added by later stories (2.3+) — this base intentionally
/// stops at the store.
/// </summary>
public abstract class MessageRepositoryBase<TMessage, TDbContext>
    where TMessage : class, IMessage
    where TDbContext : DbContext
{
    protected MessageRepositoryBase(TDbContext dbContext, TimeProvider timeProvider)
    {
        DbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    protected TDbContext DbContext { get; }

    /// <summary>
    /// Injected per the <c>conventions</c> skill (never <c>DateTime.Now/UtcNow</c>
    /// directly). Not read by this base yet — reserved for the stale-claim and
    /// completion-timestamp logic later stories add on top of this store.
    /// </summary>
    protected TimeProvider TimeProvider { get; }

    /// <summary>The store's message table; named by the inbox/outbox-specific base.</summary>
    protected abstract DbSet<TMessage> Messages { get; }

    /// <summary>
    /// Stores <paramref name="message"/> if no row with the same dedupe identity
    /// exists yet. Always inserts optimistically and relies on the store's
    /// dedupe unique index (see <see cref="ConfigureDedupeIndex"/>) to reject
    /// duplicates at the database — never a pre-check, which is racy under
    /// concurrent callers (AD-4).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call inserted the row;
    /// <see langword="false"/> when a concurrent or prior call already owns the
    /// dedupe identity ("already exists" — no exception is thrown for this
    /// case). Any failed call — whether it lost the dedupe race or
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> failed for
    /// any other reason — detaches <paramref name="message"/> from the change
    /// tracker before returning/rethrowing so <see cref="DbContext"/> stays
    /// usable for further operations by the caller.
    /// </returns>
    /// <exception cref="DbUpdateException">
    /// The save failed for a reason other than a unique-constraint violation;
    /// propagates unchanged (after detaching <paramref name="message"/>).
    /// </exception>
    public virtual async Task<bool> StoreIfNewAsync(TMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        Messages.Add(message);
        try
        {
            await DbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (UniqueViolation.IsUniqueViolation(ex))
        {
            DbContext.Entry(message).State = EntityState.Detached;
            return false;
        }
        catch
        {
            // Any other save failure (timeout, cancellation, a different
            // DbUpdateException, ...) still leaves the entity tracked as
            // Added unless we detach it here — a stale tracked entity would
            // corrupt every subsequent SaveChangesAsync on this context.
            DbContext.Entry(message).State = EntityState.Detached;
            throw;
        }
    }

    /// <summary>
    /// Entity-configuration hook (AD-4): declares the store's dedupe unique
    /// index. Command stores pass the idempotency key alone; event stores pass
    /// the composite event identity (e.g. idempotency key + event type
    /// [+ account]) — one row per distinct combination is allowed, so distinct
    /// identities sharing a key still store independently. Call this from the
    /// concrete <typeparamref name="TDbContext"/>'s <c>OnModelCreating</c>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="dedupePropertyNames"/> is <see langword="null"/> or
    /// empty; names a property that is not a public instance property of
    /// <typeparamref name="TMessage"/>; or contains the same property name
    /// more than once.
    /// </exception>
    public static void ConfigureDedupeIndex(EntityTypeBuilder<TMessage> builder, params string[] dedupePropertyNames)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (dedupePropertyNames is null || dedupePropertyNames.Length == 0)
        {
            throw new ArgumentException("At least one dedupe property name is required.", nameof(dedupePropertyNames));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyName in dedupePropertyNames)
        {
            if (typeof(TMessage).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance) is null)
            {
                throw new ArgumentException(
                    $"'{propertyName}' is not a public instance property of {typeof(TMessage).Name}.",
                    nameof(dedupePropertyNames));
            }

            if (!seen.Add(propertyName))
            {
                throw new ArgumentException(
                    $"Duplicate dedupe property name '{propertyName}'.",
                    nameof(dedupePropertyNames));
            }
        }

        builder.HasIndex(dedupePropertyNames).IsUnique();
    }
}
