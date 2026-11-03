using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[CollectionDefinition(nameof(FormatProbeFixTests), DisableParallelization = true)]
[Collection(nameof(FormatProbeFixTests))]
public sealed class FormatProbeFixTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Probe fixture: uncommitted brackets journal their own full page image (#1744)")]
    public async Task Seed_UncommittedFirstTouch_ShouldContainUncommittedImage()
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-Sql-image-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Act.
            await FormatProbeCrash.SeedAsync("Sql", root, legacy: false);

            // Assert.
            FormatProbeCrash.AssertUncommittedImages(root);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Open: a crash restart emits one physical recovery per set (#1744)")]
    public async Task Open_CrashRestart_ShouldEmitOneRecoveryPerSet()
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-Sql-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("Sql", root, legacy: false);
            await using var engine = SqlDatabaseEngine.Create("probe", new() { RootPath = root });
            // Bootstrap the parent process's source; the seeder is a separate process.
            using (var warm = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "telemetry-bootstrap")) { }
            var eventSource = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage");
            using var recorder = new EventSourceRecorder(eventSource, EventLevel.Verbose);
            EmitUnrelatedStorageEvents();

            // Act.
            await engine.OpenDatabaseAsync("probe", CancellationToken.None);

            // Assert.
            ForDatabase(recorder).Count(e => e.EventName == "RecoveryStart").ShouldBe(2);
            ForDatabase(recorder).Count(e => e.EventName == "RecoveryStop").ShouldBe(2);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Open: a clean older format is refused unchanged through writable storage (#1744)")]
    [InlineData("clean-legacy")]
    [InlineData("clean-index")]
    public async Task Open_CleanOlderFormat_ShouldRefuseUnchangedThroughWritablePath(string mode)
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-Sql-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("Sql", root, legacy: true, mode);
            var before = FormatProbeCrash.Snapshot(root);
            await using var engine = SqlDatabaseEngine.Create("probe", new() { RootPath = root });
            // Bootstrap the parent process's source; the seeder is a separate process.
            using (var warm = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "telemetry-bootstrap")) { }
            var eventSource = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage");
            using var recorder = new EventSourceRecorder(eventSource, EventLevel.Verbose);
            EmitUnrelatedStorageEvents();

            // Act.
            await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("probe", CancellationToken.None));
            await engine.DisposeAsync();

            // Assert: a real recovery pair proves the writable open ran before refusal.
            ForDatabase(recorder).Count(e => e.EventName == "RecoveryStart").ShouldBe((mode == "clean-index" ? 2 : 1));
            ForDatabase(recorder).Count(e => e.EventName == "RecoveryStop").ShouldBe((mode == "clean-index" ? 2 : 1));
            ForDatabase(recorder).Count(e => e.EventName == "StorageClosed").ShouldBe((mode == "clean-index" ? 2 : 1));
            FormatProbeCrash.AssertUnchanged(root, before);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Open: a strategy ignoring probe mode fails the invariant (#1744)")]
    public async Task Open_StrategyIgnoresReadOnly_ShouldFailInvariant()
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-Sql-invariant-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("Sql", root, legacy: false);
            await using var engine = SqlDatabaseEngine.Create("probe", new() { RootPath = root, StorageStrategy = new IgnoringProbeStrategy(root) });

            // Act.
            var failure = await Should.ThrowAsync<InvalidOperationException>(async () => await engine.OpenDatabaseAsync("probe", CancellationToken.None));

            // Assert.
            failure.Message.ShouldBe("The storage strategy did not return a recovered read-only probe.");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    private static IEnumerable<EventWrittenEventArgs> ForDatabase(EventSourceRecorder recorder)
        => recorder.Events.Where(e => e.PayloadNames?.IndexOf("database") is >= 0
            && e.Payload![e.PayloadNames.IndexOf("database")] is string database
            && (database == "probe" || database == "probe/catalog"));

    private static void EmitUnrelatedStorageEvents()
    {
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var backup = new MemoryStream();
        using (var seed = SqlStorage.Create(data, journal, backup, "telemetry-unrelated")) { }
        using var unrelated = SqlStorage.Open(new MemoryStream(data.ToArray()), new MemoryStream(journal.ToArray()), new MemoryStream(backup.ToArray()));
    }

    private sealed class IgnoringProbeStrategy : SqlStorageStrategy
    {
        private readonly string _root;
        internal IgnoringProbeStrategy(string root) => _root = root;
        public override SqlStorage CreateStorage(string databaseName, bool catalog) => throw new NotSupportedException();
        public override SqlStorage OpenStorage(string databaseName, bool catalog, CancellationToken cancellationToken = default, bool readOnly = false)
            => SqlStorage.Open(Open(catalog, "dat"), Open(catalog, "log"), Open(catalog, "bak"), checkpointOnOpen: false);
        private StorageStream Open(bool catalog, string extension) => StorageStream.FromFile(Path.Combine(_root, "probe", (catalog ? "catalog" : "sql") + "." + extension), FileMode.Open, FileShare.Read);
        public override void DropStorage(string databaseName) => throw new NotSupportedException();
        public override bool StorageExists(string databaseName, bool catalog) => true;
    }
}
