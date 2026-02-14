using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankDemo.CoreBankAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class InboxController : ControllerBase
{
    private readonly CoreBankDbContext _dbContext;

    public InboxController(CoreBankDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<IActionResult> GetInboxMessages()
    {
        var messages = await _dbContext.InboxMessages
            .OrderByDescending(m => m.ReceivedAt)
            .Take(50)
            .ToListAsync();

        return Ok(messages);
    }
}
