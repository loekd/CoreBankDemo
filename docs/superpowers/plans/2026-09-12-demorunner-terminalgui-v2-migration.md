# DemoRunner Terminal.Gui v2 Instance-Based Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `CoreBankDemo.DemoRunner`'s use of Terminal.Gui's obsolete static `Application`
class with the v2 instance-based `IApplication` model, clearing all 21 `CS0618` warnings (plus
the three files' worth of usage currently hidden behind broad `#pragma warning disable CS0618`
blocks) without changing any observable behavior.

**Architecture:** `Program.cs` creates and owns a single `IApplication` instance for the
process's lifetime and threads it as an explicit, required, leading constructor parameter into
`MainWindow` and `ConfirmationDialog`'s `TerminalConfirmationService` — no DI container, matching
the console's existing plain-`new` style. `TerminalCrashGuard` (which fires from
`AppDomain.UnhandledException`, on arbitrary threads, before a normal constructor dependency is
possible) gets the instance attached/detached around its lifetime via two new static methods
instead. Every render test gets its own instance via a small shared factory.

**Tech Stack:** .NET 10, Terminal.Gui 2.5.0 (bumped from 2.4.17 in this plan), xUnit v3,
AwesomeAssertions.

**Spec:** `docs/superpowers/specs/2026-09-12-demorunner-terminalgui-v2-migration-design.md`

## Global Constraints

- No behavior change anywhere. Every edit is a call-site substitution against a verified
  equivalent `IApplication` instance member — confirmed directly against the real Terminal.Gui
  2.5.0 assembly via reflection (see spec), not assumed from docs.
- No DI container. `IApplication` is threaded through as an explicit constructor parameter.
- No adoption of `IRunnable<T>` for `ConfirmationDialog` — stays a plain `View` run via
  `app.Run(dialog)`.
- No touching `TextView` → `EditorView`. `TextView` is independently obsolete (true already at
  2.4.17) and pointing at a different, separate, still-incubating control — explicitly out of
  scope. Its 5 pre-existing warning sites in `MainWindow.cs` (lines 255-257, 1871, 2611) get
  **narrow** replacement pragmas, not swept into the broad ones being removed.
