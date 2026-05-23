---
name: observability
description: |
  OpenTelemetry tracing rules for CoreBankDemo: ActivitySource registration, span creation, and trace context propagation.
  
  **When to use:**
  - When adding or reviewing tracing, ActivitySource, or span logic in CoreBankDemo.
  - When ensuring trace context propagation and distributed tracing best practices are followed.
  
  **When NOT to use:**
  - Do NOT use for tracing unrelated to CoreBankDemo or for non-OpenTelemetry tracing systems.
  - Do NOT use for runtime log inspection or debugging—use the aspire-mcp skill for those tasks.
---
---

## Register ActivitySource

Add every new `ActivitySource` name when calling `AddServiceDefaults`:

```csharp
builder.AddServiceDefaults(serviceName, new[] { nameof(MyProcessor), ... });
```

## Custom spans

```csharp
using var activity = _activitySource.StartActivity("OperationName");
activity?.SetTag("key", value);
```

## Trace context propagation

Persist `TraceParent` and `TraceState` on outbox/inbox rows when the message is created. Restore them when processing begins to re-attach to the originating trace.

Never swallow or break the trace chain — every background processor must propagate context to its children.

## Key file

`CoreBankDemo.ServiceDefaults/Extensions.cs` — `AddServiceDefaults`, OTEL pipeline configuration.
