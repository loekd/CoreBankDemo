# Claude Code Collaboration Instructions

## General Principles

### Code Quality
- Most important: this is a demo project featuring mission critical code. Always review and create code from this point of view.
- Ask for permission before making code changes, unless they have minimal impact.
- Keep this document up to date.
- **Don't overdo refactoring** - Keep changes minimal and focused.
- **Separate HTTP logic from business logic** - Private methods should return business types (e.g., `Task`, `Task<T>`), not HTTP results (`IActionResult`)
- **Always use proper validation** - Use data annotations and ModelState validation
- **Return all validation errors at once** - Use `{ Errors: [...] }` format with all problems in a single response

### Required Patterns

#### 1. Always Use Inbox Pattern (CoreBankAPI)
- `IdempotencyKey` is **required** (non-nullable) on all transaction requests
- Never allow direct transaction processing - always store in inbox first
- Background service processes inbox messages asynchronously

#### 2. Always Use Outbox Pattern (PaymentsAPI)
- Never process payments directly - always store in outbox first
- Background service processes outbox messages asynchronously

#### 3. Concurrency Safety
- **Use unique constraints** on `IdempotencyKey` (inbox) and `MessageId` (outbox)
- **Implement retry loops** to handle race conditions:
  ```csharp
  for (int attempt = 1; attempt <= 3; attempt++)
  {
      var existing = await dbContext.Messages.FirstOrDefaultAsync(m => m.Key == key);
      if (existing != null)
          return HandleExisting(existing);

      try
      {
          await StoreInDatabase(...);
          break; // Success
      }
      catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx &&
                                         sqliteEx.SqliteErrorCode == 19)
      {
          // UNIQUE constraint violation - another instance inserted
          await Task.Delay(TimeSpan.FromSeconds(1));
          if (attempt == 3)
              throw;
      }
  }
  ```

### Database

#### DateTime Handling
- **Use `DateTime` in database entities** (SQLite doesn't support `DateTimeOffset` in ORDER BY)
- **Use `DateTimeOffset` in DTOs/APIs** (correct type for APIs)
- **Convert when storing**: `timeProvider.GetUtcNow().UtcDateTime`
- **Convert when reading**: `new DateTimeOffset(entity.CreatedAt, TimeSpan.Zero)`

#### TimeProvider
- **Always use `TimeProvider`** instead of `DateTime.UtcNow` or `DateTimeOffset.UtcNow`
- Register as singleton: `builder.Services.AddSingleton(TimeProvider.System)`
- Use in code: `timeProvider.GetUtcNow()`

### Validation

#### Data Annotations
- Use `[Required]` for mandatory fields
- Use `[StringLength]` with min/max for string fields
- Use `[Range]` for numeric constraints
- Use `[RegularExpression]` for format validation

#### Model Validation
- Always check `ModelState.IsValid` first
- Return all errors: `BadRequest(new { Errors = GetModelErrors() })`
- Helper method:
  ```csharp
  private List<string> GetModelErrors()
  {
      return ModelState.Values
          .SelectMany(v => v.Errors)
          .Select(e => e.ErrorMessage)
          .ToList();
  }
  ```

### Service Communication

#### Dapr Service Invocation
- **Always use Dapr** for service-to-service calls, never direct HTTP
- Use app IDs: `corebank-api`, `payments-api`
- Example:
  ```csharp
  await daprClient.InvokeMethodAsync<TRequest, TResponse>(
      "corebank-api",
      "api/endpoint",
      requestData);
  ```

### Code Style

#### Controller Structure
```csharp
[ApiController]
[Route("api/[controller]")]
public class MyController(
    DbContext dbContext,
    IConfiguration configuration,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> PublicEndpoint([FromBody] Request request)
    {
        // 1. Validate
        if (!ModelState.IsValid)
            return BadRequest(new { Errors = GetModelErrors() });

        // 2. Business validation
        var validationResult = await ValidateBusinessRules(request);
        if (!validationResult.IsValid)
            return BadRequest(new { Errors = validationResult.Errors });

        // 3. Process with retry logic
        // 4. Return HTTP result
    }

    // Private methods return business types, not IActionResult
    private async Task StoreData(...) { }
    private ValidationResult ValidateBusinessRules(...) { }
}
```

#### Records for DTOs
- **Always use records** for DTOs and request/response models
- Use positional syntax:
  ```csharp
  public record MyRequest(
      [Required] string Property1,
      [Range(0, 100)] int Property2
  );
  ```

### Configuration Removed

These feature flags are **no longer used**:
- ❌ `Features:UseInbox` - inbox is always used
- ❌ `Features:UseOutbox` - outbox is always used
- ❌ Any direct processing code paths

### Aspire Configuration

#### Launch Settings
- Set `"launchBrowser": false` to prevent opening new browser tabs on F5
- Dashboard URL will be shown in console output

## Past Corrections

### Issues Fixed
1. ✅ Made `IdempotencyKey` required (was nullable)
2. ✅ Separated HTTP logic from business logic (methods returned `IActionResult`)
3. ✅ Fixed concurrency issues with unique constraint + retry pattern
4. ✅ Removed feature flags (inbox/outbox always used)
5. ✅ Changed `DateTime` in entities, `DateTimeOffset` in APIs
6. ✅ Migrated from `HttpClient` to Dapr service invocation
7. ✅ Used `TimeProvider` instead of `DateTime.UtcNow`

### Naming Patterns
- Use `MessageId` for outbox unique identifier
- Use `IdempotencyKey` for inbox unique identifier
- Use `PartitionId` for message ordering/partitioning

## Conference Demo Context

This is a **conference demo** for a 55-minute talk about:
- Mission-critical resilience patterns in banking systems
- .NET + Aspire + Dapr
- Progressive resilience: Retry → Outbox → Inbox → Message Ordering
- Chaos engineering with MS DevProxy
- Distributed tracing with OpenTelemetry/Jaeger

**Keep implementation focused on demonstrating these patterns clearly.**

