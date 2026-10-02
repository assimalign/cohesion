using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Secondary-index seeks over heavily duplicated keys through the SQL engine
/// (#1159): equality and range seeks must return exactly what a scan returns once a
/// key's duplicates span leaf splits — for indexes built by <c>CREATE INDEX</c> over
/// existing rows and for indexes maintained by the write path — and the UNIQUE and
/// FOREIGN KEY checks that ride the same seek must hold with duplicated keys and
/// duplicated prefixes.
/// </summary>
public sealed class SqlIndexDuplicateKeyTests
{
    private static async Task<(SqlDatabaseEngine Engine, IDatabaseSession Session)> CreateAsync(string name)
    {
        var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = name });
        var database = await engine.CreateDatabaseAsync(name + "-db");
        var session = await database.CreateSessionAsync();
        return (engine, session);
    }

    private static async Task<List<object?[]>> Rows(IDatabaseSession session, string sql)
    {
        var result = (await session.ExecuteAsync(sql)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result!.GetRowsAsync())
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }

        return rows;
    }

    private static async Task<long> CountAsync(IDatabaseSession session, string sql)
        => Convert.ToInt64((await Rows(session, sql)).Single()[0]);

    private static async Task<List<int>> IdsAsync(IDatabaseSession session, string sql)
        => (await Rows(session, sql)).Select(row => Convert.ToInt32(row[0])).Order().ToList();

    private static string AccessPathOf(IDatabaseSession session)
        => ((SqlDatabaseSession)session).LastStatementMetrics.ShouldNotBeNull().AccessPath;

    private static async Task InsertRowsAsync(IDatabaseSession session, string table, IEnumerable<string> tuples)
    {
        foreach (var batch in tuples.Chunk(250))
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES {string.Join(", ", batch)}");
        }
    }

    // ── The reported workload ───────────────────────────────────────────

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Indexes: every qty = id % 997 key seeks its full duplicate set (#1159)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Seek_ModuloWorkload_ShouldMatchScanForEveryKey(bool indexFirst)
    {
        // Arrange: the reported repro — one session, 12,000 single-row inserts with
        // qty = id % 997 — with the index maintained by the write path, or built by
        // CREATE INDEX over the rows afterwards.
        var (engine, session) = await CreateAsync(indexFirst ? "dup-modulo-write" : "dup-modulo-build");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;

        const int rows = 12_000;
        const int modulus = 997;
        await session.ExecuteAsync("CREATE TABLE t (id INT NOT NULL, qty INT NOT NULL)");
        if (indexFirst)
        {
            await session.ExecuteAsync("CREATE INDEX ix_qty ON t(qty)");
        }
        for (int id = 0; id < rows; id++)
        {
            await session.ExecuteAsync($"INSERT INTO t VALUES ({id}, {id % modulus})");
        }
        if (!indexFirst)
        {
            await session.ExecuteAsync("CREATE INDEX ix_qty ON t(qty)");
        }

        // Act: the scan's per-key counts are the ground truth for every seek.
        var scanned = (await Rows(session, "SELECT qty, COUNT(*) FROM t GROUP BY qty"))
            .ToDictionary(row => Convert.ToInt32(row[0]), row => Convert.ToInt64(row[1]));
        AccessPathOf(session).ShouldBe("scan");

        var mismatches = new List<string>();
        for (int qty = 0; qty < modulus; qty++)
        {
            long seeked = await CountAsync(session, $"SELECT COUNT(*) FROM t WHERE qty = {qty}");
            if (seeked != scanned[qty])
            {
                mismatches.Add($"qty = {qty}: {seeked} of {scanned[qty]}");
            }
        }
        AccessPathOf(session).ShouldBe("seek:ix_qty");

        // Assert: 36 keys hold 13 rows, the rest 12, and every seek agrees with the scan.
        scanned.Count.ShouldBe(modulus);
        scanned.Values.Count(count => count == 13).ShouldBe(rows % modulus);
        mismatches.ShouldBeEmpty();
        (await CountAsync(session, "SELECT COUNT(*) FROM t WHERE qty = 45")).ShouldBe(12);
        (await CountAsync(session, "SELECT COUNT(*) FROM t WHERE qty = 30")).ShouldBe(13);
        (await CountAsync(session, "SELECT COUNT(*) FROM t WHERE qty BETWEEN 30 AND 45")).ShouldBe(6 * 13 + 10 * 12);
    }

    // ── Randomized model check ──────────────────────────────────────────

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Indexes: randomized DML — equality and range seeks equal the scan and the model")]
    [InlineData(1159)]
    [InlineData(20261001)]
    public async Task RandomizedDml_SeeksShouldMatchScanAndModel(int seed)
    {
        // Arrange: a low-cardinality indexed column with one hot value. Updates that
        // leave qty unchanged still retire one index entry and add another, so dead
        // versions pile up inside each key's run as well.
        var (engine, session) = await CreateAsync($"dup-random-{seed}");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;

        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, qty INT NOT NULL, note VARCHAR(40))");
        await session.ExecuteAsync("CREATE INDEX ix_qty ON t(qty)");

        var random = new Random(seed);
        const int domain = 15;
        int NextQty() => random.NextDouble() < 0.35 ? 3 : random.Next(domain);

        var model = new Dictionary<int, int>(); // id → qty
        int nextId = 0;

        var initial = new List<string>();
        for (int i = 0; i < 1_500; i++)
        {
            int qty = NextQty();
            model[nextId] = qty;
            initial.Add($"({nextId++}, {qty}, 'n')");
        }
        await InsertRowsAsync(session, "t", initial);

        for (int round = 0; round < 24; round++)
        {
            bool explicitTransaction = random.Next(3) == 0;
            bool rollback = explicitTransaction && random.Next(2) == 0;
            var working = new Dictionary<int, int>(model);

            if (explicitTransaction)
            {
                await session.ExecuteAsync("BEGIN");
            }

            for (int op = 0; op < 40; op++)
            {
                var ids = working.Keys.ToList();
                int choice = random.Next(10);
                if (choice < 3 || ids.Count == 0)
                {
                    var tuples = new List<string>();
                    for (int i = random.Next(1, 30); i > 0; i--)
                    {
                        int qty = NextQty();
                        working[nextId] = qty;
                        tuples.Add($"({nextId++}, {qty}, 'i')");
                    }
                    await session.ExecuteAsync($"INSERT INTO t VALUES {string.Join(", ", tuples)}");
                }
                else if (choice < 6)
                {
                    int id = ids[random.Next(ids.Count)];
                    await session.ExecuteAsync($"UPDATE t SET note = 'u{round}-{op}' WHERE id = {id}");
                }
                else if (choice < 8)
                {
                    int id = ids[random.Next(ids.Count)];
                    int qty = NextQty();
                    await session.ExecuteAsync($"UPDATE t SET qty = {qty} WHERE id = {id}");
                    working[id] = qty;
                }
                else
                {
                    int id = ids[random.Next(ids.Count)];
                    await session.ExecuteAsync($"DELETE FROM t WHERE id = {id}");
                    working.Remove(id);
                }
            }

            if (explicitTransaction)
            {
                await session.ExecuteAsync(rollback ? "ROLLBACK" : "COMMIT");
            }

            if (!rollback)
            {
                model = working;
            }

            if (round % 4 == 3)
            {
                await VerifyAsync(session, model, domain, random);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Indexes: CREATE INDEX over UPDATE and DELETE history seeks exactly what the scan returns")]
    [InlineData(1159)]
    [InlineData(20261001)]
    public async Task CreateIndex_OverDeadVersions_SeeksShouldMatchScanAndModel(int seed)
    {
        // Arrange: a low-cardinality column with one hot value and a unique column,
        // then seeded UPDATEs (of the key and of other columns), DELETEs, and
        // rolled-back transactions — all before any index exists. The build path
        // indexes every stored version with its stamps, so each key's run mixes
        // dead versions with live ones. The version purge is held off so the dead
        // versions are still stored when the indexes are built.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions
        {
            EngineName = $"dup-build-history-{seed}",
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync($"dup-build-history-{seed}-db");
        await using var session = await database.CreateSessionAsync();

        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, qty INT NOT NULL, code INT NOT NULL, note VARCHAR(40))");

        var random = new Random(seed);
        const int rows = 2_000;
        const int domain = 7;
        const int hotRow = 7;
        int NextQty() => random.NextDouble() < 0.35 ? 3 : random.Next(domain);

        var model = new Dictionary<int, int>(); // id → qty; code is always id + 100,000
        var initial = new List<string>();
        for (int id = 0; id < rows; id++)
        {
            int qty = NextQty();
            model[id] = qty;
            initial.Add($"({id}, {qty}, {id + 100_000}, 'n')");
        }
        await InsertRowsAsync(session, "t", initial);

        // One row updated 300 times: 300 dead versions of its code key, more than
        // one leaf holds.
        for (int i = 0; i < 300; i++)
        {
            await session.ExecuteAsync($"UPDATE t SET note = 'h{i}' WHERE id = {hotRow}");
        }

        for (int op = 0; op < 1_000; op++)
        {
            var ids = model.Keys.ToList();
            int id = ids[random.Next(ids.Count)];
            int choice = random.Next(10);

            bool delete = choice is 7 or 8 && id != hotRow; // the hot row stays live

            if (delete)
            {
                await session.ExecuteAsync($"DELETE FROM t WHERE id = {id}");
                model.Remove(id);
            }
            else if (choice is >= 4 and < 7)
            {
                int qty = NextQty();
                await session.ExecuteAsync($"UPDATE t SET qty = {qty} WHERE id = {id}");
                model[id] = qty;
            }
            else if (choice < 9)
            {
                await session.ExecuteAsync($"UPDATE t SET note = 'u{op}' WHERE id = {id}");
            }
            else
            {
                int other = ids[random.Next(ids.Count)];
                await session.ExecuteAsync("BEGIN");
                await session.ExecuteAsync($"UPDATE t SET qty = {NextQty()} WHERE id = {id}");
                await session.ExecuteAsync($"DELETE FROM t WHERE id = {other}");
                await session.ExecuteAsync("ROLLBACK");
            }
        }

        // Act: build both indexes over the history.
        await session.ExecuteAsync("CREATE INDEX ix_qty ON t(qty)");
        await session.ExecuteAsync("CREATE UNIQUE INDEX ux_code ON t(code)");

        // Assert: every equality seek and every range shape equals the scan and the model.
        await VerifyAsync(session, model, domain, random);

        // The unique index finds the hot row's one live version behind its dead
        // ones, rejects a second live one, and frees a deleted row's code.
        (await IdsAsync(session, $"SELECT id FROM t WHERE code = {hotRow + 100_000}")).ShouldBe(new[] { hotRow });
        AccessPathOf(session).ShouldBe("seek:ux_code");
        var duplicate = await Should.ThrowAsync<SqlConstraintViolationException>(
            async () => await session.ExecuteAsync($"INSERT INTO t VALUES (90000, 1, {hotRow + 100_000}, 'dup')"));
        duplicate.ConstraintKind.ShouldBe("UNIQUE");

        int deleted = Enumerable.Range(0, rows).First(id => !model.ContainsKey(id));
        await session.ExecuteAsync($"INSERT INTO t VALUES (90001, 1, {deleted + 100_000}, 'reused')");
        model[90_001] = 1;

        // The write path keeps the built runs correct.
        for (int op = 0; op < 150; op++)
        {
            var ids = model.Keys.ToList();
            int id = ids[random.Next(ids.Count)];
            if (random.Next(3) == 0 && id != hotRow)
            {
                await session.ExecuteAsync($"DELETE FROM t WHERE id = {id}");
                model.Remove(id);
            }
            else
            {
                int qty = NextQty();
                await session.ExecuteAsync($"UPDATE t SET qty = {qty} WHERE id = {id}");
                model[id] = qty;
            }
        }

        await VerifyAsync(session, model, domain, random);
    }

    private static async Task VerifyAsync(IDatabaseSession session, Dictionary<int, int> model, int domain, Random random)
    {
        // The scan: every live row, exactly once.
        var scanned = (await Rows(session, "SELECT id, qty FROM t"))
            .Select(row => (Id: Convert.ToInt32(row[0]), Qty: Convert.ToInt32(row[1])))
            .OrderBy(row => row.Id)
            .ToList();
        AccessPathOf(session).ShouldBe("scan");
        scanned.ShouldBe(model.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value)).ToList());

        // Every equality seek.
        for (int qty = -1; qty <= domain; qty++)
        {
            (await IdsAsync(session, $"SELECT id FROM t WHERE qty = {qty}"))
                .ShouldBe(model.Where(entry => entry.Value == qty).Select(entry => entry.Key).Order().ToList(), $"qty = {qty}");
        }
        AccessPathOf(session).ShouldBe("seek:ix_qty");

        // Range seeks of every bound shape.
        for (int i = 0; i < 6; i++)
        {
            int low = random.Next(-1, domain + 1);
            int high = random.Next(low, domain + 1);
            var shapes = new (string Predicate, Func<int, bool> Holds)[]
            {
                ($"qty BETWEEN {low} AND {high}", qty => qty >= low && qty <= high),
                ($"qty > {low} AND qty < {high}", qty => qty > low && qty < high),
                ($"qty >= {low} AND qty < {high}", qty => qty >= low && qty < high),
                ($"qty > {low}", qty => qty > low),
                ($"qty <= {high}", qty => qty <= high),
            };

            foreach (var (predicate, holds) in shapes)
            {
                (await IdsAsync(session, $"SELECT id FROM t WHERE {predicate}"))
                    .ShouldBe(model.Where(entry => holds(entry.Value)).Select(entry => entry.Key).Order().ToList(), predicate);
                AccessPathOf(session).ShouldBe("seek:ix_qty");
            }
        }
    }

    // ── UNIQUE and FOREIGN KEY lookups through the same seek ────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: UNIQUE and FK lookups find the live version behind a split run of dead versions")]
    public async Task UniqueAndForeignKey_HeavilyUpdatedParentKey_ShouldEnforceThroughSeek()
    {
        // Arrange: every UPDATE retires the row's index entry and adds a successor
        // under the same key, so a frequently updated row grows a run of dead
        // versions that splits between leaves, with its live version somewhere in it.
        // The neighbouring keys are updated too, so splits land inside the run.
        var (engine, session) = await CreateAsync("dup-unique-fk");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;

        await session.ExecuteAsync("CREATE TABLE p (id INT NOT NULL, k INT NOT NULL, note VARCHAR(100))");
        await session.ExecuteAsync("CREATE UNIQUE INDEX ux_p_k ON p(k)");
        await session.ExecuteAsync("CREATE TABLE c (id INT NOT NULL, pk INT, CONSTRAINT fk_c_p FOREIGN KEY(pk) REFERENCES p(k))");
        await session.ExecuteAsync("CREATE INDEX ix_c_pk ON c(pk)");
        await InsertRowsAsync(session, "p", Enumerable.Range(0, 300).Select(k => $"({k}, {k}, 'seed')"));

        // Act / Assert: after every round of updates, the hot key's live version must
        // still be what the FK check, the UNIQUE check, and a plain seek find.
        for (int i = 0; i < 400; i++)
        {
            await session.ExecuteAsync($"UPDATE p SET note = 'n{i}' WHERE k = 150");
            await session.ExecuteAsync($"UPDATE p SET note = 'n{i}' WHERE k = 151");
            if (i % 3 == 0)
            {
                await session.ExecuteAsync($"UPDATE p SET note = 'n{i}' WHERE k = 152");
            }

            await session.ExecuteAsync($"INSERT INTO c VALUES ({i}, 150)");
            AccessPathOf(session).ShouldBe("constraint-seek:ux_p_k");

            var duplicate = await Should.ThrowAsync<SqlConstraintViolationException>(
                async () => await session.ExecuteAsync($"INSERT INTO p VALUES ({1_000 + i}, 150, 'dup')"),
                $"iteration {i}");
            duplicate.ConstraintKind.ShouldBe("UNIQUE");

            if (i % 20 == 19)
            {
                (await CountAsync(session, "SELECT COUNT(*) FROM p WHERE k = 150")).ShouldBe(1, $"iteration {i}");
                (await CountAsync(session, "SELECT COUNT(*) FROM p WHERE k BETWEEN 149 AND 152")).ShouldBe(4, $"iteration {i}");
            }
        }

        // The children's 400 equal keys span leaf splits; a RESTRICT parent delete
        // must see them, and once they are gone the key is free again.
        (await CountAsync(session, "SELECT COUNT(*) FROM c WHERE pk = 150")).ShouldBe(400);
        var restricted = await Should.ThrowAsync<SqlConstraintViolationException>(
            async () => await session.ExecuteAsync("DELETE FROM p WHERE k = 150"));
        restricted.ConstraintKind.ShouldBe("FOREIGN KEY");

        await session.ExecuteAsync("DELETE FROM c WHERE pk = 150");
        (await CountAsync(session, "SELECT COUNT(*) FROM c")).ShouldBe(0);
        await session.ExecuteAsync("DELETE FROM p WHERE k = 150");
        await session.ExecuteAsync("INSERT INTO p VALUES (9999, 150, 'reborn')");
        (await IdsAsync(session, "SELECT id FROM p WHERE k = 150")).ShouldBe(new[] { 9999 });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: ON DELETE CASCADE removes every child of a key whose duplicates span splits")]
    public async Task CascadeDelete_DuplicatedChildKey_ShouldRemoveEveryChild()
    {
        // Arrange: 600 children per parent through a single-column child index.
        var (engine, session) = await CreateAsync("dup-cascade");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;

        await session.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX ix_c_pid ON c(pid)");
        await session.ExecuteAsync("INSERT INTO p VALUES (1), (2), (3), (4), (5)");
        await InsertRowsAsync(session, "c", Enumerable.Range(0, 3_000).Select(id => $"({id}, {1 + id % 5})"));

        // Act
        await session.ExecuteAsync("DELETE FROM p WHERE id = 3");
        AccessPathOf(session).ShouldBe("constraint-seek:ix_c_pid");

        // Assert: the scan agrees — no orphan survived the cascade.
        var survivors = (await Rows(session, "SELECT id, pid FROM c")).Select(row => Convert.ToInt32(row[1])).ToList();
        survivors.Count.ShouldBe(2_400);
        survivors.ShouldNotContain(3);
        (await CountAsync(session, "SELECT COUNT(*) FROM c WHERE pid = 3")).ShouldBe(0);
        (await CountAsync(session, "SELECT COUNT(*) FROM c WHERE pid = 2")).ShouldBe(600);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Indexes: composite keys with a duplicated prefix seek, cascade, and stay unique")]
    public async Task CompositeKeys_DuplicatedPrefix_ShouldSeekCascadeAndEnforceUniqueness()
    {
        // Arrange: a composite UNIQUE parent key whose leading column is shared by
        // every row, and a child whose FK lookup seeks a single-column index on that
        // shared prefix (2,000 equal keys) before filtering on the second column.
        var (engine, session) = await CreateAsync("dup-composite");
        await using var engineLifetime = engine;
        await using var sessionLifetime = session;

        await session.ExecuteAsync("CREATE TABLE pp (a INT NOT NULL, b INT NOT NULL, note VARCHAR(40))");
        await session.ExecuteAsync("CREATE UNIQUE INDEX ux_pp_ab ON pp(a, b)");
        await session.ExecuteAsync("CREATE TABLE cc (id INT NOT NULL, a INT, b INT, CONSTRAINT fk_cc FOREIGN KEY(a, b) REFERENCES pp(a, b) ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX ix_cc_a ON cc(a)");
        await InsertRowsAsync(session, "pp", Enumerable.Range(0, 10).Select(b => $"(1, {b}, 'p')"));
        await InsertRowsAsync(session, "cc", Enumerable.Range(0, 2_000).Select(id => $"({id}, 1, {id % 10})"));

        // A heavily updated parent row grows dead versions of its full key.
        for (int i = 0; i < 300; i++)
        {
            await session.ExecuteAsync($"UPDATE pp SET note = 'n{i}' WHERE a = 1 AND b = 4");
            await session.ExecuteAsync($"UPDATE pp SET note = 'n{i}' WHERE a = 1 AND b = 5");
        }

        // Act / Assert: prefix and full-key seeks, uniqueness, FK insert, cascade.
        (await CountAsync(session, "SELECT COUNT(*) FROM cc WHERE a = 1")).ShouldBe(2_000);
        AccessPathOf(session).ShouldBe("seek:ix_cc_a");
        (await CountAsync(session, "SELECT COUNT(*) FROM pp WHERE a = 1")).ShouldBe(10);
        (await CountAsync(session, "SELECT COUNT(*) FROM pp WHERE a = 1 AND b = 4")).ShouldBe(1);
        AccessPathOf(session).ShouldBe("seek:ux_pp_ab");

        var duplicate = await Should.ThrowAsync<SqlConstraintViolationException>(
            async () => await session.ExecuteAsync("INSERT INTO pp VALUES (1, 4, 'dup')"));
        duplicate.ConstraintKind.ShouldBe("UNIQUE");

        await session.ExecuteAsync("INSERT INTO cc VALUES (5000, 1, 4)");
        AccessPathOf(session).ShouldBe("constraint-seek:ux_pp_ab");

        await session.ExecuteAsync("DELETE FROM pp WHERE a = 1 AND b = 4");
        var remaining = (await Rows(session, "SELECT a, b FROM cc")).Select(row => Convert.ToInt32(row[1])).ToList();
        remaining.Count.ShouldBe(1_800);
        remaining.ShouldNotContain(4);
        (await CountAsync(session, "SELECT COUNT(*) FROM cc WHERE a = 1")).ShouldBe(1_800);
    }
}
