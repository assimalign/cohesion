using System.Threading;
using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// A storage strategy for crash-simulation tests: every database's three streams
/// are durability-gated in memory (the data stream is write-through — worst-case
/// steal; the journal is durable only up to its last flush), and
/// <see cref="CaptureDurableImages"/> returns the byte images a real crash would
/// leave behind. A second strategy constructed over those images "reopens the
/// files" — both data and journal travel together, as they must.
/// With None durability, images model a process crash instead: every OS write survives,
/// including journal writes, without making a power-loss guarantee for unflushed bytes.
/// </summary>
internal sealed class CrashCaptureSqlStorageStrategy : SqlStorageStrategy
{
    private readonly Dictionary<string, (GatedStream Data, GatedStream Journal, GatedStream Backup)> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly StorageCommitDurability _durability;
    private CrashPointRecorder? _recorder;

    /// <summary>
    /// Initializes an empty strategy (databases are created through the engine).
    /// </summary>
    /// <param name="durability">The commit policy, also selecting power-loss or process-crash images.</param>
    public CrashCaptureSqlStorageStrategy(StorageCommitDurability durability = StorageCommitDurability.Synchronous)
    {
        _durability = durability;
    }

    private CrashCaptureSqlStorageStrategy(Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)> images, StorageCommitDurability durability)
    {
        _images = images;
        _durability = durability;
    }

    internal StorageCommitDurability Durability => _durability;

    internal bool UnixSparseGrowth { get; set; }
    internal override bool SparseDataGrowth => UnixSparseGrowth;
    internal long MigrationAvailableBytes { get; set; } = long.MaxValue;
    internal long VolumeCapacityBytes { get; set; } = long.MaxValue;
    internal long PeakAllocatedBytes { get; private set; }
    internal int CapacityFailures { get; private set; }
    internal long AllocatedBytes
    {
        get
        {
            lock (_sync)
            {
                long bytes = 0;
                foreach (var streams in _live.Values) { bytes += streams.Data.ChargedBytes + streams.Journal.ChargedBytes + streams.Backup.ChargedBytes; }
                return bytes;
            }
        }
    }
    internal override long GetAvailableMigrationBytes(string databaseName)
        => Math.Min(MigrationAvailableBytes, VolumeCapacityBytes - AllocatedBytes);

    private void OnGrowing(long growth)
    {
        long next = AllocatedBytes + growth;
        if (next > VolumeCapacityBytes)
        {
            CapacityFailures++;
            throw new IOException("Faked volume filled");
        }
        PeakAllocatedBytes = Math.Max(PeakAllocatedBytes, next);
    }

    internal int JournalFlushes(string databaseName)
    {
        lock (_sync) { return _live[Key(databaseName, false)].Journal.FlushCount; }
    }

    /// <summary>
    /// Captures the durable byte image of every storage this strategy has created
    /// or opened — the state a process crash would leave on disk — and returns a
    /// fresh strategy that opens databases from those images. A storage this
    /// strategy holds an image of but never opened carries its image forward
    /// unchanged, as an untouched file would.
    /// </summary>
    /// <returns>A strategy over the crash images.</returns>
    public CrashCaptureSqlStorageStrategy CaptureDurableImages()
    {
        lock (_sync)
        {
            return new CrashCaptureSqlStorageStrategy(CaptureImagesLocked(), _durability);
        }
    }

    /// <summary>
    /// Starts recording a crash point before every storage write and after every change
    /// to the retained image: each write-through write, flush, and length change.
    /// A crash point is the set of images under the selected persistence contract a process
    /// crash at that instant would leave behind, so reopening every recorded point
    /// crashes the work in between at every place it touches a file. The recording
    /// starts with the images as they are when it is armed and stops when the returned
    /// recorder is disposed.
    /// </summary>
    /// <returns>The recorder whose <see cref="CrashPointRecorder.Points"/> collect the crash points.</returns>
    /// <exception cref="InvalidOperationException">A recording is already running.</exception>
    public CrashPointRecorder RecordCrashPoints()
    {
        lock (_sync)
        {
            if (_recorder is not null)
            {
                throw new InvalidOperationException("Crash points are already being recorded.");
            }

            _recorder = new CrashPointRecorder(this);
            _recorder.Add(CaptureImagesLocked());
            return _recorder;
        }
    }

    private Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)> CaptureImagesLocked()
    {
        var images = new Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)>(_images, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, streams) in _live)
        {
            images[name] = (streams.Data.CaptureDurable(), streams.Journal.CaptureDurable(), streams.Backup.CaptureDurable());
        }

        return images;
    }

    internal Action? DurableChanged { get; set; }
    internal Action<bool>? DurableStorageChanged { get; set; }
    internal Action<bool>? StorageWriteStarting { get; set; }
    internal Action<bool>? StorageCreated { get; set; }

    private void OnWriteStarting(bool catalog)
    {
        lock (_sync)
        {
            _recorder?.Add(CaptureImagesLocked());
        }
        StorageWriteStarting?.Invoke(catalog);
    }

    private void OnDurableChange(bool catalog)
    {
        DurableChanged?.Invoke();
        lock (_sync)
        {
            _recorder?.Add(CaptureImagesLocked());
        }
        DurableStorageChanged?.Invoke(catalog);
    }

    private void StopRecording(CrashPointRecorder recorder)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_recorder, recorder))
            {
                _recorder.Add(CaptureImagesLocked());
                _recorder = null;
            }
        }
    }

    /// <summary>
    /// The crash points <see cref="RecordCrashPoints"/> collected: the durable images
    /// before the first recorded change, before each write, after every change, and when the recording
    /// stopped. Consecutive identical points are kept, so the count measures how many
    /// durable changes the recorded work made.
    /// </summary>
    public sealed class CrashPointRecorder : IDisposable
    {
        private readonly CrashCaptureSqlStorageStrategy _owner;
        private readonly List<Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)>> _points = [];

        internal CrashPointRecorder(CrashCaptureSqlStorageStrategy owner)
        {
            _owner = owner;
        }

        /// <summary>Gets the number of recorded crash points.</summary>
        public int Count
        {
            get
            {
                lock (_owner._sync)
                {
                    return _points.Count;
                }
            }
        }

        /// <summary>
        /// Returns a strategy that opens databases from the images of one crash point, as a
        /// restarted process would find its files.
        /// </summary>
        /// <param name="index">The crash point, from 0 to <see cref="Count"/> - 1.</param>
        /// <returns>A strategy over that crash point's images.</returns>
        public CrashCaptureSqlStorageStrategy Open(int index)
        {
            lock (_owner._sync)
            {
                var images = new Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, image) in _points[index])
                {
                    images[name] = ((byte[])image.Data.Clone(), (byte[])image.Journal.Clone(), (byte[])image.Backup.Clone());
                }

                return new CrashCaptureSqlStorageStrategy(images, _owner._durability);
            }
        }

        /// <inheritdoc />
        public void Dispose() => _owner.StopRecording(this);

        internal void Add(Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)> point) => _points.Add(point);
    }

    /// <summary>
    /// Gets the current durable byte image of one file set: what an opened storage
    /// has made durable so far, or the image a never-opened one was given.
    /// </summary>
    /// <param name="databaseName">The database name.</param>
    /// <param name="catalog">True for the database's catalog file set; false for its data set.</param>
    /// <returns>Copies of the storage's data, journal and backup images.</returns>
    public (byte[] Data, byte[] Journal, byte[] Backup) GetDurableImage(string databaseName, bool catalog = false)
    {
        string key = Key(databaseName, catalog);
        lock (_sync)
        {
            if (_live.TryGetValue(key, out var streams))
            {
                return (streams.Data.CaptureDurable(), streams.Journal.CaptureDurable(), streams.Backup.CaptureDurable());
            }

            if (_images.TryGetValue(key, out var image))
            {
                return ((byte[])image.Data.Clone(), (byte[])image.Journal.Clone(), (byte[])image.Backup.Clone());
            }

            throw new KeyNotFoundException($"Crash-capture storage for '{key}' does not exist.");
        }
    }

    /// <inheritdoc />
    public override SqlStorage CreateStorage(string databaseName, bool catalog)
    {
        string key = Key(databaseName, catalog);
        lock (_sync)
        {
            if (_live.ContainsKey(key))
            {
                throw new DatabaseException($"Crash-capture storage for '{key}' already exists.");
            }

            // Data write-through: the worst case for steal (every page write is
            // immediately "on disk"); the journal honors flush-gated durability.
            // None promises process-crash persistence only: retain bytes written to the OS,
            // without pretending that its unflushed journal survives a power loss.
            Action changed = () => OnDurableChange(catalog);
            Action writing = () => OnWriteStarting(catalog);
            var streams = (Data: new GatedStream(writeThrough: true, changed, writing, OnGrowing, () => UnixSparseGrowth),
                Journal: new GatedStream(writeThrough: _durability == StorageCommitDurability.None, changed, writing, OnGrowing, () => UnixSparseGrowth),
                Backup: new GatedStream(writeThrough: true, changed, writing, OnGrowing, () => UnixSparseGrowth));
            _live[key] = streams;
            if (!catalog) { _images[CreationKey(databaseName)] = (System.Text.Encoding.UTF8.GetBytes(CreationSignature), [], []); }
            var storage = SqlStorage.Create(new StorageStream(new SimulatedDurableFileHandle(streams.Data)),
                new StorageStream(new SimulatedDurableFileHandle(streams.Journal)),
                new StorageStream(new SimulatedDurableFileHandle(streams.Backup)), GetLabel(databaseName, catalog), _durability);
            try
            {
                StorageCreated?.Invoke(catalog);
                return storage;
            }
            catch
            {
                storage.Dispose();
                throw;
            }
        }
    }

    /// <inheritdoc />
    public override SqlStorage OpenStorage(string databaseName, bool catalog, CancellationToken cancellationToken = default)
    {
        string key = Key(databaseName, catalog);
        lock (_sync)
        {
            if (!_images.TryGetValue(key, out var image))
            {
                throw new DatabaseException($"Crash-capture storage for '{key}' does not exist.");
            }

            Action changed = () => OnDurableChange(catalog);
            Action writing = () => OnWriteStarting(catalog);
            var streams = (Data: new GatedStream(image.Data, writeThrough: true, changed, writing, OnGrowing, () => UnixSparseGrowth),
                Journal: new GatedStream(image.Journal, writeThrough: _durability == StorageCommitDurability.None, changed, writing, OnGrowing, () => UnixSparseGrowth),
                Backup: new GatedStream(image.Backup, writeThrough: true, changed, writing, OnGrowing, () => UnixSparseGrowth));
            _live[key] = streams;

            // Deferred checkpoint per the strategy contract: the engine analyzes
            // the recovered journal before truncation.
            return SqlStorage.Open(new StorageStream(new SimulatedDurableFileHandle(streams.Data)),
                new StorageStream(new SimulatedDurableFileHandle(streams.Journal)),
                new StorageStream(new SimulatedDurableFileHandle(streams.Backup)), checkpointOnOpen: false, _durability, cancellationToken);
        }
    }

    /// <inheritdoc />
    public override void DropStorage(string databaseName)
    {
        lock (_sync)
        {
            _images.Remove(CreationKey(databaseName));
            foreach (bool catalog in new[] { false, true })
            {
                _live.Remove(Key(databaseName, catalog));
                _images.Remove(Key(databaseName, catalog));
            }
        }
    }

    /// <inheritdoc />
    public override bool StorageExists(string databaseName, bool catalog)
    {
        string key = Key(databaseName, catalog);
        lock (_sync)
        {
            return _live.ContainsKey(key) || _images.ContainsKey(key);
        }
    }

    internal Exception? DataProbeFailure { get; set; }

    internal override SqlStorageProbe? ProbeStorage(string databaseName, bool catalog)
    {
        if (!StorageExists(databaseName, catalog)) { return null; }
        var image = GetDurableImage(databaseName, catalog);
        var probe = new SqlStorageProbe(image.Data.LongLength, image.Data, image.Journal, image.Backup);
        return !catalog && DataProbeFailure is { } failure
            ? probe with { OpenReadOnly = () => throw failure } : probe;
    }

    internal override SqlStorage RestartStorage(string databaseName, bool catalog)
    {
        lock (_sync)
        {
            _images.Remove(Key(databaseName, catalog));
            _live.Remove(Key(databaseName, catalog));
            if (!catalog) { _images.Remove(CreationKey(databaseName)); }
            return CreateStorage(databaseName, catalog);
        }
    }

    internal override bool IsCreationPending(string databaseName)
    {
        lock (_sync) { return _images.ContainsKey(CreationKey(databaseName)); }
    }

    internal override void CompleteCreation(string databaseName)
    {
        lock (_sync)
        {
            _images.Remove(CreationKey(databaseName));
            _recorder?.Add(CaptureImagesLocked());
        }
    }

    private static string CreationKey(string databaseName)
        => DatabaseFileNames.GetStorageName(databaseName) + "/" + CreationFileName;

    // A file set's key: the database's lower-case storage name and the set, as the file-based
    // strategy finds it in the database's directory (#1392).
    private static string Key(string databaseName, bool catalog)
        => DatabaseFileNames.GetStorageName(databaseName) + "/" + GetFileSet(catalog);

    /// <summary>
    /// An in-memory stream with crash semantics: the live buffer accepts every
    /// write; the durable image advances on flush (or on every write when
    /// write-through). The durable image survives disposal, which is how a
    /// "crashed process" leaves its files behind.
    /// </summary>
    private sealed class GatedStream : Stream
    {
        private readonly MemoryStream _liveBuffer;
        private readonly bool _writeThrough;
        private readonly Action _onDurableChange;
        private readonly Action _onWriteStarting;
        private readonly Action<long> _onGrowing;
        private byte[] _durable;
        private int _durableLength;
        private readonly Func<bool> _sparse;
        private long _writtenHighWater;
        internal long ChargedBytes => _sparse() ? _writtenHighWater : Length;

        internal int FlushCount { get; private set; }

        internal GatedStream(bool writeThrough, Action onDurableChange, Action onWriteStarting, Action<long> onGrowing, Func<bool> sparse)
        {
            _liveBuffer = new MemoryStream();
            _durable = Array.Empty<byte>();
            _writeThrough = writeThrough;
            _onDurableChange = onDurableChange;
            _onWriteStarting = onWriteStarting;
            _onGrowing = onGrowing;
            _sparse = sparse;
        }

        internal GatedStream(byte[] content, bool writeThrough, Action onDurableChange, Action onWriteStarting, Action<long> onGrowing, Func<bool> sparse)
        {
            // Copy into an expandable stream: MemoryStream(byte[]) cannot grow.
            _liveBuffer = new MemoryStream();
            _liveBuffer.Write(content, 0, content.Length);
            _liveBuffer.Position = 0;
            _durable = (byte[])content.Clone();
            _durableLength = content.Length;
            _writtenHighWater = content.Length;
            _writeThrough = writeThrough;
            _onDurableChange = onDurableChange;
            _onWriteStarting = onWriteStarting;
            _onGrowing = onGrowing;
            _sparse = sparse;
        }

        internal byte[] CaptureDurable() => _durable.AsSpan(0, _durableLength).ToArray();

        private void SetDurableLength(int length)
        {
            if (_durable.Length < length) { Array.Resize(ref _durable, Math.Max(length, Math.Max(256, _durable.Length * 2))); }
            if (length > _durableLength) { _durable.AsSpan(_durableLength, length - _durableLength).Clear(); }
            _durableLength = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _liveBuffer.Length;

        public override long Position
        {
            get => _liveBuffer.Position;
            set => _liveBuffer.Position = value;
        }

        public override void Flush()
        {
            FlushCount++;
            _durable = _liveBuffer.ToArray();
            _durableLength = _durable.Length;
            _onDurableChange();
        }

        public override int Read(byte[] buffer, int offset, int count) => _liveBuffer.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _liveBuffer.Seek(offset, origin);

        public override void SetLength(long value)
        {
            if (!_sparse()) { _onGrowing(Math.Max(0, value - _liveBuffer.Length)); }
            _writtenHighWater = Math.Min(_writtenHighWater, value);
            _liveBuffer.SetLength(value);

            if (_writeThrough)
            {
                SetDurableLength(checked((int)value));
                _onDurableChange();
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _onWriteStarting();
            _onGrowing(Math.Max(0, _liveBuffer.Position + count - ChargedBytes));
            _writtenHighWater = Math.Max(_writtenHighWater, _liveBuffer.Position + count);
            int position = checked((int)_liveBuffer.Position);
            _liveBuffer.Write(buffer, offset, count);

            if (_writeThrough)
            {
                SetDurableLength(checked((int)_liveBuffer.Length));
                buffer.AsSpan(offset, count).CopyTo(_durable.AsSpan(position, count));
                _onDurableChange();
            }
        }
    }
}
