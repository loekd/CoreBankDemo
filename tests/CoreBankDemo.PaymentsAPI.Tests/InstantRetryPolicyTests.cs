using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Handlers;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// spec: instant-rail-retry-after, "Retry policy". Pure arithmetic: the
/// handler supplies the clock and the random sample, this decides.
/// </summary>
public class InstantRetryPolicyTests
{
    private static readonly TimeSpan Plenty = TimeSpan.FromSeconds(7.5);

    [Fact]
    public void A_server_hint_sets_the_wait_exactly()
    {
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: TimeSpan.FromSeconds(2), remaining: Plenty, jitterSample: 0.99);

        decision.Should().Be(new InstantRetryDecision(true, TimeSpan.FromSeconds(2), InstantRetrySource.RetryAfter));
    }

    [Theory]
    [InlineData(1, 0.0, 125)]
    [InlineData(1, 0.5, 250)]
    [InlineData(1, 0.999, 375)]
    [InlineData(2, 0.5, 500)]
    [InlineData(3, 0.5, 1000)]
    [InlineData(4, 0.5, 1000)]
    [InlineData(4, 0.999, 1000)]
    public void Without_a_hint_the_wait_is_a_jittered_capped_exponential_backoff(int attempt, double sample, int expectedMs)
    {
        var decision = InstantRetryPolicy.AfterFailure(
            attempt, maxAttempts: 10, retryAfter: null, remaining: Plenty, jitterSample: sample);

        decision.Retry.Should().BeTrue();
        decision.Source.Should().Be(InstantRetrySource.Backoff);
        decision.Wait.TotalMilliseconds.Should().BeApproximately(expectedMs, 0.5);
    }

    [Fact]
    public void A_hint_that_leaves_no_useful_attempt_gives_up_without_sleeping()
    {
        // remaining 4.5 s, hint 5 s: 5 + 0.5 > 4.5 -> cancel now.
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: TimeSpan.FromSeconds(5), remaining: TimeSpan.FromSeconds(4.5), jitterSample: 0.5);

        decision.Retry.Should().BeFalse();
    }

    [Fact]
    public void A_hint_that_leaves_exactly_the_minimum_attempt_still_retries()
    {
        // remaining 2.5 s, hint 2 s: 2 + 0.5 == 2.5 -> a 500 ms attempt fits.
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: TimeSpan.FromSeconds(2), remaining: TimeSpan.FromSeconds(2.5), jitterSample: 0.5);

        decision.Retry.Should().BeTrue();
    }

    [Fact]
    public void A_backoff_that_leaves_no_useful_attempt_gives_up_without_sleeping()
    {
        // remaining 700 ms, backoff 250 ms: 250 + 500 > 700 -> cancel now.
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: null, remaining: TimeSpan.FromMilliseconds(700), jitterSample: 0.5);

        decision.Retry.Should().BeFalse();
    }

    [Fact]
    public void The_last_capped_attempt_never_sleeps()
    {
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 3, maxAttempts: 3, retryAfter: TimeSpan.FromSeconds(1), remaining: Plenty, jitterSample: 0.5);

        decision.Retry.Should().BeFalse();
    }

    [Fact]
    public void A_zero_hint_retries_at_once()
    {
        var decision = InstantRetryPolicy.AfterFailure(
            attempt: 1, maxAttempts: 3, retryAfter: TimeSpan.Zero, remaining: Plenty, jitterSample: 0.5);

        decision.Should().Be(new InstantRetryDecision(true, TimeSpan.Zero, InstantRetrySource.RetryAfter));
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(2500, true)]
    public void An_attempt_starts_only_with_the_minimum_useful_window_left(int remainingMs, bool expected)
    {
        InstantRetryPolicy.CanStartAttempt(TimeSpan.FromMilliseconds(remainingMs)).Should().Be(expected);
    }
}
