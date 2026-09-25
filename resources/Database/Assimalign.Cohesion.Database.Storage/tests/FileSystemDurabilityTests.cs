using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.FileSystem;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class FileSystemDurabilityTests
{
    [Theory]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    public void ComposedPhysicalStorage_CommitRequestsDurableJournalFlush(StorageCommitDurability durability)
    {
        WithPhysicalFileSystem(fileSystem =>
        {
            using var storage = HarnessStorage.Create(fileSystem);
            storage.CommitDurability = durability;
            storage.GroupCommitWindow = TimeSpan.Zero;
            var journalHandle = fileSystem.Opened.Single(open => open.Path == "database.log").Handle;
            journalHandle.SupportsDurableFlush.ShouldBeTrue();
            journalHandle.DurableFlushRequests.ShouldBe(0);

            using var transaction = storage.BeginTransaction();
            storage.Insert(transaction, new byte[] { 1, 2, 3 });
            transaction.Commit();

            // #1018: observe the requested operation after the complete Storage ->
            // StreamJournal -> StorageStream chain, forwarding it to a real physical handle.
            // A recovery-only assertion would pass even if this durable call were lost.
            journalHandle.DurableFlushRequests.ShouldBe(1);
            journalHandle.CompletedDurableFlushes.ShouldBe(1);
            storage.JournalDurableLsn.ShouldBe(storage.JournalLastLsn);
            storage.JournalDurableLsn.ShouldBeGreaterThan(0L);
        });
    }

    [Fact]
    public void Journal_FromPhysicalFileRequestsDurabilityAndRetainsReadSharing()
    {
        WithPhysicalFileSystem(fileSystem =>
        {
            using var journal = StreamJournal.FromFile("direct.log", fileSystem);
            long lsn = journal.AppendBegin(1);
            journal.EnsureDurable(lsn);

            var opened = fileSystem.Opened.Single();
            opened.Mode.ShouldBe(FileMode.OpenOrCreate);
            opened.Access.ShouldBe(FileAccess.ReadWrite);
            opened.Share.ShouldBe(FileShare.Read);
            opened.Handle.CompletedDurableFlushes.ShouldBe(1);
            journal.DurableLsn.ShouldBe(lsn);
        });
    }

    [Fact]
    public void Journal_NonDurableFileSystemFailsAtFirstDurableFlushWithoutAdvancingLsn()
    {
        using var fileSystem = CreateInMemoryFileSystem();
        using var journal = StreamJournal.FromFile("journal.log", fileSystem);
        long lsn = journal.AppendBegin(1);

        // Opening and ordinary I/O remain valid. #1018 requires the first request
        // for durability to fail, rather than acknowledge a memory-backed commit.
        journal.Flush(forceDurable: false);
        Should.Throw<NotSupportedException>(() => journal.EnsureDurable(lsn));
        journal.DurableLsn.ShouldBe(0L);
        journal.LastLsn.ShouldBe(lsn);
        Should.Throw<NotSupportedException>(() => journal.Flush(forceDurable: true));
        journal.DurableLsn.ShouldBe(0L);
    }

    [Theory]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    public void ComposedNonDurableStorage_CommitFailsWithoutAcknowledgingDurability(StorageCommitDurability durability)
    {
        using var fileSystem = CreateInMemoryFileSystem();
        var storage = HarnessStorage.Create(fileSystem);
        try
        {
            storage.CommitDurability = durability;
            storage.GroupCommitWindow = TimeSpan.Zero;
            using var transaction = storage.BeginTransaction();
            storage.Insert(transaction, new byte[] { 4, 5, 6 });

            // #1018: creation may succeed, but a durable commit may never be
            // acknowledged by a provider whose explicit contract rejects it.
            Should.Throw<NotSupportedException>(() => transaction.Commit());
            transaction.IsActive.ShouldBeTrue();
            storage.JournalDurableLsn.ShouldBe(0L);
            storage.JournalLastLsn.ShouldBeGreaterThan(0L);
        }
        finally
        {
            try
            {
                storage.Dispose();
            }
            catch (NotSupportedException)
            {
                // Shutdown also requests durability. Dispose releases its handles
                // in finally; this expected cleanup failure must not mask the commit assertion.
            }
        }
    }

    [Fact]
    public void StorageStream_NonDurableFileSystemRejectsSyncAndAsyncDurableFlushes()
    {
        using var fileSystem = CreateInMemoryFileSystem();
        using var stream = StorageStream.FromFile("data.dat", fileSystem);
        stream.SupportsDurableFlush.ShouldBeFalse();
        stream.Write(new byte[] { 7, 8 }, 0L);
        stream.Flush();

        Should.Throw<NotSupportedException>(() => stream.FlushDurable());
        Should.Throw<NotSupportedException>(() => stream.FlushAsync(durable: true).GetAwaiter().GetResult());
    }

    [Fact]
    public void Journal_ContractErasedToStreamFailsInsteadOfSilentlyDowngradingDurability()
    {
        WithPhysicalFileSystem(fileSystem =>
        {
            using var stream = StorageStream.FromFile("wrapped.log", fileSystem);
            using var journal = new StreamJournal((Stream)stream, leaveOpen: true);
            var handle = fileSystem.Opened.Single().Handle;
            long lsn = journal.AppendBegin(1);

            // #1018: losing the explicit contract through a Stream wrapper must
            // fail. The underlying physical type cannot be used to infer durability.
            Should.Throw<NotSupportedException>(() => journal.EnsureDurable(lsn));
            journal.DurableLsn.ShouldBe(0L);
            handle.DurableFlushRequests.ShouldBe(0);

            stream.FlushDurable();
            handle.CompletedDurableFlushes.ShouldBe(1);
        });
    }

    [Fact]
    public void StorageStream_PageIoUsesHandleOffsetsAndPreservesSequentialPosition()
    {
        using var fileSystem = new RecordingFileSystem(CreateInMemoryFileSystem());
        using var stream = StorageStream.FromFile("pages.dat", fileSystem);
        var handle = fileSystem.Opened.Single().Handle;
        var page = new byte[Page.Size];
        page[0] = 19;
        page[^1] = 37;
        var read = new byte[Page.Size];
        var header = new byte[Page.HeaderSize];
        stream.Position = 17;

        stream.WritePage((PageId)3L, page);
        stream.ReadPage((PageId)3L, read);
        stream.ReadPageHeader((PageId)3L, header);

        handle.WriteOffsets.ShouldBe(new long[] { 3L * Page.Size });
        handle.ReadOffsets.ShouldBe(new long[] { 3L * Page.Size, 3L * Page.Size });
        stream.Position.ShouldBe(17L);
        read.ShouldBe(page);
        header.ShouldBe(page[..Page.HeaderSize]);
    }

    [Fact]
    public async Task StorageStream_AsyncPageIoAdvancesOffsetsAcrossShortReads()
    {
        using var fileSystem = new RecordingFileSystem(CreateInMemoryFileSystem());
        await using var stream = StorageStream.FromFile("async-pages.dat", fileSystem);
        var handle = fileSystem.Opened.Single().Handle;
        handle.MaximumReadSize = 1024;
        var page = Enumerable.Range(0, Page.Size).Select(index => (byte)(index % 251)).ToArray();
        var read = new byte[Page.Size];
        stream.Position = 29;

        await stream.WritePageAsync((PageId)2L, page);
        await stream.ReadPageAsync((PageId)2L, read);

        handle.WriteOffsets.ShouldBe(new long[] { 2L * Page.Size });
        handle.ReadOffsets.ShouldBe(Enumerable.Range(0, Page.Size / 1024).Select(index => 2L * Page.Size + index * 1024));
        stream.Position.ShouldBe(29L);
        read.ShouldBe(page);
    }

    private static InMemoryFileSystem CreateInMemoryFileSystem()
        => new(new InMemoryFileSystemOptions { RootPath = "/", Size = Size.FromMegabytes(16) });

    private static void WithPhysicalFileSystem(Action<RecordingFileSystem> test)
    {
        var directory = Directory.CreateTempSubdirectory("cohesion-durability-");
        try
        {
            using var fileSystem = new RecordingFileSystem(new PhysicalFileSystem((FileSystemPath)directory.FullName));
            test(fileSystem);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private sealed class HarnessStorage : Storage
    {
        private HarnessStorage(IFileSystem fileSystem)
            : base(StorageStream.FromFile("database.dat", fileSystem),
                   StorageStream.FromFile("database.log", fileSystem),
                   StorageStream.FromFile("database.bak", fileSystem)) { }

        public override StorageModel Model => StorageModel.Custom;
        public long JournalDurableLsn => WriteAheadLog.DurableLsn;
        public long JournalLastLsn => WriteAheadLog.LastLsn;

        public static HarnessStorage Create(IFileSystem fileSystem)
        {
            var storage = new HarnessStorage(fileSystem);
            storage.InitializeNew((Name)"durability-regression");
            return storage;
        }

        public void Insert(IStorageTransaction transaction, byte[] data) => InsertRecord(transaction, data);
    }

    private sealed class RecordingHandle : IFileSystemFileHandle
    {
        private readonly IFileSystemFileHandle _inner;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingHandle"/> class.
        /// </summary>
        /// <param name="inner">The file handle every recorded operation is forwarded to.</param>
        public RecordingHandle(IFileSystemFileHandle inner)
        {
            _inner = inner;
        }

        public int DurableFlushRequests { get; private set; }
        public int CompletedDurableFlushes { get; private set; }
        public List<long> ReadOffsets { get; } = new();
        public List<long> WriteOffsets { get; } = new();
        public int MaximumReadSize { get; set; } = int.MaxValue;
        public long Length => _inner.Length;
        public bool SupportsDurableFlush => _inner.SupportsDurableFlush;

        public int Read(Span<byte> buffer, long offset)
        {
            ReadOffsets.Add(offset);
            return _inner.Read(buffer[..Math.Min(buffer.Length, MaximumReadSize)], offset);
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            ReadOffsets.Add(offset);
            return _inner.ReadAsync(buffer[..Math.Min(buffer.Length, MaximumReadSize)], offset, cancellationToken);
        }

        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            WriteOffsets.Add(offset);
            _inner.Write(buffer, offset);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            WriteOffsets.Add(offset);
            return _inner.WriteAsync(buffer, offset, cancellationToken);
        }

        public void SetLength(long length) => _inner.SetLength(length);

        public void Flush(bool durable)
        {
            if (durable)
            {
                DurableFlushRequests++;
            }
            _inner.Flush(durable);
            if (durable)
            {
                CompletedDurableFlushes++;
            }
        }

        public async ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
        {
            if (durable)
            {
                DurableFlushRequests++;
            }
            await _inner.FlushAsync(durable, cancellationToken);
            if (durable)
            {
                CompletedDurableFlushes++;
            }
        }

        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class RecordingFileSystem : IFileSystem
    {
        private readonly IFileSystem _inner;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingFileSystem"/> class.
        /// </summary>
        /// <param name="inner">The file system every operation is forwarded to.</param>
        public RecordingFileSystem(IFileSystem inner)
        {
            _inner = inner;
        }

        public List<(string Path, FileMode Mode, FileAccess Access, FileShare Share, RecordingHandle Handle)> Opened { get; } = new();
        public Size Size => _inner.Size;
        public Size SpaceAvailable => _inner.SpaceAvailable;
        public Size SpaceUsed => _inner.SpaceUsed;
        public string Name => _inner.Name;
        public bool IsReadOnly => _inner.IsReadOnly;
        public IFileSystemDirectory RootDirectory => _inner.RootDirectory;
        public bool Exists(FileSystemPath path) => _inner.Exists(path);
        public IFileSystemEventToken Watch(Glob? pattern) => _inner.Watch(pattern);
        public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = default) => _inner.EnumerateFileSystem(options);
        public IFileSystemDirectory GetDirectory(FileSystemPath path) => _inner.GetDirectory(path);
        public IFileSystemFile GetFile(FileSystemPath path) => new RecordingFile(this, _inner.GetFile(path), path);
        public IFileSystemInfo GetInfo(FileSystemPath path) => _inner.GetInfo(path);
        public IFileSystemDirectory CreateDirectory(FileSystemPath path) => _inner.CreateDirectory(path);
        public IFileSystemFile CreateFile(FileSystemPath path) => new RecordingFile(this, _inner.CreateFile(path), path);
        public void DeleteDirectory(FileSystemPath path) => _inner.DeleteDirectory(path);
        public void DeleteFile(FileSystemPath path) => _inner.DeleteFile(path);
        public void CopyFile(FileSystemPath source, FileSystemPath destination) => _inner.CopyFile(source, destination);
        public void Move(FileSystemPath source, FileSystemPath destination) => _inner.Move(source, destination);
        public IEnumerator<IFileSystemInfo> GetEnumerator() => _inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => _inner.Dispose();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private sealed class RecordingFile : IFileSystemFile
        {
            private readonly RecordingFileSystem _owner;
            private readonly IFileSystemFile _innerFile;
            private readonly FileSystemPath _requestedPath;

            /// <summary>
            /// Initializes a new instance of the <see cref="RecordingFile"/> class.
            /// </summary>
            /// <param name="owner">The recording file system that reports itself as this file's owner and records opened handles.</param>
            /// <param name="innerFile">The file every operation is forwarded to.</param>
            /// <param name="requestedPath">The path the caller requested, recorded with each opened handle.</param>
            public RecordingFile(RecordingFileSystem owner, IFileSystemFile innerFile, FileSystemPath requestedPath)
            {
                _owner = owner;
                _innerFile = innerFile;
                _requestedPath = requestedPath;
            }

            public Size Size => _innerFile.Size;
            public FileName Name => _innerFile.Name;
            public IFileSystemDirectory Directory => _innerFile.Directory;
            public FileSystemPath Path => _innerFile.Path;
            public DateTime CreatedOn => _innerFile.CreatedOn;
            public DateTime UpdatedOn => _innerFile.UpdatedOn;
            public DateTime AccessedOn => _innerFile.AccessedOn;
            public FileAttributes Attributes => _innerFile.Attributes;
            public IFileSystem FileSystem => _owner;
            public void SetAttributes(FileAttributes attributes) => _innerFile.SetAttributes(attributes);
            public IFileSystemEventToken Watch() => _innerFile.Watch();
            public Stream Open() => _innerFile.Open();
            public Stream Open(FileMode fileMode) => _innerFile.Open(fileMode);
            public Stream Open(FileMode fileMode, FileAccess fileAccess) => _innerFile.Open(fileMode, fileAccess);
            public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare) => _innerFile.Open(fileMode, fileAccess, fileShare);

            public IFileSystemFileHandle OpenHandle(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
            {
                var handle = new RecordingHandle(_innerFile.OpenHandle(fileMode, fileAccess, fileShare));
                _owner.Opened.Add((_requestedPath.ToString(), fileMode, fileAccess, fileShare, handle));
                return handle;
            }
        }
    }
}
