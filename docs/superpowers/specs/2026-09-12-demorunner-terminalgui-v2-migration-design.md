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

**Namespace footgun, found during plan review.** `CoreBankDemo.DemoRunner.Tests` has a nested
namespace literally named `CoreBankDemo.DemoRunner.Tests.Application`
(`tests/CoreBankDemo.DemoRunner.Tests/Application/`). Enclosing-namespace member lookup beats a
`using` directive in C#, so a plain `using Terminal.Gui.App;` plus a bare `Application.Create()`
call does not compile anywhere under `CoreBankDemo.DemoRunner.Tests.*` — `Application` resolves to
that nested namespace, not `Terminal.Gui.App.Application`. Every test file that needs to call
`Application.Create()` must use the same `using AppTerminal = Terminal.Gui.App.Application;` alias
the production code already uses, for exactly this reason.

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
- **No migrating `TextView` to `EditorView`.** `MainWindow.cs` declares three `TextView` fields
  and one method/property that name the type (5 call sites total: lines 255-257, 1871, 2611).
  `TextView` is independently marked `[Obsolete]` in Terminal.Gui — true already at the current
  2.4.17, not something 2.5.0 introduces — pointing at `EditorView`, a different control from a
  separate, still-incubating project (`tui-cs/Editor`). This has nothing to do with the static
  `Application` API and is a materially bigger, separate migration; explicitly deferred. See
  "Package bump and pragma removal" below for how its warnings stay suppressed without
  reintroducing the broad pragma this plan removes.

## Design

### Instance ownership

`Program.cs` becomes the sole owner of the single `IApplication` instance for the process's
lifetime, created (not `using`-scoped — see below) at the top of `RunConsole`:

```csharp
var app = AppTerminal.Create().Init();
```

(`AppTerminal` is the file's existing alias for `Terminal.Gui.App.Application`; `Create()` is a
new, non-obsolete static factory method, so the alias and its one remaining call are fine to
keep.) Every other `AppTerminal.X` call in `Program.cs` becomes `app.X` — `Driver`,
`RequestStop()`, `Run(window, errorHandler: ...)`. These are mechanical substitutions:
`IApplication`'s instance members have the same signatures as the static ones they replace.

