using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentApplicationBuilderTests
{
    [Fact]
    public async Task Registration_ShouldDeferConstructionAndPreserveGroupedMemoryRejection()
    {
        var builder = new RecordingBuilder();
        var context = new RecordingContext();
        int configured = 0;
        builder.AddDocuments((actualContext, options) =>
        {
            actualContext.ShouldBeSameAs(context);
            configured++;
            options.EngineName = "registered";
            options.Durability = StorageCommitDurability.Grouped;
        }).ShouldBeSameAs(builder);

        configured.ShouldBe(0);
        await using var engine = builder.Factory.ShouldNotBeNull()(context);
        configured.ShouldBe(1);
        engine.Name.ShouldBe("registered");
        engine.State.ShouldBe(EngineState.Running);
        engine.Model.ShouldBe(EngineModel.Document);
        var failure = await Should.ThrowAsync<NotSupportedException>(async () => await engine.CreateDatabaseAsync(new DatabaseName("test")));
        failure.Message.ShouldContain("DocumentStorage (test)");
        failure.Message.ShouldContain(nameof(StorageCommitDurability.Grouped));
    }

    [Fact]
    public void RejectedRegistration_ShouldNeverInvokeConfigurationOrCreateAnEngine()
    {
        var builder = new RecordingBuilder(reject: true);
        bool configured = false;
        Should.Throw<InvalidOperationException>(() => builder.AddDocuments((_, _) => configured = true))
            .Message.ShouldBe("Registration refused.");
        configured.ShouldBeFalse();
    }

    [Fact]
    public void Build_ShouldAttachDeferredComponentsAndOwnTheirLifetime()
    {
        var builder = DocumentDatabaseEngine.CreateBuilder();
        builder.EngineName = "composed";
        RecordingWorker? worker = null;
        RecordingServer? server = null;
        builder.AddWorker(engine => worker = new RecordingWorker(engine, engine.Name + "/custom"));
        builder.AddServer(engine => server = new RecordingServer(engine));
        worker.ShouldBeNull();
        server.ShouldBeNull();

        using (var engine = builder.Build())
        {
            worker.ShouldNotBeNull().Started.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            worker.Engine.ShouldBeSameAs(engine);
            engine.Workers.Count.ShouldBe(5);
            engine.Workers.ShouldContain(worker);
            engine.Servers.ShouldHaveSingleItem().ShouldBeSameAs(server);
            server.ShouldNotBeNull().Starts.ShouldBe(0);
            Should.Throw<InvalidOperationException>(() => builder.EngineName = "late");
            Should.Throw<InvalidOperationException>(() => builder.AddWorker(_ => worker));
            Should.Throw<InvalidOperationException>(() => builder.Build());
        }

        worker.ShouldNotBeNull().Disposals.ShouldBe(1);
        server.ShouldNotBeNull().Stops.ShouldBe(1);
    }

    [Fact]
    public void FailedServerFactory_ShouldDisposeEarlierComponentsAndFreezeBuilder()
    {
        var builder = DocumentDatabaseEngine.CreateBuilder();
        RecordingWorker? worker = null;
        RecordingServer? server = null;
        DocumentDatabaseEngine? product = null;
        builder.AddWorker(engine => worker = new RecordingWorker(engine));
        builder.AddServer(engine => server = new RecordingServer(engine));
        builder.AddServer(engine =>
        {
            product = engine;
            throw new InvalidOperationException("Server construction failed.");
        });

        Should.Throw<InvalidOperationException>(() => builder.Build()).Message.ShouldBe("Server construction failed.");
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
        worker.ShouldNotBeNull().Disposals.ShouldBe(1);
        server.ShouldNotBeNull().Stops.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => builder.Build());
        Should.Throw<InvalidOperationException>(() => builder.RootPath = null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NullComponentFactoryProduct_ShouldDisposeEngine(bool worker)
    {
        var builder = DocumentDatabaseEngine.CreateBuilder();
        DocumentDatabaseEngine? product = null;
        if (worker)
        {
            builder.AddWorker(engine => { product = engine; return null!; });
        }
        else
        {
            builder.AddServer(engine => { product = engine; return null!; });
        }

        Should.Throw<InvalidOperationException>(() => builder.Build());
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    [Fact]
    public void ServerForAnotherEngine_ShouldBeRejectedAndDisposedWithoutOwningThatEngine()
    {
        using var other = DocumentDatabaseEngine.Create(new());
        var server = new RecordingServer(other);
        var builder = DocumentDatabaseEngine.CreateBuilder();
        builder.AddServer(_ => server);
        Should.Throw<InvalidOperationException>(() => builder.Build()).Message.ShouldContain("owning engine");
        server.Stops.ShouldBe(1);
        other.State.ShouldBe(EngineState.Running);
    }

    [Fact]
    public async Task CustomStorageStrategy_ShouldOverrideRootAndSupportDiscoveryOpenCreateAndDrop()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-document-strategy-" + Guid.NewGuid().ToString("N"));
        string ignoredRoot = Path.Combine(directory, "ignored-root");
        Directory.CreateDirectory(directory);
        try
        {
            using var strategy = new RecordingStorageStrategy(directory);
            strategy.CreateStorage(new DatabaseName("existing"), StorageCommitDurability.Synchronous).Dispose();
            var builder = DocumentDatabaseEngine.CreateBuilder();
            builder.StorageStrategy = strategy;
            builder.RootPath = FileSystemPath.Parse(ignoredRoot);
            builder.Durability = StorageCommitDurability.Synchronous;

            await using (var engine = builder.Build())
            {
                Directory.Exists(ignoredRoot).ShouldBeFalse();
                var database = await engine.CreateDatabaseAsync(new DatabaseName("custom"));
                strategy.LastDurability.ShouldBe(StorageCommitDurability.Synchronous);
                engine.TryGetDatabase(new DatabaseName("CUSTOM"), out var found).ShouldBeTrue();
                found.ShouldBeSameAs(database);
                var names = new List<string>();
                await foreach (var available in engine.GetDatabasesAsync()) { names.Add(available.Name); }
                names.ShouldBe(["custom", "existing"]);
                strategy.Opens.ShouldBe(1);
                await engine.DropDatabaseAsync(new DatabaseName("custom"));
                strategy.StorageExists(new DatabaseName("custom")).ShouldBeFalse();
            }
            strategy.Disposed.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EmptyDatabaseName_ShouldBeRejectedAtEveryEngineEntryPoint()
    {
        await using var engine = DocumentDatabaseEngine.Create(new());
        await Should.ThrowAsync<ArgumentException>(async () => await engine.CreateDatabaseAsync(default));
        await Should.ThrowAsync<ArgumentException>(async () => await engine.OpenDatabaseAsync(default));
        await Should.ThrowAsync<ArgumentException>(async () => await engine.DropDatabaseAsync(default));
        Should.Throw<ArgumentException>(() => engine.TryGetDatabase(default, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConfigurationThatBuildsPrematurely_ShouldNotLeakItsEngine(bool throwAfterBuild)
    {
        var builder = new RecordingBuilder();
        DocumentDatabaseEngine? product = null;
        builder.AddDocuments((_, engine) =>
        {
            product = engine.Build();
            if (throwAfterBuild) { throw new InvalidOperationException("Configuration failed."); }
        });

        Should.Throw<InvalidOperationException>(() => builder.Factory.ShouldNotBeNull()(new RecordingContext()));
        product.ShouldNotBeNull().State.ShouldBe(EngineState.Disposed);
    }

    private sealed class RecordingBuilder : IDatabaseApplicationBuilder
    {
        private readonly bool _reject;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingBuilder"/> class.
        /// </summary>
        /// <param name="reject">Whether engine registration is refused with an exception.</param>
        public RecordingBuilder(bool reject = false)
        {
            _reject = reject;
        }

        internal Func<IDatabaseApplicationContext, IDatabaseEngine>? Factory { get; private set; }
        public IDatabaseApplicationBuilder AddEngine(IDatabaseEngine engine) => throw new NotSupportedException("Registration must be deferred.");
        public IDatabaseApplicationBuilder AddEngine(Func<IDatabaseApplicationContext, IDatabaseEngine> configure)
        {
            if (_reject) { throw new InvalidOperationException("Registration refused."); }
            Factory = configure;
            return this;
        }
        public IDatabaseApplication Build() => throw new NotSupportedException();
    }

    private sealed class RecordingContext : IDatabaseApplicationContext
    {
        public IReadOnlyList<IDatabaseEngine> Engines => [];
        public IReadOnlyList<IDatabaseServer> Servers => [];
        public IDatabaseEngine GetEngine(string name) => throw new KeyNotFoundException(name);
    }

    private sealed class RecordingStorageStrategy : DocumentStorageStrategy, IDisposable
    {
        private readonly string _directory;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecordingStorageStrategy"/> class.
        /// </summary>
        /// <param name="directory">The directory that holds each database's storage files.</param>
        public RecordingStorageStrategy(string directory)
        {
            _directory = directory;
        }

        internal StorageCommitDurability? LastDurability { get; private set; }
        internal int Opens { get; private set; }
        internal bool Disposed { get; private set; }

        public override DocumentStorage CreateStorage(DatabaseName databaseName, StorageCommitDurability? durability)
        {
            LastDurability = durability;
            return DocumentStorage.Create(Open(databaseName, "dat", FileMode.CreateNew), Open(databaseName, "log", FileMode.CreateNew), Open(databaseName, "bak", FileMode.CreateNew), databaseName, durability);
        }

        public override DocumentStorage OpenStorage(DatabaseName databaseName, StorageCommitDurability? durability)
        {
            Opens++;
            LastDurability = durability;
            return DocumentStorage.Open(Open(databaseName, "dat", FileMode.Open), Open(databaseName, "log", FileMode.Open), Open(databaseName, "bak", FileMode.Open), checkpointOnOpen: false, durability);
        }

        public override void DropStorage(DatabaseName databaseName)
        {
            foreach (string suffix in new[] { "dat", "log", "bak" }) { File.Delete(Path.Combine(_directory, databaseName + "." + suffix)); }
        }

        public override bool StorageExists(DatabaseName databaseName) => File.Exists(Path.Combine(_directory, databaseName + ".dat"));
        public override IEnumerable<DatabaseName> GetDatabaseNames() => Directory.EnumerateFiles(_directory, "*.dat").Select(file => new DatabaseName(Path.GetFileNameWithoutExtension(file)));
        public void Dispose() => Disposed = true;
        private StorageStream Open(DatabaseName name, string suffix, FileMode mode) => StorageStream.FromFile(Path.Combine(_directory, name + "." + suffix), mode, FileShare.Read);
    }
}
