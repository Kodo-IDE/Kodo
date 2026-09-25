using System.Security.Cryptography;
using System.Text;

namespace KodoUpdater;

// Serializes updater operations that target the same Kodo installation, even
// when they were launched for different staged transaction IDs. A dedicated
// thread owns the mutex so async continuations can release it safely.
internal sealed class InstallUpdateGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly ManualResetEventSlim _acquired = new(false);
    private readonly ManualResetEventSlim _releaseRequested = new(false);
    private readonly Thread _ownerThread;
    private int _ownsMutex;
    private int _disposed;

    private InstallUpdateGuard(Mutex mutex)
    {
        _mutex = mutex;
        _ownerThread = new Thread(OwnMutex)
        {
            IsBackground = true,
            Name = "Kodo updater install guard",
        };
        _ownerThread.Start();
        _acquired.Wait();
    }

    public static InstallUpdateGuard? TryAcquire(string installRoot)
    {
        Mutex? mutex = null;
        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
            if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
            var name = OperatingSystem.IsWindows()
                ? $"Global\\Kodo-Updater-Install-{hash}"
                : $"Kodo-Updater-Install-{hash}";
            mutex = new Mutex(initiallyOwned: false, name, out _);
            var guard = new InstallUpdateGuard(mutex);
            mutex = null;
            if (Volatile.Read(ref guard._ownsMutex) != 1)
            {
                guard.Dispose();
                return null;
            }
            return guard;
        }
        catch
        {
            try { mutex?.Dispose(); } catch { }
            return null;
        }
    }

    private void OwnMutex()
    {
        try
        {
            try { Interlocked.Exchange(ref _ownsMutex, _mutex.WaitOne(TimeSpan.Zero, exitContext: false) ? 1 : 0); }
            catch (AbandonedMutexException) { Interlocked.Exchange(ref _ownsMutex, 1); }
        }
        catch { Interlocked.Exchange(ref _ownsMutex, 0); }
        finally { _acquired.Set(); }

        if (Volatile.Read(ref _ownsMutex) != 1) return;
        _releaseRequested.Wait();
        try { _mutex.ReleaseMutex(); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _releaseRequested.Set();
        if (Thread.CurrentThread != _ownerThread)
            _ownerThread.Join();
        try { _mutex.Dispose(); } catch { }
        _acquired.Dispose();
        _releaseRequested.Dispose();
    }
}