- **Build will not be green until Task 8.** Tasks 2-7 touch one side of a call-site relationship
  at a time (e.g., `MainWindow.cs`'s constructor signature before its callers are updated), so
  the solution will not compile in between. This is expected — each task's diff is still
  independently reviewable; just don't run `dotnet build` as a pass/fail gate until Task 8 says to.
- Run `dotnet build CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj` (not the whole
  solution) after Tasks 2-3 to catch typos early even though it won't succeed until later tasks
  land — read the error list, don't expect 0 errors.

---

### Task 1: Add the shared headless-`IApplication` test factory

**Files:**
- Create: `tests/CoreBankDemo.DemoRunner.Tests/Terminal/TerminalAppFactory.cs`

**Interfaces:**
- Produces: `TerminalAppFactory.CreateHeadless(int width, int height) : IApplication` — an
  initialized (`Init("dotnet")`), screen-sized, disposable `IApplication` instance. Used by Tasks
  6 and 7.

- [ ] **Step 1: Create the factory file**

```csharp
using System.Drawing;
using Terminal.Gui.App;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>Builds a headless, disposable IApplication instance sized for deterministic render tests.</summary>
internal static class TerminalAppFactory
{
    internal static IApplication CreateHeadless(int width, int height)
    {
        var app = Application.Create().Init("dotnet");
        app.Screen = new Rectangle(0, 0, width, height);
        return app;
    }
}
```

- [ ] **Step 2: Build the test project to confirm it compiles standalone**

Run: `dotnet build tests/CoreBankDemo.DemoRunner.Tests/CoreBankDemo.DemoRunner.Tests.csproj`
Expected: succeeds (this file has no dependents yet, so nothing else changes).

- [ ] **Step 3: Commit**

```bash
git add tests/CoreBankDemo.DemoRunner.Tests/Terminal/TerminalAppFactory.cs
git commit -m "test(demorunner): add TerminalAppFactory for headless IApplication instances"
```

---

### Task 2: Migrate TerminalCrashGuard.cs to instance-based disposal

**Files:**
- Modify: `CoreBankDemo.DemoRunner/Terminal/TerminalCrashGuard.cs`

**Interfaces:**
- Produces: `TerminalCrashGuard.AttachApplication(IApplication application)`,
  `TerminalCrashGuard.DetachApplication()` — consumed by Task 5 (`Program.cs`).
- `TerminalCrashGuard.Install(string artifactsDirectory)` (unchanged signature) and
  `TerminalCrashGuard.Report(...)` (unchanged) are still consumed by Task 5.

There is no existing dedicated test file for `TerminalCrashGuard` (it hooks
`AppDomain.UnhandledException`, which isn't practically unit-testable) — verification here is a
build check plus the manual smoke test in Task 9.

- [ ] **Step 1: Replace the `AppTerminal` alias with a plain `IApplication` using**

Change line 4 from:

```csharp
using AppTerminal = Terminal.Gui.App.Application;
```

to:

```csharp
using Terminal.Gui.App;
```

- [ ] **Step 2: Add the attach/detach static field and methods**

Add, right after the existing `_reported` field (after line 49, before the blank line at 50):

```csharp
    private static IApplication? _application;
```

Add two new internal methods, placed right after the closing brace of `Install` (which currently
ends the block that sets `TaskScheduler.UnobservedTaskException`, i.e. right after line 86's
closing `}`):

```csharp
    /// <summary>
    /// Records the live application instance so a crash on any thread can dispose it. Called by
    /// <c>Program.RunConsole</c> right after the instance is created.
    /// </summary>
    internal static void AttachApplication(IApplication application)
    {
        Volatile.Write(ref _application, application);
    }

    /// <summary>
    /// Clears the recorded instance once it has been disposed normally, so a later crash (e.g.
    /// during process teardown) does not try to dispose it a second time.
    /// </summary>
    internal static void DetachApplication()
    {
        Volatile.Write(ref _application, null);
    }
```

- [ ] **Step 3: Replace the obsolete `Shutdown()` call in `RestoreTerminal`**

In `RestoreTerminal()`, change:

```csharp
        try
        {
            AppTerminal.Shutdown();
        }
        catch (Exception)
        {
            // Expected when the driver is the thing that broke. The raw sequences below are
            // precisely the fallback for this case.
        }
```

to:

```csharp
        try
        {
            Volatile.Read(ref _application)?.Dispose();
        }
        catch (Exception)
        {
            // Expected when the driver is the thing that broke. The raw sequences below are
            // precisely the fallback for this case.
        }
```

- [ ] **Step 4: Build the DemoRunner project (errors expected — MainWindow/Program not yet migrated)**

Run: `dotnet build CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj`
Expected: this file alone compiles; the project as a whole may still show pre-existing CS0618
warnings from `Program.cs`/`MainWindow.cs` until Tasks 3-5 land. No new errors should originate
from `TerminalCrashGuard.cs` itself.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.DemoRunner/Terminal/TerminalCrashGuard.cs
git commit -m "refactor(demorunner): TerminalCrashGuard disposes an attached IApplication instance"
```

---

### Task 3: Migrate ConfirmationDialog.cs

**Files:**
- Modify: `CoreBankDemo.DemoRunner/Terminal/ConfirmationDialog.cs`

**Interfaces:**
- Consumes: nothing new from other tasks.
- Produces: `TerminalConfirmationService(IApplication app)` — consumed by Task 4 (`MainWindow.cs`'s
  default-confirmation-service branch).

- [ ] **Step 1: Replace the `AppTerminal` alias and update `TerminalConfirmationService`**

Change:

```csharp
using CoreBankDemo.DemoRunner.Application;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

internal sealed record ConfirmationRequest(string Title, string Command, IReadOnlyList<string> Instances);

internal interface IConfirmationService
{
    bool Confirm(ConfirmationRequest request);
}

#pragma warning disable CS0618
internal sealed class TerminalConfirmationService : IConfirmationService
{
    public bool Confirm(ConfirmationRequest request)
    {
        var dialog = new DestructiveConfirmationDialog(request);
        dialog.FocusCancel();
        AppTerminal.Run(dialog);
        return dialog.Result == true;
    }
}
```

to:

```csharp
using CoreBankDemo.DemoRunner.Application;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CoreBankDemo.DemoRunner.Terminal;

internal sealed record ConfirmationRequest(string Title, string Command, IReadOnlyList<string> Instances);

internal interface IConfirmationService
{
    bool Confirm(ConfirmationRequest request);
}

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

(Note: the `#pragma warning disable CS0618` right before `TerminalConfirmationService` is deleted
here; the matching `#pragma warning restore CS0618` at the very end of the file, after
`DestructiveConfirmationDialog`'s closing brace, is deleted in the next step since
`DestructiveConfirmationDialog` itself doesn't reference the obsolete API — only
`TerminalConfirmationService` did.)

- [ ] **Step 2: Remove the trailing `#pragma warning restore CS0618`**

Delete the last line of the file:

```csharp
#pragma warning restore CS0618
```

- [ ] **Step 3: Build the DemoRunner project**

Run: `dotnet build CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj`
Expected: `ConfirmationDialog.cs` compiles cleanly on its own. `MainWindow.cs` will fail to build
at this point because its default-confirmation-service branch (`new TerminalConfirmationService()`,
no argument) no longer matches this constructor — that's fixed in Task 4, which comes next. If you
see that specific error, it confirms this task is correct; don't fix it here.

- [ ] **Step 4: Commit**

```bash
git add CoreBankDemo.DemoRunner/Terminal/ConfirmationDialog.cs
git commit -m "refactor(demorunner): TerminalConfirmationService takes an IApplication instance"
```

---

### Task 4: Migrate MainWindow.cs

**Files:**
- Modify: `CoreBankDemo.DemoRunner/Terminal/MainWindow.cs`

**Interfaces:**
- Consumes: `TerminalConfirmationService(IApplication app)` from Task 3.
- Produces: `MainWindow(IApplication app, OperatorConsoleController controller, Func<Task>
  onExitRequested, ThemeMode theme = ThemeMode.Dark)` (public) and
  `MainWindow(IApplication app, OperatorConsoleController controller, Func<Task> onExitRequested,
  IConfirmationService? confirmation, bool startPolling, bool marshalUpdates = true,
  TimeProvider? time = null, ThemeMode theme = ThemeMode.Dark)` (internal) — both gain a new,
  required, leading `IApplication app` parameter. Consumed by Task 5 (`Program.cs`) and Tasks 6-7
  (tests).

- [ ] **Step 1: Replace the top-of-file alias and the class-wide pragma**

Change:

```csharp
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner.Terminal;

#pragma warning disable CS0618
public sealed class MainWindow : Window
{
```

to:

```csharp
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CoreBankDemo.DemoRunner.Terminal;

public sealed class MainWindow : Window
{
```

- [ ] **Step 2: Delete the trailing `#pragma warning restore CS0618`**

`MainWindow.cs` currently ends its class body with `#pragma warning restore CS0618` right before
the file's final closing brace. Delete that line.

- [ ] **Step 3: Add narrow pragmas around the 5 pre-existing, unrelated `TextView` warning sites**

These are independent of this migration (see Global Constraints / spec Non-goals) — wrap only
these three spots so they keep compiling silently:

Around the three field declarations (originally lines 255-257):

```csharp
#pragma warning disable CS0618 // TextView is obsolete in favor of a separate EditorView control; out of scope here (see migration spec).
    private readonly TextView _evidenceHeader = new() { ReadOnly = true, WordWrap = false };
    private readonly TextView _evidenceRequest = new() { ReadOnly = true, WordWrap = false };
    private readonly TextView _evidenceResponse = new() { ReadOnly = true, WordWrap = false };
#pragma warning restore CS0618
```

Around `SetPaneText`'s signature (originally line 1871) — only the signature line needs the
pragma, since `TextView` is only *named* there, not in the method body:

```csharp
#pragma warning disable CS0618 // TextView is obsolete in favor of a separate EditorView control; out of scope here (see migration spec).
    private static void SetPaneText(TextView pane, string text)
#pragma warning restore CS0618
    {
```

Around `EvidenceResponsePane` (originally line 2611):

```csharp
#pragma warning disable CS0618 // TextView is obsolete in favor of a separate EditorView control; out of scope here (see migration spec).
    internal TextView EvidenceResponsePane => _evidenceResponse;
#pragma warning restore CS0618
```

- [ ] **Step 4: Add the `_app` field and stop `_repaints` being a field initializer**

Change:

```csharp
    private readonly UiRepaintCoalescer _repaints = new(AppTerminal.Invoke);
```

to:

```csharp
    private readonly IApplication _app;
    private readonly UiRepaintCoalescer _repaints;
```

(Field initializers can't reference constructor parameters — `_repaints` needs `app.Invoke`, which
only exists once `app` is a constructor parameter, so its construction moves into the constructor
body in the next step.)

- [ ] **Step 5: Add `app` as the leading parameter on both constructors, and assign the new fields**

Change:

```csharp
    public MainWindow(OperatorConsoleController controller, Func<Task> onExitRequested, ThemeMode theme = ThemeMode.Dark)
        : this(controller, onExitRequested, null, true, theme: theme)
    {
    }

    internal MainWindow(
        OperatorConsoleController controller,
        Func<Task> onExitRequested,
        IConfirmationService? confirmation,
        bool startPolling,
        bool marshalUpdates = true,
        TimeProvider? time = null,
        ThemeMode theme = ThemeMode.Dark)
    {
        OperatorTheme.Register(theme);
        _controller = controller;
        _time = time ?? TimeProvider.System;
        _onExitRequested = onExitRequested;
        _confirmation = confirmation ?? new TerminalConfirmationService();
        _marshalUpdates = marshalUpdates;
```

to:

```csharp
    public MainWindow(IApplication app, OperatorConsoleController controller, Func<Task> onExitRequested, ThemeMode theme = ThemeMode.Dark)
        : this(app, controller, onExitRequested, null, true, theme: theme)
    {
    }

    internal MainWindow(
        IApplication app,
        OperatorConsoleController controller,
        Func<Task> onExitRequested,
        IConfirmationService? confirmation,
        bool startPolling,
        bool marshalUpdates = true,
        TimeProvider? time = null,
        ThemeMode theme = ThemeMode.Dark)
    {
        OperatorTheme.Register(theme);
        _app = app;
        _repaints = new(_app.Invoke);
        _controller = controller;
        _time = time ?? TimeProvider.System;
        _onExitRequested = onExitRequested;
        _confirmation = confirmation ?? new TerminalConfirmationService(_app);
        _marshalUpdates = marshalUpdates;
```

Everything after `_marshalUpdates = marshalUpdates;` in the constructor body is unchanged.

- [ ] **Step 6: Replace the obsolete `Invoke` call in `RunOnUiThread`**

Change:

```csharp
    private void RunOnUiThread(Action action)
    {
        if (_marshalUpdates)
        {
            AppTerminal.Invoke(action);
        }
        else
        {
            action();
        }
    }
```

to:

```csharp
    private void RunOnUiThread(Action action)
    {
        if (_marshalUpdates)
        {
            _app.Invoke(action);
        }
        else
        {
            action();
        }
    }
```

- [ ] **Step 7: Build the DemoRunner project**

Run: `dotnet build CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj`
Expected: `MainWindow.cs` and `ConfirmationDialog.cs` now compile together cleanly. `Program.cs`
will fail (it still calls the old 3-argument public `MainWindow(...)` constructor) — that's Task 5,
next. If that's the only remaining error, this task is correct.

- [ ] **Step 8: Commit**

```bash
git add CoreBankDemo.DemoRunner/Terminal/MainWindow.cs
git commit -m "refactor(demorunner): MainWindow takes an IApplication instance"
```

---

### Task 5: Migrate Program.cs

**Files:**
- Modify: `CoreBankDemo.DemoRunner/Program.cs`

**Interfaces:**
- Consumes: `MainWindow(IApplication app, ...)` from Task 4,
  `TerminalCrashGuard.AttachApplication`/`DetachApplication` from Task 2.

- [ ] **Step 1: Remove the method-wide pragma and create the instance (not `using`-scoped)**

Change:

```csharp
#pragma warning disable CS0618
    private static int RunConsole(OperatorConsoleController controller, ThemeMode theme)
    {
        Exception? crash = null;
        AppTerminal.Init();

        // Terminal.Gui's own clipboard shells out to xclip, which exists in the sandbox
        // but has no display to hand the text to, so Ctrl+C silently copied nothing.
        // OSC 52 asks the terminal emulator itself instead; see Osc52Clipboard.
        var clipboard = new Osc52Clipboard(
            Console.Out,
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("TMUX"));
        if (AppTerminal.Driver is { } driver)
        {
            driver.Clipboard = clipboard;
        }

        var window = new MainWindow(controller, async () =>
        {
            await controller.ShutdownAsync(CancellationToken.None);
            AppTerminal.RequestStop();
        }, theme);
```

to:

```csharp
    private static int RunConsole(OperatorConsoleController controller, ThemeMode theme)
    {
        Exception? crash = null;
        var app = AppTerminal.Create().Init();
        TerminalCrashGuard.AttachApplication(app);

        // Terminal.Gui's own clipboard shells out to xclip, which exists in the sandbox
        // but has no display to hand the text to, so Ctrl+C silently copied nothing.
        // OSC 52 asks the terminal emulator itself instead; see Osc52Clipboard.
        var clipboard = new Osc52Clipboard(
            Console.Out,
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("TMUX"));
        if (app.Driver is { } driver)
        {
            driver.Clipboard = clipboard;
        }

        var window = new MainWindow(app, controller, async () =>
        {
            await controller.ShutdownAsync(CancellationToken.None);
            app.RequestStop();
        }, theme);
```

(`AppTerminal` stays as the alias for `Terminal.Gui.App.Application` — `Create()` is a new,
non-obsolete static factory method, so keeping the alias for that one call is fine; no new
`using` needed since `app` is always referred to via `var`, never by naming `IApplication`
explicitly in this file.)

- [ ] **Step 2: Replace the remaining `AppTerminal.X` calls inside the try block**

Change:

```csharp
            AppTerminal.Run(window, errorHandler: exception =>
            {
                crash ??= exception;

                // Stopped rather than resumed: a console that keeps running after an unhandled
                // fault is a console that may now be showing something untrue.
                AppTerminal.RequestStop();
                return true;
            });
```

to:

```csharp
            app.Run(window, errorHandler: exception =>
            {
                crash ??= exception;

                // Stopped rather than resumed: a console that keeps running after an unhandled
                // fault is a console that may now be showing something untrue.
                app.RequestStop();
                return true;
            });
```

- [ ] **Step 3: Replace `Shutdown()` with `Dispose()` in the `finally` block, and detach the crash guard**

Change:

```csharp
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            try
            {
                controller.ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Could not stop the owned AppHost cleanly: {ex.Message}");
            }
            AppTerminal.Shutdown();
        }
    }
#pragma warning restore CS0618
```

to:

```csharp
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            try
            {
                controller.ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"Could not stop the owned AppHost cleanly: {ex.Message}");
            }
            app.Dispose();
            TerminalCrashGuard.DetachApplication();
        }
    }
```

This preserves the exact original ordering the comments call out: disposal happens in `finally`,
before the post-`try` `TerminalCrashGuard.Report(crash, ...)` call below it — never after, which
is why this is deliberately *not* a top-level `using var app = ...;` (see spec).

- [ ] **Step 4: Build and run the full solution**

Run: `dotnet build CoreBankDemo.sln`
Expected: still fails — `MainWindowTests.cs`, `EvidencePaneRenderTests.cs`, and
`NavigationRailRenderTests.cs` (Tasks 6-7) still call the old constructor shapes. Confirm the only
remaining errors are in those three test files, and that `CoreBankDemo.DemoRunner.csproj` itself
now builds clean with 0 warnings:

Run: `dotnet build CoreBankDemo.DemoRunner/CoreBankDemo.DemoRunner.csproj`
Expected: `Build succeeded`, 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add CoreBankDemo.DemoRunner/Program.cs
git commit -m "refactor(demorunner): Program owns and threads the IApplication instance"
```

---

### Task 6: Update MainWindowTests.cs's 4 constructor call sites

**Files:**
- Modify: `tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowTests.cs`

**Interfaces:**
- Consumes: `MainWindow(IApplication app, ...)` from Task 4.

None of these 4 call sites reference `AppTerminal`/`Application` today — they all pass
`marshalUpdates: false`, so they never render and never actually invoke `app.Invoke` at runtime.
They still need a valid `IApplication` instance to satisfy the constructor (it's captured into
`_repaints`'s delegate at construction time regardless of whether it's ever called) —
`Application.Create()` alone (no `Init()`) is enough; delegate creation from an instance method
just binds `this`, it doesn't require the driver to be running.

- [ ] **Step 1: Add the missing using directive**

Add to the top of the file (alphabetically among the existing `using` lines, e.g. right after
`using System.Drawing;`):

```csharp
using Terminal.Gui.App;
```

- [ ] **Step 2: Update `RefreshAndOrderlyExit_RunThroughActualMainWindowPaths`**

Change:

```csharp
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        var exited = false;
        using var window = new MainWindow(
            controller,
            () =>
            {
                exited = true;
                return Task.CompletedTask;
            },
            new FakeConfirmationService(),
            startPolling: false,
            marshalUpdates: false);
```

to:

```csharp
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        var exited = false;
        using var app = Application.Create();
        using var window = new MainWindow(
            app,
            controller,
            () =>
            {
                exited = true;
                return Task.CompletedTask;
            },
            new FakeConfirmationService(),
            startPolling: false,
            marshalUpdates: false);
```

- [ ] **Step 3: Update `Window_ResolvesTheRequestedPalette`**

Change:

```csharp
        var controller = new OperatorHarness().CreateController();
        using var window = new MainWindow(
            controller,
            () => Task.CompletedTask,
            new FakeConfirmationService(),
            startPolling: false,
            marshalUpdates: false,
            theme: mode);
```

to:

```csharp
        var controller = new OperatorHarness().CreateController();
        using var app = Application.Create();
        using var window = new MainWindow(
            app,
            controller,
            () => Task.CompletedTask,
            new FakeConfirmationService(),
            startPolling: false,
            marshalUpdates: false,
            theme: mode);
```

- [ ] **Step 4: Update `RefreshAndQuit_SurviveTheRemovedStatusBarAsWindowWideKeys`**

Change:

```csharp
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        var exited = false;
        using var window = new MainWindow(
            controller,
            () => { exited = true; return Task.CompletedTask; },
            null,
            startPolling: false,
            marshalUpdates: false);

        window.HandleKeyForTest(Key.R).Should().BeTrue();
```

to:

```csharp
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        var exited = false;
        using var app = Application.Create();
        using var window = new MainWindow(
            app,
            controller,
            () => { exited = true; return Task.CompletedTask; },
            null,
            startPolling: false,
            marshalUpdates: false);

        window.HandleKeyForTest(Key.R).Should().BeTrue();
```

- [ ] **Step 5: Update `QuitButton_ClickAlwaysTerminatesTheApplication`**

Change:

```csharp
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        var exited = false;
        using var window = new MainWindow(
            controller,
            () => { exited = true; return Task.CompletedTask; },
            null,
            startPolling: false,
            marshalUpdates: false);

        window.QuitButton.InvokeCommand(Command.Accept);
```

to:

```csharp
        var harness = new OperatorHarness();
        var controller = harness.CreateController();
        var exited = false;
        using var app = Application.Create();
        using var window = new MainWindow(
            app,
            controller,
            () => { exited = true; return Task.CompletedTask; },
            null,
            startPolling: false,
            marshalUpdates: false);

        window.QuitButton.InvokeCommand(Command.Accept);
```

- [ ] **Step 6: Build and run this test file**

Run: `dotnet build tests/CoreBankDemo.DemoRunner.Tests/CoreBankDemo.DemoRunner.Tests.csproj`
Expected: still fails overall (Tasks 7 not done yet) unless `EvidencePaneRenderTests.cs`/
`NavigationRailRenderTests.cs` are also already broken the same way — that's expected. Search the
build output specifically for `MainWindowTests.cs` and confirm it has zero errors of its own.

- [ ] **Step 7: Commit**

```bash
git add tests/CoreBankDemo.DemoRunner.Tests/Terminal/MainWindowTests.cs
git commit -m "test(demorunner): MainWindowTests passes an IApplication instance"
```

---

### Task 7: Migrate EvidencePaneRenderTests.cs and NavigationRailRenderTests.cs

**Files:**
- Modify: `tests/CoreBankDemo.DemoRunner.Tests/Terminal/EvidencePaneRenderTests.cs`
- Modify: `tests/CoreBankDemo.DemoRunner.Tests/Terminal/NavigationRailRenderTests.cs`

**Interfaces:**
- Consumes: `TerminalAppFactory.CreateHeadless(int, int)` from Task 1,
  `MainWindow(IApplication app, ...)` from Task 4.

Both files are small enough to replace in full.

- [ ] **Step 1: Replace the full contents of `EvidencePaneRenderTests.cs`**

```csharp
using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>
/// The Details pane is asserted against the actual screen buffer rather than against
/// <c>TextView.Text</c>, for the same reason the navigation rail is: a pane whose scroll offset
/// is one column out holds exactly the right text and draws the wrong thing. Every geometry and
/// text assertion in <c>MainWindowTests</c> passed while the RESPONSE column was rendering as
/// <c>ESPONSE</c> on a real terminal.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class EvidencePaneRenderTests
{
    /// <summary>
    /// Moving the caret in a column, reading another record and coming back must not leave the
    /// pane scrolled. <c>TextView</c> keeps its viewport offset across an assignment to
    /// <c>Text</c>, so the caret left behind by an arrow key or a click dragged the next record
    /// one column left: every line lost its first character and a line holding nothing but
    /// <c>{</c> disappeared entirely.
    /// </summary>
    [Fact]
    public async Task ReadingAnotherRecordAndComingBack_DrawsTheResponseColumnFromItsFirstCharacter()
    {
        using var app = TerminalAppFactory.CreateHeadless(140, 40);
        OperatorTheme.Register(ThemeMode.Dark);
        using var window = await EvidenceWindowAsync(app);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, 140, 40);
        window.HandleKeyForTest(Key.D3);
        window.ResizeForTest(140, 40);
        window.RenderForTest();
        app.LayoutAndDraw(true);

        // The operator reads into the column, then walks the list and comes back.
        window.EvidenceResponsePane.SetFocus();
        window.EvidenceResponsePane.NewKeyDownEvent(Key.End);
        window.EvidenceList.SelectedItem = 1;
        window.RenderForTest();
        app.LayoutAndDraw(true);
        window.EvidenceList.SelectedItem = 0;
        window.RenderForTest();
        app.LayoutAndDraw(true);

        window.EvidenceResponsePane.Viewport.Location.X.Should().Be(
            0, "a new record is read from its first column, not from the last one's offset");

        var column = ResponseColumnAsDrawn(app, window);
        column[0].Should().Be("RESPONSE");
        column.Should().Contain("HTTP 202 Accepted");
        column.Should().Contain("{", "a line holding only an opening brace is the first casualty of a one-column scroll");
    }

    /// <summary>
    /// The payload draws on the workspace's own surface, at the workspace's own text colour.
    /// A <see cref="Scheme"/> built from a single <see cref="Attribute"/> lets Terminal.Gui
    /// blend the roles it was not given, and a <c>ReadOnly</c> <c>TextView</c> picks the blend:
    /// the pane rendered <c>#C9D3DE</c> on <c>#536B88</c> — about 3.4:1 — while every geometry,
    /// text and border assertion passed. The border carries the separation; the fill does not.
    /// </summary>
    [Theory]
    [InlineData(ThemeMode.Dark, "#E8ECF1", "#0B1220")]
    [InlineData(ThemeMode.Light, "#1F2328", "#FFFFFF")]
    public async Task ThePayloadDrawsOnTheWorkspacesOwnSurface(ThemeMode mode, string foreground, string background)
    {
        using var app = TerminalAppFactory.CreateHeadless(140, 40);
        // The window registers the palette itself, so it is the one that must be told.
        using var window = await EvidenceWindowAsync(app, mode);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, 140, 40);
        window.HandleKeyForTest(Key.D3);
        window.ResizeForTest(140, 40);
        window.RenderForTest();
        app.LayoutAndDraw(true);

        var origin = window.EvidenceResponsePane.FrameToScreen();
        var cell = app.Driver!.Contents![origin.Y, origin.X];

        cell.Grapheme.Should().Be("R", "this is the first cell of the RESPONSE column");
        cell.Attribute!.Value.Foreground.Should().Be(new Color(foreground));
        cell.Attribute!.Value.Background.Should().Be(
            new Color(background),
            "the pane is the same surface as the rest of the workspace, not a dimmed variant");
    }

    /// <summary>A console holding one payment record that carries a full HTTP exchange.</summary>
    private static async Task<MainWindow> EvidenceWindowAsync(IApplication app, ThemeMode theme = ThemeMode.Dark)
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending",
            Body, null, TimeSpan.FromMilliseconds(5),
            new HttpExchange(
                "POST",
                "http://127.0.0.1:5294/api/payments",
                [new EvidenceHeader("Idempotency-Key", "demo-key-001")],
                Body,
                202,
                "Accepted",
                [new EvidenceHeader("Content-Type", "application/json")],
                Body)));
        await controller.SubmitPaymentAsync(
            new PaymentRequest("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Standard),
            IdempotencyMode.Generated, null, CancellationToken.None);
        return new MainWindow(
            app, controller, () => Task.CompletedTask, null, startPolling: false, marshalUpdates: false, theme: theme);
    }

    private const string Body =
        """{"transactionId":"tx-8821","status":"Pending","note":"long enough that the column can scroll"}""";

    /// <summary>Reads the RESPONSE column's cells straight off the driver's screen buffer.</summary>
    private static List<string> ResponseColumnAsDrawn(IApplication app, MainWindow window)
    {
        var pane = window.EvidenceResponsePane;
        var origin = pane.FrameToScreen();
        var contents = app.Driver!.Contents!;
        var lines = new List<string>();
        for (var row = 0; row < pane.Frame.Height; row++)
        {
            var line = new StringBuilder();
            for (var offset = 0; offset < pane.Frame.Width; offset++)
            {
                var grapheme = contents[origin.Y + row, origin.X + offset].Grapheme;
                line.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
            }

            lines.Add(line.ToString().TrimEnd());
        }

        return lines;
    }
}
```

- [ ] **Step 2: Replace the full contents of `NavigationRailRenderTests.cs`**

```csharp
using System.Text;
using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Terminal;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>
/// The rail is asserted against the actual screen buffer rather than against view geometry,
/// because geometry is what let an empty rail ship. A Button reserves a one-cell right/bottom
/// Margin for its drop shadow; pinning the row to a single line left <c>Frame.Height</c> at 1
/// and <c>Viewport.Height</c> at 0, so every row drew nothing while <c>Frame</c>,
/// <c>Visible</c> and <c>Text</c> all still read exactly as a passing test expected. Only the
/// rendered cells tell the truth about a terminal UI.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class NavigationRailRenderTests
{
    private const int RailInnerWidth = 14;

    [Fact]
    public void EveryWorkspaceRendersOnItsOwnRow_LeftAlignedAndUnwrapped()
    {
        var rows = RenderRail();

        rows.Should().ContainInOrder(
            "▸1 Operations",
            "2 Resources",
            "3 Evidence",
            "4 Tests",
            "5 Faults");
    }

    /// <summary>
    /// The shortcut digits form a column the operator aims at mid-sentence, so every row starts
    /// at the same cell. An undecorated Button centres its caption by default, which put the
    /// five rows at five different indents.
    /// </summary>
    [Fact]
    public void EveryRowStartsAtTheSameColumn()
    {
        var rows = RenderRail();
        var indents = rows
            .Where(row => row.TrimStart().Length > 0)
            .Select(row => row.Length - row.TrimStart().Length)
            .Distinct();

        indents.Should().ContainSingle("a ragged rail has no column of shortcuts to aim at");
    }

    /// <summary>Reads the rail's cells straight out of the driver's output buffer.</summary>
    private static List<string> RenderRail()
    {
        using var app = TerminalAppFactory.CreateHeadless(100, 30);
        OperatorTheme.Register(ThemeMode.Dark);
        var controller = new OperatorHarness().CreateController();
        using var window = new MainWindow(
            app, controller, () => Task.CompletedTask, null, startPolling: false, marshalUpdates: false);
        app.Begin(window);
        window.Frame = new System.Drawing.Rectangle(0, 0, 100, 30);
        app.LayoutAndDraw(true);

        var contents = app.Driver!.Contents!;
        var rows = new List<string>();
        // Rail rows start below the window border, the topology bar and the rail's own
        // border; column 2 is the first cell inside the rail.
        for (var row = 3; row < 3 + 10; row++)
        {
            var line = new StringBuilder();
            for (var column = 2; column < 2 + RailInnerWidth; column++)
            {
                var grapheme = contents[row, column].Grapheme;
                line.Append(string.IsNullOrEmpty(grapheme) || grapheme == "\0" ? " " : grapheme);
            }

            var text = line.ToString().TrimEnd();
            if (text.Trim().Length > 0)
            {
                rows.Add(text.Trim());
            }
        }

        return rows;
    }
}
```

- [ ] **Step 3: Build and run the full solution**

Run: `dotnet tool restore && dotnet restore CoreBankDemo.sln && dotnet build CoreBankDemo.sln --no-restore`
Expected: `Build succeeded`, **0 warnings**, 0 errors — this is the first point in the plan where
the whole solution compiles clean. If any warnings remain, stop and diagnose before continuing;
don't proceed to Task 8 with an unexplained warning.

Run: `dotnet test CoreBankDemo.sln --no-build`
Expected: all 8 test projects `Passed!`, in particular `CoreBankDemo.DemoRunner.Tests` at 687
tests (same count as before this plan — no test was added or removed, only their setup changed).

- [ ] **Step 4: Commit**

```bash
git add tests/CoreBankDemo.DemoRunner.Tests/Terminal/EvidencePaneRenderTests.cs tests/CoreBankDemo.DemoRunner.Tests/Terminal/NavigationRailRenderTests.cs
git commit -m "test(demorunner): render tests use TerminalAppFactory and an IApplication instance"
```

---

### Task 8: Bump Terminal.Gui to 2.5.0 and amend ADR-015

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `docs/adr/ADR-015-presentation-safe-terminal-demo-console.md`

The code migration (Tasks 1-7) already builds and passes at 0 warnings against the *current*
2.4.17 — `IApplication`/`Application.Create()` already exist there (confirmed via reflection
against the actual 2.4.17 assembly). This task takes the version bump that was deliberately
deferred out of the earlier mechanical package-update round specifically to land with this
migration, and re-verifies against it as a separate, bisectable step.

- [ ] **Step 1: Bump the package version**

In `Directory.Packages.props`, change:

```xml
    <!-- Story 7.4: Terminal.Gui is the sole UI package for the standalone
         CoreBankDemo.DemoRunner presentation console (ADR-015). Pinned centrally
         so exactly one version is used repo-wide. -->
    <PackageVersion Include="Terminal.Gui" Version="2.4.17" />
