namespace CoreBankDemo.ServiceDefaults.Configuration;

public record OutboxProcessingOptions : ProcessingOptionsBase
{
    public const string SectionName = "OutboxProcessing";
}
