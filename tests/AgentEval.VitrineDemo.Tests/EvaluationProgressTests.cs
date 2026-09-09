// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.Evals;

namespace AgentEval.VitrineDemo.Tests;

public sealed class EvaluationProgressTests
{
    [Fact]
    public async Task ProgressCarriesTypedVerdictsForAllControlsAndGates()
    {
        var progress = new RecordingEvaluationProgressSink();

        var result = await EvaluationSuite.RunAsync(cancellationToken: default, progress: progress);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(6, result.Gates.Count);
        Assert.DoesNotContain(result.Gates,
            static gate => gate.Name.Contains("control", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(result.Gates.Count,
            progress.Events.Count(item => item.Kind == EvaluationProgressKind.GateCompleted));
        Assert.DoesNotContain(progress.Events,
            static item => item.Kind == EvaluationProgressKind.GateCompleted &&
                string.Equals(item.Id, "controls", StringComparison.Ordinal));
        Assert.Equal(43,
            progress.Events.Count(item => item.Kind == EvaluationProgressKind.ControlCompleted));
        Assert.Equal(43,
            progress.Events.Count(item => item.Kind == EvaluationProgressKind.ControlHealthyCompleted));
        Assert.Equal(VitrineOfflineBenchmark.CheckKeys.Count,
            progress.Events.Count(item => item.Kind == EvaluationProgressKind.BenchmarkCheckCompleted));
        Assert.All(progress.Events.Where(item => item.Kind == EvaluationProgressKind.GateCompleted),
            item => Assert.NotNull(item.Gate));
        Assert.All(progress.Events.Where(item => item.Kind == EvaluationProgressKind.ControlCompleted),
            item => Assert.NotNull(item.Control));
        foreach (var control in result.Controls)
            Assert.Equal(
                [
                    EvaluationProgressKind.ControlStarted,
                    EvaluationProgressKind.ControlHealthyCompleted,
                    EvaluationProgressKind.ControlBrokenCompleted,
                    EvaluationProgressKind.ControlRestoredCompleted,
                    EvaluationProgressKind.ControlCompleted,
                ],
                progress.Events.Where(item => item.Id == control.Id).Select(item => item.Kind));
    }
}
