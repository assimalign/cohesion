using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(FormatProbeFixTests))]
public sealed class FormatProbeSecondPassTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Format 35 refuses clean and recovered crash views without changing any file (#1744, #1787)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_Format35_ShouldRefuseRepeatedlyWithoutWrites(bool clean)
    {
        // Arrange: the crash fixture includes committed changes and uncommitted
        // full page images in both file sets. Clean mode checkpoints those sets.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-format35-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("Sql", root, legacy: false, clean ? "clean-format35" : "format35");
            if (!clean) { FormatProbeCrash.AssertUncommittedImages(root); }
            var before = FormatProbeCrash.Snapshot(root);

            // Act / Assert: dispose and repeat, comparing data, logs, backups and sidecars.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await using (var engine = SqlDatabaseEngine.Create("format35-probe", new() { RootPath = root }))
                {
                    var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                        await engine.OpenDatabaseAsync("probe", CancellationToken.None));
                    failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS06041");
                    failure.Message.ShouldContain("uses data-storage format 35", Case.Sensitive);
                    failure.Message.ShouldContain("supports only format 37", Case.Sensitive);
                }
                FormatProbeCrash.AssertUnchanged(root, before);
            }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Unclean format 34 refuses before alternate-owner validation and preserves every byte (#1744, #1777)")]
    public async Task Open_Format34WithInvalidAlternateOwner_ShouldRefuseRecoveredFormatWithoutWrites()
    {
        // Arrange: the catalog marker and poisoned root are committed in unclean journals.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-format34-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("Sql", root, legacy: false, "format34-owner");
            FormatProbeCrash.AssertUncommittedImages(root);
            var before = FormatProbeCrash.Snapshot(root);

            // Act: repeat the refusal to prove that the first attempt did not scrub or checkpoint.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await using (var engine = SqlDatabaseEngine.Create("format34-probe", new() { RootPath = root }))
                {
                    var failure = await Should.ThrowAsync<DatabaseException>(async () => await engine.OpenDatabaseAsync("probe", CancellationToken.None));

                    // Assert: physical-owner attachment would report corruption instead.
                    failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS06041");
                    failure.Message.ShouldContain("34", Case.Sensitive);
                    failure.Message.ShouldContain("supports only format 37", Case.Sensitive);
                }
                FormatProbeCrash.AssertUnchanged(root, before);
            }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Open: each required pre-scan receives the open cancellation token (#1744)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_PreScan_ShouldReceiveCancellationToken(bool clean)
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-prescan-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("Sql", root, legacy: false, clean ? "clean-current" : "current");
            var strategy = new TrackingStrategy(root);
            await using var engine = SqlDatabaseEngine.Create("probe", new() { RootPath = root, StorageStrategy = strategy });
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // Act.
            await engine.OpenDatabaseAsync("probe", cancellation.Token);

            // Assert.
            strategy.Tokens.Count.ShouldBe(clean ? 2 : 1);
            strategy.Tokens.ShouldAllBe(token => token == cancellation.Token);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Pre-scan: built-in memory strategy forwards cancellation (#1744)")]
    public void RequiresRecovery_CanceledMemoryPreScan_ShouldThrowCancellation()
    {
        // Arrange.
        var strategy = new InMemorySqlStorageStrategy();
        using (var data = strategy.CreateStorage("probe", catalog: false)) { }
        using (var catalog = strategy.CreateStorage("probe", catalog: true)) { }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act and assert.
        Should.Throw<OperationCanceledException>(() => strategy.RequiresRecovery("probe", catalog: true, cancellation.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Pre-scan: built-in file strategy forwards cancellation (#1744)")]
    public void RequiresRecovery_CanceledFilePreScan_ShouldThrowCancellation()
    {
        // Arrange.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-cancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "probe"));
            File.WriteAllBytes(Path.Combine(root, "probe", "catalog.dat"), new byte[8192]);
            File.WriteAllBytes(Path.Combine(root, "probe", "catalog.log"), []);
            var strategy = new FileSystemSqlStorageStrategy(root);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            // Act and assert.
            Should.Throw<OperationCanceledException>(() => strategy.RequiresRecovery("probe", catalog: true, cancellation.Token));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    private sealed class TrackingStrategy : SqlStorageStrategy
    {
        private readonly FileSystemSqlStorageStrategy _inner;
        internal TrackingStrategy(string root) => _inner = new FileSystemSqlStorageStrategy(root);
        internal List<CancellationToken> Tokens { get; } = [];
        internal override bool RequiresRecovery(string databaseName, bool catalog, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            return _inner.RequiresRecovery(databaseName, catalog, cancellationToken);
        }
        public override SqlStorage CreateStorage(string databaseName, bool catalog) => _inner.CreateStorage(databaseName, catalog);
        public override SqlStorage OpenStorage(string databaseName, bool catalog, CancellationToken cancellationToken = default, bool readOnly = false)
            => _inner.OpenStorage(databaseName, catalog, cancellationToken, readOnly);
        public override void DropStorage(string databaseName) => _inner.DropStorage(databaseName);
        public override bool StorageExists(string databaseName, bool catalog) => _inner.StorageExists(databaseName, catalog);
    }
}
