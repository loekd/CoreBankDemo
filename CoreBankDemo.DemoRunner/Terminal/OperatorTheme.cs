using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;

namespace CoreBankDemo.DemoRunner.Terminal;

/// <summary>
/// Which surface the console paints itself on. Dark is the default cockpit identity;
/// Light exists for projector work, where a near-black canvas washes out.
/// </summary>
public enum ThemeMode
{
    Dark,
    Light,
}

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

    /// <summary>
    /// The active workspace's rail row: <c>text-on-accent</c> on <c>accent-navy</c>
    /// (DESIGN.md, Nav rail item). The <c>▸</c> marker is what actually carries the active
    /// state — this fill is reinforcement, so the rail still reads correctly in a monochrome
    /// terminal. The foreground is <c>text-on-accent</c> rather than <c>text-primary</c> on
    /// purpose: <c>text-primary</c> inverts between modes while <c>accent-navy</c> deliberately
    /// does not, so a straight token swap would put dark text on mid-navy at 2.37:1 in light
    /// mode. As written it measures 5.62:1 dark and 6.66:1 light.
    /// </summary>
    internal const string NavigationActiveScheme = "CoreBankNavActive";

    /// <summary>
    /// The eight values every scheme is derived from. Both modes fill the same shape, so the
    /// scheme table below is written once and the palette is the only thing that swaps —
    /// no view needs to know which mode is in force.
    /// </summary>
    private sealed record Palette(
        string SurfaceBase,
        string SurfaceRaised,
        string SurfaceOverlay,
        string TextPrimary,
        string AccentTeal,
        string AccentNavy,
        string TextOnAccent,
        string StateFailedOnRaised);

    /// <summary>
    /// The original cockpit palette: a restrained teal-on-navy control room.
    /// </summary>
    private static readonly Palette DarkPalette = new(
        SurfaceBase: "#0B1220",
        SurfaceRaised: "#132036",
        SurfaceOverlay: "#182A44",
        TextPrimary: "#E8ECF1",
        AccentTeal: "#2FB7A8",
        AccentNavy: "#325F8C",
        TextOnAccent: "#E8ECF1",
        StateFailedOnRaised: "#E06862");

    /// <summary>
    /// The projector palette, anchored to the Ghostty "GitHub Light Default" theme this is
    /// presented in: its exact background (#FFFFFF) and foreground (#1F2328), so the console
    /// does not read as a foreign rectangle inside the terminal that hosts it.
    /// <para>
    /// These are not the dark hexes on a light background. Every dark accent measured between
    /// 2.17:1 and 3.96:1 on white — amber and green were effectively invisible — so the light
    /// mode carries its own values, each darkened to clear 4.5:1 on every surface it is drawn
    /// on and to reach roughly 6:1 or better on the canvas, which is the reading that matters
    /// from the back of a room. See DESIGN.md Colors for the full measured table.
    /// </para>
    /// </summary>
    private static readonly Palette LightPalette = new(
        SurfaceBase: "#FFFFFF",
        SurfaceRaised: "#EAEEF2",
        SurfaceOverlay: "#D8DEE4",
        TextPrimary: "#1F2328",
        AccentTeal: "#136066",
        // accent-navy is the one accent that deliberately does not invert between modes.
        AccentNavy: "#325F8C",
        TextOnAccent: "#FFFFFF",
        StateFailedOnRaised: "#A40E26");

    private static bool _registered;
    private static ThemeMode _mode = ThemeMode.Dark;

    internal static ThemeMode Mode => _mode;

    /// <summary>
    /// Installs the seven schemes for <paramref name="mode"/>. Safe to call repeatedly: the
    /// first call adds the schemes, later calls with a different mode remap them in place.
    /// </summary>
    internal static void Register(ThemeMode mode = ThemeMode.Dark)
    {
        var palette = mode == ThemeMode.Light ? LightPalette : DarkPalette;

        // Scheme *names* are stable across modes and only their contents change, so a mode
        // switch never has to walk the view tree re-assigning SchemeName. A view that was
        // given only a SchemeName resolves it through SchemeManager at draw time rather than
        // caching the Scheme (View.HasScheme stays false), so remapping the name is enough:
        // every view keeps the scheme it was constructed with and simply redraws in the new
        // palette.
        var schemes = SchemeManager.Schemes;
        foreach (var (name, scheme) in BuildSchemes(palette))
        {
            if (_registered && schemes is not null)
            {
                schemes[name] = scheme;
            }
            else
            {
                SchemeManager.AddScheme(name, scheme);
            }
        }

        _registered = true;
        _mode = mode;
    }

    /// <summary>
    /// Flips to the other mode and returns the one now in force. The caller is responsible
    /// for the redraw; nothing here touches focus, scroll position, or any view state, so a
    /// toggle mid-demo cannot disturb what the operator was doing.
    /// </summary>
    internal static ThemeMode Toggle()
    {
        Register(_mode == ThemeMode.Light ? ThemeMode.Dark : ThemeMode.Light);
        return _mode;
    }

    private static IEnumerable<(string Name, Scheme Scheme)> BuildSchemes(Palette p)
    {
        yield return (BaseScheme, Scheme(p.TextPrimary, p.SurfaceBase));
        yield return (RailScheme, Scheme(p.TextPrimary, p.SurfaceRaised));
        yield return (ActionScheme, Scheme(p.SurfaceBase, p.AccentTeal));
        yield return (DestructiveScheme, Scheme(p.StateFailedOnRaised, p.SurfaceRaised));
        yield return (OverlayScheme, Scheme(p.TextPrimary, p.SurfaceOverlay));
        yield return (LockExemptScheme, Scheme(p.AccentTeal, p.SurfaceBase));
        yield return (NavigationActiveScheme, Scheme(p.TextOnAccent, p.AccentNavy));
    }

    internal static void Apply(View view, string schemeName) => view.SchemeName = schemeName;

    private static Scheme Scheme(string foreground, string background) =>
        new(new global::Terminal.Gui.Drawing.Attribute(new Color(foreground), new Color(background)));
}
