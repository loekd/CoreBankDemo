using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace CoreBankDemo.DemoRunner.Terminal;

internal static class OperatorTheme
{
    internal const string BaseScheme = "CoreBankCockpit";
    internal const string RailScheme = "CoreBankRail";
    internal const string ActionScheme = "CoreBankAction";
    internal const string DestructiveScheme = "CoreBankDestructive";
    internal const string OverlayScheme = "CoreBankOverlay";

    /// <summary>
    /// The shared lock-exempt signature: <c>accent-teal</c> on <c>surface-base</c> — an
    /// outline with no fill, deliberately distinct from the filled teal primary action and
    /// from a dimmed disabled control. Worn by exactly the controls that stay live while the
    /// single-action-in-flight lock is held: burst Cancel, every fault slider, and panic-off.
    /// No control may wear it without being genuinely lock-exempt.
    /// </summary>
    internal const string LockExemptScheme = "CoreBankLockExempt";

    private static bool _registered;

    internal static void Register()
    {
        if (_registered)
        {
            return;
        }

        SchemeManager.AddScheme(BaseScheme, Scheme("#E8ECF1", "#0B1220"));
        SchemeManager.AddScheme(RailScheme, Scheme("#E8ECF1", "#132036"));
        SchemeManager.AddScheme(ActionScheme, Scheme("#0B1220", "#2FB7A8"));
        SchemeManager.AddScheme(DestructiveScheme, Scheme("#E06862", "#132036"));
        SchemeManager.AddScheme(OverlayScheme, Scheme("#E8ECF1", "#182A44"));
        SchemeManager.AddScheme(LockExemptScheme, Scheme("#2FB7A8", "#0B1220"));
        _registered = true;
    }

    internal static void Apply(View view, string schemeName) => view.SchemeName = schemeName;

    private static Scheme Scheme(string foreground, string background) =>
        new(new global::Terminal.Gui.Drawing.Attribute(new Color(foreground), new Color(background)));
}