```

to:

```xml
    <!-- Story 7.4: Terminal.Gui is the sole UI package for the standalone
         CoreBankDemo.DemoRunner presentation console (ADR-015). Pinned centrally
         so exactly one version is used repo-wide. -->
    <PackageVersion Include="Terminal.Gui" Version="2.5.0" />
```

- [ ] **Step 2: Amend ADR-015**

In `docs/adr/ADR-015-presentation-safe-terminal-demo-console.md`, in the "Terminal.Gui as the
pinned TUI adapter" section, change:

```markdown
### Terminal.Gui as the pinned TUI adapter

Terminal.Gui's stable v2 line is the only UI package, pinned centrally at **2.4.17** in `Directory.Packages.props` (one version for the whole repo, consistent with existing central package management). The package boundary stays thin:
```

to:

```markdown
### Terminal.Gui as the pinned TUI adapter

Terminal.Gui's stable v2 line is the only UI package, pinned centrally at **2.5.0** in `Directory.Packages.props` (one version for the whole repo, consistent with existing central package management). **Amended (Terminal.Gui v2 instance migration, 2026-09-12):** bumped from 2.4.17. The static `Terminal.Gui.App.Application` class this console originally used is obsolete and slated for removal; the console now owns an instance-based `IApplication` created in `Program.cs` and threaded explicitly to the views and services that need it. See `docs/superpowers/specs/2026-09-12-demorunner-terminalgui-v2-migration-design.md`. The package boundary stays thin:
```

- [ ] **Step 3: Full clean rebuild and test against 2.5.0**

Run:
```bash
find . -type d \( -name bin -o -name obj \) -not -path "*/node_modules/*" | xargs rm -rf
dotnet tool restore
dotnet restore CoreBankDemo.sln
dotnet build CoreBankDemo.sln --no-restore
```
Expected: `Build succeeded`, 0 warnings, 0 errors, against 2.5.0 specifically (a clean rebuild
rules out anything masked by incremental build state).

Run: `dotnet test CoreBankDemo.sln --no-build`
Expected: all 8 test projects pass, same counts as Task 7's verification.

- [ ] **Step 4: Commit**

```bash
git add Directory.Packages.props docs/adr/ADR-015-presentation-safe-terminal-demo-console.md
git commit -m "chore(demorunner): bump Terminal.Gui to 2.5.0, amend ADR-015"
```

---

### Task 9: Manual smoke test

**Files:** none (verification only).

Automated tests can't exercise `TerminalCrashGuard`'s actual `AppDomain.UnhandledException` path
(that's the one place this migration touches real thread/lifetime semantics, not just call-site
substitution), so this is a manual check before considering the migration done.

- [ ] **Step 1: Launch the console and confirm ordinary start/quit**

Run: `dotnet run --project CoreBankDemo.DemoRunner`
Expected: the console starts normally (Operations workspace visible), and pressing `Shift+Q`
(or clicking Quit) exits cleanly, returning the terminal to a normal shell prompt with no leftover
alternate-screen artifacts.

- [ ] **Step 2: Confirm a forced crash still restores the terminal**

With the console running, from another terminal find its PID (`pgrep -f CoreBankDemo.DemoRunner`)
and send it a signal that reaches `AppDomain.UnhandledException` indirectly is hard to force
externally — instead, temporarily add a `throw new InvalidOperationException("smoke test");` at
the top of `OnStateChanged` in `MainWindow.cs`, trigger any state change (e.g. press a key that
refreshes a workspace), confirm the terminal is restored and the crash report file appears under
`.demo-runner-artifacts/`, then **revert the temporary throw** (`git checkout -- CoreBankDemo.DemoRunner/Terminal/MainWindow.cs` if nothing else uncommitted is pending in that file, or a manual undo).

Expected: terminal returns to a normal shell prompt (not a dead alternate-screen view), and a
`crash-<timestamp>.log` file exists under the repository's `.demo-runner-artifacts/` directory
containing the exception detail.

- [ ] **Step 3: Report results**

No commit for this task — it's verification only. If both checks pass, the migration is
complete: `dotnet build CoreBankDemo.sln` shows 0 warnings, `dotnet test CoreBankDemo.sln` passes
in full, and the console behaves identically to before under both normal and crash exits.
