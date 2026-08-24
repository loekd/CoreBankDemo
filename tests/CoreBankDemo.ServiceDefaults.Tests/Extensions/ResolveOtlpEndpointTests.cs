using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreBankDemo.ServiceDefaults.Tests.Extensions;

/// <summary>
/// Story 3.4: <c>ResolveOtlpEndpoint</c>'s full <c>JAEGER_OTLP_ENDPOINT</c>
/// parsing matrix, exercised directly now that the member is promoted from
/// <c>private</c> to <c>internal</c> (mirrors story 3.2's
/// <c>CooperativeLockCancellation.CancelSafely</c> pattern — reachable here
/// via the project's <c>InternalsVisibleTo</c> to
/// <c>CoreBankDemo.ServiceDefaults.Tests</c>). Each test seeds
/// <c>JAEGER_OTLP_ENDPOINT</c> via an in-memory configuration source added
/// last, so it deterministically wins over whatever the test host's real
/// environment variables happen to contain — no live OTLP collector, no
/// network calls.
/// </summary>
public class ResolveOtlpEndpointTests
{
    private static WebApplicationBuilder CreateBuilder(string? jaegerOtlpEndpoint)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Explicitly set (or explicitly null out) the key so this test is
        // deterministic regardless of any ambient JAEGER_OTLP_ENDPOINT the
        // host machine/CI happens to have set — the in-memory source is
        // added last, so it wins the configuration provider precedence.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JAEGER_OTLP_ENDPOINT"] = jaegerOtlpEndpoint,
        });
        return builder;
    }

    [Fact]
    public void Unset_returns_null()
    {
        var builder = CreateBuilder(null);

        var result = builder.ResolveOtlpEndpoint();

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_or_whitespace_returns_null(string value)
    {
        var builder = CreateBuilder(value);

        var result = builder.ResolveOtlpEndpoint();

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("http://jaeger:4317")]
    [InlineData("https://jaeger-collector:4318")]
    public void Absolute_http_or_https_uri_is_returned_unchanged(string value)
    {
        var builder = CreateBuilder(value);

        var result = builder.ResolveOtlpEndpoint();

        result.Should().Be(new Uri(value));
    }

    [Fact]
    public void Tcp_scheme_with_explicit_port_is_rewritten_to_http_and_keeps_the_port()
    {
        var builder = CreateBuilder("tcp://jaeger:4317");

        var result = builder.ResolveOtlpEndpoint();

        result.Should().Be(new Uri("http://jaeger:4317"));
    }

    [Fact]
    public void Tcp_scheme_with_a_non_default_explicit_port_keeps_that_exact_port()
    {
        var builder = CreateBuilder("tcp://jaeger:9411");

        var result = builder.ResolveOtlpEndpoint();

        result.Should().Be(new Uri("http://jaeger:9411"));
    }

    [Fact]
    public void Tcp_scheme_without_an_explicit_port_defaults_the_port_to_4317()
    {
        var builder = CreateBuilder("tcp://jaeger");

        var result = builder.ResolveOtlpEndpoint();

        result.Should().Be(new Uri("http://jaeger:4317"));
    }

    [Fact]
    public void Bare_host_port_is_normalized_to_an_http_uri()
    {
        var builder = CreateBuilder("jaeger:4317");

        var result = builder.ResolveOtlpEndpoint();

        result.Should().Be(new Uri("http://jaeger:4317"));
    }

    [Fact]
    public void Bare_ip_host_port_is_normalized_to_an_http_uri()
    {
        var builder = CreateBuilder("10.0.0.5:4317");

        var result = builder.ResolveOtlpEndpoint();

        result.Should().Be(new Uri("http://10.0.0.5:4317"));
    }

    [Fact]
    public void Unparseable_value_throws_InvalidOperationException()
    {
        var builder = CreateBuilder(":::not a uri");

        var act = () => builder.ResolveOtlpEndpoint();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*:::not a uri*");
    }
}
