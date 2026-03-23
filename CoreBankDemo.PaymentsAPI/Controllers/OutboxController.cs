using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    [HttpGet("check-index")]
    public async Task<IActionResult> CheckIndex(CancellationToken cancellationToken = default)
    {
        var sql = """
                  ß
                              SELECT indexname, indexdef
                              FROM pg_indexes
                              WHERE tablename = 'OutboxMessages'
                              ORDER BY indexname
                  """;

        await using var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var indexes = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            indexes.Add(new
            {
                IndexName = reader.GetString(0),
                IndexDef = reader.GetString(1)
            });
        }

        return Ok(new
        {
            IndexCount = indexes.Count,
            Indexes = indexes,
            HasUniqueIdempotencyIndex = indexes.Any(i =>
                i.ToString()!.Contains("IdempotencyKey") &&
                i.ToString()!.Contains("UNIQUE"))
        });
    }
}
