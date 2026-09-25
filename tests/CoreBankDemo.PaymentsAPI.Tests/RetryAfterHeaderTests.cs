using AwesomeAssertions;
using CoreBankDemo.PaymentsAPI.Outbox;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// spec: instant-rail-retry-after -- both forms RFC 9110 allows for
/// Retry-After are honoured; anything else is "absent", never an error.
/// </summary>
public class RetryAfterHeaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, IEnumerable<string>> Headers(string name, string value) =>
        new(StringComparer.OrdinalIgnoreCase) { [name] = [value] };

    [Fact]
    public void Parses_delta_seconds()
    {
        RetryAfterHeader.TryParse(Headers("Retry-After", "2"), new FakeTimeProvider(Now), out var retryAfter)
            .Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Parses_an_http_date_relative_to_the_time_provider()
    {
        var date = Now.AddSeconds(3).ToString("r");

        RetryAfterHeader.TryParse(Headers("Retry-After", date), new FakeTimeProvider(Now), out var retryAfter)
            .Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void An_http_date_in_the_past_is_a_zero_wait()
    {
        var date = Now.AddSeconds(-30).ToString("r");

        RetryAfterHeader.TryParse(Headers("Retry-After", date), new FakeTimeProvider(Now), out var retryAfter)
            .Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Matches_the_header_name_case_insensitively()
    {
        RetryAfterHeader.TryParse(Headers("retry-after", "1"), new FakeTimeProvider(Now), out var retryAfter)
            .Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("1.5")]
    public void An_unparseable_or_negative_value_is_absent(string value)
    {
        RetryAfterHeader.TryParse(Headers("Retry-After", value), new FakeTimeProvider(Now), out _)
            .Should().BeFalse();
    }

    [Fact]
    public void A_missing_header_is_absent()
    {
        RetryAfterHeader.TryParse(Headers("Content-Type", "application/json"), new FakeTimeProvider(Now), out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Null_headers_are_absent()
    {
        RetryAfterHeader.TryParse(null, new FakeTimeProvider(Now), out _).Should().BeFalse();
    }
}
