namespace WindowsIntelligence;

// A lease protects native model handles while loading, decoding or synthesising.
// The deadline starts at the end of speech; an in-flight operation may finish safely.
internal sealed class IdleModelCache : IDisposable
{
    internal static readonly TimeSpan Retention = TimeSpan.FromSeconds(30);
    private readonly object gate = new();
    private readonly Action release;
    private readonly Func<DateTimeOffset> now;
    private readonly Timer timer;
    private readonly TimeSpan retention;
    private DateTimeOffset deadline;
    private int users;
    private bool disposed, expired = true;

    internal IdleModelCache(Action release, TimeSpan? retention = null, Func<DateTimeOffset>? now = null)
    {
        this.release = release;
        this.retention = retention ?? Retention;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        timer = new Timer(_ => TryExpire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal IDisposable Acquire()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            users++;
            expired = false;
            deadline = now() + retention;
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return new Lease(this);
        }
    }

    internal void SpeechEnded()
    {
        lock (gate)
        {
            if (disposed || expired) return;
            deadline = now() + retention;
            Schedule();
        }
    }

    private void EndLease()
    {
        lock (gate)
        {
            users--;
            if (users == 0 && !disposed && !expired) Schedule();
        }
    }

    private void Schedule() => timer.Change(
        TimeSpan.FromMilliseconds(Math.Max(1, (deadline - now()).TotalMilliseconds)), Timeout.InfiniteTimeSpan);

    internal bool TryExpire()
    {
        lock (gate)
        {
            if (disposed || expired || users != 0 || now() < deadline) return false;
            release();
            expired = true;
            timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            timer.Dispose();
        }
    }

    private sealed class Lease(IdleModelCache owner) : IDisposable
    {
        private IdleModelCache? cache = owner;
        public void Dispose() => Interlocked.Exchange(ref cache, null)?.EndLease();
    }
}
