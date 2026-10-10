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
/// indexes, primary keys and scans alike; and rows keep the kind and offset they
/// were written with. Databases written with the format-3 key encoding are
/// refused at open, not rebuilt (<see cref="SqlDataStorageFormatTests"/>).
/// </summary>
public sealed class SqlTemporalKeyIdentityTests
{
    private static readonly DateTime Noon = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTimeOffset Instant = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every predicate shape the planner turns into an index seek.</summary>
    private static readonly string[] Predicates =
    [
        "{0} = @p", "@p = {0}", "{0} >= @p", "{0} > @p", "{0} <= @p", "{0} < @p", "{0} BETWEEN @p AND @q",
    ];

    /// <summary>Equal ticks in all three kinds, neighbors on both sides, and NULL.</summary>
    private static readonly object?[][] TimestampRows =
    [
        [1, Noon],
        [2, Utc(Noon)],
        [3, Local(Noon)],
        [4, Utc(Noon.AddHours(-1))],
        [5, Noon.AddHours(1)],
        [6, Local(Noon.AddHours(1))],
        [7, null],
    ];

    /// <summary>
    /// One instant at three offsets, plus neighbors whose local wall-clock time
    /// sorts opposite to their instant (11:00Z at +01:00 reads 12:00; 13:00Z at
    /// −08:00 reads 05:00), and NULL.
    /// </summary>
    private static readonly object?[][] InstantRows =
    [
        [1, Instant],
        [2, Instant.ToOffset(TimeSpan.FromHours(3))],
        [3, Instant.ToOffset(TimeSpan.FromHours(-5.5))],
        [4, Instant.AddHours(-1).ToOffset(TimeSpan.FromHours(1))],
        [5, Instant.AddHours(1).ToOffset(TimeSpan.FromHours(-8))],
        [6, Instant.AddHours(1)],
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
        object?[][] keyed = [[1, Noon], [4, Utc(Noon.AddHours(-1))], [6, Local(Noon.AddHours(1))], [8, Utc(Noon.AddHours(2))]];
        await CreateParityTablesAsync(session, "TIMESTAMP", TimestampRows, keyed);

        // Act + Assert: every probe kind, every seekable predicate shape.
        foreach (var probe in new[] { Noon, Utc(Noon), Local(Noon) })
        {
            await AssertParityAsync(session, new Dictionary<string, object?> { ["p"] = probe, ["q"] = Local(Noon.AddHours(1)) });
        }

        // The scans are the reference; pin the reference to hand-computed answers.
        var probeUtc = new Dictionary<string, object?> { ["p"] = Utc(Noon), ["q"] = Noon.AddHours(1) };
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
            [1, At(Instant, 3)], [4, At(Instant.AddHours(-1), 1)], [5, At(Instant.AddHours(1), -8)], [8, At(Instant.AddHours(2), 14)],
        ];
        await CreateParityTablesAsync(session, "TIMESTAMPTZ", InstantRows, keyed);

        // Act + Assert.
        foreach (double offset in new[] { 0, 3, -5.5, 14, -12 })
        {
            await AssertParityAsync(session, new Dictionary<string, object?> { ["p"] = At(Instant, offset), ["q"] = At(Instant.AddHours(1), 9) });
        }

