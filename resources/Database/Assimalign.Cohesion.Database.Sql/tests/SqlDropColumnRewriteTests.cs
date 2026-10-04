using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// DROP COLUMN's row rewrite (#1237): every stored version — live, tombstoned, or
/// visible only to an older snapshot — is rewritten where it lies, so the entries
/// every index holds for it (key, entry reference, writer) and the version store's
/// locations stay valid. The workload is the one that used to reach the rewrite's
/// relocation fallback: versions written before an <c>ADD COLUMN ... DEFAULT</c>
/// with a long default, on full pages, rewritten after a drop. Materializing that
/// default grew each of them by more than its page had free, so the old rewrite
/// deleted and re-inserted them elsewhere and left their index entries pointing at
/// the freed slots.
/// </summary>
public sealed class SqlDropColumnRewriteTests : IDisposable
{
    private const int InitialRows = 360;
    private static readonly string LongDefault = new('p', 1_500);

    private readonly string _rootPath;

    public SqlDropColumnRewriteTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-drop-column", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            try
            {
                Directory.Delete(_rootPath, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: versions a materializing rewrite would outgrow stay in place, and every index seeks what the scan reads, now, under an older snapshot and after reopen")]
    public async Task DropColumn_VersionsOnFullPagesBehindALongDefault_ShouldStayInPlaceWithEveryIndexConsistent()
    {
        // Arrange: a primary key, two non-unique secondary indexes and a UNIQUE index;
        // full pages of versions written before the long DEFAULT existed.
        var options = new SqlDatabaseEngineOptions { EngineName = "drop-column-rewrite", RootPath = _rootPath };
        var engine = SqlDatabaseEngine.Create(options);
        var database = await engine.CreateDatabaseAsync("rewrite-db");
        var writer = await database.CreateSessionAsync();
        var reader = await database.CreateSessionAsync();

        await ExecuteAsync(writer, "CREATE TABLE t (id INT NOT NULL PRIMARY KEY, grp INT NOT NULL, score INT NOT NULL, note VARCHAR(40), code VARCHAR(20) NOT NULL)");
        await ExecuteAsync(writer, "CREATE INDEX ix_grp ON t (grp)");
        await ExecuteAsync(writer, "CREATE INDEX ix_score ON t (score)");
        await ExecuteAsync(writer, "CREATE UNIQUE INDEX ux_code ON t (code)");

        var model = new Dictionary<long, TableRow>();
        foreach (var batch in Enumerable.Range(0, InitialRows).Chunk(60))
        {
            var tuples = new List<string>();
            foreach (int id in batch)
            {
                var row = new TableRow(id, id % 7, id % 50, CodeOf(id), LongDefault);
                model.Add(id, row);
                tuples.Add($"({id}, {row.Grp}, {row.Score}, 'note-{id:D4}-for-the-dropped-column', '{row.Code}')");
            }

            await ExecuteAsync(writer, $"INSERT INTO t (id, grp, score, note, code) VALUES {string.Join(", ", tuples)}");
        }

        // ADD COLUMN is logical: no existing version stores pad, every one reads the default.
        await ExecuteAsync(writer, $"ALTER TABLE t ADD COLUMN pad VARCHAR(1600) DEFAULT '{LongDefault}'");

        // The reader's snapshot pins the versions the DML below tombstones: they are dead
        // to the writer and still the current state for the reader.
        var snapshot = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var snapshotModel = new Dictionary<long, TableRow>(model);

        await ExecuteAsync(writer, "UPDATE t SET score = score + 1000 WHERE id % 5 = 0");
        foreach (long id in model.Keys.Where(id => id % 5 == 0).ToList())
        {
            model[id] = model[id] with { Score = model[id].Score + 1000 };
        }

        await ExecuteAsync(writer, "DELETE FROM t WHERE id % 11 = 0");
        foreach (long id in model.Keys.Where(id => id % 11 == 0).ToList())
        {
            model.Remove(id);
        }

        // Versions written after the ADD store pad: explicitly, or as the materialized default.
        for (int id = 1_000; id < 1_020; id++)
        {
            bool explicitPad = id % 2 == 0;
            var row = new TableRow(id, id % 7, id % 50, CodeOf(id), explicitPad ? $"short-{id}" : LongDefault);
            model.Add(id, row);
            await ExecuteAsync(writer, explicitPad
                ? $"INSERT INTO t (id, grp, score, note, code, pad) VALUES ({id}, {row.Grp}, {row.Score}, NULL, '{row.Code}', '{row.Pad}')"
                : $"INSERT INTO t (id, grp, score, note, code) VALUES ({id}, {row.Grp}, {row.Score}, 'late-{id}', '{row.Code}')");
        }

        var instance = (SqlDatabaseInstance)database;
        ulong objectId = ObjectIdOf(database);
        instance.DataStorage.GetOwnerPages(objectId).Count.ShouldBeGreaterThanOrEqualTo(4,
            "the versions written before the ADD must fill pages that cannot absorb a 1,500-byte growth");
        var before = StoredVersions(instance, objectId);
        before.Count.ShouldBe(InitialRows + InitialRows / 5 + 20, "every initial version, the new version each update wrote, and the late inserts");
        var probe = snapshotModel.Values.Concat(model.Values).ToList();

        // Act
        await ExecuteAsync(writer, "ALTER TABLE t DROP COLUMN note");

        // Assert: nothing moved and every version shrank — the rewrite spliced the dropped
        // component out where it lay, stamps unchanged.
        var after = StoredVersions(instance, objectId);
        after.Keys.Order().ShouldBe(before.Keys.Order(), "no version may change its location");
        foreach (var (location, version) in after)
        {
            version.Length.ShouldBeLessThan(before[location].Length, $"the version at {location} still stores the dropped column");
            version.Writer.ShouldBe(before[location].Writer);
            version.Deleter.ShouldBe(before[location].Deleter);
        }

        await AssertConsistentAsync(database, writer, model, probe, "after DROP COLUMN");
        await AssertConsistentAsync(database, reader, snapshotModel, probe, "under the snapshot taken before the DML");
        await snapshot.RollbackAsync(CancellationToken.None);

        await AssertUniqueEnforcedAsync(writer, model);
        await AssertConsistentAsync(database, writer, model, probe, "after the UNIQUE probes");

        await reader.DisposeAsync();
        await writer.DisposeAsync();
        await engine.DisposeAsync();

        // Assert: the rewrite and every index survive reopen.
        await using var reopenedEngine = SqlDatabaseEngine.Create(options);
        var reopened = await reopenedEngine.OpenDatabaseAsync("rewrite-db");
        await using var session = await reopened.CreateSessionAsync();
        await AssertConsistentAsync(reopened, session, model, probe, "after reopen");
        await AssertUniqueEnforcedAsync(session, model);
        await AssertConsistentAsync(reopened, session, model, probe, "after the UNIQUE probes past reopen");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: versions that never stored the dropped column are left byte-identical")]
    public async Task DropColumn_ColumnAbsentFromOlderVersions_ShouldLeaveThoseVersionsByteIdentical()
    {
        // Arrange: versions written before the ADD do not store extra; later ones do.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "drop-column-absent" });
        var database = await engine.CreateDatabaseAsync("absent-db");
        await using var session = await database.CreateSessionAsync();

        await ExecuteAsync(session, "CREATE TABLE t (id INT NOT NULL, label VARCHAR(20) NOT NULL)");
        await ExecuteAsync(session, "CREATE INDEX ix_label ON t (label)");
        await ExecuteAsync(session, "INSERT INTO t (id, label) VALUES (1, 'one'), (2, 'two'), (3, 'three')");
        await ExecuteAsync(session, $"ALTER TABLE t ADD COLUMN extra VARCHAR(1600) DEFAULT '{LongDefault}'");
        await ExecuteAsync(session, "INSERT INTO t (id, label, extra) VALUES (4, 'four', 'stored'), (5, 'five', NULL)");
        await ExecuteAsync(session, "UPDATE t SET label = 'two-updated' WHERE id = 2");

        var instance = (SqlDatabaseInstance)database;
        ulong objectId = ObjectIdOf(database);
        var before = StoredVersions(instance, objectId);

        // Act
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN extra");

        // Assert: the three original versions are untouched, byte for byte; the versions
        // that stored extra lost exactly its component and stayed where they were.
        var after = StoredVersions(instance, objectId);
        after.Keys.Order().ShouldBe(before.Keys.Order());
        int untouched = 0;
        foreach (var (location, version) in after)
        {
            if (version.Bytes.AsSpan().SequenceEqual(before[location].Bytes))
            {
                untouched++;
            }
            else
            {
                version.Length.ShouldBeLessThan(before[location].Length);
            }
        }

        // Ids 1 and 3, the tombstoned version of id 2, and none of the versions that stored extra
        // (4, 5 and the updated id 2, written with the default materialized).
        untouched.ShouldBe(3);
        var rows = await Rows(session, "SELECT id, label FROM t ORDER BY id");
        rows.Select(row => (Convert.ToInt64(row[0], CultureInfo.InvariantCulture), (string)row[1]!)).ShouldBe(
            [(1L, "one"), (2L, "two-updated"), (3L, "three"), (4L, "four"), (5L, "five")]);
        foreach (var label in new[] { "one", "two", "two-updated", "three", "four", "five" })
        {
            var seeked = await Rows(session, $"SELECT id FROM t WHERE label = '{label}'");
            AccessPathOf(session).ShouldBe("seek:ix_label");
            seeked.Count.ShouldBe(label == "two" ? 0 : 1, $"label '{label}'");
        }
    }

    // ── Consistency checks ─────────────────────────────────────────────

    /// <summary>
    /// The scan is checked against the model, then every index is sought for every key any
    /// version ever carried: each seek must return exactly the ids the scan holds for the key,
    /// through that index.
    /// </summary>
    private static async Task AssertConsistentAsync(IDatabase database, IDatabaseSession session,
        Dictionary<long, TableRow> expected, List<TableRow> probe, string when)
    {
        var scanned = await Rows(session, "SELECT id, grp, score, code, pad FROM t");
        AccessPathOf(session).ShouldBe("scan");
        var actual = scanned.Select(row => new TableRow(
            Convert.ToInt64(row[0], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[1], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[2], CultureInfo.InvariantCulture),
            (string)row[3]!,
            (string)row[4]!)).OrderBy(row => row.Id).ToList();
        actual.ShouldBe(expected.Values.OrderBy(row => row.Id).ToList(), $"the scan {when}");

        var indexes = IndexedColumns(database);
        indexes.Keys.Order().ShouldBe(["code", "grp", "id", "score"]);

        var mismatches = new List<string>();
        foreach (var (column, index) in indexes)
        {
            foreach (object key in probe.Concat(expected.Values).Select(row => KeyOf(row, column)).Distinct())
            {
                var seeked = (await Rows(session, $"SELECT id FROM t WHERE {column} = {Literal(key)}"))
                    .Select(row => Convert.ToInt64(row[0], CultureInfo.InvariantCulture)).Order().ToList();
                string path = AccessPathOf(session);
                var scannedIds = actual.Where(row => KeyOf(row, column).Equals(key)).Select(row => row.Id).Order().ToList();

                if (path != $"seek:{index}" || !seeked.SequenceEqual(scannedIds))
                {
                    mismatches.Add($"{column} = {Literal(key)} via {path}: seek [{string.Join(", ", seeked)}], scan [{string.Join(", ", scannedIds)}]");
                }
            }
        }

        mismatches.ShouldBeEmpty($"every index seek must equal the scan {when}");
    }

    /// <summary>
    /// UNIQUE holds for live keys on the primary key and the unique index, through INSERT and
    /// UPDATE, while the key of a deleted row is free again.
    /// </summary>
    private static async Task AssertUniqueEnforcedAsync(IDatabaseSession session, Dictionary<long, TableRow> model)
    {
        var live = model.Values.Where(row => row.Id < InitialRows).OrderBy(row => row.Id).ToList();
        var first = live[0];
        var second = live[1];

        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"INSERT INTO t (id, grp, score, code) VALUES ({first.Id}, 1, 1, 'unused-code')")))
            .Message.ShouldContain("UNIQUE", Case.Sensitive);
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"INSERT INTO t (id, grp, score, code) VALUES (5000, 1, 1, '{first.Code}')")))
            .Message.ShouldContain("UNIQUE", Case.Sensitive);
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"UPDATE t SET code = '{first.Code}' WHERE id = {second.Id}")))
            .Message.ShouldContain("UNIQUE", Case.Sensitive);

        // The deleted row's code is free: its entry is tombstoned, not live.
        long freed = Enumerable.Range(0, InitialRows)
            .First(candidate => candidate % 11 == 0 && !model.ContainsKey(candidate) && !model.ContainsKey(candidate + 10_000));
        var reused = new TableRow(freed + 10_000, 3, 3, CodeOf(freed), "reused");
        await ExecuteAsync(session, $"INSERT INTO t (id, grp, score, code, pad) VALUES ({reused.Id}, 3, 3, '{reused.Code}', 'reused')");
        model.Add(reused.Id, reused);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private sealed record TableRow(long Id, long Grp, long Score, string Code, string Pad);

    private sealed record StoredVersion(byte[] Bytes, TransactionSequence Writer, TransactionSequence Deleter)
    {
        public int Length => Bytes.Length;
    }

    private static string CodeOf(long id) => $"code-{id:D5}";

    private static object KeyOf(TableRow row, string column) => column switch
    {
        "id" => row.Id,
        "grp" => row.Grp,
        "score" => row.Score,
        "code" => row.Code,
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
    };

    private static string Literal(object value) => value switch
    {
        string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
        long number => number.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    private static ulong ObjectIdOf(IDatabase database)
    {
        ((SqlDatabaseInstance)database).Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        return table.ObjectId;
    }

    /// <summary>Maps each single-column index of <c>t</c> to its column.</summary>
    private static Dictionary<string, string> IndexedColumns(IDatabase database)
        => ((SqlDatabaseInstance)database).Catalog.GetIndexes(ObjectIdOf(database))
            .ToDictionary(index => index.ColumnNames.Single().ToLowerInvariant(), index => index.Name);

    /// <summary>Every version stored in the table's record chain, by location.</summary>
    private static Dictionary<(PageId PageId, int SlotIndex), StoredVersion> StoredVersions(SqlDatabaseInstance instance, ulong objectId)
    {
        var versions = new Dictionary<(PageId PageId, int SlotIndex), StoredVersion>();
        using var iterator = instance.DataStorage.GetUnitIterator(objectId);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            byte[] bytes = unit.Data.ToArray();
            var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes);
            versions.Add((unit.PageId, unit.SlotIndex), new StoredVersion(bytes, writer, deleter));
        }

        return versions;
    }

    private static string AccessPathOf(IDatabaseSession session)
        => ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull().AccessPath;

    private static async Task ExecuteAsync(IDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(QueryResultStatus.Success, sql.Length > 120 ? sql[..120] : sql);
    }

    private static async Task<List<object?[]>> Rows(IDatabaseSession session, string sql)
    {
        var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result!.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = row.GetValue(index);
            }

            rows.Add(values);
        }

        return rows;
    }
}
