# DemoRunner: migrate off Terminal.Gui's obsolete static Application API

**Date:** 2026-09-12
**Status:** Draft
**Author:** Claude (Sonnet 5), on behalf of Loek Duys

## Context

`CoreBankDemo.DemoRunner`'s build carries 21 `CS0618` warnings: two render test files
(`tests/CoreBankDemo.DemoRunner.Tests/Terminal/EvidencePaneRenderTests.cs`,
`.../NavigationRailRenderTests.cs`) and `CoreBankDemo.DemoRunner/Terminal/TerminalCrashGuard.cs`
call Terminal.Gui's static `Terminal.Gui.App.Application` class, whose whole surface is marked
`[Obsolete]` with "The legacy static Application object is going away."

This is not new. `Program.cs`, `Terminal/MainWindow.cs`, and `Terminal/ConfirmationDialog.cs`
already call the same obsolete static API just as heavily, but wrap it in
`#pragma warning disable/restore CS0618` spanning almost their entire bodies — a prior, deliberate
decision to defer the real migration. The three unwrapped files are the only reason the build
isn't silently at zero warnings already.

Terminal.Gui v2's actual replacement is **not a rename**. It's an architectural shift: a
disposable `IApplication` instance (created via `Application.Create().Init()`) replaces the static
class everywhere; views get an `App` property to reach it instead of `Application.Driver`;
disposal is explicit instead of `Application.Shutdown()` tearing everything down implicitly.

