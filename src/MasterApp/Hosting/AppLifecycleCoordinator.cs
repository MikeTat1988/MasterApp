using System.Collections.Concurrent;

namespace MasterApp.Hosting;

public sealed class AppLifecycleCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Enter(string appId)
    {
        var gate = GetGate(appId);
        gate.Wait();
        return new Lease(gate);
    }

    public async Task<IDisposable> EnterAsync(string appId, CancellationToken cancellationToken = default)
    {
        var gate = GetGate(appId);
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private SemaphoreSlim GetGate(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("App id is required.", nameof(appId));
        }

        return _gates.GetOrAdd(appId, static _ => new SemaphoreSlim(1, 1));
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Lease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
