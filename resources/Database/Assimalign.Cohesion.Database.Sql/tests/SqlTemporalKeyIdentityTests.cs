using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

/// <summary>
/// TIMESTAMP and TIMESTAMPTZ key identity (#1099): index keys, seek bounds and
/// unique enforcement follow SQL equality — wall-clock ticks without the
/// <see cref="DateTimeKind"/>, instants without the offset — through secondary
/// indexes, primary keys and scans alike; and rows normalize TIMESTAMP kind while keeping the written TIMESTAMPTZ offset (#1110). Databases written with the format-3 key encoding are
/// refused at open, not rebuilt (<see cref="SqlDataStorageFormatTests"/>).
/// </summary>
public sealed class SqlTemporalKeyIdentityTests
{
    private static readonly DateTime _noon = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset _instant = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every predicate shape the planner turns into an index seek.</summary>
    private static readonly string[] _predicates =
    [
        "{0} = @p", "@p = {0}", "{0} >= @p", "{0} > @p", "{0} <= @p", "{0} < @p", "{0} BETWEEN @p AND @q",
    ];

    /// <summary>Equal ticks in all three kinds, neighbors on both sides, and NULL.</summary>
    private static readonly object?[][] _timestampRows =
    [
        [1, _noon],
        [2, Utc(_noon)],
        [3, Local(_noon)],
        [4, Utc(_noon.AddHours(-1))],
        [5, _noon.AddHours(1)],
        [6, Local(_noon.AddHours(1))],
        [7, null],
    ];

    /// <summary>
    /// One instant at three offsets, plus neighbors whose local wall-clock time
    /// sorts opposite to their instant (11:00Z at +01:00 reads 12:00; 13:00Z at
    /// −08:00 reads 05:00), and NULL.
    /// </summary>
    private static readonly object?[][] _instantRows =
    [
        [1, _instant],
        [2, _instant.ToOffset(TimeSpan.FromHours(3))],
        [3, _instant.ToOffset(TimeSpan.FromHours(-5.5))],
        [4, _instant.AddHours(-1).ToOffset(TimeSpan.FromHours(1))],
        [5, _instant.AddHours(1).ToOffset(TimeSpan.FromHours(-8))],
        [6, _instant.AddHours(1)],
        [7, null],
    ];

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime Local(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Local);

    private static DateTimeOffset At(DateTimeOffset value, double hours) => value.ToOffset(TimeSpan.FromHours(hours));

