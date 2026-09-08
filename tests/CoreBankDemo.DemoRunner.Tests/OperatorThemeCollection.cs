using Xunit;

namespace CoreBankDemo.DemoRunner.Tests;

/// <summary>
/// Terminal.Gui's SchemeManager is process-global and OperatorTheme registers into it, so any
/// test that constructs a MainWindow (which registers the dark palette) or flips the palette
/// directly shares one piece of mutable process state. xUnit runs each test class as its own
/// collection in parallel by default, which would let one class re-register dark in the middle
/// of another's light-mode assertion. Every test class that touches the theme joins this
/// collection so they run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OperatorThemeCollection
{
    public const string Name = "OperatorTheme";
}
