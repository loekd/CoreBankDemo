using CoreBankDemo.DemoRunner.Terminal;

namespace CoreBankDemo.DemoRunner;

public sealed record CliOptions(bool Doctor, bool Help, ThemeMode? Theme, IReadOnlyList<string> Errors)
{
    /// <summary>
    /// Read when neither <c>--light</c> nor <c>--dark</c> is given, so a presenter can pin the
    /// palette once in the terminal profile that runs the talk instead of on every command.
    /// </summary>
    internal const string ThemeEnvironmentVariable = "COREBANK_DEMO_THEME";

    public bool IsValid => Errors.Count == 0;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var doctor = false;
        var help = false;
        ThemeMode? theme = null;
        var errors = new List<string>();
        foreach (var argument in args)
        {
            switch (argument)
            {
                case "--doctor":
                    doctor = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--light":
                    theme = ThemeMode.Light;
                    break;
                case "--dark":
                    theme = ThemeMode.Dark;
                    break;
                case "--show":
                case "--rehearse":
                case "--scenario":
                case "--resume":
                    errors.Add($"'{argument}' was retired. Run the reusable console without scenario arguments.");
                    break;
                default:
                    errors.Add($"Unknown argument '{argument}'.");
                    break;
            }
        }

        return new CliOptions(doctor, help, theme, errors);
    }

    /// <summary>
    /// Resolves the palette to start in: an explicit flag wins, then
    /// <c>COREBANK_DEMO_THEME</c>, then dark. An unrecognised environment value is ignored
    /// rather than rejected — a stale profile variable must never keep the console from
    /// starting, least of all minutes before a talk.
    /// </summary>
    public static ThemeMode ResolveTheme(ThemeMode? flag, string? environmentValue)
    {
        if (flag is { } explicitMode)
        {
            return explicitMode;
        }

        return environmentValue?.Trim().ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.Dark,
        };
    }

    public static string HelpText =>
        """
        CoreBankDemo DemoRunner — reusable terminal operator console

        Usage:
          dotnet run --project CoreBankDemo.DemoRunner
          dotnet run --project CoreBankDemo.DemoRunner -- --doctor

        Options:
          --doctor   Print local prerequisites and detected topology state; start nothing.
          --light    Start in the light palette, for a projector. Toggle live with T.
          --dark     Start in the dark cockpit palette (the default). Toggle live with T.
          --help     Show this help.

        Environment:
          COREBANK_DEMO_THEME=light|dark   Palette to start in when no flag is given.
        """;
}