        var probe = new Dictionary<string, object?> { ["p"] = At(Instant, 14), ["q"] = At(Instant.AddHours(1), -8) };
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
            [1, 1, Instant, Noon], [2, 1, At(Instant, 3), Utc(Noon)], [3, 1, At(Instant, -5.5), Local(Noon)],
            [4, 2, At(Instant, 3), Noon], [5, 1, At(Instant.AddHours(1), -8), Local(Noon.AddHours(1))],
            [6, 1, At(Instant.AddHours(-1), 1), Utc(Noon.AddHours(-1))],
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
            foreach (var probe in new[] { Noon, Utc(Noon), Local(Noon) })
            {
                var parameters = new Dictionary<string, object?> { ["p"] = At(Instant, offset), ["q"] = At(Instant.AddHours(1), 2), ["t"] = probe };
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

        var exact = new Dictionary<string, object?> { ["p"] = At(Instant, 7), ["t"] = Local(Noon) };
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
        await ExecuteAsync(session, "INSERT INTO unique_ts VALUES (1, @p)", P(Noon));
        await ExecuteAsync(session, "INSERT INTO keyed_ts VALUES (@p, 1)", P(Utc(Noon)));

        // Act + Assert: inserts and updates of equal ticks in another kind fail.
        foreach (var equal in new[] { Noon, Utc(Noon), Local(Noon) })
        {
            await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO unique_ts VALUES (2, @p)", P(equal)));
            await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO keyed_ts VALUES (@p, 2)", P(equal)));
        }

        await ExecuteAsync(session, "INSERT INTO unique_ts VALUES (2, @p)", P(Utc(Noon.AddTicks(1))));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "UPDATE unique_ts SET ts = @p WHERE id = 2", P(Local(Noon))));

        // Re-writing a row's own value in another kind is not a duplicate, and
        // the row keeps the kind it was last written with.
        await ExecuteAsync(session, "UPDATE unique_ts SET ts = @p WHERE id = 1", P(Local(Noon)));
        (await ValuesAsync(session, "SELECT ts FROM unique_ts WHERE id = 1")).Single().ShouldBeOfType<DateTime>().Kind.ShouldBe(DateTimeKind.Local);
        (await IdsAsync(session, "SELECT id FROM unique_ts ORDER BY id")).ShouldBe([1, 2]);
        (await IdsAsync(session, "SELECT id FROM keyed_ts ORDER BY id")).ShouldBe([1]);

        // Backfills reject rows that are duplicates only under SQL equality.
        await ExecuteAsync(session, "CREATE TABLE loose_ts (id INT, ts TIMESTAMP)");
        await ExecuteAsync(session, "INSERT INTO loose_ts VALUES (1, @a), (2, @b)", new Dictionary<string, object?> { ["a"] = Noon, ["b"] = Utc(Noon) });
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "CREATE UNIQUE INDEX ux_ts ON loose_ts (ts)"));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "ALTER TABLE loose_ts ADD CONSTRAINT uq_ts UNIQUE (ts)"));
        await ExecuteAsync(session, "CREATE INDEX ix_ts ON loose_ts (ts)");
        (await IdsAsync(session, "SELECT id FROM loose_ts WHERE ts = @p ORDER BY id", P(Local(Noon)))).ShouldBe([1, 2]);
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
        await ExecuteAsync(session, "INSERT INTO unique_at VALUES (1, @p)", P(Instant));
        await ExecuteAsync(session, "INSERT INTO keyed_at VALUES (@p, 1)", P(At(Instant, 3)));
        await ExecuteAsync(session, "INSERT INTO tenant_at VALUES (1, @p), (2, @q)", new Dictionary<string, object?> { ["p"] = Instant, ["q"] = At(Instant, 3) });

        // Act + Assert.
        foreach (double offset in new[] { 0, 3, -5.5, 14 })
        {
            await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO unique_at VALUES (2, @p)", P(At(Instant, offset))));
            await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO keyed_at VALUES (@p, 2)", P(At(Instant, offset))));
            await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO tenant_at VALUES (1, @p)", P(At(Instant, offset))));
        }

        await ExecuteAsync(session, "INSERT INTO unique_at VALUES (2, @p)", P(At(Instant.AddTicks(1), 3)));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "UPDATE unique_at SET at = @p WHERE id = 2", P(At(Instant, -8))));
        await ExecuteAsync(session, "UPDATE unique_at SET at = @p WHERE id = 1", P(At(Instant, -8)));
        (await ValuesAsync(session, "SELECT at FROM unique_at WHERE id = 1")).Single().ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.FromHours(-8));
        (await IdsAsync(session, "SELECT id FROM unique_at ORDER BY id")).ShouldBe([1, 2]);

        await ExecuteAsync(session, "CREATE TABLE loose_at (id INT, at TIMESTAMPTZ)");
        await ExecuteAsync(session, "INSERT INTO loose_at VALUES (1, @a), (2, @b)", new Dictionary<string, object?> { ["a"] = Instant, ["b"] = At(Instant, -5.5) });
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "CREATE UNIQUE INDEX ux_at ON loose_at (at)"));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "ALTER TABLE loose_at ADD CONSTRAINT uq_at UNIQUE (at)"));
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
        await ExecuteAsync(session, "INSERT INTO days VALUES (@p)", P(Utc(Noon)));
        await ExecuteAsync(session, "INSERT INTO moments VALUES (@p)", P(At(Instant, 3)));

        // Act + Assert: the parent lookup seeks the identity, so equal values in
        // another kind or offset satisfy the reference...
        await ExecuteAsync(session, "INSERT INTO day_refs VALUES (1, @p)", P(Local(Noon)));
        Metrics(session).AccessPath.ShouldStartWith("constraint-seek:");
        await ExecuteAsync(session, "INSERT INTO moment_refs VALUES (1, @p)", P(At(Instant, -5.5)));
        Metrics(session).AccessPath.ShouldStartWith("constraint-seek:");

        // ...unequal values do not...
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO day_refs VALUES (2, @p)", P(Local(Noon.AddTicks(1)))));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "INSERT INTO moment_refs VALUES (2, @p)", P(At(Instant.AddTicks(1), 3))));

        // ...and the reverse lookup finds the children, so RESTRICT holds.
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "DELETE FROM days"));
        await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(session, "DELETE FROM moments"));
        (await IdsAsync(session, "SELECT id FROM day_refs ORDER BY id")).ShouldBe([1]);
        (await IdsAsync(session, "SELECT id FROM moment_refs ORDER BY id")).ShouldBe([1]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal keys: rows keep the written kind and offset while index keys hold the identity (#1099)")]
    public async Task Rows_ShouldKeepWrittenKindAndOffset_WhileKeysHoldIdentity()
    {
        // Arrange.
        await using var engine = SqlDatabaseEngine.Create("temporal-roundtrip", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("temporal-roundtrip");
        await using var session = await database.CreateSessionAsync();
        await ExecuteAsync(session, "CREATE TABLE t (id INT, ts TIMESTAMP, at TIMESTAMPTZ)");
        await ExecuteAsync(session, "CREATE INDEX ix_ts ON t (ts)");
        await ExecuteAsync(session, "CREATE INDEX ix_at ON t (at)");

        // Act.
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, @ts, @at)", new Dictionary<string, object?> { ["ts"] = Local(Noon), ["at"] = At(Instant, 3) });
        var row = (await RowsAsync(session, "SELECT ts, at FROM t WHERE ts = @p AND at = @q",
            new Dictionary<string, object?> { ["p"] = Utc(Noon), ["q"] = At(Instant, -5.5) })).Single();

        // Assert: the row round-trips what was written...
        var timestamp = row[0].ShouldBeOfType<DateTime>();
        timestamp.Ticks.ShouldBe(Noon.Ticks);
        timestamp.Kind.ShouldBe(DateTimeKind.Local);
        var instant = row[1].ShouldBeOfType<DateTimeOffset>();
        instant.UtcTicks.ShouldBe(Instant.UtcTicks);
        instant.Offset.ShouldBe(TimeSpan.FromHours(3));

        // ...while each key holds only the identity: Unspecified ticks, offset zero.
        (await KeysAsync(database, "t", "ix_ts")).Single().ShouldBe(Encode(writer => writer.AppendDateTime(Noon)));
        (await KeysAsync(database, "t", "ix_at")).Single().ShouldBe(Encode(writer => writer.AppendDateTimeOffset(Instant)));
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
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, @a), (2, @b)", new Dictionary<string, object?> { ["a"] = Instant, ["b"] = At(Instant, 3) });
        var uncommitted = await session.BeginTransactionAsync();
        uncommitted.ShouldNotBeNull();
        await ExecuteAsync(session, "INSERT INTO t VALUES (3, @p)", P(At(Instant, -5.5)));
        var flusher = await database.CreateSessionAsync();
        await ExecuteAsync(flusher, "INSERT INTO t VALUES (4, @p)", P(At(Instant, 9)));

        // Act.
        var reopenedEngine = SqlDatabaseEngine.Create("temporal-crash-reopen", new SqlDatabaseEngineOptions { StorageStrategy = strategy.CaptureDurableImages() });
        await using var _ = reopenedEngine;
        var reopened = await reopenedEngine.OpenDatabaseAsync("crash-db");

        // Assert: the seek sees the committed instants only, through entries
        // that mirror the surviving rows exactly.
        await using (var verify = await reopened.CreateSessionAsync())
        {
            (await IdsAsync(verify, "SELECT id FROM t WHERE at = @p ORDER BY id", P(At(Instant, 1)))).ShouldBe([1, 2, 4]);
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
        foreach (string template in Predicates)
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

    private static Task<QueryResult> ExecuteAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
        => session.ExecuteAsync(sql, parameters).AsTask();

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await ExecuteAsync(session, sql, parameters)).ShouldBeAssignableTo<QueryResultSet>();
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
