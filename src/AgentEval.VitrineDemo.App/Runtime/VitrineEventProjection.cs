// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using AgentEval.VitrineDemo.App.Models;

namespace AgentEval.VitrineDemo.App.Runtime;

/// <summary>
/// Optional audience pacing over already-recorded facts. Publishing never waits; therefore this
/// projection cannot affect execution timing or verdicts.
/// </summary>
public sealed class VitrineEventProjection : IAsyncDisposable
{
    private readonly Channel<VitrineEvent> _channel = Channel.CreateUnbounded<VitrineEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Func<int> _delayMilliseconds;
    private readonly Func<VitrineEvent, Task> _present;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _pump;
    private int _disposed;

    public VitrineEventProjection(Func<int> delayMilliseconds, Func<VitrineEvent, Task> present)
    {
        _delayMilliseconds = delayMilliseconds ?? throw new ArgumentNullException(nameof(delayMilliseconds));
        _present = present ?? throw new ArgumentNullException(nameof(present));
        _pump = PumpAsync();
    }

    public bool TryPublish(VitrineEvent item) => _channel.Writer.TryWrite(item);

    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        try { await _pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task PumpAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
        {
            var delay = Math.Max(0, _delayMilliseconds());
            if (delay > 0) await Task.Delay(delay, _cancellation.Token).ConfigureAwait(false);
            try { await _present(item).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancellation.Cancel();
        _channel.Writer.TryComplete();
        try { await _pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cancellation.Dispose();
    }
}
