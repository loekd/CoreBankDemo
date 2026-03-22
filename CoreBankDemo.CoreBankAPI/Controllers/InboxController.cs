using CoreBankDemo.CoreBankAPI.Inbox;
using CoreBankDemo.Messaging.Inbox;

namespace CoreBankDemo.CoreBankAPI.Controllers;

public class InboxController : InboxControllerBase<InboxMessage, CoreBankDbContext>
{
    public InboxController(InboxMessageRepository repository) : base(repository)
    {
    }
}
