using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

/// <summary>
/// The Cancel payment path, one test per row of the story's I/O matrix. The rule every one of
/// them is written against is the same: the console never synthesises an outcome — a cancel
/// resolves a payment only on the bank's own answer, and a cancel that fails, times out or
/// returns something unrecognised leaves the payment exactly where it was.
/// </summary>
public class OperatorConsoleCancelTests
{
    private static readonly PaymentRequest InstantPayment =
        new("NL91ABNA0417164300", "NL20INGB0001234567", 250m, "EUR", PaymentRail.Instant);

    [Fact]
    public async Task Cancel_Accepted_ResolvesTheRowOnTheBanksOwnAnswerAndLeavesTheStrip()
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.QueueCancellations(Answer(PaymentCancelOutcome.Cancelled, 200, "Cancelled"));
        harness.Time.Advance(TimeSpan.FromSeconds(5));

        var result = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.Cancelled);
        row.IsOpen.Should().BeFalse("a proven withdrawal leaves the STILL OPEN strip");
        row.Note.Should().Be("withdrawn before execution · no money moved");
        // Two clocks, never one -- and a final clock that is not zero. CoreBank publishes nothing
        // at all for a replayed cancellation, so a card left waiting for an event to stamp it
        // would read 0s for ever.
        row.ProcessedAt.Should().Be(ProcessedAt);
        row.ObservedAt.Should().NotBeNull();
        (row.ObservedAt!.Value - row.SubmittedAt).Should().Be(
            TimeSpan.FromSeconds(5),
            "the card's final clock is a duration the room can read, not a permanent 0s");
        controller.State.Evidence.Last().Succeeded.Should().BeTrue();
        controller.State.Evidence.Last().Target.Should().Be(KnownEndpoints.TransactionCancel);
        harness.Payments.Cancellations.Single().Should().BeEquivalentTo(new PaymentCancellation(
            InstantPayment.FromAccount,
            InstantPayment.ToAccount,
            InstantPayment.Amount,
            "EUR",
            "tx-8821"));
    }

    /// <summary>
    /// The console never re-labels a committed payment as cancelled on the strength of having
    /// asked: the bank's answer wins, and the card is told why it now says the opposite.
    /// </summary>
    [Theory]
    [InlineData("Completed", PaymentTrackingState.Settled)]
    [InlineData("Failed", PaymentTrackingState.Rejected)]
    public async Task Cancel_TooLate_ReportsTheCommittedOutcomeTheBankReturned(
        string committedStatus,
        PaymentTrackingState expected)
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.QueueCancellations(Answer(PaymentCancelOutcome.AlreadyCommitted, 200, committedStatus));

        var result = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain("too late to cancel — the bank had already executed it");
        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(expected);
        row.Note.Should().Be("too late to cancel — the bank had already executed it");
    }

    /// <summary>
    /// A refusal is about this instant, not about the payment. The console prints the status the
    /// bank stated and never a sentence about what the bank is doing.
    /// </summary>
    [Theory]
    [InlineData("Processing")]
    [InlineData("Pending")]
    public async Task Cancel_RefusedWithANonTerminalStatus_LeavesThePaymentExactlyWhereItWas(string status)
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.QueueCancellations(Answer(PaymentCancelOutcome.Refused, 409, status));

        var result = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be($"cancel refused (409) — the bank reports status {status}");
        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.Awaiting, "the refusal proved nothing about this payment");
        row.IsOpen.Should().BeTrue("the action slot returns to Cancel payment");
        controller.State.Evidence.Last().Succeeded.Should().BeFalse();
        controller.State.Evidence.Last().StatusCode.Should().Be(409);
    }

    /// <summary>
    /// A terminal status in a 409 body is an outcome the console has been <i>told</i>. A status
    /// the bank stated about its own row is proof; a refusal to act is not.
    /// </summary>
    [Fact]
    public async Task Cancel_RefusedWithATerminalStatus_IsRenderedAsTheOutcomeItWasTold()
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.QueueCancellations(Answer(PaymentCancelOutcome.Refused, 409, "Failed"));

        var result = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        var row = controller.State.TrackedPayments.Single();
        row.State.Should().Be(PaymentTrackingState.Rejected);
        row.IsOpen.Should().BeFalse("a status the bank stated about its own row moves it off the strip");
    }

    [Fact]
    public async Task Cancel_TransportFailure_AssertsNothingAndKeepsTheRecord()
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.QueueCancellations(new PaymentCancellationResult(
            PaymentCancelOutcome.TransportFailure,
            0,
            "tx-8821",
            null,
            null,
            "The cancel request timed out; the payment is left exactly as it was.",
            TimeSpan.FromMilliseconds(9)));

        var result = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("timed out").And.Contain("left exactly as it was");
        controller.State.TrackedPayments.Single().State.Should().Be(PaymentTrackingState.Awaiting);
        controller.State.Evidence.Last().Succeeded.Should().BeFalse();
        controller.State.Evidence.Last().TransactionId.Should().Be("tx-8821");
    }

    /// <summary>
    /// Duplicate activation is debounced to exactly one dispatch, as everywhere else, so a second
    /// press mid-sentence cannot become a second cancellation.
    /// </summary>
    [Fact]
    public async Task Cancel_ActivatedTwiceWhileOneIsInFlight_DispatchesExactlyOne()
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.QueueCancellations(Answer(PaymentCancelOutcome.Cancelled, 200, "Cancelled"));
        harness.Payments.CancelStarted = new TaskCompletionSource();
        harness.Payments.ReleaseCancel = new TaskCompletionSource();

        var first = controller.CancelPaymentAsync("tx-8821", CancellationToken.None);
        await harness.Payments.CancelStarted.Task;
        var second = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);
        harness.Payments.ReleaseCancel.SetResult();
        var firstResult = await first;

        firstResult.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeFalse();
        harness.Payments.Cancellations.Should().ContainSingle();
    }

    /// <summary>
    /// Cancel is exempt from confirmation, never from the lock: some other mutating action in
    /// flight refuses it exactly as it refuses every other mutating control.
    /// </summary>
    [Fact]
    public async Task Cancel_WhileAnotherMutationIsInFlight_IsRefusedLikeEveryOtherMutatingControl()
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.SubmissionStarted = new TaskCompletionSource();
        harness.Payments.ReleaseSubmission = new TaskCompletionSource();
        var submit = controller.SubmitPaymentAsync(
            InstantPayment,
            IdempotencyMode.Supplied,
            "tx-9999",
            CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;

        var refused = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        harness.Payments.ReleaseSubmission.SetResult();
        await submit;
        refused.Succeeded.Should().BeFalse();
        refused.Message.Should().Contain("Another mutating action is already in flight");
        harness.Payments.Cancellations.Should().BeEmpty();
    }

    /// <summary>
    /// The one narrow concession, and the reason an instant payment's whole budget window is
    /// cancellable: an outstanding answer is that payment's own state rather than a console
    /// action awaiting a result.
    /// </summary>
    [Fact]
    public async Task Cancel_DuringItsOwnSubmission_IsDispatchedBeforeTheBankAnswers()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending", "{}", null, TimeSpan.Zero));
        harness.Payments.QueueCancellations(Answer(PaymentCancelOutcome.Cancelled, 200, "Cancelled"));
        harness.Payments.SubmissionStarted = new TaskCompletionSource();
        harness.Payments.ReleaseSubmission = new TaskCompletionSource();

        var submit = controller.SubmitPaymentAsync(
            InstantPayment,
            IdempotencyMode.Supplied,
            "tx-8821",
            CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;

        // The card already holds the payment, with its clock running and no answer in sight.
        var inFlight = controller.State.TrackedPayments.Single();
        inFlight.AwaitingResponse.Should().BeTrue();
        inFlight.HttpStatusCode.Should().Be(0, "the console never shows a status code it did not receive");
        controller.State.SelectedPayment.Should().Be("tx-8821");

        var cancel = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);
        harness.Payments.ReleaseSubmission.SetResult();
        await submit;

        cancel.Succeeded.Should().BeTrue();
        harness.Payments.Cancellations.Should().ContainSingle();
    }

    [Fact]
    public async Task Cancel_OfAPaymentThatAlreadyHasAProvenOutcome_IsRefusedAndRecorded()
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Feed.PushCompleted("tx-8821", new DateTimeOffset(2026, 8, 29, 12, 4, 31, TimeSpan.Zero));

        var result = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("already has a proven outcome");
        harness.Payments.Cancellations.Should().BeEmpty();
        controller.State.Evidence.Last().Summary.Should().Contain("Cancel payment refused");
        controller.State.Evidence.Last().Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_WithNoTransactionId_IsRefusedWithTheOmittedModeReason()
    {
        var (controller, harness) = await SubmittedAsync();

        var result = await controller.CancelPaymentAsync(string.Empty, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Omitted mode sends no key");
        harness.Payments.Cancellations.Should().BeEmpty();
        controller.State.Evidence.Last().Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// Selection is moved by the operator and by nothing else, and an id naming no tracked
    /// payment clears it rather than pinning the card to a row that is not there.
    /// </summary>
    [Fact]
    public async Task SelectPayment_OnlyEverNamesARowTheSessionActuallyHas()
    {
        var (controller, _) = await SubmittedAsync();

        controller.SelectPayment("tx-8821");
        controller.State.SelectedPayment.Should().Be("tx-8821");

        controller.SelectPayment("tx-nothing");
        controller.State.SelectedPayment.Should().BeNull();
    }

    /// <summary>
    /// Currency is not an operator input: the console always sends EUR (brief §6).
    /// </summary>
    [Fact]
    public async Task EveryCancelRequest_CarriesTheSameEurTheSubmissionDid()
    {
        var (controller, harness) = await SubmittedAsync();
        harness.Payments.QueueCancellations(Answer(PaymentCancelOutcome.Cancelled, 200, "Cancelled"));

        await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        harness.Payments.Submissions.Single().Request.Currency.Should().Be("EUR");
        harness.Payments.Cancellations.Single().Currency.Should().Be("EUR");
    }

    /// <summary>
    /// A second press mid-sentence must not become a second cancellation, and the console-wide
    /// lock is not what proves it here: this is the one path that bypasses <c>ActiveMutation</c>,
    /// because a payment's own submission is not "some other action" its cancel waits behind.
    /// </summary>
    [Fact]
    public async Task Cancel_ActivatedTwiceDuringItsOwnSubmission_StillDispatchesExactlyOne()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending", "{}", null, TimeSpan.Zero));
        harness.Payments.QueueCancellations(Answer(PaymentCancelOutcome.Cancelled, 200, "Cancelled"));
        harness.Payments.SubmissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.CancelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var submit = controller.SubmitPaymentAsync(
            InstantPayment, IdempotencyMode.Supplied, "tx-8821", CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;

        // Both presses land while the submission holds the console-wide lock, so only the
        // per-payment debounce can be what turns the second one away.
        var first = controller.CancelPaymentAsync("tx-8821", CancellationToken.None);
        await harness.Payments.CancelStarted.Task;
        var second = await controller.CancelPaymentAsync("tx-8821", CancellationToken.None);

        harness.Payments.ReleaseCancel.SetResult();
        var firstResult = await first;
        harness.Payments.ReleaseSubmission.SetResult();
        await submit;

        firstResult.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeFalse();
        harness.Payments.Cancellations.Should().ContainSingle();
    }

    private static readonly DateTimeOffset ProcessedAt = new(2026, 8, 29, 12, 4, 31, 882, TimeSpan.Zero);

    private static PaymentCancellationResult Answer(PaymentCancelOutcome outcome, int statusCode, string status) =>
        new(outcome, statusCode, "tx-8821", status, "{}", null, TimeSpan.FromMilliseconds(6), ProcessedAt);

    private static async Task<(OperatorConsoleController Controller, OperatorHarness Harness)> SubmittedAsync()
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(TopologyProfile.Regular));
        var controller = harness.CreateController();
        await controller.AttachAsync(TopologyProfile.Regular, CancellationToken.None);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "payment-id", "tx-8821", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(
            InstantPayment,
            IdempotencyMode.Supplied,
            "tx-8821",
            CancellationToken.None);
        return (controller, harness);
    }
}
