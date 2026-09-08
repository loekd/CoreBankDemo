using AwesomeAssertions;
using CoreBankDemo.DemoRunner.Terminal;
using Xunit;

namespace CoreBankDemo.DemoRunner.Tests.Terminal;

/// <summary>
/// The console froze after a burst because every one of ~1200 state changes queued its own full
/// render and the operator's keystroke waited behind all of them. These tests pin both halves of
/// the fix: the burst collapses, and nothing is lost when it does.
/// </summary>
public class UiRepaintCoalescerTests
{
    [Fact]
    public void ABurstOfRequests_PostsExactlyOneRepaint()
    {
        var posted = new List<Action>();
        var coalescer = new UiRepaintCoalescer(posted.Add);
        var rendered = 0;

        for (var i = 0; i < 1200; i++)
        {
            coalescer.Request(() => rendered++);
        }

        posted.Should().ContainSingle("1200 state changes are one repaint's worth of work");

        posted.Single()();
        rendered.Should().Be(1);
    }

    [Fact]
    public void AfterThePendingRepaintRuns_TheNextRequestPostsAgain()
    {
        var posted = new List<Action>();
        var coalescer = new UiRepaintCoalescer(posted.Add);

        coalescer.Request(() => { });
        posted.Single()();

        coalescer.Request(() => { });

        posted.Should().HaveCount(2, "coalescing must not latch on and stop repainting");
    }

    /// <summary>
    /// The subtle one. If the pending flag were cleared *after* the render instead of before, a
    /// state change raised during that render would be folded into a repaint that had already
    /// read state, and the console would sit showing a value one behind -- which for this console
    /// means showing something untrue while looking perfectly healthy.
    /// </summary>
    [Fact]
    public void AChangeRaisedWhileRendering_StillSchedulesAFollowUp()
    {
        var posted = new List<Action>();
        var coalescer = new UiRepaintCoalescer(posted.Add);

        coalescer.Request(() => coalescer.Request(() => { }));
        posted.Single()();

        posted.Should().HaveCount(2, "the change raised mid-render must not be swallowed");
    }

    [Fact]
    public void ConcurrentRequests_NeverPostMoreThanOnePendingRepaint()
    {
        var posted = new List<Action>();
        var gate = new object();
        var coalescer = new UiRepaintCoalescer(action =>
        {
            lock (gate)
            {
                posted.Add(action);
            }
        });

        Parallel.For(0, 500, _ => coalescer.Request(() => { }));

        posted.Should().ContainSingle();
    }
}
