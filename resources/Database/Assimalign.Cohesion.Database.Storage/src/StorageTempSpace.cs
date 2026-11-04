using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using Assimalign.Cohesion.Database.Storage.Internal;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>Owns an engine's disposable query files and atomically accounts physical pages.</summary>
/// <remarks>No journal, recovery, transactions or shared buffer pool participates. Dispose statements
/// after success, failure or cancellation. A directory's active files belong to one space.</remarks>
[DebuggerDisplay("StorageTempSpace {EngineQuota}")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class StorageTempSpace : IDisposable
{
    /// <summary>The physical page size in bytes, including framing and CRC-32C.</summary>
    public const int PageSize = 8192;
    private readonly HashSet<StorageTempStatement> _statements = [];
    private readonly Func<long> _availableBytes;
    private readonly Func<long> _freeSpaceFloor;
    private FileStream? _lease;
    private long _bytes;
    private long _pendingBytes;
    private bool _disposed;
    internal readonly object Gate = new();
    internal Action? BeforePageWrite { get; set; }
    internal long Bytes => _bytes;

    private StorageTempSpace(FileSystemPath directory, long? engineQuota, Func<long> floor, Func<long>? available)
    {
        DirectoryPath = Path.GetFullPath(directory);
        _availableBytes = available ?? (() => StorageTempVolumeSpace.AvailableBytes(DirectoryPath));
        _freeSpaceFloor = floor;
        // Measured once at engine creation, before the startup sweep. The floor is independent
        // and is measured again before each page reservation, under Gate.
        EngineQuota = engineQuota ?? Math.Min(10L * 1024 * 1024 * 1024, Math.Max(0, _availableBytes()) / 4);
        if (Directory.Exists(DirectoryPath))
        {
            OpenDirectory();
            // An idle engine open leaves no lock sidecar behind a refused format preflight.
            // The first run reacquires the lease and sweeps again before holding it until close.
            _lease!.Dispose(); _lease = null;
        }
    }

    /// <summary>Creates a space, sweeping owned leftover files if its directory already exists.</summary>
    /// <param name="directory">A dedicated directory; creation is deferred until the first run.</param>
    /// <param name="engineQuota">Physical bytes allowed; null uses min(10 GiB, 25% of free bytes measured now).</param>
    /// <param name="freeSpaceFloor">Nonnegative engine reserve in bytes, sampled before each page; null means zero.</param>
    /// <returns>The exclusively owned temporary space.</returns>
    /// <exception cref="ArgumentException">The directory is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The quota is negative.</exception>
    /// <exception cref="IOException">Another space owns the directory or a sweep fails.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory cannot be accessed.</exception>
    /// <remarks>The reserve callback runs under the temp-space monitor and must not acquire
    /// engine registry locks or storage latches. Use a constant or a lock-free snapshot.</remarks>
    public static StorageTempSpace Create(FileSystemPath directory, long? engineQuota = null, Func<long>? freeSpaceFloor = null)
        => CreateCore(directory, engineQuota, freeSpaceFloor ?? (() => 0), null);

    internal static StorageTempSpace CreateCore(FileSystemPath directory, long? engineQuota, Func<long> floor, Func<long>? available)
    {
        if (directory.IsEmpty) { throw new ArgumentException(ErrorMessages.TempDirectoryEmpty, nameof(directory)); }
        if (engineQuota is { } quota) { ArgumentOutOfRangeException.ThrowIfNegative(quota); }
        return new StorageTempSpace(directory, engineQuota, floor, available);
    }

    /// <summary>Gets the normalized dedicated directory.</summary>
    public string DirectoryPath { get; }
    /// <summary>Gets the engine quota fixed at creation, in physical bytes.</summary>
    public long EngineQuota { get; }

    /// <summary>Creates a statement lifetime with a physical-byte quota.</summary>
    /// <param name="quota">The statement quota; the engine normally supplies four times its memory budget.</param>
    /// <returns>A statement whose disposal closes every run it owns.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The quota is negative.</exception>
    /// <exception cref="ObjectDisposedException">The space is disposed.</exception>
    public StorageTempStatement CreateStatement(long quota)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quota);
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var statement = new StorageTempStatement(this, quota);
            _statements.Add(statement);
            return statement;
        }
    }

    internal FileStream CreateFile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lease is null) { OpenDirectory(); }
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.Read | FileShare.Delete, BufferSize = 0, Options = FileOptions.DeleteOnClose | FileOptions.RandomAccess };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = File.GetUnixFileMode(DirectoryPath) & (UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
        return new FileStream(Path.Combine(DirectoryPath, "cohesion-" + Guid.NewGuid().ToString("N") + ".ctmp"), options);
    }

    private void OpenDirectory()
    {
        // No temp directory is created when an engine only probes an incompatible file set.
        // Lease before sweeping: another engine must never delete live runs.
        if (!Directory.Exists(DirectoryPath))
        {
            if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(DirectoryPath); }
            else
            {
                string parent = Path.GetDirectoryName(DirectoryPath)!;
                Directory.CreateDirectory(parent);
                Directory.CreateDirectory(DirectoryPath, File.GetUnixFileMode(parent));
            }
        }
        string leasePath = Path.Combine(DirectoryPath, ".cohesion-temp.lock");
        var lease = new FileStream(leasePath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // Unix FileStream can ignore unsupported flock operations. Verify exclusion
                // before sweeping, rather than mistaking an unprotected handle for ownership.
                bool exclusive = false;
                try { using var probe = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when (error.HResult is 11 or 35) { exclusive = true; }
                if (!exclusive) { throw new IOException(ErrorMessages.TempLeaseUnsupported); }
            }
            foreach (string path in Directory.EnumerateFiles(DirectoryPath, "cohesion-*.ctmp"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.Length == 41 && Guid.TryParseExact(name.AsSpan(9), "N", out _)) { File.Delete(path); }
            }
            _lease = lease;
        }
        catch { lease.Dispose(); throw; } // A failed sweep must release the directory lease.
    }

    internal void Reserve(StorageTempStatement statement)
    {
        string? code = statement.Bytes > statement.Quota - PageSize ? StorageTempException.StatementQuotaCode
            : _bytes > EngineQuota - PageSize ? StorageTempException.EngineQuotaCode : null;
        if (code is null)
        {
            long floor = _freeSpaceFloor();
            ArgumentOutOfRangeException.ThrowIfNegative(floor);
            long free = _availableBytes();
            if (free < floor || free - floor < _pendingBytes + PageSize) { code = StorageTempException.FreeSpaceCode; }
        }
        if (code is not null)
        {
            StorageEventSource.Log.TempRefused(code);
            throw new StorageTempException(code, ErrorMessages.TempSpaceRefused);
        }
        _bytes += PageSize; _pendingBytes += PageSize; statement.Bytes += PageSize;
        StorageEventSource.Log.TempBytesChanged(PageSize);
    }
    internal void Written() { _pendingBytes -= PageSize; StorageEventSource.Log.TempPageWritten(); }
    internal void Release(StorageTempStatement statement, long bytes, long pending)
    {
        _bytes -= bytes; _pendingBytes -= pending; statement.Bytes -= bytes;
        if (bytes != 0) { StorageEventSource.Log.TempBytesChanged(-bytes); }
    }
    internal void Forget(StorageTempStatement statement)
    {
        if (!_disposed) { _statements.Remove(statement); }
    }

    /// <summary>Closes every statement and file; repeated calls do nothing.</summary>
    /// <exception cref="AggregateException">One or more closes failed after every statement was released.</exception>
    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            List<Exception>? failures = null;
            try
            {
                foreach (var statement in _statements)
                {
                    try { statement.Dispose(); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { (failures ??= []).Add(error); } // Cleanup continues through all statements.
                }
            }
            finally
            {
                _statements.Clear();
                try { _lease?.Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { (failures ??= []).Add(error); }
                finally { _lease = null; }
            }
            if (failures is not null) { throw new AggregateException(ErrorMessages.TempCleanupFailed, failures); }
        }
    }
    private sealed class DebugView
    {
        private readonly StorageTempSpace _target;
        public DebugView(StorageTempSpace target) { _target = target; }
        public string DirectoryPath => _target.DirectoryPath;
        public long EngineQuota => _target.EngineQuota;
    }
}
