using System;
using System.Collections.Concurrent;
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
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// DROP COLUMN as a dropped-attribute change (#1241): the catalog marks the column's
/// physical ordinal dropped and no stored version is read or written. Every value is
/// checked against a model of the table. A crash at every point of the statement
/// reopens on one definition with each value in its own column; SELECTs running beside
/// the drop read every value in its own column on whichever definition they bound; a
/// re-added name never reads the dropped values; and indexes and constraints on the
/// neighbouring columns keep answering and enforcing exactly what they did.
/// </summary>
public sealed class SqlDropColumnTests : IDisposable
{
    private static readonly string LongDefault = new('p', 1_500);

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-drop-column", Guid.NewGuid().ToString("N"));

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

    // ── Crash at every point ───────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: a crash at every point reopens on one definition with every value in its column (#1241)")]
    public async Task DropColumn_CrashAtEveryPoint_ShouldReopenWithEveryValueInItsColumn()
    {
        // Arrange: committed rows, versions an UPDATE replaced and rows a DELETE
        // tombstoned, indexes on the columns either side of the one to drop.
        var storage = new CrashCaptureSqlStorageStrategy();
        var model = new Dictionary<int, ModelRow>();
        CrashCaptureSqlStorageStrategy.CrashPointRecorder recorder;
        await using (var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "drop-column-crash", StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("crash-db");
            await using var session = await database.CreateSessionAsync();
            await CreateModelTableAsync(session, "t", model, rows: 120);
            await ExecuteAsync(session, "UPDATE t SET c = c + 1000 WHERE id % 4 = 0");
            foreach (int id in model.Keys.Where(id => id % 4 == 0).ToList())
            {
                model[id] = model[id] with { C = model[id].C + 1000 };
            }

            await ExecuteAsync(session, "DELETE FROM t WHERE id % 9 = 0");
            foreach (int id in model.Keys.Where(id => id % 9 == 0).ToList())
            {
                model.Remove(id);
            }

            // Act: record the durable images after every write the drop makes.
            using (recorder = storage.RecordCrashPoints())
            {
                await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN b");
            }
        }

        // Assert: every crash point reopens, on the definition before or after the drop,
        // and every value the scan and the seeks return sits in its own column; the
        // reopened table takes writes on the definition it found.
        var outcomes = new List<string>();
        var mismatches = new List<string>();
        for (int point = 0; point < recorder.Count; point++)
        {
            await using var reopened = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
            {
                EngineName = $"drop-column-crash-{point}",
                StorageStrategy = recorder.Open(point),
            });
            var database = await reopened.OpenDatabaseAsync("crash-db");
            await using var session = await database.CreateSessionAsync();
            ((SqlDatabaseInstance)database).Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            bool dropped = table.FindColumn("b") is null;

            var found = await CheckTableAsync(session, "t", model, seeks: true);
            var written = new Dictionary<int, ModelRow>(model);
            var row = ModelRow.Of(5_000);
            written.Add(row.Id, row);
            await ExecuteAsync(session, dropped
                ? $"INSERT INTO t VALUES ({row.Id}, {row.A}, {row.C}, '{row.D}', {row.E})"
                : $"INSERT INTO t VALUES ({row.Id}, {row.A}, '{row.B}', {row.C}, '{row.D}', {row.E})");
            found.AddRange(await CheckTableAsync(session, "t", written, seeks: false));

            outcomes.Add($"{point}:{(dropped ? "after" : "before")}:{found.Count}");
            mismatches.AddRange(found.Select(mismatch => $"crash point {point}: {mismatch}"));
        }

