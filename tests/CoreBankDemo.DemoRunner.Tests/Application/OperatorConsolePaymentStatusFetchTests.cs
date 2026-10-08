using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Tests.Fakes;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Application;

/// <summary>
/// Fetch reads PaymentsAPI's view of a payment (ADR-027). It is evidence, never proof: the card
/// and the still-open strip stay driven by the outcome feed.
/// </summary>
public class OperatorConsolePaymentStatusFetchTests
{
    private static readonly PaymentRequest StandardPayment =
        new("NL91ABNA0417164300", "NL20INGB0001234567", 10m, "EUR", PaymentRail.Standard);

    private const string PendingBody =
        """{"paymentId":"tx-1","transactionId":"tx-1","status":"Pending","amount":10.00,"currency":"EUR","processedAt":"2026-08-29T12:00:00+00:00"}""";

    private static async Task<(OperatorConsoleController Controller, OperatorHarness Harness)> AttachedAsync(
        TopologyProfile profile = TopologyProfile.Regular)
    {
        var harness = new OperatorHarness();
        harness.Aspire.Queue(OperatorHarness.Snapshot(profile));
        var controller = harness.CreateController();
        (await controller.AttachAsync(profile, CancellationToken.None)).Succeeded.Should().BeTrue();
        return (controller, harness);
    }

    [Fact]
    public async Task WithoutATopology_IsRefused_RecordedAndNeverSent()
    {
        var harness = new OperatorHarness();
        var controller = harness.CreateController();

        var result = await controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        harness.Payments.FetchIds.Should().BeEmpty();
        var record = controller.State.Evidence.Last();
        record.Title.Should().Be("Payment status fetch refused");
        record.Summary.Should().Contain("Start or attach a topology");
        controller.State.PaymentStatusFetch!.Result.Should().BeSameAs(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankId_IsRefused_AndNeverSent(string id)
    {
        var (controller, harness) = await AttachedAsync();

        var result = await controller.FetchPaymentStatusAsync(id, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        harness.Payments.FetchIds.Should().BeEmpty();
        controller.State.Evidence.Last().Summary.Should().Contain("Payment status fetch refused").And.Contain("payment id");
    }

    [Theory]
    [InlineData(TopologyProfile.Regular)]
    [InlineData(TopologyProfile.LoadTests)]
    public async Task AnAnswer_IsRecordedAsFetched_OnTheAttachedProfile(TopologyProfile profile)
    {
        var (controller, harness) = await AttachedAsync(profile);
        harness.Payments.QueueInspections(new InspectionResult(
            true, 200, KnownEndpoints.PaymentStatus, PendingBody, null, TimeSpan.FromMilliseconds(38)));

        await controller.FetchPaymentStatusAsync("  tx-1 ", CancellationToken.None);

        harness.Payments.FetchIds.Should().Equal("tx-1");
        harness.Payments.FetchProfiles.Should().Equal(profile);
        var record = controller.State.Evidence.Last();
        record.Kind.Should().Be(EvidenceKind.OutcomeQuery);
        record.Title.Should().Be(EvidenceTitles.PaymentStatusFetched);
        record.Succeeded.Should().BeTrue();
        record.StatusCode.Should().Be(200);
        var fetch = controller.State.PaymentStatusFetch!;
        fetch.TransactionId.Should().Be("tx-1");
        fetch.InFlight.Should().BeFalse();
        fetch.Result!.Body.Should().Be(PendingBody);
    }

    [Fact]
    public async Task NotFound_IsAnAnswer_NotAFailure()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.QueueInspections(new InspectionResult(
            false, 404, KnownEndpoints.PaymentStatus, string.Empty, "HTTP 404", TimeSpan.FromMilliseconds(21)));

        await controller.FetchPaymentStatusAsync("unknown", CancellationToken.None);

        var record = controller.State.Evidence.Last();
        record.Title.Should().Be(EvidenceTitles.PaymentStatusFetched);
        record.Succeeded.Should().BeTrue("an unknown id is an answer");
    }

    [Fact]
    public async Task NoAnswer_IsRecordedAsFailed()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.QueueInspections(new InspectionResult(
            false, 0, KnownEndpoints.PaymentStatus, null, "Connection refused", TimeSpan.FromSeconds(3)));

        await controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);

        var record = controller.State.Evidence.Last();
        record.Title.Should().Be(EvidenceTitles.PaymentStatusFetchFailed);
        record.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task WhileTheCallIsOut_TheFetchIsInFlight()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.FetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseFetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var fetch = controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);
        await harness.Payments.FetchStarted.Task;

        controller.State.PaymentStatusFetch!.InFlight.Should().BeTrue();
        controller.State.PaymentStatusFetch.StartedAt.Should().Be(harness.Time.GetUtcNow());

        harness.Payments.ReleaseFetch.SetResult();
        await fetch;
        controller.State.PaymentStatusFetch!.InFlight.Should().BeFalse();
    }

    [Fact]
    public async Task AFetch_NeverChangesATrackedPayment()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "tx-1", "tx-1", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        await controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Supplied, "tx-1", CancellationToken.None);
        var before = controller.State.TrackedPayments;
        harness.Payments.QueueInspections(new InspectionResult(
            true, 200, KnownEndpoints.PaymentStatus,
            PendingBody.Replace("Pending", "Completed"), null, TimeSpan.FromMilliseconds(38)));

        await controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);

        controller.State.TrackedPayments.Should().Equal(before, "the card is driven by the feed, never by a fetch");
        controller.State.Evidence.Last().Account.Should().Be(StandardPayment.ToAccount);
    }

    [Fact]
    public async Task AFetch_IsNotHeldBackByAnInFlightAction()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.SubmissionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseSubmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.Queue(new PaymentResult(
            PaymentOutcome.Pending, 202, "tx-1", "tx-1", "Pending", "{}", null, TimeSpan.FromMilliseconds(5)));
        var submit = controller.SubmitPaymentAsync(StandardPayment, IdempotencyMode.Supplied, "tx-1", CancellationToken.None);
        await harness.Payments.SubmissionStarted.Task;
        controller.State.ActiveMutation.Should().NotBeNull();

        await controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);

        harness.Payments.FetchIds.Should().Equal("tx-1");
        harness.Payments.ReleaseSubmission.SetResult();
        await submit;
    }

    [Fact]
    public async Task ASecondFetch_WhileOneIsOut_IsIgnored_WithoutACallOrARecord()
    {
        var (controller, harness) = await AttachedAsync();
        harness.Payments.FetchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Payments.ReleaseFetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = controller.FetchPaymentStatusAsync("tx-1", CancellationToken.None);
        await harness.Payments.FetchStarted.Task;
        var recordsBefore = controller.State.Evidence.Count;

        var second = controller.FetchPaymentStatusAsync("tx-2", CancellationToken.None);
        var ignoredAtOnce = second.IsCompleted;
        var stateWhileOut = controller.State.PaymentStatusFetch;
        harness.Payments.ReleaseFetch.SetResult();
        await Task.WhenAll(first, second);

        harness.Payments.FetchIds.Should().Equal("tx-1");
        ignoredAtOnce.Should().BeTrue("the second press is answered without waiting on a call");
        (await second).Succeeded.Should().BeFalse();
        stateWhileOut!.TransactionId.Should().Be("tx-1");
        stateWhileOut.InFlight.Should().BeTrue();
        controller.State.Evidence.Should().HaveCount(recordsBefore + 1, "only the first fetch is recorded");
    }
}
