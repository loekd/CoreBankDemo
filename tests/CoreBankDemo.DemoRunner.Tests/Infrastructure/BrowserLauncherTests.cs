using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Application;
using CoreBankDemo.DemoRunner.Infrastructure;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Infrastructure;

public class BrowserLauncherTests
{
    [Fact]
    public async Task OpenAsync_AspireDashboardWithNoVerifiedLoopbackUrl_NeverAttemptsAnOsLaunch()
    {
        var launcher = new BrowserLauncher();

        var result = await launcher.OpenAsync(KnownLinks.AspireDashboard, verifiedUrl: null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Url.Should().BeNull("there is nothing verified yet for the operator to see or copy");
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("https://not-loopback.example.com")]
    public async Task OpenAsync_AspireDashboardWithAnUnverifiedUrl_IsRejected(string verifiedUrl)
    {
        var launcher = new BrowserLauncher();

        var result = await launcher.OpenAsync(KnownLinks.AspireDashboard, verifiedUrl, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Url.Should().BeNull();
    }

    [Fact]
    public async Task OpenAsync_AspireDashboardWithAVerifiedLoopbackUrl_ReturnsThatUrlRegardlessOfWhetherAnOsBrowserIsReachable()
    {
        // No OS browser exists in this environment (e.g. the CI/sandbox), so the OS-level
        // launch attempt can legitimately fail -- but the verified URL must still come back
        // so the caller can offer it for terminal-side viewing/copying instead of silently
        // losing it.
        var launcher = new BrowserLauncher();
        const string verifiedUrl = "http://127.0.0.1:19999";

        var result = await launcher.OpenAsync(KnownLinks.AspireDashboard, verifiedUrl, CancellationToken.None);

        result.Url.Should().Be(verifiedUrl);
    }

    [Fact]
    public async Task OpenAsync_Jaeger_ResolvesItsFixedUrlRegardlessOfWhetherAnOsBrowserIsReachable()
    {
        var launcher = new BrowserLauncher();

        var result = await launcher.OpenAsync(KnownLinks.Jaeger, verifiedUrl: null, CancellationToken.None);

        result.Url.Should().NotBeNullOrWhiteSpace();
        result.Url.Should().Be(EndpointResolver.LinkFor(KnownLinks.Jaeger));
    }
}
