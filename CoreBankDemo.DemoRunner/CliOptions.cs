namespace CoreBankDemo.DemoRunner;

public sealed record CliOptions(bool Doctor, bool Help, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var doctor = false;
        var help = false;
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

        return new CliOptions(doctor, help, errors);
    }

    public static string HelpText =>
        """
        CoreBankDemo DemoRunner — reusable terminal operator console

        Usage:
          dotnet run --project CoreBankDemo.DemoRunner
          dotnet run --project CoreBankDemo.DemoRunner -- --doctor

        Options:
          --doctor   Print local prerequisites and detected topology state; start nothing.
          --help     Show this help.
        """;
}
