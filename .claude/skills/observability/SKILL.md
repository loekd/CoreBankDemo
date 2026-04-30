---
name: observability
description: "OpenTelemetry tracing rules for CoreBankDemo: ActivitySource registration, span creation, and trace context propagation."
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
