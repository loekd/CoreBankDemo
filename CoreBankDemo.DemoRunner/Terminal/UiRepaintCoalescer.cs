namespace CoreBankDemo.DemoRunner.Terminal;

/// <summary>
/// Collapses a burst of repaint requests into a single pending repaint on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// The controller raises a state change on every mutation, and one arriving outcome event causes
/// two of them. A 200-payment burst broadcasts three events per payment, so without this roughly
/// twelve hundred full renders queue on the UI thread while the burst drains -- and the operator's
/// next keystroke waits behind all of them. The console stays alive and keeps repainting
/// throughout, which is why it reads as a freeze rather than a crash.
/// </para>
/// <para>
/// Coalescing is lossless because the queued action re-reads current state when it runs, rather
/// than capturing the state that scheduled it. Dropping a request is therefore only ever dropping
/// a redundant read of the same source.
/// </para>
/// </remarks>
internal sealed class UiRepaintCoalescer(Action<Action> post)
{
    private int _queued;

    /// <summary>
    /// Requests a repaint. The first request posts <paramref name="render"/>; further requests
    /// made before it runs are folded into it.
    /// </summary>
    internal void Request(Action render)
    {
        if (Interlocked.Exchange(ref _queued, 1) == 1)
        {
            return;
        }

        post(() =>
        {
            // Cleared *before* the render, never after. A state change raised while rendering
            // must be able to schedule the follow-up that reflects it; clearing afterwards would
            // swallow that change and leave the console showing a state that is one behind --
            // which for this console means showing something untrue.
            Interlocked.Exchange(ref _queued, 0);
            render();
        });
    }
}
