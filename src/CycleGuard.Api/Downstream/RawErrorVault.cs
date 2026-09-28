using System.Collections.Concurrent;

namespace CycleGuard.Api.Downstream;

/// <summary>Unmasked synthetic error text, held only in memory.</summary>
public sealed record RawError(string Message, string StackTrace);

/// <summary>
/// The demo needs to show raw versus masked error text side by side, but the masking rule is
/// that nothing unmasked is ever persisted. So the raw synthetic text lives here: in process
/// memory, bounded, and gone on restart. The database only ever holds the masked form.
/// </summary>
public sealed class RawErrorVault
{
    private const int Capacity = 2000;

    private readonly ConcurrentDictionary<long, RawError> _items = new();
    private readonly ConcurrentQueue<long> _insertionOrder = new();

    public void Store(long attemptId, string? message, string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (_items.TryAdd(attemptId, new RawError(message, stackTrace ?? string.Empty)))
        {
            _insertionOrder.Enqueue(attemptId);
        }

        while (_insertionOrder.Count > Capacity && _insertionOrder.TryDequeue(out var evicted))
        {
            _items.TryRemove(evicted, out _);
        }
    }

    public RawError? Get(long attemptId) => _items.TryGetValue(attemptId, out var error) ? error : null;

    public void Clear()
    {
        _items.Clear();
        while (_insertionOrder.TryDequeue(out _))
        {
        }
    }
}
