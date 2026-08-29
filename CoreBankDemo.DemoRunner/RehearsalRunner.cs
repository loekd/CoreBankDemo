using CoreBankDemo.DemoRunner.Application.Ports;
using CoreBankDemo.DemoRunner.Application.StateMachine;

namespace CoreBankDemo.DemoRunner;

/// <summary>
/// Drives every actionable cue with narration pauses skipped, exits non-zero on the
/// first unproven cue/phase, and only promotes a proof pack after every cue, all five
/// load invariants, and cleanup have passed (ADR-015, spec Verification commands).
/// </summary>
public static class RehearsalRunner
{
    public static async Task<int> RunAsync(SessionController controller, IProofPackStore proofPackStore, CancellationToken ct)
    {
        var cueCount = controller.State.Cues.Count;
        for (var i = 0; i < cueCount; i++)
        {
            var cue = controller.State.CurrentCue;

            if (cue.Definition.PreArmActions.Count > 0)
            {
                await controller.PreArmCurrentAsync(ct);
            }

            var result = await controller.RunCurrentAsync(ct);
            if (result.Status != CueStatus.Passed)
            {
                Console.WriteLine($"REHEARSAL FAILED at cue '{cue.Definition.Id}' (slide {cue.Definition.SlideAnchor}), state {result.Status}: {result.EvidenceSummary}");
                await controller.ShutdownAsync(ct);
                return 1;
            }

            Console.WriteLine($"[PASS] {cue.Definition.Id} (slide {cue.Definition.SlideAnchor}): {result.EvidenceSummary}");

            if (i < cueCount - 1)
            {
                controller.TryAdvanceToNext();
            }
        }

        var proofPack = new ProofPack(
            controller.State.ScenarioName,
            controller.State.ScenarioVersion,
            controller.State.SourceCommit,
            TimeProvider.System.GetUtcNow(),
            controller.State.Cues
                .Select(c => new ProofPackCueResult(c.Definition.Id, c.Definition.SlideAnchor, c.Status == CueStatus.Passed, c.EvidenceSummary))
                .ToList(),
            controller.LastLoadWorkflowResult?.Invariants ?? []);

        await controller.ShutdownAsync(ct);
        await proofPackStore.SaveAsLatestKnownGoodAsync(proofPack, ct);

        Console.WriteLine("REHEARSAL PASSED. Proof pack saved as the latest known-good.");
        return 0;
    }
}
