namespace DN42Atlas.OptOut.ManualRequests;

// Global, process-local fixed window: forwarded headers and caller-controlled identities are irrelevant.
public sealed class ContactRateLimiter(TimeProvider? clock = null, int limit = 10)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private DateTimeOffset start;
    private int count;
    public bool TryAcquire()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            if (now - start >= TimeSpan.FromMinutes(1)) { start = now; count = 0; }
            return count < limit && ++count <= limit;
        }
    }
}
