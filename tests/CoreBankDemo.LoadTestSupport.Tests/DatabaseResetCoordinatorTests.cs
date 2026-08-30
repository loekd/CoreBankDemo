using AwesomeAssertions;
using CoreBankDemo.ServiceDefaults;
using Moq;
using Xunit;

namespace CoreBankDemo.LoadTestSupport.Tests;

public class DatabaseResetCoordinatorTests
{
    [Fact]
    public async Task Successful_reset_publishes_release_only_after_both_databases_are_done()
    {
        var calls = new List<string>();
        var resetter = new Mock<ILoadTestDatabaseResetter>();
        resetter.Setup(r => r.ResetAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("reset"))
            .ReturnsAsync(new DatabaseResetResult(10, 100_000_000m));
        var publisher = new Mock<IProcessorStartGatePublisher>();
        publisher.Setup(p => p.HasReleaseGenerationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        publisher.Setup(p => p.ReleaseAsync(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("release"))
            .Returns(Task.CompletedTask);
        var coordinator = CreateCoordinator(resetter.Object, publisher.Object);

        var result = await coordinator.ResetAndReleaseAsync(TestContext.Current.CancellationToken);

        calls.Should().Equal("reset", "release");
        result.Should().Be(new DatabaseResetResult(10, 100_000_000m));
    }

    [Fact]
    public async Task Failed_reset_never_releases_processors()
    {
        var resetter = new Mock<ILoadTestDatabaseResetter>();
        resetter.Setup(r => r.ResetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("core database reset failed"));
        var publisher = new Mock<IProcessorStartGatePublisher>(MockBehavior.Strict);
        publisher.Setup(p => p.HasReleaseGenerationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var coordinator = CreateCoordinator(resetter.Object, publisher.Object);

        var act = () => coordinator.ResetAndReleaseAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        publisher.Verify(p => p.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failed_release_is_reported_to_the_reset_caller()
    {
        var resetter = new Mock<ILoadTestDatabaseResetter>();
        resetter.Setup(r => r.ResetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseResetResult(10, 100_000_000m));
        var publisher = new Mock<IProcessorStartGatePublisher>();
        publisher.Setup(p => p.HasReleaseGenerationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        publisher.Setup(p => p.ReleaseAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("replicas did not acknowledge"));
        var state = new DatabaseResetState();
        var coordinator = new DatabaseResetCoordinator(resetter.Object, publisher.Object, state);

        var act = () => coordinator.ResetAndReleaseAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutException>();

        var retry = () => coordinator.ResetAndReleaseAsync(TestContext.Current.CancellationToken);
        await retry.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*restart the load-test AppHost*");
        resetter.Verify(r => r.ResetAsync(It.IsAny<CancellationToken>()), Times.Once);
        publisher.Verify(p => p.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Repeated_reset_returns_the_first_result_without_touching_open_processors()
    {
        var resetter = new Mock<ILoadTestDatabaseResetter>();
        resetter.Setup(r => r.ResetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatabaseResetResult(10, 100_000_000m));
        var publisher = new Mock<IProcessorStartGatePublisher>();
        publisher.Setup(p => p.HasReleaseGenerationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        publisher.Setup(p => p.ReleaseAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var coordinator = CreateCoordinator(resetter.Object, publisher.Object);

        await coordinator.ResetAndReleaseAsync(TestContext.Current.CancellationToken);
        await coordinator.ResetAndReleaseAsync(TestContext.Current.CancellationToken);

        resetter.Verify(r => r.ResetAsync(It.IsAny<CancellationToken>()), Times.Once);
        publisher.Verify(p => p.ReleaseAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Existing_release_generation_prevents_reset_after_support_service_restart()
    {
        var resetter = new Mock<ILoadTestDatabaseResetter>(MockBehavior.Strict);
        var publisher = new Mock<IProcessorStartGatePublisher>(MockBehavior.Strict);
        publisher.Setup(p => p.HasReleaseGenerationAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var coordinator = CreateCoordinator(resetter.Object, publisher.Object);

        var act = () => coordinator.ResetAndReleaseAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already released*");
        resetter.VerifyNoOtherCalls();
        publisher.VerifyAll();
    }

    private static DatabaseResetCoordinator CreateCoordinator(
        ILoadTestDatabaseResetter resetter,
        IProcessorStartGatePublisher publisher) =>
        new(resetter, publisher, new DatabaseResetState());
}
