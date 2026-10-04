using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

using Assimalign.Cohesion.Database.Sql;
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
/// </summary>
public sealed class CrashCaptureSqlStorageStrategy : ISqlStorageStrategy
{
    private readonly Dictionary<string, (GatedStream Data, GatedStream Journal, GatedStream Backup)> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private CrashPointRecorder? _recorder;

    /// <summary>
    /// Initializes an empty strategy (databases are created through the engine).
    /// </summary>
    public CrashCaptureSqlStorageStrategy() { }

    private CrashCaptureSqlStorageStrategy(Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)> images)
    {
        _images = images;
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
            return new CrashCaptureSqlStorageStrategy(CaptureImagesLocked());
        }
    }

    /// <summary>
    /// Starts recording a crash point after every change to the durable image of any
    /// storage this strategy holds: each write to a write-through stream, each flush,
    /// and each length change. A crash point is the set of durable images a process
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

    private void OnDurableChange()
    {
        lock (_sync)
        {
            _recorder?.Add(CaptureImagesLocked());
        }
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
    /// before the first recorded change, after every change, and when the recording
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

                return new CrashCaptureSqlStorageStrategy(images);
            }
        }

        /// <inheritdoc />
        public void Dispose() => _owner.StopRecording(this);

        internal void Add(Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)> point) => _points.Add(point);
    }

    /// <summary>
    /// Gets the current durable byte image of one storage: what an opened storage
    /// has made durable so far, or the image a never-opened one was given.
    /// </summary>
    /// <param name="storageName">The storage name (a database name, or one suffixed with <c>.catalog</c>).</param>
    /// <returns>Copies of the storage's data, journal and backup images.</returns>
    public (byte[] Data, byte[] Journal, byte[] Backup) GetDurableImage(string storageName)
    {
        lock (_sync)
        {
            if (_live.TryGetValue(storageName, out var streams))
            {
                return (streams.Data.CaptureDurable(), streams.Journal.CaptureDurable(), streams.Backup.CaptureDurable());
            }

            if (_images.TryGetValue(storageName, out var image))
            {
                return ((byte[])image.Data.Clone(), (byte[])image.Journal.Clone(), (byte[])image.Backup.Clone());
            }

            throw new KeyNotFoundException($"Crash-capture storage for '{storageName}' does not exist.");
        }
    }

    /// <inheritdoc />
    public SqlStorage CreateStorage(string databaseName)
    {
        lock (_sync)
        {
            if (_live.ContainsKey(databaseName))
            {
                throw new DatabaseException($"Crash-capture storage for '{databaseName}' already exists.");
            }

            // Data write-through: the worst case for steal (every page write is
            // immediately "on disk"); the journal honors flush-gated durability.
            var streams = (Data: new GatedStream(writeThrough: true, OnDurableChange), Journal: new GatedStream(writeThrough: false, OnDurableChange),
                Backup: new GatedStream(writeThrough: true, OnDurableChange));
            _live[databaseName] = streams;
            return SqlStorage.Create(new StorageStream(new SimulatedDurableFileHandle(streams.Data)),
                new StorageStream(new SimulatedDurableFileHandle(streams.Journal)),
                new StorageStream(new SimulatedDurableFileHandle(streams.Backup)), databaseName);
        }
    }

    /// <inheritdoc />
    public SqlStorage OpenStorage(string databaseName)
    {
        lock (_sync)
        {
            if (!_images.TryGetValue(databaseName, out var image))
            {
                throw new DatabaseException($"Crash-capture storage for '{databaseName}' does not exist.");
            }

            var streams = (Data: new GatedStream(image.Data, writeThrough: true, OnDurableChange),
                Journal: new GatedStream(image.Journal, writeThrough: false, OnDurableChange),
                Backup: new GatedStream(image.Backup, writeThrough: true, OnDurableChange));
            _live[databaseName] = streams;

            // Deferred checkpoint per the strategy contract: the engine analyzes
            // the recovered journal before truncation.
            return SqlStorage.Open(new StorageStream(new SimulatedDurableFileHandle(streams.Data)),
                new StorageStream(new SimulatedDurableFileHandle(streams.Journal)),
                new StorageStream(new SimulatedDurableFileHandle(streams.Backup)), checkpointOnOpen: false);
        }
    }

    /// <inheritdoc />
    public void DropStorage(string databaseName)
    {
        lock (_sync)
        {
            _live.Remove(databaseName);
            _images.Remove(databaseName);
        }
    }

    /// <inheritdoc />
    public bool StorageExists(string databaseName)
    {
        lock (_sync)
        {
            return _live.ContainsKey(databaseName) || _images.ContainsKey(databaseName);
        }
    }

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
        private byte[] _durable;

        internal GatedStream(bool writeThrough, Action onDurableChange)
        {
            _liveBuffer = new MemoryStream();
            _durable = Array.Empty<byte>();
            _writeThrough = writeThrough;
            _onDurableChange = onDurableChange;
        }

        internal GatedStream(byte[] content, bool writeThrough, Action onDurableChange)
        {
            // Copy into an expandable stream: MemoryStream(byte[]) cannot grow.
            _liveBuffer = new MemoryStream();
            _liveBuffer.Write(content, 0, content.Length);
            _liveBuffer.Position = 0;
            _durable = (byte[])content.Clone();
            _writeThrough = writeThrough;
            _onDurableChange = onDurableChange;
        }

        internal byte[] CaptureDurable() => (byte[])_durable.Clone();

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
            _durable = _liveBuffer.ToArray();
            _onDurableChange();
        }

        public override int Read(byte[] buffer, int offset, int count) => _liveBuffer.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _liveBuffer.Seek(offset, origin);

        public override void SetLength(long value)
        {
            _liveBuffer.SetLength(value);

            if (_writeThrough)
            {
                _durable = _liveBuffer.ToArray();
                _onDurableChange();
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _liveBuffer.Write(buffer, offset, count);

            if (_writeThrough)
            {
                _durable = _liveBuffer.ToArray();
                _onDurableChange();
            }
        }
    }
}
