using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[CollectionDefinition(nameof(FormatProbeFixTests), DisableParallelization = true)]
[Collection(nameof(FormatProbeFixTests))]
public sealed class FormatProbeFixTests
{
    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Probe fixture: uncommitted brackets journal their own full page image (#1744)")]
    public async Task Seed_UncommittedFirstTouch_ShouldContainUncommittedImage()
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-KeyValuePair-image-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Act.
            await FormatProbeCrash.SeedAsync("KeyValuePair", root, legacy: false);

            // Assert.
            FormatProbeCrash.AssertUncommittedImages(root);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Open: a crash restart emits one physical recovery per set (#1744)")]
    public async Task Open_CrashRestart_ShouldEmitOneRecoveryPerSet()
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-KeyValuePair-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("KeyValuePair", root, legacy: false);
            await using var engine = KeyValueDatabaseEngine.Create("probe", new() { RootPath = root });
            // Bootstrap the parent process's source; the seeder is a separate process.
            using (var warm = KeyValueStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "telemetry-bootstrap")) { }
            var eventSource = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage");
            using var recorder = new EventSourceRecorder(eventSource, EventLevel.Verbose);

            // Act.
            await engine.OpenDatabaseAsync("probe", CancellationToken.None);

            // Assert.
            recorder.Events.Count(e => e.EventName == "RecoveryStart").ShouldBe(2);
            recorder.Events.Count(e => e.EventName == "RecoveryStop").ShouldBe(2);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [KeyValuePair] - Open: a clean older format is refused unchanged through writable storage (#1744)")]
    [InlineData("clean-legacy")]
    [InlineData("clean-index")]
    public async Task Open_CleanOlderFormat_ShouldRefuseUnchangedThroughWritablePath(string mode)
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-KeyValuePair-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("KeyValuePair", root, legacy: true, mode);
            var before = FormatProbeCrash.Snapshot(root);
            await using var engine = KeyValueDatabaseEngine.Create("probe", new() { RootPath = root });
            // Bootstrap the parent process's source; the seeder is a separate process.
            using (var warm = KeyValueStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "telemetry-bootstrap")) { }
            var eventSource = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage");
            using var recorder = new EventSourceRecorder(eventSource, EventLevel.Verbose);

            // Act.
            await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("probe", CancellationToken.None));
            await engine.DisposeAsync();

            // Assert: a real recovery pair proves the writable open ran before refusal.
            recorder.Events.Count(e => e.EventName == "RecoveryStart").ShouldBe((mode == "clean-index" ? 2 : 1));
            recorder.Events.Count(e => e.EventName == "RecoveryStop").ShouldBe((mode == "clean-index" ? 2 : 1));
            recorder.Events.Count(e => e.EventName == "StorageClosed").ShouldBe((mode == "clean-index" ? 2 : 1));
            FormatProbeCrash.AssertUnchanged(root, before);
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "resources", "Database"))) { repository = repository.Parent; }
            string source = File.ReadAllText(Path.Combine(repository!.FullName, "resources", "Database", "Assimalign.Cohesion.Database.KeyValuePair", "src", "KeyValueDatabaseEngine.cs"));
            source.ShouldContain("_strategy.RequiresRecovery(name, catalog: true)", Case.Sensitive);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [KeyValuePair] - Open: a strategy ignoring probe mode fails the invariant (#1744)")]
    public async Task Open_StrategyIgnoresReadOnly_ShouldFailInvariant()
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-KeyValuePair-invariant-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("KeyValuePair", root, legacy: false);
            await using var engine = KeyValueDatabaseEngine.Create("probe", new() { RootPath = root, StorageStrategy = new IgnoringProbeStrategy(root) });

            // Act.
            var failure = await Should.ThrowAsync<InvalidOperationException>(async () => await engine.OpenDatabaseAsync("probe", CancellationToken.None));

            // Assert.
            failure.Message.ShouldBe("The storage strategy did not return a recovered read-only probe.");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    private sealed class IgnoringProbeStrategy : KeyValueStorageStrategy
    {
        private readonly string _root;
        internal IgnoringProbeStrategy(string root) => _root = root;
        public override KeyValueStorage CreateStorage(string databaseName, bool catalog) => throw new NotSupportedException();
        public override KeyValueStorage OpenStorage(string databaseName, bool catalog, bool readOnly = false)
            => KeyValueStorage.Open(Open(catalog, "dat"), Open(catalog, "log"), Open(catalog, "bak"), checkpointOnOpen: false);
        private StorageStream Open(bool catalog, string extension) => StorageStream.FromFile(Path.Combine(_root, "probe", (catalog ? "catalog" : "keyvalue") + "." + extension), FileMode.Open, FileShare.Read);
        public override void DropStorage(string databaseName) => throw new NotSupportedException();
        public override bool StorageExists(string databaseName, bool catalog) => true;
    }
}
