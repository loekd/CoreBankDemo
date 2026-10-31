using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Terminal;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests;

/// <summary>
/// The palettes are the one place in this console where a plausible-looking edit can silently
/// make the surface unreadable from the back of a room, so the contrast floor is asserted
/// rather than trusted. The dark palette was tuned light-on-dark and every one of its accents
/// measured between 2.17:1 and 3.96:1 on white, which is why light mode carries its own hexes
/// instead of reusing these on a pale background.
/// </summary>
[Collection(OperatorThemeCollection.Name)]
public class OperatorThemeTests
{
    /// <summary>WCAG AA for normal-size text.</summary>
    private const double AaNormalText = 4.5;

    private static readonly string[] AllSchemes =
    [
        OperatorTheme.BaseScheme,
        OperatorTheme.RailScheme,
        OperatorTheme.ActionScheme,
        OperatorTheme.DestructiveScheme,
        OperatorTheme.OverlayScheme,
        OperatorTheme.LockExemptScheme,
        OperatorTheme.NavigationActiveScheme,
    ];

    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void EveryScheme_ClearsAaContrast(ThemeMode mode)
    {
        OperatorTheme.Register(mode);

        foreach (var name in AllSchemes)
        {
            var normal = SchemeManager.GetScheme(name).Normal;
            var ratio = ContrastRatio(normal.Foreground, normal.Background);

            ratio.Should().BeGreaterThanOrEqualTo(
                AaNormalText,
                "scheme '{0}' in {1} mode must stay legible on a projector",
                name,
                mode);
        }
    }

    /// <summary>
    /// DESIGN.md says there is no hover state. Terminal.Gui disagrees: a scheme that only pins
    /// <c>Normal</c> gets a derived <c>Highlight</c> role, drawn whenever the mouse is over a
    /// control, which is the surface colour lightened by 20% in italic -- navy on navy for the
    /// rail, at well under 2:1. The driver also loses mouse-leave events on a fast move or a
    /// re-entry from outside the rail, so that derived row then sticks. Pinning the hover role
    /// to the normal one removes both the unreadable hover and the stuck variant of it.
    /// </summary>
    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void EveryScheme_DrawsAHoveredControlExactlyAsANormalOne(ThemeMode mode)
    {
        OperatorTheme.Register(mode);

        foreach (var name in AllSchemes.Append(OperatorTheme.EvidencePaneScheme))
        {
            var scheme = SchemeManager.GetScheme(name);

            scheme.GetAttributeForRole(VisualRole.Highlight).Should().Be(
                scheme.Normal,
                "scheme '{0}' in {1} mode must not change colour or go italic under the mouse",
                name,
                mode);
        }
    }

    [Theory]
    [InlineData(ThemeMode.Dark)]
    [InlineData(ThemeMode.Light)]
    public void Register_IsIdempotent(ThemeMode mode)
    {
        OperatorTheme.Register(mode);
        var first = SchemeManager.GetScheme(OperatorTheme.BaseScheme).Normal;

        OperatorTheme.Register(mode);
        var second = SchemeManager.GetScheme(OperatorTheme.BaseScheme).Normal;

        second.Foreground.Should().Be(first.Foreground);
        second.Background.Should().Be(first.Background);
    }

    /// <summary>
    /// The live toggle remaps the six scheme names in place instead of walking the view tree,
    /// so this is the behaviour the T key depends on: same names, different colours.
    /// </summary>
    [Fact]
    public void Toggle_RemapsSchemesInPlace()
    {
        OperatorTheme.Register(ThemeMode.Dark);
        var darkBase = SchemeManager.GetScheme(OperatorTheme.BaseScheme).Normal;

        var mode = OperatorTheme.Toggle();

        mode.Should().Be(ThemeMode.Light);
        OperatorTheme.Mode.Should().Be(ThemeMode.Light);

        var lightBase = SchemeManager.GetScheme(OperatorTheme.BaseScheme).Normal;
        lightBase.Background.Should().NotBe(darkBase.Background);

        OperatorTheme.Toggle().Should().Be(ThemeMode.Dark);
        SchemeManager.GetScheme(OperatorTheme.BaseScheme).Normal.Background
            .Should().Be(darkBase.Background);
    }

    /// <summary>
    /// Light mode is a distinct palette, not the dark one on a pale canvas. If a future edit
    /// makes the two modes share a canvas tone, the contrast assertions above would still pass
    /// while the console became unreadable in one of them.
    /// </summary>
    [Fact]
    public void LightAndDark_AreGenuinelyDifferentSurfaces()
    {
        OperatorTheme.Register(ThemeMode.Dark);
        var dark = SchemeManager.GetScheme(OperatorTheme.BaseScheme).Normal;

        OperatorTheme.Register(ThemeMode.Light);
        var light = SchemeManager.GetScheme(OperatorTheme.BaseScheme).Normal;

        Luminance(dark.Background).Should().BeLessThan(0.2, "the cockpit canvas is near-black");
        Luminance(light.Background).Should().BeGreaterThan(0.8, "the projector canvas is near-white");
    }

    private static double ContrastRatio(Color a, Color b)
    {
        var (high, low) = Luminance(a) >= Luminance(b)
            ? (Luminance(a), Luminance(b))
            : (Luminance(b), Luminance(a));

        return (high + 0.05) / (low + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte raw)
        {
            var c = raw / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }
}
