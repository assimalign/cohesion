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
