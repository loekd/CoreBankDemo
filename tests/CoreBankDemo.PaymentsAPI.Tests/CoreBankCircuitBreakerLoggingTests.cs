using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Polly;
using Polly.CircuitBreaker;
using Xunit;

namespace CoreBankDemo.PaymentsAPI.Tests;

/// <summary>
/// The three circuit transitions each produce one structured log line so
/// Loki shows the circuit's life cycle (ADR-029). Opened is a Warning and
/// names the break duration and the outcome that tripped it; the other two
/// are Information.
/// </summary>
public class CoreBankCircuitBreakerLoggingTests
{
    private readonly Mock<ILogger> _logger = new();

    public CoreBankCircuitBreakerLoggingTests() =>
        _logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

    [Fact]
    public void Opened_logs_a_warning_with_the_break_duration_and_the_status_that_tripped_it()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var args = new OnCircuitOpenedArguments<HttpResponseMessage>(
            ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken), Outcome.FromResult(response), TimeSpan.FromSeconds(10), isManual: false);

        CoreBankCircuitBreakerLogging.Opened(_logger.Object, args);

        var line = SingleLine(LogLevel.Warning);
        line.Should().Contain("10000").And.Contain("503");
    }

    [Fact]
    public void Opened_names_the_exception_type_when_an_exception_tripped_it()
    {
        var args = new OnCircuitOpenedArguments<HttpResponseMessage>(
            ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken),
            Outcome.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")),
            TimeSpan.FromSeconds(10),
            isManual: false);

        CoreBankCircuitBreakerLogging.Opened(_logger.Object, args);

        SingleLine(LogLevel.Warning).Should().Contain(nameof(HttpRequestException));
    }

    [Fact]
    public void HalfOpened_logs_information()
    {
        CoreBankCircuitBreakerLogging.HalfOpened(_logger.Object, new OnCircuitHalfOpenedArguments(ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken)));

        SingleLine(LogLevel.Information).Should().Contain("half-open");
    }

    [Fact]
    public void Closed_logs_information()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var args = new OnCircuitClosedArguments<HttpResponseMessage>(
            ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken), Outcome.FromResult(response), isManual: false);

        CoreBankCircuitBreakerLogging.Closed(_logger.Object, args);

        SingleLine(LogLevel.Information).Should().Contain("closed");
    }

    private string SingleLine(LogLevel level)
    {
        var invocation = _logger.Invocations.Should().ContainSingle(i => i.Method.Name == nameof(ILogger.Log)).Which;
        invocation.Arguments[0].Should().Be(level);
        return invocation.Arguments[2].ToString()!;
    }
}