        mismatches.Count.ShouldBe(0,
            $"crash points (index:definition:mismatches) [{string.Join(", ", outcomes)}]; first: {string.Join(" | ", mismatches.Take(6))}");
        outcomes[0].ShouldStartWith("0:before:");
        outcomes[^1].ShouldBe($"{outcomes.Count - 1}:after:0");
    }

    // ── Concurrent SELECTs ─────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: SELECTs running beside it read every value in its column (#1241)")]
    public async Task DropColumn_ConcurrentSelects_ShouldReadEveryValueInItsColumn()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "drop-column-readers" });
        var database = await engine.CreateDatabaseAsync("readers-db");
        await using var setup = await database.CreateSessionAsync();

        const int readers = 4;
        const int rows = 3_000;
        var failures = new ConcurrentQueue<string>();
        long statements = 0;
        long wrongRows = 0;
        long failedStatements = 0;
        long beforeShape = 0;
        long afterShape = 0;

        // a, c and d carry indexes and a CHECK, so the droppable columns are b (between
        // two indexed columns) and e (the last one).
        string[] dropped = ["b", "e", "b", "e"];

        for (int round = 0; round < dropped.Length; round++)
        {
            string table = $"t{round}";
            string column = dropped[round];
            var model = new Dictionary<int, ModelRow>();
            await CreateModelTableAsync(setup, table, model, rows);

            using var stop = new CancellationTokenSource();
            int started = 0;
            var tasks = Enumerable.Range(0, readers).Select(reader => Task.Run(async () =>
            {
                await using var session = await database.CreateSessionAsync();
                int query = 0;
                Interlocked.Increment(ref started);
                while (!stop.IsCancellationRequested)
                {
                    int id = 1 + (((reader * 997) + (query * 31)) % rows);
                    string sql = (query++ % 3) switch
                    {
                        0 => $"SELECT * FROM {table}",
                        1 => $"SELECT * FROM {table} WHERE c = {ModelRow.Of(id).C}",
                        _ => $"SELECT * FROM {table} WHERE id >= {id} AND id < {id + 200}",
                    };

                    try
                    {
                        var (columns, result) = await RowsAsync(session, sql);
                        Interlocked.Increment(ref statements);
                        Interlocked.Increment(ref columns.Contains(column, StringComparer.OrdinalIgnoreCase) ? ref beforeShape : ref afterShape);
                        foreach (var row in result)
                        {
                            var mismatches = Compare(columns, [row], model).ToList();
                            if (mismatches.Count > 0)
                            {
                                Interlocked.Increment(ref wrongRows);
                                failures.Enqueue($"{table}, {sql}: {mismatches[0]}");
                            }
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failedStatements);
                        failures.Enqueue($"{table}, {sql}: {exception.GetType().Name}: {exception.Message}");
                    }
                }
            })).ToArray();

            // Act: drop a column once every reader has completed SELECTs on the old
            // definition, while they scan and seek; let them read on afterwards until they
            // have completed as many on the new one.
            long before = Interlocked.Read(ref beforeShape);
            await WaitUntilAsync(() => Volatile.Read(ref started) == readers && Interlocked.Read(ref beforeShape) >= before + (2 * readers));
            await ExecuteAsync(setup, $"ALTER TABLE {table} DROP COLUMN {column}");
            long after = Interlocked.Read(ref afterShape);
            await WaitUntilAsync(() => Interlocked.Read(ref afterShape) >= after + (2 * readers));
            stop.Cancel();
            await Task.WhenAll(tasks);

            (await CheckTableAsync(setup, table, model, seeks: false)).ShouldBeEmpty();
        }

        // Assert: readers ran on both definitions, and none read a value in a wrong column.
        beforeShape.ShouldBeGreaterThan(0);
        afterShape.ShouldBeGreaterThan(0);
        failures.Count.ShouldBe(0,
            $"{wrongRows} wrong rows and {failedStatements} failed statements in {statements} statements; " +
            $"first: {string.Join(" | ", failures.Take(6))}");
    }

    // ── No version is rewritten ────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: every stored version stays byte-identical and every index seeks what the scan reads, now, under an older snapshot and after reopen")]
    public async Task DropColumn_FullPagesBehindALongDefault_ShouldRewriteNoVersionAndKeepEveryIndexConsistent()
    {
        // Arrange: a primary key, two non-unique secondary indexes and a UNIQUE index; full
        // pages of versions written before a long DEFAULT existed (the workload that made
        // the former in-place rewrite move versions away from their index entries, #1237).
        var options = new SqlDatabaseEngineOptions { EngineName = "drop-column-pages", RootPath = _rootPath };
        var engine = SqlDatabaseEngine.Create(options);
        var database = await engine.CreateDatabaseAsync("pages-db");
        var writer = await database.CreateSessionAsync();
        var reader = await database.CreateSessionAsync();

        await ExecuteAsync(writer, "CREATE TABLE t (id INT NOT NULL PRIMARY KEY, grp INT NOT NULL, score INT NOT NULL, note VARCHAR(40), code VARCHAR(20) NOT NULL)");
        await ExecuteAsync(writer, "CREATE INDEX ix_grp ON t (grp)");
        await ExecuteAsync(writer, "CREATE INDEX ix_score ON t (score)");
        await ExecuteAsync(writer, "CREATE UNIQUE INDEX ux_code ON t (code)");

        var model = new Dictionary<long, PagedRow>();
        foreach (var batch in Enumerable.Range(0, 360).Chunk(60))
        {
            var tuples = new List<string>();
            foreach (int id in batch)
            {
                var row = new PagedRow(id, id % 7, id % 50, CodeOf(id), LongDefault);
                model.Add(id, row);
                tuples.Add($"({id}, {row.Grp}, {row.Score}, 'note-{id:D4}-for-the-dropped-column', '{row.Code}')");
            }

            await ExecuteAsync(writer, $"INSERT INTO t (id, grp, score, note, code) VALUES {string.Join(", ", tuples)}");
        }

        await ExecuteAsync(writer, $"ALTER TABLE t ADD COLUMN pad VARCHAR(1600) DEFAULT '{LongDefault}'");

        // The reader's snapshot pins the versions the DML below tombstones.
        var snapshot = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var snapshotModel = new Dictionary<long, PagedRow>(model);

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

        for (int id = 1_000; id < 1_020; id++)
        {
            bool explicitPad = id % 2 == 0;
            var row = new PagedRow(id, id % 7, id % 50, CodeOf(id), explicitPad ? $"short-{id}" : LongDefault);
            model.Add(id, row);
            await ExecuteAsync(writer, explicitPad
                ? $"INSERT INTO t (id, grp, score, note, code, pad) VALUES ({id}, {row.Grp}, {row.Score}, NULL, '{row.Code}', '{row.Pad}')"
                : $"INSERT INTO t (id, grp, score, note, code) VALUES ({id}, {row.Grp}, {row.Score}, 'late-{id}', '{row.Code}')");
        }

        var instance = (SqlDatabaseInstance)database;
        ulong objectId = ObjectIdOf(instance, "t");
        instance.DataStorage.GetOwnerPages(objectId).Count.ShouldBeGreaterThanOrEqualTo(4);
        var before = StoredVersions(instance, objectId);
        var probe = snapshotModel.Values.Concat(model.Values).ToList();

        // Act
        await ExecuteAsync(writer, "ALTER TABLE t DROP COLUMN note");

        // Assert: every version is where it was, byte for byte — the drop read and wrote none.
        AssertUnchanged(before, StoredVersions(instance, objectId));
        instance.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns.Select(column => column.Name).ShouldBe(["id", "grp", "score", "code", "pad"]);
        table.DroppedColumnOrdinals.ShouldBe([3]);
        table.PhysicalColumnCount.ShouldBe(6);

        await AssertPagedConsistentAsync(instance, writer, model, probe, "after DROP COLUMN");
        await AssertPagedConsistentAsync(instance, reader, snapshotModel, probe, "under the snapshot taken before the DML");
        await snapshot.RollbackAsync(CancellationToken.None);

        // Writes after the drop store NULL at the dropped ordinal and keep UNIQUE enforced.
        await AssertUniqueEnforcedAsync(writer, model);
        await ExecuteAsync(writer, "UPDATE t SET pad = 'after-drop' WHERE id = 1");
        model[1] = model[1] with { Pad = "after-drop" };
        await AssertPagedConsistentAsync(instance, writer, model, probe, "after writes on the new definition");

        await reader.DisposeAsync();
        await writer.DisposeAsync();
        await engine.DisposeAsync();

        // Assert: the dropped layout and every index survive reopen.
        await using var reopenedEngine = SqlDatabaseEngine.Create(options);
        var reopened = (SqlDatabaseInstance)await reopenedEngine.OpenDatabaseAsync("pages-db");
        reopened.Catalog.TryGetTable("dbo", "t", out var persisted).ShouldBeTrue();
        persisted.DroppedColumnOrdinals.ShouldBe([3]);
        await using var session = await reopened.CreateSessionAsync();
        await AssertPagedConsistentAsync(reopened, session, model, probe, "after reopen");
        await AssertUniqueEnforcedAsync(session, model);
        await AssertPagedConsistentAsync(reopened, session, model, probe, "after the UNIQUE probes past reopen");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: the drop reads no version, so a damaged one neither fails nor is touched by it")]
    public async Task DropColumn_MalformedVersion_ShouldNeitherFailNorTouchIt()
    {
        // Arrange: the dropped column comes first, so its component directly follows the
        // object id; one version gets a component tag no type uses.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "drop-column-malformed" });
        var database = await engine.CreateDatabaseAsync("malformed-db");
        await using var session = await database.CreateSessionAsync();

        await ExecuteAsync(session, "CREATE TABLE t (note VARCHAR(40), id INT NOT NULL PRIMARY KEY, code VARCHAR(20) NOT NULL)");
        await ExecuteAsync(session, "CREATE INDEX ix_code ON t (code)");
        await ExecuteAsync(session, "INSERT INTO t (note, id, code) VALUES ('n1', 1, 'c1'), ('n2', 2, 'c2'), ('n3', 3, 'c3')");

        var instance = (SqlDatabaseInstance)database;
        ulong objectId = ObjectIdOf(instance, "t");
        var (location, intact) = StoredVersions(instance, objectId).OrderBy(pair => pair.Key).Last();
        byte[] malformed = intact.Bytes.ToArray();
        int noteTag = SqlRowCodec.StampHeaderSize + new DatabaseKeyWriter().AppendInt64((long)objectId).ToArray().Length;
        ((DatabaseType)malformed[noteTag]).ShouldBe(DatabaseType.String);
        malformed[noteTag] = 0xEE;
        instance.DataStorage.UpdateRow(location.PageId, location.SlotIndex, malformed);
        var before = StoredVersions(instance, objectId);

        // Act
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN note");

        // Assert: the drop committed without reading the damaged version, which is still as
        // it was; reading it is where the damage surfaces.
        instance.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns.Select(column => column.Name).ShouldBe(["id", "code"]);
        AssertUnchanged(before, StoredVersions(instance, objectId));
        await Should.ThrowAsync<Exception>(async () => await RowsAsync(session, "SELECT * FROM t"));

        // With the version repaired, every row reads in its own columns and the index seeks it.
        instance.DataStorage.UpdateRow(location.PageId, location.SlotIndex, intact.Bytes);
        var (columns, rows) = await RowsAsync(session, "SELECT * FROM t ORDER BY id");
        columns.ShouldBe(["id", "code"]);
        ShouldBeRows(rows, [[1, "c1"], [2, "c2"], [3, "c3"]]);
        foreach (var code in new[] { "c1", "c2", "c3" })
        {
            (await RowsAsync(session, $"SELECT id FROM t WHERE code = '{code}'")).Rows.Count.ShouldBe(1, code);
            AccessPathOf(session).ShouldBe("seek:ix_code");
        }
    }

    // ── DROP then ADD COLUMN with the same name ────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: a column re-added under the same name never reads the dropped values, through every cycle and after reopen")]
    public async Task DropColumn_ThenAddSameName_ShouldNeverReadTheDroppedValues()
    {
        // Arrange: versions that store each generation of the column.
        var options = new SqlDatabaseEngineOptions { EngineName = "drop-column-readd", RootPath = _rootPath };
        var expected = new Dictionary<int, (string Label, object? Extra, string Tail)>();
        await using (var engine = SqlDatabaseEngine.Create(options))
        {
            var database = await engine.CreateDatabaseAsync("readd-db");
            await using var session = await database.CreateSessionAsync();
            await ExecuteAsync(session, "CREATE TABLE t (id INT NOT NULL PRIMARY KEY, extra VARCHAR(20), label VARCHAR(20) NOT NULL, tail VARCHAR(20) DEFAULT 'tail-default')");
            await ExecuteAsync(session, "CREATE INDEX ix_label ON t (label)");
            await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'gen0-1', 'one', 't1'), (2, 'gen0-2', 'two', 't2')");
            await ExecuteAsync(session, "UPDATE t SET extra = 'gen0-1-updated' WHERE id = 1");

            // Act: drop/re-add cycles, each re-added column a different type and default. The
            // NOT NULL DEFAULT addition validates every existing row through the layout, and
            // its CHECK holds for the generation it was declared on.
            await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN extra");
            await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN extra INT NOT NULL DEFAULT 7");
            await ExecuteAsync(session, "ALTER TABLE t ADD CONSTRAINT ck_t_extra CHECK (extra > 0)");
            await ExecuteAsync(session, "INSERT INTO t (id, label, tail, extra) VALUES (3, 'three', 't3', 30)");
            ShouldBeRows((await RowsAsync(session, "SELECT id, extra FROM t ORDER BY id")).Rows, [[1, 7], [2, 7], [3, 30]]);
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "INSERT INTO t (id, label, extra) VALUES (99, 'bad', -1)"))).Message.ShouldContain("ck_t_extra");
            await ExecuteAsync(session, "ALTER TABLE t DROP CONSTRAINT ck_t_extra");
            await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN extra");
            await ExecuteAsync(session, "INSERT INTO t (id, label, tail) VALUES (4, 'four', 't4')");
            await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN extra VARCHAR(20)");
            await ExecuteAsync(session, "INSERT INTO t (id, label, extra) VALUES (5, 'five', 'gen2-5')");
            await ExecuteAsync(session, "UPDATE t SET label = 'two-updated' WHERE id = 2");

            expected[1] = ("one", null, "t1");
            expected[2] = ("two-updated", null, "t2");
            expected[3] = ("three", null, "t3");
            expected[4] = ("four", null, "t4");
            expected[5] = ("five", "gen2-5", "tail-default");

            // Assert
            await AssertReAddedAsync(session, expected, "after three cycles");
            ((SqlDatabaseInstance)database).Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            table.Columns.Select(column => column.Name).ShouldBe(["id", "label", "tail", "extra"]);
            table.DroppedColumnOrdinals.ShouldBe([1, 4]);
            table.PhysicalColumnCount.ShouldBe(6);
            Enumerable.Range(0, 4).Select(table.GetPhysicalOrdinal).ShouldBe([0, 2, 3, 5]);

            // The persisted DEFAULT of a column behind the dropped ordinals still resolves.
            await ExecuteAsync(session, "INSERT INTO t (id, label, extra) VALUES (6, 'six', 'neg')");
            expected[6] = ("six", "neg", "tail-default");
            await AssertReAddedAsync(session, expected, "after an insert on the third generation");
        }

        // Assert: the layout, the defaults and the values survive reopen.
        await using var reopened = SqlDatabaseEngine.Create(options);
        var restored = await reopened.OpenDatabaseAsync("readd-db");
        await using var restoredSession = await restored.CreateSessionAsync();
        await AssertReAddedAsync(restoredSession, expected, "after reopen");
        await ExecuteAsync(restoredSession, "ALTER TABLE t DROP COLUMN extra");
        await ExecuteAsync(restoredSession, "ALTER TABLE t ADD COLUMN extra INT DEFAULT 42");
        var (columns, rows) = await RowsAsync(restoredSession, "SELECT * FROM t ORDER BY id");
        columns.ShouldBe(["id", "label", "tail", "extra"]);
        rows.Select(row => row[3]).ShouldAllBe(value => Equals(value, 42), "the fourth generation reads its default for every version");
    }

    // ── Row size ───────────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: dropped columns behind the last live one cost later versions nothing, so a row near the size limit stays writable")]
    public async Task DropColumn_RepeatedTrailingDrops_ShouldNotGrowLaterVersions()
    {
        // Arrange: one row a few dozen bytes under the largest record a page holds.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "drop-column-row-size" });
        var database = await engine.CreateDatabaseAsync("row-size-db");
        await using var session = await database.CreateSessionAsync();
        var instance = (SqlDatabaseInstance)database;
        string big = new('x', 8000);
        await ExecuteAsync(session, "CREATE TABLE t (id INT NOT NULL PRIMARY KEY, big VARCHAR(8000))");
        await ExecuteAsync(session, $"INSERT INTO t VALUES (1, '{big}')");
        ulong objectId = ObjectIdOf(instance, "t");
        int length = StoredVersions(instance, objectId).Values.ShouldHaveSingleItem().Bytes.Length;
        (Assimalign.Cohesion.Database.Storage.Units.SlottedPage.MaxRecordSize - length).ShouldBeLessThan(100);

        // Act: a hundred columns added and dropped behind the last live one, then writes.
        for (int cycle = 0; cycle < 100; cycle++)
        {
            await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN e INT");
            await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN e");
        }

        await ExecuteAsync(session, "UPDATE t SET big = big WHERE id = 1");
        await ExecuteAsync(session, $"INSERT INTO t VALUES (2, '{big}')");

        // Assert: the new versions store no component for the hundred dropped ordinals.
        StoredVersions(instance, objectId).Values.Where(version => version.Deleter == TransactionSequence.None)
            .Select(version => version.Bytes.Length).ShouldBe([length, length]);
        ShouldBeRows((await RowsAsync(session, "SELECT id, big FROM t ORDER BY id")).Rows, [[1, big], [2, big]]);
    }

    // ── Neighbouring indexes and constraints ───────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: indexes and constraints on the neighbouring columns keep answering and enforcing what they did")]
    public async Task DropColumn_NeighbouringIndexesAndConstraints_ShouldKeepEnforcingThem()
    {
        // Arrange: b sits between two indexed, CHECKed columns; the child references the
        // parent's d through its UNIQUE index, and its own key sits after a dropped column.
        var options = new SqlDatabaseEngineOptions { EngineName = "drop-column-neighbours", RootPath = _rootPath };
        var model = new Dictionary<int, ModelRow>();
        await using (var engine = SqlDatabaseEngine.Create(options))
        {
            var database = await engine.CreateDatabaseAsync("neighbours-db");
            await using var session = await database.CreateSessionAsync();
            await CreateModelTableAsync(session, "p", model, rows: 200);
            await ExecuteAsync(session,
                "CREATE TABLE ch (cid INT NOT NULL PRIMARY KEY, junk VARCHAR(20), parent_d VARCHAR(20) NOT NULL, qty INT NOT NULL, " +
                "CONSTRAINT fk_ch_p FOREIGN KEY (parent_d) REFERENCES p(d), CONSTRAINT ck_ch_qty CHECK (qty > 0))");
            await ExecuteAsync(session, "INSERT INTO ch VALUES (1, 'j1', 'd-00001', 5), (2, 'j2', 'd-00002', 6)");

            // Act
            await ExecuteAsync(session, "ALTER TABLE p DROP COLUMN b");
            await ExecuteAsync(session, "ALTER TABLE ch DROP COLUMN junk");

            // Assert: every index seeks what the model holds.
            (await CheckTableAsync(session, "p", model, seeks: true)).ShouldBeEmpty();
            await AssertNeighbourConstraintsAsync(session, model);

            // An index built after the drop over a column behind the dropped ordinal, and a
            // constraint whose backfill reads every row, both decode through the layout.
            await ExecuteAsync(session, "CREATE INDEX ix_p_e ON p (e)");
            foreach (var row in model.Values.Where(row => row.Id % 13 == 0))
            {
                var (_, seeked) = await RowsAsync(session, $"SELECT id FROM p WHERE e = {row.E}");
                AccessPathOf(session).ShouldBe("seek:ix_p_e");
                seeked.ShouldHaveSingleItem()[0].ShouldBe(row.Id);
            }

            await ExecuteAsync(session, "ALTER TABLE p ADD CONSTRAINT ck_p_ec CHECK (e > c)");
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ALTER TABLE p ADD CONSTRAINT ck_p_ce CHECK (e < c)")))
                .Message.ShouldContain("ck_p_ce");
            await ExecuteAsync(session, "ALTER TABLE p ADD CONSTRAINT ux_p_e UNIQUE (e)");
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                $"INSERT INTO p VALUES (9001, 1, 2, 'd-09001', {model[1].E})"))).Message.ShouldContain("UNIQUE");

            ShouldBeRows((await RowsAsync(session, "SELECT cid, parent_d, qty FROM ch ORDER BY cid")).Rows, [[1, "d-00001", 5], [2, "d-00002", 6]]);
        }

        // Assert: after reopen the persisted CHECKs, keys and indexes still bind by name.
        await using var reopened = SqlDatabaseEngine.Create(options);
        var restored = await reopened.OpenDatabaseAsync("neighbours-db");
        await using var restoredSession = await restored.CreateSessionAsync();
        (await CheckTableAsync(restoredSession, "p", model, seeks: true)).ShouldBeEmpty();
        await AssertNeighbourConstraintsAsync(restoredSession, model);
        (await Should.ThrowAsync<DatabaseException>(async () => await restoredSession.ExecuteAsync(
            "INSERT INTO p VALUES (9002, 1, 2, 'd-09002', 1)"))).Message.ShouldContain("ck_p_ec");
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: a drop the catalog refuses fails with the catalog's message and changes nothing")]
    [InlineData("t", "missing", "Table 'dbo.t' has no column named 'missing'.")]
    [InlineData("t", "id", "Column 'id' is part of the primary key of 'dbo.t' and cannot be dropped.")]
    [InlineData("t", "code", "Column 'code' is referenced by index 'ix_code' on 'dbo.t'. Drop the index first.")]
    [InlineData("solo", "sole", "Cannot drop the last column of 'dbo.solo'.")]
    public async Task DropColumn_RefusedByTheCatalog_ShouldFailWithItsMessageAndChangeNothing(string tableName, string column, string message)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "drop-column-refused" });
        var database = await engine.CreateDatabaseAsync("refused-db");
        await using var session = await database.CreateSessionAsync();

        // The table-level CHECK reads the key, so it must not displace the key's own refusal.
        await ExecuteAsync(session,
            "CREATE TABLE t (id INT NOT NULL PRIMARY KEY, note VARCHAR(40), code VARCHAR(20) NOT NULL, CONSTRAINT ck_t_id CHECK (id > 0))");
        await ExecuteAsync(session, "CREATE INDEX ix_code ON t (code)");
        await ExecuteAsync(session, "INSERT INTO t (id, note, code) VALUES (1, 'n1', 'c1'), (2, 'n2', 'c2')");
        await ExecuteAsync(session, "CREATE TABLE solo (sole INT)");
        await ExecuteAsync(session, "INSERT INTO solo (sole) VALUES (1)");

        var instance = (SqlDatabaseInstance)database;
        instance.Catalog.TryGetTable("dbo", tableName, out var target).ShouldBeTrue();
        var before = StoredVersions(instance, target.ObjectId);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"ALTER TABLE {tableName} DROP COLUMN {column}"));

        // Assert: the same published definition, and every version as it was.
        failure.Message.ShouldBe(message);
        instance.Catalog.TryGetTable("dbo", tableName, out var table).ShouldBeTrue();
        table.ShouldBeSameAs(target);
        AssertUnchanged(before, StoredVersions(instance, target.ObjectId));
    }

    // ── Result shape and metadata ──────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DROP COLUMN: SELECT *, the wire result header and INFORMATION_SCHEMA show only live columns, densely numbered")]
    public async Task DropColumn_ResultShapeAndMetadata_ShouldShowOnlyLiveColumns()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        await ExecuteAsync(client, "CREATE TABLE orders (id INT NOT NULL PRIMARY KEY, legacy VARCHAR(20), amount DECIMAL(12, 2), note VARCHAR(20))");
        await ExecuteAsync(client, "INSERT INTO orders VALUES (1, 'old', 12.50, 'first')");

        // Act
        await ExecuteAsync(client, "ALTER TABLE orders DROP COLUMN legacy");
        await ExecuteAsync(client, "ALTER TABLE orders ADD COLUMN legacy BIGINT DEFAULT 9");
        await ExecuteAsync(client, "INSERT INTO orders VALUES (2, 3.25, 'second', 10)");

        // Assert: the wire header names and types the live columns in their order.
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT * FROM orders ORDER BY id").Encode());
        var header = ProtocolResultHeaderMessage.Decode((await client.ExpectAsync(ProtocolMessageType.ResultHeader)).Payload.Span);
        header.Columns.Select(column => column.Item1).ShouldBe(["id", "amount", "note", "legacy"]);
        header.Columns.Select(column => column.Item2).ShouldBe(
            [(byte)DatabaseType.Int32, (byte)DatabaseType.Decimal, (byte)DatabaseType.String, (byte)DatabaseType.Int64]);
        var rows = await ReadWireRowsAsync(client);
        ShouldBeRows(rows, [[1, 12.50m, "first", 9L], [2, 3.25m, "second", 10L]]);

        // INFORMATION_SCHEMA numbers the live columns 1..n, as the SQL standard does.
        var columns = await QueryWireAsync(client,
            "SELECT COLUMN_NAME, ORDINAL_POSITION, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders' ORDER BY ORDINAL_POSITION");
        ShouldBeRows(columns, [["id", 1L, "INTEGER"], ["amount", 2L, "NUMERIC"], ["note", 3L, "CHARACTER VARYING"], ["legacy", 4L, "BIGINT"]]);
        var indexed = await QueryWireAsync(client,
            "SELECT COLUMN_NAME, ORDINAL_POSITION FROM COHESION_SCHEMA.INDEXES WHERE TABLE_NAME = 'orders'");
        ShouldBeRows(indexed, [["id", 1L]]);
    }

    // ── Model ──────────────────────────────────────────────────────────

    /// <summary>
    /// One row of a model table <c>(id, a, b, c, d, e)</c>: every column's value derives
    /// from the id with its own type and transform, so a value read under the wrong
    /// column can never pass for the right one.
    /// </summary>
    private sealed record ModelRow(int Id, int A, string B, int C, string D, long E)
    {
        internal static ModelRow Of(int id)
            => new(id, id * 3, $"b-{id:D5}-dropped", (id * 7) + 1, $"d-{id:D5}", (id * 11L) + 5);

        internal object? Get(string column) => column.ToLowerInvariant() switch
        {
            "id" => Id,
            "a" => A,
            "b" => B,
            "c" => C,
            "d" => D,
            "e" => E,
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
        };
    }

    /// <summary>
    /// Creates a model table: an index on a (before b), on c (after b) and a UNIQUE index on
    /// d, and a CHECK across a and c, so b is the droppable column between constrained
    /// neighbours and e the droppable last one.
    /// </summary>
    private static async Task CreateModelTableAsync(IDatabaseSession session, string table, Dictionary<int, ModelRow> model, int rows)
    {
        await ExecuteAsync(session,
            $"CREATE TABLE {table} (id INT NOT NULL PRIMARY KEY, a INT NOT NULL, b VARCHAR(40), c INT NOT NULL, d VARCHAR(20) NOT NULL, e BIGINT, " +
            $"CONSTRAINT ck_{table}_ca CHECK (c > a))");
        await ExecuteAsync(session, $"CREATE INDEX ix_{table}_a ON {table} (a)");
        await ExecuteAsync(session, $"CREATE INDEX ix_{table}_c ON {table} (c)");
        await ExecuteAsync(session, $"CREATE UNIQUE INDEX ux_{table}_d ON {table} (d)");

        foreach (var batch in Enumerable.Range(1, rows).Chunk(250))
        {
            var tuples = new List<string>();
            foreach (int id in batch)
            {
                var row = ModelRow.Of(id);
                model.Add(id, row);
                tuples.Add($"({row.Id}, {row.A}, '{row.B}', {row.C}, '{row.D}', {row.E})");
            }

            await ExecuteAsync(session, $"INSERT INTO {table} (id, a, b, c, d, e) VALUES {string.Join(", ", tuples)}");
        }
    }

    /// <summary>
    /// Compares a result against the model: every row must be a model row and every value
    /// must be the model's value for the column the result names.
    /// </summary>
    private static IEnumerable<string> Compare(IReadOnlyList<string> columns, List<object?[]> rows, Dictionary<int, ModelRow> model)
    {
        int idOrdinal = -1;
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], "id", StringComparison.OrdinalIgnoreCase))
            {
                idOrdinal = i;
            }
        }

        if (idOrdinal < 0)
        {
            yield return $"the result has no id column: [{string.Join(", ", columns)}]";
            yield break;
        }

        foreach (var row in rows)
        {
            if (row[idOrdinal] is not int id || !model.TryGetValue(id, out var expected))
            {
                yield return $"row with id {Format(row[idOrdinal])} is not in the model";
                continue;
            }

            for (int i = 0; i < columns.Count; i++)
            {
                object? want = expected.Get(columns[i]);
                if (!Equals(want, row[i]))
                {
                    yield return $"id {id}, column {columns[i]}: expected {Format(want)}, read {Format(row[i])}";
                }
            }
        }
    }

    /// <summary>
    /// Checks a model table completely: the scan, then (optionally) every index and the
    /// primary key sought for every seventh model row, each against the model.
    /// </summary>
    private static async Task<List<string>> CheckTableAsync(IDatabaseSession session, string table, Dictionary<int, ModelRow> model, bool seeks)
    {
        var mismatches = new List<string>();
        var (columns, rows) = await RowsAsync(session, $"SELECT * FROM {table}");
        mismatches.AddRange(Compare(columns, rows, model).Select(mismatch => $"scan: {mismatch}"));
        var ids = rows.Select(row => row[0]).OfType<int>().Order().ToList();
        if (!ids.SequenceEqual(model.Keys.Order()))
        {
            mismatches.Add($"scan: read {ids.Count} ids, the model holds {model.Count}");
        }

        if (!seeks)
        {
            return mismatches;
        }

        // Each seek fetches the version its entry references, so it decodes versions the
        // scan reached in storage order.
        foreach (var row in model.Values.Where(row => row.Id % 7 == 1))
        {
            foreach (var (column, value) in new[] { ("id", (object)row.Id), ("a", row.A), ("c", row.C), ("d", $"'{row.D}'") })
            {
                string sql = $"SELECT * FROM {table} WHERE {column} = {value}";
                IReadOnlyList<string> seekColumns;
                List<object?[]> seekRows;
                try
                {
                    (seekColumns, seekRows) = await RowsAsync(session, sql);
                }
                catch (DatabaseException exception)
                {
                    // A value decoded into the wrong column fails the residual comparison.
                    mismatches.Add($"{sql}: {exception.Message}");
                    continue;
                }

                string path = AccessPathOf(session);
                if (!path.StartsWith("seek:", StringComparison.Ordinal))
                {
                    mismatches.Add($"{sql}: ran as {path}");
                }

                if (seekRows.Count != 1)
                {
                    mismatches.Add($"{sql}: {seekRows.Count} rows");
                }

                mismatches.AddRange(Compare(seekColumns, seekRows, model).Select(mismatch => $"{sql}: {mismatch}"));
            }
        }

        return mismatches;
    }

    /// <summary>
    /// The model table's constraints on the neighbours of the dropped column, and the
    /// child's foreign key and CHECK, still refuse what they refused.
    /// </summary>
    private static async Task AssertNeighbourConstraintsAsync(IDatabaseSession session, Dictionary<int, ModelRow> model)
    {
        var first = model[1];
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            $"INSERT INTO p VALUES (8001, 1, 2, '{first.D}', 3)"))).Message.ShouldContain("UNIQUE");
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT INTO p VALUES (8002, 50, 40, 'd-08002', 3)"))).Message.ShouldContain("ck_p_ca");
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "UPDATE p SET a = c + 1 WHERE id = 3"))).Message.ShouldContain("ck_p_ca");
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT INTO ch VALUES (9, 'd-99999', 5)"))).Message.ShouldContain("fk_ch_p");
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT INTO ch VALUES (9, 'd-00003', 0)"))).Message.ShouldContain("ck_ch_qty");
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "DELETE FROM p WHERE id = 1"))).Message.ShouldContain("fk_ch_p");
        // A table-level CHECK lists no columns; the drop finds it through the columns its
        // bound predicate reads and names it, as it names a foreign key on either side.
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "ALTER TABLE p DROP COLUMN c"))).Message.ShouldBe("Column 'c' is referenced by constraint 'ck_p_ca'. Drop the constraint first.");
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "ALTER TABLE ch DROP COLUMN parent_d"))).Message.ShouldBe("Column 'parent_d' is referenced by constraint 'fk_ch_p'. Drop the constraint first.");
        (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "ALTER TABLE p DROP COLUMN d"))).Message.ShouldBe("Column 'd' is referenced by constraint 'fk_ch_p'. Drop the constraint first.");
        (await CheckTableAsync(session, "p", model, seeks: false)).ShouldBeEmpty();
    }

    /// <summary>The re-added table reads exactly the expected generation of every column, by scan and by seek.</summary>
    private static async Task AssertReAddedAsync(IDatabaseSession session, Dictionary<int, (string Label, object? Extra, string Tail)> expected, string when)
    {
        var (columns, rows) = await RowsAsync(session, "SELECT * FROM t ORDER BY id");
        columns.ShouldBe(["id", "label", "tail", "extra"], when);
        ShouldBeRows(rows, expected.OrderBy(pair => pair.Key)
            .Select(pair => new object?[] { pair.Key, pair.Value.Label, pair.Value.Tail, pair.Value.Extra }).ToList(), when);
        foreach (var (id, row) in expected)
        {
            var (_, seeked) = await RowsAsync(session, $"SELECT id, extra FROM t WHERE label = '{row.Label}'");
            AccessPathOf(session).ShouldBe("seek:ix_label", when);
            seeked.ShouldHaveSingleItem().ShouldBe(new object?[] { id, row.Extra }, when);
        }
    }

    // ── Paged-table checks (full pages behind a long DEFAULT) ──────────

    private sealed record PagedRow(long Id, long Grp, long Score, string Code, string Pad);

    private sealed record StoredVersion(byte[] Bytes, TransactionSequence Writer, TransactionSequence Deleter);

    private static string CodeOf(long id) => $"code-{id:D5}";

    /// <summary>
    /// The scan is checked against the model, then every index is sought for every key any
    /// version ever carried: each seek must return exactly the ids the scan holds for the key,
    /// through that index.
    /// </summary>
    private static async Task AssertPagedConsistentAsync(SqlDatabaseInstance database, IDatabaseSession session,
        Dictionary<long, PagedRow> expected, List<PagedRow> probe, string when)
    {
        var (_, scanned) = await RowsAsync(session, "SELECT id, grp, score, code, pad FROM t");
        AccessPathOf(session).ShouldBe("scan");
        var actual = scanned.Select(row => new PagedRow(
            Convert.ToInt64(row[0], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[1], CultureInfo.InvariantCulture),
            Convert.ToInt64(row[2], CultureInfo.InvariantCulture),
            (string)row[3]!,
            (string)row[4]!)).OrderBy(row => row.Id).ToList();
        actual.ShouldBe(expected.Values.OrderBy(row => row.Id).ToList(), $"the scan {when}");

        var indexes = database.Catalog.GetIndexes(ObjectIdOf(database, "t"))
            .ToDictionary(index => index.ColumnNames.Single().ToLowerInvariant(), index => index.Name);
        indexes.Keys.Order().ShouldBe(["code", "grp", "id", "score"]);

        var mismatches = new List<string>();
        foreach (var (column, index) in indexes)
        {
            foreach (object key in probe.Concat(expected.Values).Select(row => KeyOf(row, column)).Distinct())
            {
                var (_, seekRows) = await RowsAsync(session, $"SELECT id FROM t WHERE {column} = {Literal(key)}");
                var seeked = seekRows.Select(row => Convert.ToInt64(row[0], CultureInfo.InvariantCulture)).Order().ToList();
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

    /// <summary>UNIQUE holds for live keys on the primary key and the unique index, through INSERT and UPDATE.</summary>
    private static async Task AssertUniqueEnforcedAsync(IDatabaseSession session, Dictionary<long, PagedRow> model)
    {
        var live = model.Values.Where(row => row.Id < 360).OrderBy(row => row.Id).ToList();
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
    }

    private static object KeyOf(PagedRow row, string column) => column switch
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

    /// <summary>Every version stored in a table's record chain, by location.</summary>
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

    /// <summary>Every version is where it was, byte for byte.</summary>
    private static void AssertUnchanged(
        Dictionary<(PageId PageId, int SlotIndex), StoredVersion> before,
        Dictionary<(PageId PageId, int SlotIndex), StoredVersion> after)
    {
        after.Keys.Order().ShouldBe(before.Keys.Order());
        foreach (var (location, version) in after)
        {
            version.Bytes.ShouldBe(before[location].Bytes, $"the version at {location}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the readers made no progress for a minute");
            await Task.Delay(1);
        }
    }

    private static void ShouldBeRows(List<object?[]> actual, IReadOnlyList<object?[]> expected, string? because = null)
    {
        actual.Count.ShouldBe(expected.Count, because);
        for (int index = 0; index < expected.Count; index++)
        {
            actual[index].ShouldBe(expected[index], $"row {index}{(because is null ? string.Empty : $", {because}")}");
        }
    }

    private static ulong ObjectIdOf(SqlDatabaseInstance instance, string table)
    {
        instance.Catalog.TryGetTable("dbo", table, out var definition).ShouldBeTrue();
        return definition.ObjectId;
    }

    private static string AccessPathOf(IDatabaseSession session)
        => ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull().AccessPath;

    private static string Format(object? value) => value switch
    {
        null => "NULL",
        string text => $"'{text}'",
        _ => $"{Convert.ToString(value, CultureInfo.InvariantCulture)} ({value.GetType().Name})",
    };

    private static async Task ExecuteAsync(IDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(QueryResultStatus.Success, sql.Length > 120 ? sql[..120] : sql);
    }

    private static async Task<(IReadOnlyList<string> Columns, List<object?[]> Rows)> RowsAsync(IDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<QueryResultSet>()!;
        var columns = result.Columns.Select(column => column.Name).ToArray();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = row.GetValue(index);
            }

            rows.Add(values);
        }

        return (columns, rows);
    }

    private static async Task ExecuteAsync(ProtocolTestClient client, string sql)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    private static async Task<List<object?[]>> QueryWireAsync(ProtocolTestClient client, string sql)
    {
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create(sql).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        return await ReadWireRowsAsync(client);
    }

    private static async Task<List<object?[]>> ReadWireRowsAsync(ProtocolTestClient client)
    {
        var rows = new List<object?[]>();
        while (true)
        {
            var frame = await client.ReadAsync();
            frame.ShouldNotBeNull();
            if (frame.Value.Type == ProtocolMessageType.ResultComplete)
            {
                return rows;
            }

            frame.Value.Type.ShouldBe(ProtocolMessageType.ResultRow);
            var values = new List<object?>();
            var reader = new DatabaseKeyReader(frame.Value.Payload.Span);
            while (!reader.IsAtEnd)
            {
                values.Add(DatabaseValueCodec.Read(ref reader));
            }

            rows.Add([.. values]);
        }
    }
}
