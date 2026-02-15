using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CoreBankDemo.PaymentsAPI.Outbox;

namespace CoreBankDemo.PaymentsAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OutboxController(PaymentsDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetOutboxMessages(CancellationToken cancellationToken = default)
    {
        var messages = await dbContext.OutboxMessages
            .OrderByDescending(m => m.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

        return Ok(messages);
    }
}