This was verified directly against the real `Terminal.Gui 2.5.0` assembly via a reflection probe
(a scratch console app referencing the package, dumping `IApplication`'s members), not just the
migration docs:

- `IApplication : IDisposable` — `Dispose()` is the literal, direct replacement for
  `Application.Shutdown()`.
- `Application.Create(ITimeProvider timeProvider = null)` and `IApplication.Init(string?
  driverName = null)` both have optional/defaulted parameters, so `Application.Create().Init()`
  and `Init("dotnet")` (the test driver name) both compile as expected.
- `Window` (what `MainWindow` and the dialogs derive from, via `Runnable`) implements
  `IRunnable`, so `app.Run(window, errorHandler: ...)` and `app.Begin(window)` accept it exactly
  as `Application.Run`/`Begin` do today.
- `Begin(IRunnable)` returns a `SessionToken` (v1/2.4.17 discarded whatever `Begin` returned too —
  the existing test call sites already ignore the return value as a bare statement, so this is not
  a behavior change, just a type the migration doesn't need to touch).

Also grepped the codebase for zero use of anything from the new API today, confirming this is a
green-field migration with no partial adoption to reconcile.

`ADR-015` pins Terminal.Gui centrally at exactly `2.4.17`. This migration bumps that pin to
`2.5.0` and needs a short amendment note on that ADR, not a silent version drift.

DemoRunner is a **standalone presentation console for a conference talk** (ADR-015) with zero
project references into the banking system — its own small, self-contained surface, which is
exactly why this migration is scoped tightly rather than opportunistically improved.

## Non-goals

- No adoption of `IRunnable<T>` for `ConfirmationDialog` — it stays a plain `View` run via
  `app.Run(dialog)`, same shape as today, just against an instance.
- No introduction of a DI container. DemoRunner has never used one (`new` throughout); the
  `IApplication` instance is threaded through as an explicit constructor parameter, consistent
  with the console's existing style.
- No adoption of Terminal.Gui's `Testing.IInputInjector` API to replace the render tests' manual
  `Begin`/`LayoutAndDraw` sequencing. Worth a future look, out of scope here.
- No behavior change anywhere. Every replacement is a call-site substitution against an
  equivalent instance member with the same shape as the static one it replaces.
- No touching `View.Text`, `IAcceptTarget`, or `ConfigurationManager` — the three other 2.5.0
  breaking changes. Grepped the codebase; none of the three are used.

## Design

### Instance ownership

`Program.cs` becomes the sole owner of the single `IApplication` instance for the process's
lifetime:

```csharp
using var app = Application.Create().Init();
```

replacing `AppTerminal.Init()` / `AppTerminal.Shutdown()`. Every other `AppTerminal.X` call in
`Program.cs` becomes `app.X` — `Driver`, `RequestStop()`, `Run(window, errorHandler: ...)`,
`Invoke`. These are mechanical substitutions: `IApplication`'s instance members have the same
signatures as the static ones they replace.

### Threading the instance into MainWindow and ConfirmationDialog

`MainWindow`'s `UiRepaintCoalescer` field needs `app.Invoke` at field-initializer time, before the
window is ever passed to `Run`/`Begin` — i.e. before the framework-populated `View.App` property
would be set. So `View.App` isn't viable as the access path here. Both `MainWindow` and
`ConfirmationDialog`'s `TerminalConfirmationService` take `IApplication` as an explicit
constructor parameter, supplied by `Program.cs` at construction time:

```csharp
var window = new MainWindow(app, controller, onExit, theme);
```

```csharp
internal sealed class TerminalConfirmationService(IApplication app) : IConfirmationService
{
    public bool Confirm(ConfirmationRequest request)
    {
        var dialog = new DestructiveConfirmationDialog(request);
        dialog.FocusCancel();
        app.Run(dialog);
        return dialog.Result == true;
    }
}
```

### TerminalCrashGuard

`RestoreTerminal()` fires from `AppDomain.CurrentDomain.UnhandledException`, on whatever thread
the runtime hands it, precisely when the object graph may already be broken — it cannot take a
normal constructor dependency. `TerminalCrashGuard.Install(...)` gains an `IApplication` parameter
captured into a static field:

```csharp
internal static void Install(string artifactsDirectory, IApplication application)
```

`Program.cs` calls `TerminalCrashGuard.Install(artifactsDirectory, app)` right after creating the
instance — the same place it already installs the guard. `RestoreTerminal()` replaces
`AppTerminal.Shutdown()` with `instance.Dispose()`, inside the same defensive
`try { ... } catch (Exception) { /* driver already broken */ }` block it already has. Belt-and-braces
behavior is unchanged; only the mechanism for handing the terminal back becomes instance-based.

### Tests

`EvidencePaneRenderTests.cs` and `NavigationRailRenderTests.cs` currently hand-roll an identical
`Init("dotnet")` → set `Screen` → `Begin(window)` → `LayoutAndDraw(true)` → ... → `Shutdown()`
sequence in every test method. This becomes a shared test fixture (new file,
`tests/CoreBankDemo.DemoRunner.Tests/Terminal/TerminalAppFixture.cs` or similar) that wraps
`Application.Create().Init("dotnet")` behind `IDisposable`, so each test gets an isolated
`IApplication` instance:

```csharp
using var fixture = new TerminalAppFixture(width: 140, height: 40);
fixture.App.Begin(window);
window.RenderForTest();
fixture.App.LayoutAndDraw(true);
```

This both clears the obsolete-API warnings in both files and removes the duplicated boilerplate
between them — a genuine simplification, not just a warning fix.

### Package bump and pragma removal

`Directory.Packages.props`: `Terminal.Gui` `2.4.17` → `2.5.0`. `ADR-015` gets a short amendment
note (in its existing amendment style) recording the version bump and pointing at this spec.

Once `Program.cs`, `MainWindow.cs`, and `ConfirmationDialog.cs` no longer reference anything from
the obsolete static API, their `#pragma warning disable/restore CS0618` blocks are deleted
entirely — that pragma exists only to suppress warnings this migration eliminates at the source.

## Files touched

- `CoreBankDemo.DemoRunner/Program.cs`
- `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs`
- `CoreBankDemo.DemoRunner/Terminal/ConfirmationDialog.cs`
- `CoreBankDemo.DemoRunner/Terminal/TerminalCrashGuard.cs`
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/EvidencePaneRenderTests.cs`
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/NavigationRailRenderTests.cs`
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/` — new shared test fixture file
- `Directory.Packages.props` (Terminal.Gui version)
- `docs/adr/ADR-015-presentation-safe-terminal-demo-console.md` (amendment note)

## Testing

- `dotnet build CoreBankDemo.sln` — 0 warnings expected (down from 21), 0 errors.
- `dotnet test CoreBankDemo.sln` — all 8 test projects pass, in particular
  `CoreBankDemo.DemoRunner.Tests` (687 tests today) with no behavior change.
- Manual smoke: launch the console for real (`dotnet run --project CoreBankDemo.DemoRunner`) and
  confirm normal start/quit, and a forced crash still restores the terminal — `TerminalCrashGuard`
  is the one path unit tests can't fully exercise (it hooks `AppDomain.UnhandledException`).

## Risk

Contained to the six files above plus one new test file. No behavior change intended anywhere —
every replacement is call-site substitution against a verified equivalent instance member. The
main residual risk is the crash-guard path, which is inherently hard to unit test; the manual
smoke test above is the mitigation.