**Not a top-level `using`.** `RunConsole`'s existing `finally` block disposes the terminal
(`AppTerminal.Shutdown()`) *before* the post-`try` code calls `TerminalCrashGuard.Report(crash,
...)` — the comment on the current code is explicit that the report "must not happen underneath a
live run loop" and is "deliberately deferred until after" the shutdown. A top-level `using var app
= ...;` would instead dispose at the end of the whole method, *after* `Report` runs, silently
reversing that ordering. So `app.Dispose()` replaces `AppTerminal.Shutdown()` in place, inside the
same `finally` block, and `app` is declared as a plain `var`, not `using var`.

### Threading the instance into MainWindow and ConfirmationDialog

`MainWindow`'s `UiRepaintCoalescer` field is currently a **field initializer**
(`private readonly UiRepaintCoalescer _repaints = new(AppTerminal.Invoke);`), which needs
`app.Invoke` — but field initializers cannot reference constructor parameters, only other
instance members. So this field moves from an initializer to a plain declaration, assigned in the
constructor body once `app` is available. This also rules out `View.App` as the access path here:
even if it worked, the window hasn't been passed to `Run`/`Begin` yet at construction time, so
`View.App` wouldn't be populated regardless.

Both `MainWindow`'s two constructors (public and internal) and
`ConfirmationDialog`'s `TerminalConfirmationService` take `IApplication` as an explicit,
**required, leading** constructor parameter, supplied by the caller:

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

`MainWindow`'s internal constructor (used by 6 test call sites — see Tests below) also gains the
same leading `IApplication app` parameter, and its default-confirmation-service branch becomes
`_confirmation = confirmation ?? new TerminalConfirmationService(_app);`.

### TerminalCrashGuard

`RestoreTerminal()` fires from `AppDomain.CurrentDomain.UnhandledException`, on whatever thread
the runtime hands it, precisely when the object graph may already be broken — it cannot take a
normal constructor dependency, and it exists (and is called) *before* the `IApplication` instance
does: `TerminalCrashGuard.Install(artifactsDirectory)` is called from `Main()`, once, to wire the
`AppDomain`/`TaskScheduler` handlers, before `RunConsole` (and the app instance it owns) exists at
all. So the app instance is threaded in separately, after the fact, via two new methods:

```csharp
internal static void AttachApplication(IApplication application)  // static field, Volatile.Write
internal static IApplication? TakeApplication()                   // Interlocked.Exchange(ref _application, null)
```

`Program.cs` calls `AttachApplication(app)` in `RunConsole` right after creating the instance.
Both normal shutdown (`RunConsole`'s `finally`) and a crash (`RestoreTerminal()`) call
`TakeApplication()` to get ownership before disposing — `Interlocked.Exchange` makes this a true
atomic hand-off, so whichever of the two calls it first is the only one that gets a non-null
result back, closing a double-dispose race a simpler read-then-clear version would leave open (a
crash arriving between disposing and clearing could otherwise dispose the same instance twice).
`RestoreTerminal()` replaces `AppTerminal.Shutdown()` with `TakeApplication()?.Dispose()`, inside
the same defensive `try { ... } catch (Exception) { /* driver already broken */ }` block it
already has. Belt-and-braces behavior is unchanged; only the mechanism for handing the terminal
back becomes instance-based.

### Tests

**MainWindow's constructor signature changes** (both overloads gain a leading `IApplication app`
parameter — see below), which means every test that constructs a `MainWindow` needs updating,
not just the two files that reference `AppTerminal` today. All call sites:

- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowTests.cs` — 4 direct call sites (none of
  which reference `AppTerminal` today; they pass `marshalUpdates: false` and never render, so they
  only need a plain, uninitialized `IApplication` instance to satisfy the constructor), plus its
  own `CreateWindow(...)` helper with 59 further callers.
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowFaultsTests.cs` — a separate file, easy
  to miss (a `grep "new MainWindow("` finds none of it): it has an identical `CreateWindow(...)`
  helper, with 12 callers, using the same target-typed `new(...)` construction that made both
  helpers invisible to that grep in the first place.
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/EvidencePaneRenderTests.cs` — 1 call site (the
  `EvidenceWindowAsync` helper).
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/NavigationRailRenderTests.cs` — 1 call site
  (inside `RenderRail`).

`IApplication` doesn't need to be initialized (`Init()`) to construct — `Application.Create()`
alone is enough to satisfy a constructor that only needs `app.Invoke` as a delegate target
(delegate creation from an instance method just binds `this`; it doesn't require the driver to be
running). So `MainWindowTests.cs`'s 4 non-rendering call sites each just add
`using var app = Application.Create();` and pass `app` as the new first constructor argument — no
other change.

`EvidencePaneRenderTests.cs` and `NavigationRailRenderTests.cs` actually render, so they need a
real `Init("dotnet")` + sized `Screen` + disposal — currently hand-rolled identically in every
test method (`Init("dotnet")` → set `Screen` → `Begin(window)` → `LayoutAndDraw(true)` → ... →
`Shutdown()`). This becomes a small static factory (new file,
`tests/CoreBankDemo.DemoRunner.Tests/Terminal/TerminalAppFactory.cs`):

```csharp
internal static class TerminalAppFactory
{
    internal static IApplication CreateHeadless(int width, int height)
    {
        var app = Application.Create().Init("dotnet");
        app.Screen = new System.Drawing.Rectangle(0, 0, width, height);
        return app;
    }
}
```

used as:

```csharp
using var app = TerminalAppFactory.CreateHeadless(140, 40);
...
app.Begin(window);
window.Frame = new System.Drawing.Rectangle(0, 0, 140, 40);
...
app.LayoutAndDraw(true);
```

`IApplication : IDisposable`, so the `using` also replaces the existing `try { ... } finally {
AppTerminal.Shutdown(); }` wrapper — a genuine simplification (one line instead of a wrapping
try/finally), not just a warning fix, and consistent with the officially documented v2 pattern.
Everything between `Begin` and the end of the `using` block (frame assignment, key handling,
resize, redraw calls) is otherwise untouched — same sequence, same assertions, just `app.X`
instead of `AppTerminal.X`.

### Package bump and pragma removal

`Directory.Packages.props`: `Terminal.Gui` `2.4.17` → `2.5.0`. `ADR-015` gets a short amendment
note (in its existing amendment style) recording the version bump and pointing at this spec.

Once `Program.cs`, `MainWindow.cs`, and `ConfirmationDialog.cs` no longer reference anything from
the obsolete static `Application` API, their whole-method/whole-file `#pragma warning
disable/restore CS0618` blocks are deleted — that pragma exists only to suppress warnings this
migration eliminates at the source. `MainWindow.cs` keeps three **narrow** replacement pragma
pairs, scoped to only the pre-existing, unrelated `TextView`-obsolete call sites (see
Non-goals): one around the three field declarations (lines 255-257), one around `SetPaneText`'s
signature (line 1871), one around the `EvidenceResponsePane` property (line 2611). This keeps the
build at 0 warnings without re-suppressing anything the `Application` migration itself touches.

## Files touched

- `CoreBankDemo.DemoRunner/Program.cs`
- `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs`
- `CoreBankDemo.DemoRunner/Terminal/ConfirmationDialog.cs`
- `CoreBankDemo.DemoRunner/Terminal/TerminalCrashGuard.cs`
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowTests.cs` — 4 direct constructor call
  sites plus its own `CreateWindow` helper (59 further callers), all collateral from the
  `MainWindow` signature change; no `AppTerminal` usage today
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowFaultsTests.cs` — same shape, its own
  `CreateWindow` helper (12 callers); easy to miss, found only during plan review
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/EvidencePaneRenderTests.cs`
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/NavigationRailRenderTests.cs`
- `tests/CoreBankDemo.DemoRunner.Tests/Terminal/TerminalAppFactory.cs` — new
- `Directory.Packages.props` (Terminal.Gui version)
- `docs/adr/ADR-015-presentation-safe-terminal-demo-console.md` (amendment note)

## Testing

- `dotnet build CoreBankDemo.sln` — 0 warnings expected (down from 21), 0 errors. (The 3 narrow
  `TextView`-scoped pragma pairs in `MainWindow.cs` are deliberate and expected to stay silent —
  see "Package bump and pragma removal".)
- `dotnet test CoreBankDemo.sln` — all 8 test projects pass, in particular
  `CoreBankDemo.DemoRunner.Tests` (687 tests today) with no behavior change.
- Manual smoke: launch the console for real (`dotnet run --project CoreBankDemo.DemoRunner`) and
  confirm normal start/quit, and a forced crash still restores the terminal — `TerminalCrashGuard`
  is the one path unit tests can't fully exercise (it hooks `AppDomain.UnhandledException`).

## Risk

Contained to the eight files above plus one new test file. No behavior change intended anywhere —
every replacement is call-site substitution against a verified equivalent instance member. The
main residual risk is the crash-guard path, which is inherently hard to unit test; the manual
smoke test above is the mitigation.
