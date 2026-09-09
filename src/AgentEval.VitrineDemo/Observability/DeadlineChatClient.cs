// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Observability;

/// <summary>A model boundary exceeded its independently configured wall-clock budget.</summary>
public sealed class ModelCallTimeoutException : TimeoutException
{
    public ModelCallTimeoutException() : base("The model call exceeded its configured deadline.") { }
}

/// <summary>
/// Applies a deadline to each non-streaming and streaming model call. Caller cancellation remains
/// cancellation; only expiry of this client's private timer becomes
/// <see cref="ModelCallTimeoutException"/>.
/// </summary>
public sealed class DeadlineChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly TimeSpan _timeout;
    private readonly bool _ownsInner;

    public DeadlineChatClient(IChatClient inner, TimeSpan timeout, bool ownsInner = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeout = timeout;
        _ownsInner = ownsInner;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            return await _inner.GetResponseAsync(messages, options, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new ModelCallTimeoutException();
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        await using var enumerator = _inner
            .GetStreamingResponseAsync(messages, options, deadline.Token)
            .GetAsyncEnumerator(deadline.Token);

        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                throw new ModelCallTimeoutException();
            }

            if (!moved) yield break;
            yield return enumerator.Current;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        if (_ownsInner) _inner.Dispose();
    }
}
