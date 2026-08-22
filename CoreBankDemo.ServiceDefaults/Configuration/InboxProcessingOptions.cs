namespace CoreBankDemo.ServiceDefaults.Configuration;

/// <summary>
/// Empty marker subclass of <see cref="ProcessingOptionsBase"/> bound to the
/// <c>InboxProcessing</c> configuration section. Carries no members of its
/// own; adding one requires a corresponding entry in
/// <c>DeadOptionMembersTests</c>'s known-consumers list.
/// </summary>
public sealed record InboxProcessingOptions : ProcessingOptionsBase
{
    public const string SectionName = "InboxProcessing";
}
