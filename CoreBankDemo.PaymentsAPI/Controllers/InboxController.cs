using CoreBankDemo.Messaging.Inbox;
using CoreBankDemo.PaymentsAPI.Inbox;

namespace CoreBankDemo.PaymentsAPI.Controllers;

public class InboxController : InboxControllerBase<InboxMessage, PaymentsDbContext>
{
    public InboxController(InboxMessageRepository repository) : base(repository)
    {
    }
}
