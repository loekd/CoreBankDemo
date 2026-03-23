# Task Completion Checklist

After completing a coding task in CoreBankDemo:

1. **Build** - Ensure the solution compiles: `dotnet build CoreBankDemo.sln`
2. **Feature flags** - If adding new behavior, check if it should be gated behind a `Features` flag in `appsettings.json`
3. **Constants** - Use `MessageConstants.Status.*` and `MessageConstants.Defaults.*`, not magic strings
4. **Shared library** - If the change applies to both inbox and outbox patterns, consider updating base classes in `CoreBankDemo.Messaging`
5. **Configuration** - If adding new config options, add to `CoreBankDemo.ServiceDefaults/Configuration/` with Options pattern
6. **Load tests** - For significant behavior changes, run `dotnet run --project CoreBankDemo.LoadTests` to validate exactly-once semantics
7. **OpenTelemetry** - New background processing steps should create `Activity` spans using the existing `ActivitySource`
