using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Storage;

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

    /// <summary>
    /// Initializes an empty strategy (databases are created through the engine).
    /// </summary>
    public CrashCaptureSqlStorageStrategy() { }

    private CrashCaptureSqlStorageStrategy(Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)> images)
    {
        _images = images;
    }

    /// <summary>
    /// Captures the durable byte image of every database this strategy has
    /// created — the state a process crash would leave on disk — and returns a
    /// fresh strategy that opens databases from those images.
    /// </summary>
    /// <returns>A strategy over the crash images.</returns>
    public CrashCaptureSqlStorageStrategy CaptureDurableImages()
    {
        lock (_sync)
        {
            var images = new Dictionary<string, (byte[] Data, byte[] Journal, byte[] Backup)>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, streams) in _live)
            {
                images[name] = (streams.Data.CaptureDurable(), streams.Journal.CaptureDurable(), streams.Backup.CaptureDurable());
            }

            return new CrashCaptureSqlStorageStrategy(images);
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
            var streams = (Data: new GatedStream(writeThrough: true), Journal: new GatedStream(writeThrough: false), Backup: new GatedStream(writeThrough: true));
            _live[databaseName] = streams;
            return SqlStorage.Create(streams.Data, streams.Journal, streams.Backup, databaseName);
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

            var streams = (Data: new GatedStream(image.Data, writeThrough: true), Journal: new GatedStream(image.Journal, writeThrough: false), Backup: new GatedStream(image.Backup, writeThrough: true));
            _live[databaseName] = streams;

            // Deferred checkpoint per the strategy contract: the engine analyzes
            // the recovered journal before truncation.
            return SqlStorage.Open(streams.Data, streams.Journal, streams.Backup, checkpointOnOpen: false);
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
        private byte[] _durable;

        internal GatedStream(bool writeThrough)
        {
            _liveBuffer = new MemoryStream();
            _durable = Array.Empty<byte>();
            _writeThrough = writeThrough;
        }

        internal GatedStream(byte[] content, bool writeThrough)
        {
            // Copy into an expandable stream: MemoryStream(byte[]) cannot grow.
            _liveBuffer = new MemoryStream();
            _liveBuffer.Write(content, 0, content.Length);
            _liveBuffer.Position = 0;
            _durable = (byte[])content.Clone();
            _writeThrough = writeThrough;
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
        }

        public override int Read(byte[] buffer, int offset, int count) => _liveBuffer.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _liveBuffer.Seek(offset, origin);

        public override void SetLength(long value)
        {
            _liveBuffer.SetLength(value);

            if (_writeThrough)
            {
                _durable = _liveBuffer.ToArray();
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _liveBuffer.Write(buffer, offset, count);

            if (_writeThrough)
            {
                _durable = _liveBuffer.ToArray();
            }
        }
    }
}
