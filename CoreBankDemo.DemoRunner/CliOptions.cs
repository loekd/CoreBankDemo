namespace CoreBankDemo.DemoRunner;

/// <summary>Binds only the five documented flags — nothing else (ADR-015 code map).</summary>
public sealed record CliOptions(bool Doctor, bool Show, bool Rehearse, string ScenarioName, bool Resume)
{
    public const string DefaultScenarioName = "mission-critical-talk-v7";

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var doctor = false;
        var show = false;
        var rehearse = false;
        var resume = false;
        var scenarioName = DefaultScenarioName;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--doctor":
                    doctor = true;
                    break;
                case "--show":
                    show = true;
                    break;
                case "--rehearse":
                    rehearse = true;
                    break;
                case "--resume":
                    resume = true;
                    break;
                case "--scenario" when i + 1 < args.Count:
                    scenarioName = args[++i];
                    break;
            }
        }

        return new CliOptions(doctor, show, rehearse, scenarioName, resume);
    }
}
