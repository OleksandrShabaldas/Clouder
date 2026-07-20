namespace Clouder.Storage;

/// <summary>
/// A token-bucket rate limiter shared by every concurrent transfer in one direction.
///
/// Previously each transfer throttled itself independently, so a 1 MB/s cap with four
/// concurrent uploads actually allowed 4 MB/s. One limiter shared across transfers makes
/// the configured speed limit mean what it says.
///
/// Tokens accrue at the configured rate, up to one second's worth, so short bursts of
/// small reads aren't penalised.
/// </summary>
public sealed class BandwidthLimiter
{
    private readonly Func<long> _nowMs;
    private readonly object _gate = new();

    private double _tokens;
    private long _lastRefillMs;
    private long _bytesPerSecond;

    public BandwidthLimiter(long bytesPerSecond = 0, Func<long>? nowMs = null)
    {
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        // Start with the budget available: a transfer shouldn't stall for a second
        // before its first byte just because the limiter was created a moment ago.
        _tokens = _bytesPerSecond;
        _lastRefillMs = _nowMs();
    }

    /// <summary>Bytes per second. 0 means unlimited.</summary>
    public long BytesPerSecond
    {
        get { lock (_gate) return _bytesPerSecond; }
        set
        {
            lock (_gate)
            {
                _bytesPerSecond = Math.Max(0, value);
                // Reconfiguring the limit grants a fresh allowance, so changing the
                // setting takes effect immediately instead of after a stall.
                _tokens = _bytesPerSecond;
                _lastRefillMs = _nowMs();
            }
        }
    }

    /// <summary>
    /// Tries to consume <paramref name="bytes"/> from the budget. When there aren't
    /// enough tokens yet, returns false and how long to wait before retrying.
    /// </summary>
    public bool TryConsume(int bytes, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (bytes <= 0) return true;

        lock (_gate)
        {
            if (_bytesPerSecond <= 0) return true; // unlimited

            Refill();

            // A read larger than the whole per-second budget can never fit in the
            // bucket, so waiting for enough tokens would deadlock the transfer.
            // Let it through once the bucket is full, which paces it correctly.
            if (bytes > _bytesPerSecond)
            {
                if (_tokens >= _bytesPerSecond)
                {
                    _tokens = 0;
                    return true;
                }
                retryAfter = TimeSpan.FromSeconds((_bytesPerSecond - _tokens) / (double)_bytesPerSecond);
                return false;
            }

            if (_tokens >= bytes)
            {
                _tokens -= bytes;
                return true;
            }

            retryAfter = TimeSpan.FromSeconds((bytes - _tokens) / (double)_bytesPerSecond);
            return false;
        }
    }

    /// <summary>Waits until <paramref name="bytes"/> can be consumed from the budget.</summary>
    public async ValueTask ConsumeAsync(int bytes, CancellationToken ct = default)
    {
        while (!TryConsume(bytes, out var retryAfter))
        {
            ct.ThrowIfCancellationRequested();
            if (retryAfter <= TimeSpan.Zero) retryAfter = TimeSpan.FromMilliseconds(1);
            await Task.Delay(retryAfter, ct);
        }
    }

    private void Refill()
    {
        long now = _nowMs();
        long elapsedMs = now - _lastRefillMs;
        if (elapsedMs <= 0) return;

        _lastRefillMs = now;
        _tokens = Math.Min(_bytesPerSecond, _tokens + _bytesPerSecond * (elapsedMs / 1000.0));
    }
}

/// <summary>
/// The upload and download budgets shared by every sync service, so speed limits apply
/// across all transfers rather than per transfer.
/// </summary>
public sealed class TransferBudget
{
    public BandwidthLimiter Upload { get; } = new();
    public BandwidthLimiter Download { get; } = new();
}
