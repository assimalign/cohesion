using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

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
    private readonly long? _requestedQuota;
    private readonly object _directoryGate = new();
    private readonly object _probeGate = new();
    private FileStream? _lease;
    private long _bytes;
    private long _pendingBytes;
    private long _reservedTotal;
    private long _releasedTotal;
    private long _probeReserved;
    private long _probeReleased;
    private long _availableAtProbe;
    private long _probeTick;
    private int _unavailableWarning;
    private bool _quotaKnown;
    private bool _disposed;
    // Lock order: run lock, then _probeGate, then Gate; never the reverse. Gate covers only accounting and
    // registries. No syscall or event listener runs under it. Future callers must not hold
    // a storage page latch across temporary-space calls. Directory/probe gates never enter runs.
    internal readonly object Gate = new();
    internal Action? BeforeFileCreate { get; set; }
    internal Action? BeforePageWrite { get; set; }
    internal Action? BeforePageRead { get; set; }
    internal long Bytes { get { lock (Gate) { return _bytes; } } }
    internal long PendingBytes { get { lock (Gate) { return _pendingBytes; } } }

    private StorageTempSpace(FileSystemPath directory, long? engineQuota, Func<long> floor, Func<long>? available)
    {
        DirectoryPath = directory;
        _requestedQuota = engineQuota;
        _availableBytes = available ?? (() => StorageTempVolumeSpace.AvailableBytes(DirectoryPath));
        _freeSpaceFloor = floor;
        try
        {
            DirectoryPath = Path.GetFullPath(directory);
            if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath) || new DirectoryInfo(DirectoryPath).LinkTarget is not null)
            { OpenDirectory(); }
            // Fix the quota after sweeping: orphan bytes must not reduce a lifetime's quota.
            InitializeProbe();
            ReleaseLease();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            EngineQuota = 0; _quotaKnown = false;
            try { ReleaseLease(); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { _ = StorageTempException.Unavailable(cleanup); } // Startup remains available; preserve the first cause.
            WarnUnavailable(error);
        }
    }

    /// <summary>Creates a space, sweeping owned leftover files if its directory already exists.</summary>
    /// <param name="directory">A dedicated directory; creation is deferred until the first run.</param>
    /// <param name="engineQuota">Physical bytes allowed; null uses min(10 GiB, 25% of free bytes after sweeping).</param>
    /// <param name="freeSpaceFloor">Required nonnegative engine reserve, supplied as a constant or lock-free snapshot.</param>
    /// <returns>A space; unavailable temporary storage warns and is retried by the first run.</returns>
    /// <exception cref="ArgumentException">The directory is empty or default.</exception>
    /// <exception cref="ArgumentNullException">The reserve callback is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The quota is negative.</exception>
    /// <remarks>A directory belongs to one spilling engine. Choose a different TempDirectory for
    /// another engine. The reserve callback must not acquire engine registry locks or page latches.</remarks>
    public static StorageTempSpace Create(FileSystemPath directory, long? engineQuota, Func<long> freeSpaceFloor)
        => CreateCore(directory, engineQuota, freeSpaceFloor, null);

    internal static StorageTempSpace CreateCore(FileSystemPath directory, long? engineQuota, Func<long> floor, Func<long>? available)
    {
        if (directory == default || directory.IsEmpty) { throw new ArgumentException(ErrorMessages.TempDirectoryEmpty, nameof(directory)); }
        ArgumentNullException.ThrowIfNull(floor);
        if (engineQuota is { } quota) { ArgumentOutOfRangeException.ThrowIfNegative(quota); }
        return new StorageTempSpace(directory, engineQuota, floor, available);
    }

    /// <summary>Gets the dedicated directory, normalized when path setup succeeds.</summary>
    public string DirectoryPath { get; }
    /// <summary>Gets the physical-byte quota fixed after sweeping, or zero until an unavailable probe succeeds.</summary>
    public long EngineQuota { get; private set; }

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
            Check();
            var statement = new StorageTempStatement(this, quota);
            _statements.Add(statement); return statement;
        }
    }

    internal FileStream CreateFile()
    {
        try
        {
            lock (_directoryGate)
            {
                Check();
                if (_lease is null)
                {
                    try { OpenDirectory(); if (!_quotaKnown) { InitializeProbe(); } }
                    catch
                    {
                        try { ReleaseLease(); }
                        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                        { _ = StorageTempException.Unavailable(cleanup); } // Preserve the setup cause.
                        throw;
                    }
                }
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
                    Share = FileShare.Delete, BufferSize = 0, Options = FileOptions.DeleteOnClose | FileOptions.SequentialScan };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                BeforeFileCreate?.Invoke();
                return new FileStream(Path.Combine(DirectoryPath, "cohesion-" + Guid.NewGuid().ToString("N") + ".ctmp"), options);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { WarnUnavailable(error); throw StorageTempException.Unavailable(error); }
    }

    private void WarnUnavailable(Exception cause)
    {
        if (Interlocked.Exchange(ref _unavailableWarning, 1) == 0)
        { StorageEventSource.Log.TempUnavailable(StorageTempException.UnavailableCode, cause); }
    }

    private void OpenDirectory()
    {
        string leasePath = Path.Combine(DirectoryPath, ".cohesion-temp.lock");
        // Check before opening: Windows DeleteOnClose follows a symbolic link to its target.
        if (new DirectoryInfo(DirectoryPath).LinkTarget is not null || new FileInfo(leasePath).LinkTarget is not null)
        { throw new IOException(ErrorMessages.TempDirectoryUnsafe); }
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(DirectoryPath); }
        else
        {
            Directory.CreateDirectory(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetUnixFileMode(DirectoryPath) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            { throw new IOException(ErrorMessages.TempDirectoryUnsafe); }
        }
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite,
            Share = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None, BufferSize = 1,
            Options = OperatingSystem.IsWindows() ? FileOptions.None : FileOptions.DeleteOnClose };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        var lease = new FileStream(leasePath, options);
        try
        {
            // Recheck after opening to narrow a same-owner replacement race.
            if (new FileInfo(leasePath).LinkTarget is not null)
            { throw new IOException(ErrorMessages.TempDirectoryUnsafe); }
            if (!OperatingSystem.IsWindows())
            {
                // Unix lock verification relies on the delete-on-close inode verification.
                File.SetUnixFileMode(lease.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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
        catch
        {
            try { if (OperatingSystem.IsWindows()) { File.Delete(leasePath); } }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { _ = StorageTempException.Unavailable(cleanup); }
            try { lease.Dispose(); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { _ = StorageTempException.Unavailable(cleanup); }
            throw;
        }
    }
    private void ReleaseLease()
    {
        var lease = _lease; _lease = null;
        if (lease is null) { return; }
        try { if (OperatingSystem.IsWindows()) { File.Delete(Path.Combine(DirectoryPath, ".cohesion-temp.lock")); } }
        catch
        {
            try { lease.Dispose(); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { _ = StorageTempException.Unavailable(cleanup); }
            throw;
        }
        lease.Dispose();
    }
    private long ProbeAvailable()
    {
        try { return Math.Max(0, _availableBytes()); }
        catch (OverflowException) { return long.MaxValue; }
    }
    private void InitializeProbe()
    {
        lock (_probeGate)
        {
            long free = ProbeAvailable();
            lock (Gate)
            {
                _availableAtProbe = free; _probeReserved = _reservedTotal; _probeReleased = _releasedTotal;
                _probeTick = Environment.TickCount64;
                if (!_quotaKnown) { EngineQuota = _requestedQuota ?? Math.Min(10L * 1024 * 1024 * 1024, free / 4); }
                _quotaKnown = true;
            }
        }
    }
    private bool ProbeDue() => _reservedTotal - _probeReserved >= 128L * PageSize ||
        Environment.TickCount64 - _probeTick > 1000;

    private void RefreshProbe(bool required = false)
    {
        if (!required) { lock (Gate) { if (!ProbeDue()) { return; } } }
        if (required) { Monitor.Enter(_probeGate); }
        else if (!Monitor.TryEnter(_probeGate)) { return; }
        try
        {
            long reserved, released, pending;
            lock (Gate)
            {
                if (!required && !ProbeDue()) { return; }
                reserved = _reservedTotal; released = _releasedTotal; pending = _pendingBytes;
            }
            long free = ProbeAvailable(); // Never a syscall under Gate.
            lock (Gate)
            {
                // A close may already be reflected in free. Keep the preceding sample and its
                // credits rather than either dropping that release or crediting it twice.
                // The unchanged window/timestamp makes the next reservation eligible to retry.
                if (_releasedTotal != released) { return; }
                _availableAtProbe = Math.Max(0, free - pending); _probeReserved = reserved;
                _probeReleased = released; _probeTick = Environment.TickCount64;
            }
        }
        finally { Monitor.Exit(_probeGate); }
    }
    internal void Reserve(StorageTempStatement statement)
    {
        RefreshProbe();
        string? code;
        for (int attempt = 0; ; attempt++)
        {
            long floor = _freeSpaceFloor();
            ArgumentOutOfRangeException.ThrowIfNegative(floor);
            lock (Gate)
            {
                statement.Check();
                code = statement.Bytes > statement.Quota - PageSize ? StorageTempException.StatementQuotaCode
                    : _bytes > EngineQuota - PageSize ? StorageTempException.EngineQuotaCode : null;
                long free = _availableAtProbe - (_reservedTotal - _probeReserved);
                long released = _releasedTotal - _probeReleased;
                free = free > long.MaxValue - released ? long.MaxValue : free + released;
                if (code is null && (free < floor || free - floor < PageSize)) { code = StorageTempException.FreeSpaceCode; }
                if (code is null)
                {
                    _bytes += PageSize; _pendingBytes += PageSize; _reservedTotal += PageSize;
                    statement.Bytes += PageSize; statement.Peak = Math.Max(statement.Peak, statement.Bytes);
                }
            }
            if (code != StorageTempException.FreeSpaceCode || attempt != 0) { break; }
            // A refusal ends the statement: pay once for a fresh sample before refusing it.
            RefreshProbe(required: true);
        }
        if (code is not null)
        {
            StorageEventSource.Log.TempRefused(code);
            string message = code == StorageTempException.StatementQuotaCode ? ErrorMessages.TempStatementQuota
                : code == StorageTempException.EngineQuotaCode ? ErrorMessages.TempEngineQuota : ErrorMessages.TempFreeSpace;
            throw new StorageTempException(code, message);
        }
        StorageEventSource.Log.TempBytesChanged(PageSize);
    }
    internal void Written()
    {
        lock (Gate) { _pendingBytes -= PageSize; }
        StorageEventSource.Log.TempPageWritten();
    }
    internal void Release(StorageTempStatement statement, long bytes, long pending, bool reclaimed)
    {
        lock (Gate) { _bytes -= bytes; _pendingBytes -= pending; statement.Bytes -= bytes; _releasedTotal += reclaimed ? bytes : pending; }
        if (bytes != 0) { StorageEventSource.Log.TempBytesChanged(-bytes); }
    }
    internal void Check() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
    internal void Forget(StorageTempStatement statement) { lock (Gate) { _statements.Remove(statement); } }

    /// <summary>Closes every statement and file; repeated calls do nothing.</summary>
    /// <exception cref="AggregateException">One or more coded closes failed after every statement was released.</exception>
    public void Dispose()
    {
        StorageTempStatement[] statements;
        lock (Gate)
        {
            if (_disposed) { return; }
            _disposed = true; statements = [.. _statements]; _statements.Clear();
        }
        List<Exception>? failures = null;
        foreach (var statement in statements)
        {
            try { statement.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { (failures ??= []).Add(error); } // Continue releasing every owner after a failed close.
        }
        try { lock (_directoryGate) { ReleaseLease(); } }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { (failures ??= []).Add(StorageTempException.Unavailable(error)); }
        if (failures is not null) { throw new AggregateException(ErrorMessages.TempCleanupFailed, failures); }
    }
    private sealed class DebugView
    {
        private readonly StorageTempSpace _target;
        public DebugView(StorageTempSpace target) { _target = target; }
        public string DirectoryPath => _target.DirectoryPath;
        public long EngineQuota => _target.EngineQuota;
    }
}
