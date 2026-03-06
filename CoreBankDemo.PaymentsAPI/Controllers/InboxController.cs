using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InboxController(PaymentsDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetInboxMessages(CancellationToken cancellationToken = default)
    {
        var messages = await dbContext.InboxMessages
            .OrderByDescending(m => m.ReceivedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        return Ok(messages);
    }
}

