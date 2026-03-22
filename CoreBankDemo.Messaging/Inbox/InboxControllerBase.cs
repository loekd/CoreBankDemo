using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.Messaging.Inbox;

/// <summary>
/// Base controller for monitoring inbox messages across all services.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public abstract class InboxControllerBase<TMessage, TDbContext> : ControllerBase
    where TMessage : class, IInboxMessage
    where TDbContext : DbContext
{
    protected readonly InboxMessageRepositoryBase<TMessage, TDbContext> Repository;

    protected InboxControllerBase(InboxMessageRepositoryBase<TMessage, TDbContext> repository)
    {
        Repository = repository;
    }

    [HttpGet]
    public virtual async Task<IActionResult> GetInboxMessages(CancellationToken cancellationToken = default)
    {
        var messages = await Repository.GetRecentMessagesAsync(50, cancellationToken);
        return Ok(messages);
    }
}
