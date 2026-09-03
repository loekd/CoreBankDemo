namespace CoreBankDemo.DemoRunner.Application;

public enum TerminalLayoutMode
{
    Preferred,
    Compact,
    BelowMinimum,
}

public static class InteractionPolicies
{
    public static bool ConfirmsDestructiveAction(char key) => key == 'Y';

    public static TerminalLayoutMode LayoutFor(int width, int height)
    {
        if (width < 80 || height < 24)
        {
            return TerminalLayoutMode.BelowMinimum;
        }

        return width < 100 || height < 30
            ? TerminalLayoutMode.Compact
            : TerminalLayoutMode.Preferred;
    }
}