    // ── Seek / scan parity ─────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal keys: TIMESTAMP seeks agree with scans for every DateTimeKind (#1099)")]
    public async Task Timestamp_SeeksAndScans_ShouldAgreeAcrossKinds()
    {
        // Arrange: identical rows behind a scan, a maintained index, a built
        // index, and (one value per tick count) a primary key and its scan twin.
        await using var engine = SqlDatabaseEngine.Create("temporal-timestamp", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("temporal-timestamp");
        await using var session = await database.CreateSessionAsync();
        object?[][] keyed = [[1, _noon], [4, Utc(_noon.AddHours(-1))], [6, Local(_noon.AddHours(1))], [8, Utc(_noon.AddHours(2))]];
        await CreateParityTablesAsync(session, "TIMESTAMP", _timestampRows, keyed);

        // Act + Assert: every probe kind, every seekable predicate shape.
        foreach (var probe in new[] { _noon, Utc(_noon), Local(_noon) })
        {
            await AssertParityAsync(session, new Dictionary<string, object?> { ["p"] = probe, ["q"] = Local(_noon.AddHours(1)) });
        }

        // The scans are the reference; pin the reference to hand-computed answers.
        var probeUtc = new Dictionary<string, object?> { ["p"] = Utc(_noon), ["q"] = _noon.AddHours(1) };
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts = @p ORDER BY id", probeUtc)).ShouldBe([1, 2, 3]);
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts >= @p ORDER BY id", probeUtc)).ShouldBe([1, 2, 3, 5, 6]);
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts > @p ORDER BY id", probeUtc)).ShouldBe([5, 6]);
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts <= @p ORDER BY id", probeUtc)).ShouldBe([1, 2, 3, 4]);
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts < @p ORDER BY id", probeUtc)).ShouldBe([4]);
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts BETWEEN @p AND @q ORDER BY id", probeUtc)).ShouldBe([1, 2, 3, 5, 6]);
        (await IdsAsync(session, "SELECT id FROM keyed WHERE ts = @p ORDER BY id", probeUtc)).ShouldBe([1]);
        Metrics(session).AccessPath.ShouldStartWith("seek:");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal keys: TIMESTAMPTZ seeks agree with scans for one instant at any offset (#1099)")]
    public async Task TimestampWithTimeZone_SeeksAndScans_ShouldAgreeAcrossOffsets()
    {
        // Arrange.
        await using var engine = SqlDatabaseEngine.Create("temporal-instant", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("temporal-instant");
        await using var session = await database.CreateSessionAsync();
        object?[][] keyed =
        [
            [1, At(_instant, 3)], [4, At(_instant.AddHours(-1), 1)], [5, At(_instant.AddHours(1), -8)], [8, At(_instant.AddHours(2), 14)],
        ];
        await CreateParityTablesAsync(session, "TIMESTAMPTZ", _instantRows, keyed);

        // Act + Assert.
        foreach (double offset in new[] { 0, 3, -5.5, 14, -12 })
        {
            await AssertParityAsync(session, new Dictionary<string, object?> { ["p"] = At(_instant, offset), ["q"] = At(_instant.AddHours(1), 9) });
        }

        var probe = new Dictionary<string, object?> { ["p"] = At(_instant, 14), ["q"] = At(_instant.AddHours(1), -8) };
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts = @p ORDER BY id", probe)).ShouldBe([1, 2, 3]);
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts >= @p ORDER BY id", probe)).ShouldBe([1, 2, 3, 5, 6]);
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts > @p ORDER BY id", probe)).ShouldBe([5, 6]);
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts <= @p ORDER BY id", probe)).ShouldBe([1, 2, 3, 4]);
        (await IdsAsync(session, "SELECT id FROM indexed WHERE ts < @p ORDER BY id", probe)).ShouldBe([4]);
        (await IdsAsync(session, "SELECT id FROM keyed WHERE ts = @p ORDER BY id", probe)).ShouldBe([1]);
        Metrics(session).AccessPath.ShouldStartWith("seek:");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal keys: composite equality prefixes and temporal ranges agree with scans (#1099)")]
    public async Task CompositeIndex_TemporalPrefixAndRange_ShouldAgreeWithScan()
    {
        // Arrange: the temporal component both inside the equality prefix
        // (tenant, at) and as the range column after it.
        await using var engine = SqlDatabaseEngine.Create("temporal-composite", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("temporal-composite");
        await using var session = await database.CreateSessionAsync();
        foreach (string table in new[] { "scanned", "indexed" })
        {
            await ExecuteAsync(session, $"CREATE TABLE {table} (id INT, tenant INT, at TIMESTAMPTZ, ts TIMESTAMP)");
        }
        await ExecuteAsync(session, "CREATE INDEX ix_tenant_at_ts ON indexed (tenant, at, ts)");
        object?[][] rows =
        [
            [1, 1, _instant, _noon], [2, 1, At(_instant, 3), Utc(_noon)], [3, 1, At(_instant, -5.5), Local(_noon)],
            [4, 2, At(_instant, 3), _noon], [5, 1, At(_instant.AddHours(1), -8), Local(_noon.AddHours(1))],
            [6, 1, At(_instant.AddHours(-1), 1), Utc(_noon.AddHours(-1))],
        ];
        foreach (string table in new[] { "scanned", "indexed" })
        {
            foreach (var row in rows)
            {
                await ExecuteAsync(session, $"INSERT INTO {table} VALUES (@id, @tenant, @at, @ts)",
                    new Dictionary<string, object?> { ["id"] = row[0], ["tenant"] = row[1], ["at"] = row[2], ["ts"] = row[3] });
            }
        }

        string[] predicates =
        [
            "tenant = 1 AND at = @p", "tenant = 1 AND at >= @p", "tenant = 1 AND at < @p",
            "tenant = 1 AND at BETWEEN @p AND @q", "tenant = 1 AND at = @p AND ts = @t",
            "tenant = 1 AND at = @p AND ts >= @t", "tenant = 1 AND at = @p AND ts < @t",
        ];

        // Act + Assert.
        foreach (double offset in new[] { 0, 3, -5.5 })
        {
            foreach (var probe in new[] { _noon, Utc(_noon), Local(_noon) })
            {
                var parameters = new Dictionary<string, object?> { ["p"] = At(_instant, offset), ["q"] = At(_instant.AddHours(1), 2), ["t"] = probe };
                foreach (string predicate in predicates)
                {
                    int[] expected = await IdsAsync(session, $"SELECT id FROM scanned WHERE {predicate} ORDER BY id", parameters);
                    Metrics(session).AccessPath.ShouldBe("scan");
                    (await IdsAsync(session, $"SELECT id FROM indexed WHERE {predicate} ORDER BY id", parameters))
                        .ShouldBe(expected, $"{predicate} at offset {offset} with {probe.Kind}");
                    Metrics(session).AccessPath.ShouldBe("seek:ix_tenant_at_ts");
                }
            }
        }

        var exact = new Dictionary<string, object?> { ["p"] = At(_instant, 7), ["t"] = Local(_noon) };
        (await IdsAsync(session, "SELECT id FROM indexed WHERE tenant = 1 AND at = @p AND ts = @t ORDER BY id", exact)).ShouldBe([1, 2, 3]);
    }

    // ── Uniqueness ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal keys: UNIQUE and PRIMARY KEY TIMESTAMP reject equal ticks of another kind (#1099)")]
    public async Task UniqueTimestamp_ShouldRejectEqualTicksOfAnotherKind()
    {
        // Arrange.
        await using var engine = SqlDatabaseEngine.Create("temporal-unique-ts", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("temporal-unique-ts");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE unique_ts (id INT, ts TIMESTAMP UNIQUE)");
        await ExecuteAsync(session, "CREATE TABLE keyed_ts (ts TIMESTAMP PRIMARY KEY, id INT)");
        await ExecuteAsync(session, "INSERT INTO unique_ts VALUES (1, @p)", P(_noon));
        await ExecuteAsync(session, "INSERT INTO keyed_ts VALUES (@p, 1)", P(Utc(_noon)));

        // Act + Assert: inserts and updates of equal ticks in another kind fail.
        foreach (var equal in new[] { _noon, Utc(_noon), Local(_noon) })
        {
            await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "INSERT INTO unique_ts VALUES (2, @p)", P(equal)));
            await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "INSERT INTO keyed_ts VALUES (@p, 2)", P(equal)));
        }

        await ExecuteAsync(session, "INSERT INTO unique_ts VALUES (2, @p)", P(Utc(_noon.AddTicks(1))));
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "UPDATE unique_ts SET ts = @p WHERE id = 2", P(Local(_noon))));

        // Re-writing a row's own value in another kind is not a duplicate, and
        // the row keeps the kind it was last written with.
        await ExecuteAsync(session, "UPDATE unique_ts SET ts = @p WHERE id = 1", P(Local(_noon)));
        (await ValuesAsync(session, "SELECT ts FROM unique_ts WHERE id = 1")).Single().ShouldBeOfType<DateTime>().Kind.ShouldBe(DateTimeKind.Unspecified);
        (await IdsAsync(session, "SELECT id FROM unique_ts ORDER BY id")).ShouldBe([1, 2]);
        (await IdsAsync(session, "SELECT id FROM keyed_ts ORDER BY id")).ShouldBe([1]);

        // Backfills reject rows that are duplicates only under SQL equality.
        await ExecuteAsync(session, "CREATE TABLE loose_ts (id INT, ts TIMESTAMP)");
        await ExecuteAsync(session, "INSERT INTO loose_ts VALUES (1, @a), (2, @b)", new Dictionary<string, object?> { ["a"] = _noon, ["b"] = Utc(_noon) });
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "CREATE UNIQUE INDEX ux_ts ON loose_ts (ts)"));
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "ALTER TABLE loose_ts ADD CONSTRAINT uq_ts UNIQUE (ts)"));
        await ExecuteAsync(session, "CREATE INDEX ix_ts ON loose_ts (ts)");
        (await IdsAsync(session, "SELECT id FROM loose_ts WHERE ts = @p ORDER BY id", P(Local(_noon)))).ShouldBe([1, 2]);
        Metrics(session).AccessPath.ShouldBe("seek:ix_ts");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal keys: UNIQUE and PRIMARY KEY TIMESTAMPTZ reject the same instant at another offset (#1099)")]
    public async Task UniqueTimestampWithTimeZone_ShouldRejectSameInstantAtAnotherOffset()
    {
        // Arrange.
        await using var engine = SqlDatabaseEngine.Create("temporal-unique-at", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("temporal-unique-at");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE unique_at (id INT, at TIMESTAMPTZ UNIQUE)");
        await ExecuteAsync(session, "CREATE TABLE keyed_at (at TIMESTAMPTZ PRIMARY KEY, id INT)");
        await ExecuteAsync(session, "CREATE TABLE tenant_at (tenant INT, at TIMESTAMPTZ, UNIQUE (tenant, at))");
        await ExecuteAsync(session, "INSERT INTO unique_at VALUES (1, @p)", P(_instant));
        await ExecuteAsync(session, "INSERT INTO keyed_at VALUES (@p, 1)", P(At(_instant, 3)));
        await ExecuteAsync(session, "INSERT INTO tenant_at VALUES (1, @p), (2, @q)", new Dictionary<string, object?> { ["p"] = _instant, ["q"] = At(_instant, 3) });

        // Act + Assert.
        foreach (double offset in new[] { 0, 3, -5.5, 14 })
        {
            await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "INSERT INTO unique_at VALUES (2, @p)", P(At(_instant, offset))));
            await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "INSERT INTO keyed_at VALUES (@p, 2)", P(At(_instant, offset))));
            await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "INSERT INTO tenant_at VALUES (1, @p)", P(At(_instant, offset))));
        }

        await ExecuteAsync(session, "INSERT INTO unique_at VALUES (2, @p)", P(At(_instant.AddTicks(1), 3)));
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "UPDATE unique_at SET at = @p WHERE id = 2", P(At(_instant, -8))));
        await ExecuteAsync(session, "UPDATE unique_at SET at = @p WHERE id = 1", P(At(_instant, -8)));
        (await ValuesAsync(session, "SELECT at FROM unique_at WHERE id = 1")).Single().ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.FromHours(-8));
        (await IdsAsync(session, "SELECT id FROM unique_at ORDER BY id")).ShouldBe([1, 2]);

        await ExecuteAsync(session, "CREATE TABLE loose_at (id INT, at TIMESTAMPTZ)");
        await ExecuteAsync(session, "INSERT INTO loose_at VALUES (1, @a), (2, @b)", new Dictionary<string, object?> { ["a"] = _instant, ["b"] = At(_instant, -5.5) });
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "CREATE UNIQUE INDEX ux_at ON loose_at (at)"));
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "ALTER TABLE loose_at ADD CONSTRAINT uq_at UNIQUE (at)"));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal keys: foreign keys match parents across kinds and offsets through constraint seeks (#1099)")]
    public async Task ForeignKeys_TemporalKeys_ShouldMatchAcrossKindsAndOffsets()
    {
        // Arrange: parents keyed by TIMESTAMP and TIMESTAMPTZ, children that
        // reference them with equal values in another kind or offset.
        await using var engine = SqlDatabaseEngine.Create("temporal-references", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("temporal-references");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE days (ts TIMESTAMP PRIMARY KEY)");
        await ExecuteAsync(session, "CREATE TABLE moments (at TIMESTAMPTZ PRIMARY KEY)");
        await ExecuteAsync(session, "CREATE TABLE day_refs (id INT, ts TIMESTAMP REFERENCES days(ts) ON DELETE RESTRICT)");
        await ExecuteAsync(session, "CREATE TABLE moment_refs (id INT, at TIMESTAMPTZ REFERENCES moments(at) ON DELETE RESTRICT)");
        await ExecuteAsync(session, "CREATE INDEX ix_day_refs ON day_refs (ts)");
        await ExecuteAsync(session, "CREATE INDEX ix_moment_refs ON moment_refs (at)");
        await ExecuteAsync(session, "INSERT INTO days VALUES (@p)", P(Utc(_noon)));
        await ExecuteAsync(session, "INSERT INTO moments VALUES (@p)", P(At(_instant, 3)));

        // Act + Assert: the parent lookup seeks the identity, so equal values in
        // another kind or offset satisfy the reference...
        await ExecuteAsync(session, "INSERT INTO day_refs VALUES (1, @p)", P(Local(_noon)));
        Metrics(session).AccessPath.ShouldStartWith("constraint-seek:");
        await ExecuteAsync(session, "INSERT INTO moment_refs VALUES (1, @p)", P(At(_instant, -5.5)));
        Metrics(session).AccessPath.ShouldStartWith("constraint-seek:");

        // ...unequal values do not...
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "INSERT INTO day_refs VALUES (2, @p)", P(Local(_noon.AddTicks(1)))));
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "INSERT INTO moment_refs VALUES (2, @p)", P(At(_instant.AddTicks(1), 3))));

        // ...and the reverse lookup finds the children, so RESTRICT holds.
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "DELETE FROM days"));
        await SqlConstraintAssertions.ThrowAsync(() => ExecuteAsync(session, "DELETE FROM moments"));
        (await IdsAsync(session, "SELECT id FROM day_refs ORDER BY id")).ShouldBe([1]);
        (await IdsAsync(session, "SELECT id FROM moment_refs ORDER BY id")).ShouldBe([1]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal keys: rows normalize the wall clock kind and retain the instant offset while keys hold identity (#1099)")]
    public async Task Rows_WrittenKindAndOffset_ShouldNormalizeClockAndKeepInstantOffset()
    {
        // Arrange.
        await using var engine = SqlDatabaseEngine.Create("temporal-roundtrip", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("temporal-roundtrip");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE t (id INT, ts TIMESTAMP, at TIMESTAMPTZ)");
        await ExecuteAsync(session, "CREATE INDEX ix_ts ON t (ts)");
        await ExecuteAsync(session, "CREATE INDEX ix_at ON t (at)");

        // Act.
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, @ts, @at)", new Dictionary<string, object?> { ["ts"] = Local(_noon), ["at"] = At(_instant, 3) });
        var row = (await RowsAsync(session, "SELECT ts, at FROM t WHERE ts = @p AND at = @q",
            new Dictionary<string, object?> { ["p"] = Utc(_noon), ["q"] = At(_instant, -5.5) })).Single();

        // Assert: storage normalizes the wall clock kind and retains the instant offset...
        var timestamp = row[0].ShouldBeOfType<DateTime>();
        timestamp.Ticks.ShouldBe(_noon.Ticks);
        timestamp.Kind.ShouldBe(DateTimeKind.Unspecified);
        var instant = row[1].ShouldBeOfType<DateTimeOffset>();
        instant.UtcTicks.ShouldBe(_instant.UtcTicks);
        instant.Offset.ShouldBe(TimeSpan.FromHours(3));

        // ...while each key holds only the identity: Unspecified ticks, offset zero.
        (await KeysAsync(database, "t", "ix_ts")).Single().ShouldBe(Encode(writer => writer.AppendDateTime(_noon)));
        (await KeysAsync(database, "t", "ix_at")).Single().ShouldBe(Encode(writer => writer.AppendDateTimeOffset(_instant)));
    }

    // ── Crash recovery ─────────────────────────────────────────────────

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal keys: crash recovery scrubs an unproven writer's identity-encoded entries (#1099)")]
    public async Task Recovery_UnprovenTemporalWriter_ShouldScrubIdentityEntries()
    {
        // Arrange: committed rows at two offsets, an uncommitted writer whose
        // records become durable through a later commit, then a crash.
        var strategy = new CrashCaptureSqlStorageStrategy();
        var engine = SqlDatabaseEngine.Create("temporal-crash", new SqlDatabaseEngineOptions { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("crash-db");
        var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE t (id INT, at TIMESTAMPTZ)");
        await ExecuteAsync(session, "CREATE INDEX ix_at ON t (at)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, @a), (2, @b)", new Dictionary<string, object?> { ["a"] = _instant, ["b"] = At(_instant, 3) });
        var uncommitted = await session.BeginTransactionAsync();
        uncommitted.ShouldNotBeNull();
        await ExecuteAsync(session, "INSERT INTO t VALUES (3, @p)", P(At(_instant, -5.5)));
        var flusher = await database.CreateSessionAsync();
        await ExecuteAsync(flusher, "INSERT INTO t VALUES (4, @p)", P(At(_instant, 9)));

        // Act.
        var reopenedEngine = SqlDatabaseEngine.Create("temporal-crash-reopen", new SqlDatabaseEngineOptions { StorageStrategy = strategy.CaptureDurableImages() });
        await using var _ = reopenedEngine;
        var reopened = await reopenedEngine.OpenDatabaseAsync("crash-db");

        // Assert: the seek sees the committed instants only, through entries
        // that mirror the surviving rows exactly.
        await using (var verify = await reopened.CreateSessionAsync())
        {
            (await IdsAsync(verify, "SELECT id FROM t WHERE at = @p ORDER BY id", P(At(_instant, 1)))).ShouldBe([1, 2, 4]);
            Metrics(verify).AccessPath.ShouldBe("seek:ix_at");
            (await IdsAsync(verify, "SELECT id FROM t ORDER BY id")).ShouldBe([1, 2, 4]);
        }
        (await KeysAsync(reopened, "t", "ix_at")).Count.ShouldBe(3);

        // The crashed engine's disposal is best-effort (its streams are gated).
        await engine.DisposeAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task CreateParityTablesAsync(SqlDatabaseSession session, string type, object?[][] rows, object?[][] keyed)
    {
        await ExecuteAsync(session, $"CREATE TABLE scanned (id INT, ts {type})");
        await ExecuteAsync(session, $"CREATE TABLE indexed (id INT, ts {type})");
        await ExecuteAsync(session, $"CREATE TABLE built (id INT, ts {type})");
        await ExecuteAsync(session, $"CREATE TABLE keyed (ts {type} PRIMARY KEY, id INT)");
        await ExecuteAsync(session, $"CREATE TABLE keyed_scan (ts {type}, id INT)");

        // Maintenance path: the index exists before the rows arrive.
        await ExecuteAsync(session, "CREATE INDEX ix_indexed ON indexed (ts)");

        foreach (var row in rows)
        {
            var parameters = new Dictionary<string, object?> { ["id"] = row[0], ["ts"] = row[1] };
            foreach (string table in new[] { "scanned", "indexed", "built" })
            {
                await ExecuteAsync(session, $"INSERT INTO {table} VALUES (@id, @ts)", parameters);
            }
        }

        foreach (var row in keyed)
        {
            var parameters = new Dictionary<string, object?> { ["id"] = row[0], ["ts"] = row[1] };
            await ExecuteAsync(session, "INSERT INTO keyed VALUES (@ts, @id)", parameters);
            await ExecuteAsync(session, "INSERT INTO keyed_scan VALUES (@ts, @id)", parameters);
        }

        // Build path: the index is created over existing rows.
        await ExecuteAsync(session, "CREATE INDEX ix_built ON built (ts)");
    }

    /// <summary>Runs every predicate shape on the scan reference and on each indexed twin.</summary>
    private static async Task AssertParityAsync(SqlDatabaseSession session, Dictionary<string, object?> parameters)
    {
        await AssertSeekMatchesScanAsync(session, "indexed", "scanned", "ts", "seek:ix_indexed", parameters);
        await AssertSeekMatchesScanAsync(session, "built", "scanned", "ts", "seek:ix_built", parameters);
        await AssertSeekMatchesScanAsync(session, "keyed", "keyed_scan", "ts", "seek:", parameters);
    }

    private static async Task AssertSeekMatchesScanAsync(
        SqlDatabaseSession session, string indexedTable, string scanTable, string column, string accessPath, Dictionary<string, object?> parameters)
    {
        foreach (string template in _predicates)
        {
            string predicate = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, column);
            int[] expected = await IdsAsync(session, $"SELECT id FROM {scanTable} WHERE {predicate} ORDER BY id", parameters);
            Metrics(session).AccessPath.ShouldBe("scan");

            int[] actual = await IdsAsync(session, $"SELECT id FROM {indexedTable} WHERE {predicate} ORDER BY id", parameters);
            actual.ShouldBe(expected, $"{indexedTable}: {predicate} with @p = {Describe(parameters["p"])}");
            Metrics(session).AccessPath.ShouldStartWith(accessPath);
        }
    }

    private static string Describe(object? value) => value switch
    {
        DateTime timestamp => $"{timestamp:O} ({timestamp.Kind})",
        DateTimeOffset instant => instant.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        _ => $"{value}",
    };

    private static Dictionary<string, object?> P(object? value) => new() { ["p"] = value };

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await ExecuteAsync(session, sql, parameters)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result!.GetRowsAsync())
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < row.FieldCount; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }

    private static async Task<object?[]> ValuesAsync(SqlDatabaseSession session, string sql)
        => (await RowsAsync(session, sql)).Select(row => row[0]).ToArray();

    private static async Task<int[]> IdsAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => (await RowsAsync(session, sql, parameters)).Select(row => (int)row[0]!).ToArray();

    private static SqlStatementMetrics Metrics(SqlDatabaseSession session)
        => session.LastStatementMetrics.ShouldNotBeNull();

    private static byte[] Encode(Action<DatabaseKeyWriter> append)
    {
        var writer = new DatabaseKeyWriter();
        append(writer);
        return writer.ToArray();
    }

    /// <summary>Materializes the keys a fresh snapshot sees in the named index.</summary>
    private static async Task<List<byte[]>> KeysAsync(SqlDatabase database, string table, string indexName)
    {
        var instance = database;
        instance.Catalog.TryGetTable("dbo", table, out var catalogTable).ShouldBeTrue();
        instance.IndexManager.TryGetIndex(catalogTable.ObjectId, indexName, out var index).ShouldBeTrue($"index '{indexName}' should be attached");

        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync();

        try
        {
            var keys = new List<byte[]>();
            await using var cursor = index.OpenCursor(transaction.Context, IndexKeyRange.All);
            while (await cursor.MoveNextAsync())
            {
                keys.Add(cursor.CurrentKey.Encoded.ToArray());
            }
            return keys;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }
}
