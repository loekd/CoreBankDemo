using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

/// <summary>
/// What an Evidence list row says. A row is a title of a few words and, on every record about a
/// transaction, the creditor account — never a status code, a transaction id or an explaining
/// clause. Those stay on <see cref="EvidenceRecord.Summary"/>, which these tests leave alone.
/// </summary>
public class OperatorConsoleEvidenceTitleTests
{
    private const string Debtor = "NL91ABNA0417164300";
    private const string Creditor = "NL20INGB0001234567";

    private static readonly DateTimeOffset ProcessedAt = new(2026, 8, 29, 12, 4, 31, 882, TimeSpan.Zero);

    private static readonly PaymentRequest InstantPayment = new(Debtor, Creditor, 250m, "EUR", PaymentRail.Instant);

    [Theory]
    [InlineData(PaymentOutcome.Pending, 202, EvidenceTitles.TransactionPending)]
    [InlineData(PaymentOutcome.Completed, 200, EvidenceTitles.TransactionCompleted)]
    [InlineData(PaymentOutcome.Failed, 200, EvidenceTitles.TransactionFailed)]
    [InlineData(PaymentOutcome.Rejected, 400, EvidenceTitles.PaymentError)]
    [InlineData(PaymentOutcome.TransportFailure, 0, EvidenceTitles.PaymentError)]
    [InlineData(PaymentOutcome.Cancelled, 504, EvidenceTitles.TransactionCancelled)]
    public async Task Submit_TitlesTheRowByOutcome_AndNamesTheCreditor(PaymentOutcome outcome, int statusCode, string title)
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.Queue(Payment(outcome, statusCode));

        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Supplied, "tx-8821", CancellationToken.None);

        var record = controller.State.Evidence.Last(row => row.Kind == EvidenceKind.Payment);
        record.Title.Should().Be(title);
        record.Account.Should().Be(Creditor);
        record.Title.Should().NotMatchRegex(@"\d", "a status code is the Details pane's to show");
    }

    [Fact]
    public async Task Resend_PrefixesTheTitle()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.Queue(Payment(PaymentOutcome.Completed, 200), Payment(PaymentOutcome.Completed, 200));
        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Supplied, "tx-8821", CancellationToken.None);

        await controller.ResendLastPaymentAsync(CancellationToken.None);

        var record = controller.State.Evidence.Last(row => row.Kind == EvidenceKind.Payment);
        record.Title.Should().Be("Resend completed");
        record.Account.Should().Be(Creditor);
    }

    [Theory]
    [InlineData(PaymentCancelOutcome.Cancelled, 200, "Cancelled", EvidenceTitles.TransactionCancelled)]
    [InlineData(PaymentCancelOutcome.AlreadyCommitted, 200, "Completed", EvidenceTitles.CancelTooLate)]
    [InlineData(PaymentCancelOutcome.Refused, 409, "Processing", EvidenceTitles.CancelRefused)]
    [InlineData(PaymentCancelOutcome.TransportFailure, 0, "", EvidenceTitles.CancelFailed)]
    public async Task Cancel_TitlesTheRowByTheBanksAnswer_AndNamesTheCreditor(
        PaymentCancelOutcome outcome,
        int statusCode,
        string status,
        string title)
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.QueueCancellations(new PaymentCancellationResult(
            outcome, statusCode, "tx-8821", status, "{}", null, TimeSpan.FromMilliseconds(6), ProcessedAt));

        await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        var record = controller.State.Evidence.Last();
        record.Title.Should().Be(title);
        record.Account.Should().Be(Creditor);
    }

    [Fact]
    public async Task TrackedEvents_AreTitledByWhatTheBankSaid_AndNameTheCreditor()
    {
        var (controller, harness) = await SubmittedAsync();

        harness.Feed.PushCompleted("tx-8821", ProcessedAt);
        var settled = controller.State.Evidence.Last();
        harness.Feed.PushFailed("tx-8821", ProcessedAt, "insufficient funds");
        var rejected = controller.State.Evidence.Last();

        settled.Title.Should().Be(EvidenceTitles.TransactionSettled);
        settled.Account.Should().Be(Creditor);
        rejected.Title.Should().Be(EvidenceTitles.TransactionRejected);
        rejected.Account.Should().Be(Creditor);
    }

    [Fact]
    public async Task BalanceLeg_NamesTheAccountItMoved_NotTheCreditor()
    {
        var (controller, harness) = await SubmittedAsync();

        harness.Feed.PushBalance("tx-8821", Debtor, -250m, 4750m);

        var record = controller.State.Evidence.Last();
        record.Title.Should().Be(EvidenceTitles.BalanceUpdated);
        record.Account.Should().Be(Debtor);
    }

    [Fact]
    public async Task Burst_TitlesItsAggregateByProgress_AndItsEventsNameTheTemplatesCreditor()
    {
        var (controller, harness) = await AttachedAsync();

        await controller.RunBurstAsync(InstantPayment, 2, 1, CancellationToken.None);
        harness.Feed.PushCompleted(harness.Payments.Submissions[0].IdempotencyKey!, ProcessedAt);

        var burst = controller.State.Evidence.Single(row => row.Kind == EvidenceKind.Burst);
        burst.Title.Should().Be("Burst finished 2/2");
        burst.Account.Should().BeNull("a burst is an aggregate, not one transaction");
        var settled = controller.State.Evidence.Last();
        settled.Title.Should().Be(EvidenceTitles.TransactionSettled);
        settled.Account.Should().Be(Creditor);
    }

    [Fact]
    public async Task Burst_WithdrawnPaymentRow_IsTitledAndNamesTheCreditor()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.Queue(Payment(PaymentOutcome.Cancelled, 504));

        await controller.RunBurstAsync(InstantPayment, 1, 1, CancellationToken.None);

        var withdrawn = controller.State.Evidence.Single(row => row.Kind == EvidenceKind.Payment);
        withdrawn.Title.Should().Be(EvidenceTitles.TransactionCancelled);
        withdrawn.Account.Should().Be(Creditor);
    }

    [Theory]
    [InlineData("com.corebank.something.new", "Unknown event com.corebank.something.new")]
    [InlineData(OutcomeEventTypes.TransactionCompleted, EvidenceTitles.EventWithoutTransactionId)]
    public async Task UnreadableEvent_IsTitledWithoutATransactionOrAnAccount(string eventType, string title)
    {
        var (controller, harness) = await AttachedAsync();

        harness.Feed.PushUnreadable(eventType, "{}");

        var record = controller.State.Evidence.Last();
        record.Title.Should().Be(title);
        record.Account.Should().BeNull();
    }

    [Fact]
    public async Task FeedTransitions_AreTitledInTwoOrThreeWords()
    {
        var (controller, harness) = await AttachedAsync();
        controller.State.Evidence.Should().Contain(row => row.Title == EvidenceTitles.FeedListening);

        harness.Feed.Fault(ProcessedAt);
        controller.State.Evidence.Last().Title.Should().Be(EvidenceTitles.FeedLost);

        harness.Feed.Resume(ProcessedAt.AddSeconds(4));
        controller.State.Evidence.Last().Title.Should().Be(EvidenceTitles.FeedListeningAgain);
    }

    [Fact]
    public async Task Refusal_IsTitledByItsAction_AndKeepsItsReasonOnTheSummary()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();

        await controller.QueryOutcomeAsync("tx-8821", CancellationToken.None);

        var record = controller.State.Evidence.Last();
        record.Title.Should().Be("Outcome query refused");
        record.Summary.Should().Contain("Start or attach a topology");
    }

    [Fact]
    public async Task OutcomeQuery_NamesTheCreditorOfAPaymentThisConsoleTracks()
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.QueueInspections(new InspectionResult(true, 200, "outcome", "{}", null, TimeSpan.FromMilliseconds(3)));

        await controller.QueryOutcomeAsync("tx-8821", CancellationToken.None);

        var record = controller.State.Evidence.Last();
        record.Title.Should().Be(EvidenceTitles.OutcomeQueried);
        record.Account.Should().Be(Creditor);
    }

    [Fact]
    public async Task TopologyRecord_WhoseSummaryIsAlreadyATitle_CarriesNoSecondCopy()
    {
        var (controller, _) = await AttachedAsync();

        var attached = controller.State.Evidence.First(row => row.Kind == EvidenceKind.Topology);

        attached.Summary.Should().Be("Attached to Regular");
        attached.Title.Should().BeNull("the row falls back to the summary");
    }

    [Fact]
    public async Task StatusLine_NeverMentionsTheRunGeneration()
    {
        var (controller, harness) = await AttachedAsync();
        controller.State.StatusLine.Should().NotContain("generation");
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202));

        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Supplied, "tx-8821", CancellationToken.None);

        controller.State.StatusLine.Should().NotContain("generation");
    }

    /// <summary>
    /// The Evidence list is 42% of the workspace: about 49 columns on a 140-column terminal,
    /// of which the gutter takes 4 and an IBAN 19.
    /// </summary>
    [Fact]
    public void EveryFixedTitle_FitsTheListBesideAnIban()
    {
        var titles = typeof(EvidenceTitles)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.GetRawConstantValue() is string)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        titles.Should().NotBeEmpty();
        titles.Should().OnlyContain(title => title.Length <= 28);
        titles.Where(title => title.StartsWith("Transaction", StringComparison.Ordinal))
            .Should().OnlyContain(title => title.Length <= 24, "these rows also carry an IBAN");
    }

    [Fact]
    public async Task Export_IsUnchanged_TheTitleAndTheAccountAreRowWordingOnly()
    {
        var (controller, _) = await SubmittedAsync();

        var json = System.Text.Json.JsonSerializer.Serialize(controller.State.Evidence);

        json.Should().Contain("\"Summary\"").And.NotContain("\"Title\"").And.NotContain("\"Account\"");
    }

    private static PaymentResult Payment(PaymentOutcome outcome, int statusCode) =>
        new(outcome, statusCode, "payment-id", "tx-8821", outcome.ToString(), "{}", null, TimeSpan.FromMilliseconds(5));

    private static async Task<(OperatorConsoleController Controller, OperatorHarness Harness)> AttachedAsync()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        (await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None)).Succeeded.Should().BeTrue();
        return (controller, harness);
    }

    private static async Task<(OperatorConsoleController Controller, OperatorHarness Harness)> SubmittedAsync()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.Queue(Payment(PaymentOutcome.Pending, 202));
        await controller.SubmitPaymentAsync(InstantPayment, IdempotencyMode.Supplied, "tx-8821", CancellationToken.None);
        return (controller, harness);
    }
}
