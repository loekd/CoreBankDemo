using System.Drawing;
using Terminal.Gui.App;
using AppTerminal = Terminal.Gui.App.Application;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>Builds a headless, disposable IApplication instance sized for deterministic render tests.</summary>
internal static class TerminalAppFactory
{
    internal static IApplication CreateHeadless(int width, int height)
    {
        var app = AppTerminal.Create().Init("dotnet");
        app.Screen = new Rectangle(0, 0, width, height);
        return app;
    }
}
