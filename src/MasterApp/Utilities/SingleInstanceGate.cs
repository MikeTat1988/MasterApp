namespace MasterApp.Utilities;

public static class SingleInstanceGate
{
    public static SingleInstanceLease TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var mutex = new Mutex(initiallyOwned: false, name, out _);
        try
        {
            if (mutex.WaitOne(0))
            {
                return new SingleInstanceLease(true, mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceLease(true, mutex);
        }

        mutex.Dispose();
        return new SingleInstanceLease(false, null);
    }
}

public sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _disposed;

    internal SingleInstanceLease(bool isAcquired, Mutex? mutex)
    {
        IsAcquired = isAcquired;
        _mutex = mutex;
    }

    public bool IsAcquired { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The OS already released the mutex or ownership was lost.
        }

        _mutex.Dispose();
    }
}
