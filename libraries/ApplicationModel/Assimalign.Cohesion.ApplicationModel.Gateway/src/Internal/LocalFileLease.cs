using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Holds a cooperative process-local and cross-process lease on a stable sidecar file.
/// The sidecar is deliberately retained after release so every process continues to lock
/// the same file-system identity.
/// </summary>
internal sealed class LocalFileLease : IDisposable
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly FileStream _stream;
    private readonly SemaphoreSlim _processGate;
    private int _disposed;

    private LocalFileLease(FileStream stream, SemaphoreSlim processGate)
    {
        _stream = stream;
        _processGate = processGate;
    }

    public static async Task<LocalFileLease> AcquireAsync(
        string path,
        bool waitForAvailability,
        string contentionMessage,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        SemaphoreSlim processGate = ProcessGates.GetOrAdd(
            fullPath,
            static _ => new SemaphoreSlim(1, 1));
        bool processGateHeld;
        if (waitForAvailability)
        {
            await processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            processGateHeld = true;
        }
        else
        {
            processGateHeld = await processGate
                .WaitAsync(TimeSpan.Zero, cancellationToken)
                .ConfigureAwait(false);
            if (!processGateHeld)
            {
                throw new InvalidOperationException(contentionMessage);
            }
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream? stream = null;
                try
                {
                    stream = new FileStream(
                        fullPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        OperatingSystem.IsMacOS() ? FileShare.None : FileShare.ReadWrite,
                        bufferSize: 1,
                        FileOptions.None);
                    if (!OperatingSystem.IsMacOS())
                    {
                        stream.Lock(0, 1);
                    }
                    if (stream.Length == 0)
                    {
                        stream.SetLength(1);
                    }

                    return new LocalFileLease(stream, processGate);
                }
                catch (IOException exception) when (IsLockContention(exception))
                {
                    stream?.Dispose();
                    if (!waitForAvailability)
                    {
                        throw new InvalidOperationException(contentionMessage, exception);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    stream?.Dispose();
                    throw;
                }
            }
        }
        catch
        {
            processGate.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _stream.Dispose();
        }
        finally
        {
            _processGate.Release();
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        int nativeError = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows()
            ? nativeError is 32 or 33
            : nativeError is 11 or 13 or 35;
    }
}
