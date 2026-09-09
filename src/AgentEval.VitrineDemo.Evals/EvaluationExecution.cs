// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals;
internal sealed class CountingEvaluator(AgentEval.Core.IEvaluator inner) : AgentEval.Core.IEvaluator {
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);
    public Task<AgentEval.Core.EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria,
        CancellationToken cancellationToken = default) {
        Interlocked.Increment(ref _callCount);
        return inner.EvaluateAsync(input, output, criteria, cancellationToken);
    }
}
