using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTempSpaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cohesion-sql-temp-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Temp space: in-memory requires an explicit directory")]
    public async Task Create_InMemoryOptions_ShouldEnableOnlyExplicitSpill()
    {
        // Arrange
        await using var disabled = SqlDatabaseEngine.Create("disabled", new());
        var options = new SqlDatabaseEngineOptions { TempDirectory = _root, StatementMemoryBudget = 123456 };
        await using var enabled = SqlDatabaseEngine.Create("enabled", options);

        // Act
        enabled.TempSpace.ShouldNotBeNull();
        using var statement = enabled.TempSpace.CreateStatement(enabled.StatementTempSpaceQuota);
        using var run = statement.CreateRun();
        run.Append([1, 2, 3]); run.Seal();

        // Assert
        disabled.TempSpace.ShouldBeNull();
        enabled.StatementTempSpaceQuota.ShouldBe(4 * 123456);
        using var reader = run.OpenReader(8192);
        byte[] bytes = new byte[3]; reader.TryRead(bytes, out int length).ShouldBeTrue();
        length.ShouldBe(3); bytes.ShouldBe([1, 2, 3]);
        await enabled.DisposeAsync();
        Directory.GetFiles(_root).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Temp space: defaults, snapshots, directory restriction and startup sweep")]
    public async Task Create_FileOptions_ShouldKeepSpaceUnderRootAndSweep()
    {
        // Arrange
        string temp = Path.Combine(_root, ".cohesion-temp");
        Directory.CreateDirectory(temp);
        string orphan = Path.Combine(temp, "cohesion-" + Guid.NewGuid().ToString("N") + ".ctmp");
        File.WriteAllBytes(orphan, [1]);
        var options = new SqlDatabaseEngineOptions { RootPath = _root, TempSpaceQuota = 65536, StatementTempSpaceQuota = 8192 };

        // Act
        await using var engine = SqlDatabaseEngine.Create("file", options);
        options.TempSpaceQuota = 0; options.StatementTempSpaceQuota = 0;

        // Assert
        engine.TempSpace.ShouldNotBeNull(); engine.TempSpace.DirectoryPath.ShouldBe(temp);
        engine.TempSpace.EngineQuota.ShouldBe(65536); engine.StatementTempSpaceQuota.ShouldBe(8192);
        File.Exists(orphan).ShouldBeFalse();
        Directory.GetFiles(temp).ShouldBeEmpty();
        Should.Throw<ArgumentException>(() => SqlDatabaseEngine.Create("escape", new() { RootPath = _root, TempDirectory = Path.GetTempPath() }));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("negative", new() { TempSpaceQuota = -1 }));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("negative", new() { StatementTempSpaceQuota = -1 }));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Spill substrate: actual SQL keys preserve direction, collation, NULLs and stable ties")]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    public async Task Sort_SqlDirectedKeys_ShouldMatchLogicalOrder(int orderValue, int collationId)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("keys", new() { TempDirectory = _root, TempSpaceQuota = 32 * 1024 * 1024 });
        using var statement = engine.TempSpace!.CreateStatement(32 * 1024 * 1024);
        using var sort = StorageTempSorter.Create(statement, 65536, 256);
        var order = (SqlCatalogIndexKeyOrder)orderValue;
        var collation = Collation.FromId((byte)collationId);
        string?[] words = [null, "", "a", "A", "aa", "é", "e", "Z", "😀", "𐐀", "𐐨"];
        var source = Enumerable.Range(0, 5000).Select(i => (Word: words[(i * 7) % words.Length], Id: i)).ToArray();
        foreach (var row in source)
        {
            var writer = new DatabaseKeyWriter();
            SqlRowCodec.AppendKeyValue(writer, DatabaseType.String, row.Word, collation);
            byte[] key = writer.ToArray(); SqlIndexKeyEncoding.Transform(key, order);
            byte[] value = new byte[128]; BinaryPrimitives.WriteInt32LittleEndian(value, row.Id);
            sort.Append(key, value);
        }
        bool descending = (orderValue & 2) != 0, nullsFirst = (orderValue & 1) == 0;
        var expected = source.OrderBy(row => row.Word is null ? (nullsFirst ? 0 : 1) : (nullsFirst ? 1 : 0))
            .ThenBy(row => row.Word, Comparer<string?>.Create((a, b) =>
                a is null || b is null ? 0 : descending ? collation.Compare(b, a) : collation.Compare(a, b)))
            .Select(row => row.Id).ToArray();

        // Act
        using var output = sort.Complete();
        using var reader = StorageTempSortReader.Create(output, 8192 + 256);
        var actual = new List<int>();
        while (reader.MoveNext()) { actual.Add(BinaryPrimitives.ReadInt32LittleEndian(reader.Value.Span)); }

        // Assert
        actual.ShouldBe(expected); sort.RunsCreated.ShouldBeGreaterThan(1); sort.MergePasses.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Temp substrate: SQL keeps its existing CS05067 refusal")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_WithOrWithoutTempDirectory_ShouldKeepBudgetRefusal(bool directory)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("no-operator-spill", new()
        { StatementMemoryBudget = 4096, TempDirectory = directory ? (FileSystemPath?)_root : null });
        var database = await engine.CreateDatabaseAsync("budget", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(a INT,v INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES " +
            string.Join(',', Enumerable.Range(0, 100).Select(i => $"({i},{i})")) + ";", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT a,COUNT(*) FROM t GROUP BY GROUPING SETS ((a),());", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        if (directory) { Directory.Exists(_root).ShouldBeFalse(); } // No operator requested a temp run.
    }
}
