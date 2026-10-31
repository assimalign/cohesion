using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL bounded execution allocation")]
public sealed class SqlCopyToTests
{
    private readonly ITestOutputHelper _output;
    public SqlCopyToTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - COPY: generated all-type exports equal SELECT CAST and PostgreSQL formatting")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToAsync_AllTypes_ShouldMatchOracle(bool csv)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-types", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (i TINYINT, s SMALLINT, n INT, b BIGINT, d NUMERIC(12,3), r REAL, f DOUBLE PRECISION, ok BOOLEAN, txt TEXT, fixed CHAR(8), bytes BYTEA, dt DATE, tm TIME, ts TIMESTAMP, tz TIMESTAMP WITH TIME ZONE, span INTERVAL, u UUID, j JSON, jb JSONB)");
        string[] names = ["i", "s", "n", "b", "d", "r", "f", "ok", "txt", "fixed", "bytes", "dt", "tm", "ts", "tz", "span", "u", "j", "jb"];
        string?[] strings = [null, "", "\\N", "a\tb", "a\nb\rc", "back\\slash", "a,b|c", "\"quoted\"", "é中😀", "\b\f\v", "\\."];
        for (int index = 0; index < strings.Length; index++)
        {
            object?[] values = [(sbyte)-12, (short)1234, index, 9223372036854770000L, 1234.500m, -0.25f, 1.23456789012345e-18,
                index % 2 == 0, strings[index], "abc", new byte[] { 0, 1, 255 }, new DateOnly(2026, 10, 31),
                new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(1234567)), new DateTime(2026, 10, 31, 12, 34, 56).AddTicks(1234567),
                new DateTimeOffset(2026, 10, 31, 12, 34, 56, TimeSpan.FromHours(-4)), TimeSpan.FromDays(-2) - TimeSpan.FromMinutes(3),
                Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), "{\"s\":\"é\"}", Encoding.UTF8.GetBytes("{\"a\":[1,null]}")];
            var parameters = names.Select((name, position) => (name, values[position])).ToDictionary(pair => pair.name, pair => pair.Item2);
            if (index == 0) { foreach (string name in names) { parameters[name] = null; } }
            await session.ExecuteAsync("INSERT INTO t VALUES (" + string.Join(",", names.Select(name => "@" + name)) + ")", parameters);
        }
        var rows = (DatabaseResultSet)await session.ExecuteAsync("SELECT " + string.Join(",", names.Select(name => "CAST(" + name + " AS TEXT)")) + " FROM t");
        var expected = new StringBuilder();
        expected.Append(string.Join(csv ? ',' : '\t', names.Select(name => Oracle(name, csv)))).Append('\n');
        await foreach (var record in rows.GetRowsAsync())
        { expected.Append(string.Join(csv ? ',' : '\t', Enumerable.Range(0, record.FieldCount).Select(i => Oracle(record.GetString(i), csv)))).Append('\n'); }
        await rows.DisposeAsync();
        using var destination = new MemoryStream();
        // Act
        long count = await session.CopyToAsync("COPY t TO STDOUT (FORMAT " + (csv ? "csv" : "text") + ", HEADER)", destination);
        // Assert
        count.ShouldBe(strings.Length);
        Encoding.UTF8.GetString(destination.ToArray()).ShouldBe(expected.ToString());
        destination.CanWrite.ShouldBeTrue();
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    // Independent oracle follows commands/copyto.c:1443-1708. SELECT CAST supplies the
    // engine's representation; these transformations model PostgreSQL's COPY framing.
    private static string Oracle(string? value, bool csv)
    {
        if (value is null) { return csv ? "" : "\\N"; }
        if (csv)
        {
            return value.Length == 0 || value.Any(character => ",\"\r\n".Contains(character))
                ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
        return value.Replace("\\", "\\\\").Replace("\b", "\\b").Replace("\f", "\\f").Replace("\n", "\\n")
            .Replace("\r", "\\r").Replace("\t", "\\t").Replace("\v", "\\v");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: reordered columns custom CSV NULL quote and escape follow options")]
    public async Task CopyToAsync_CustomOptions_ShouldEncodeSelectedColumns()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-options", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(a TEXT, b TEXT, c INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES ('a~!b', 'nil', 1), (NULL, '', 2)");
        using var output = new MemoryStream();
        // Act
        await session.CopyToAsync("COPY t(b,a) TO STDOUT (FORMAT csv, DELIMITER '|', NULL 'nil', QUOTE '~', ESCAPE '!', HEADER, FORCE_QUOTE(a))", output);
        // Assert
        Encoding.UTF8.GetString(output.ToArray()).ShouldBe("b|a\n~nil~|~a!~!!b~\n|nil\n");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: header uses catalog spelling after case-insensitive column binding")]
    public async Task CopyToAsync_DifferentlySpelledColumns_ShouldUseCatalogHeader()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-header", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(actual_name INT)");
        using var output = new MemoryStream();
        // Act
        (await session.CopyToAsync("COPY t(ACTUAL_NAME) TO STDOUT (FORMAT csv, HEADER)", output)).ShouldBe(0);
        // Assert
        Encoding.UTF8.GetString(output.ToArray()).ShouldBe("actual_name\n");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: overlapping exports and pre-cancellation retain the active destination")]
    public async Task CopyToAsync_OverlapAndPreCancellation_ShouldReleaseOnlyOwnAdmission()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-admission", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1)");
        using var output = new SqlCopyGateStream();
        var copying = session.CopyToAsync("COPY t TO STDOUT", output).AsTask();
        // Act
        try
        {
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Should.ThrowAsync<InvalidOperationException>(async () => await session.CopyToAsync("COPY t TO STDOUT", Stream.Null));
            var ordinary = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("COPY t TO STDOUT"));
            ordinary.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05094");
        }
        finally { output.Release.TrySetResult(); }
        // Assert
        (await copying).ShouldBe(1);
        Encoding.UTF8.GetString(output.ToArray()).ShouldBe("1\n");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.CopyToAsync("COPY t TO STDOUT", Stream.Null, canceled.Token));
        (await session.CopyToAsync("COPY t TO STDOUT", Stream.Null)).ShouldBe(1);
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - COPY: binding and transaction refusals release all ownership")]
    [InlineData("COPY t(a,a) TO STDOUT", "CS01069")]
    [InlineData("SELECT 1", "CS05093")]
    [InlineData("COPY t(missing) TO STDOUT", "CS01015")]
    [InlineData("COPY t(a) TO STDOUT (FORMAT csv, FORCE_QUOTE(b))", "CS01069")]
    [InlineData("COPY t(a) TO STDOUT (FORMAT csv, FORCE_QUOTE(a,a))", "CS01069")]
    [InlineData("COPY t TO STDOUT", "CS04075")]
    public async Task CopyToAsync_Refusals_ShouldReleaseSnapshot(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-refusal", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(a INT, b INT)");
        if (code == "CS04075") { await session.ExecuteAsync("BEGIN"); }
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.CopyToAsync(sql, Stream.Null));
        // Assert
        error.Diagnostics.ShouldContain(d => d.Code == code);
        if (session.CurrentTransaction is not null) { await session.ExecuteAsync("ROLLBACK"); }
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        await session.ExecuteAsync("INSERT INTO t VALUES(1,2)");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - COPY: slow delivery holds a snapshot while a writer completes invisibly")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CopyToAsync_Backpressure_ShouldPermitInvisibleWriter(bool cancel, bool truncate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-snapshot", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        await reader.ExecuteAsync("CREATE TABLE t(id INT)");
        await reader.ExecuteAsync("INSERT INTO t VALUES(1),(2),(3)");
        using var output = new SqlCopyGateStream();
        using var cancellation = new CancellationTokenSource();
        // Act
        Task<long> copying = reader.CopyToAsync("COPY t TO STDOUT", output, cancellation.Token).AsTask();
        try
        {
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            reader.LastStatementMetrics!.RecordsExamined.ShouldBe(1);
            database.Coordinator.GetOpenContexts().Count.ShouldBe(1);
            (await writer.ExecuteAsync(truncate ? "TRUNCATE t" : "UPDATE t SET id=id+10").AsTask().WaitAsync(TimeSpan.FromSeconds(30)))
                .AffectedCount.ShouldBe(truncate ? 0 : 3);
            await writer.ExecuteAsync("INSERT INTO t VALUES(99)");
            if (cancel) { cancellation.Cancel(); }
        }
        finally { output.Release.TrySetResult(); }
        // Assert
        if (cancel) { await Should.ThrowAsync<OperationCanceledException>(async () => await copying); }
        else
        {
            (await copying).ShouldBe(3);
            Encoding.UTF8.GetString(output.ToArray()).ShouldBe("1\n2\n3\n");
        }
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: destination exceptions release snapshot and permit session reuse")]
    public async Task CopyToAsync_DestinationFailure_ShouldReleaseSnapshot()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-io", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1)");
        using var output = new SqlCopyGateStream { Fail = true };
        output.Release.TrySetResult();
        // Act / Assert
        await Should.ThrowAsync<IOException>(async () => await session.CopyToAsync("COPY t TO STDOUT", output));
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        (await session.CopyToAsync("COPY t TO STDOUT", Stream.Null)).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: session disposal cancels delivery and joins snapshot cleanup")]
    public async Task CopyToAsync_SessionDisposed_ShouldCancelAndJoin()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-disposal", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2)");
        using var output = new SqlCopyGateStream();
        var copying = session.CopyToAsync("COPY t TO STDOUT", output).AsTask();
        // Act
        try
        {
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally { output.Release.TrySetResult(); }
        // Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await copying);
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        output.CanWrite.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: historical defaults dropped columns and empty headers use the bound definition")]
    public async Task CopyToAsync_ChangedLayout_ShouldExportLiveColumns()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-layout", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT, old_value TEXT)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1,'drop')");
        await session.ExecuteAsync("ALTER TABLE t ADD COLUMN fresh TEXT DEFAULT 'frozen'");
        await session.ExecuteAsync("ALTER TABLE t ALTER COLUMN fresh SET DEFAULT 'new'");
        await session.ExecuteAsync("ALTER TABLE t DROP COLUMN old_value");
        using var output = new MemoryStream();
        // Act
        (await session.CopyToAsync("COPY t TO STDOUT (FORMAT csv, HEADER)", output)).ShouldBe(1);
        // Assert
        Encoding.UTF8.GetString(output.ToArray()).ShouldBe("id,fresh\n1,frozen\n");
        await session.ExecuteAsync("DELETE FROM t");
        output.SetLength(0);
        (await session.CopyToAsync("COPY t TO STDOUT (FORMAT csv, HEADER)", output)).ShouldBe(0);
        Encoding.UTF8.GetString(output.ToArray()).ShouldBe("id,fresh\n");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - COPY: terminator lookalikes follow PostgreSQL single-column quoting")]
    [InlineData("COPY t(a) TO STDOUT (FORMAT csv)", "\"\\.\"\n")]
    [InlineData("COPY t TO STDOUT (FORMAT csv)", "\\.,x\n")]
    [InlineData("COPY t(a) TO STDOUT", "\\\\.\n")]
    public async Task CopyToAsync_TerminatorLookalike_ShouldEscape(string sql, string expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-terminator", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(a TEXT, b TEXT)");
        await session.ExecuteAsync("INSERT INTO t VALUES('\\.','x')");
        using var output = new MemoryStream();
        // Act
        await session.CopyToAsync(sql, output);
        // Assert
        Encoding.UTF8.GetString(output.ToArray()).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - COPY: views and system views cannot masquerade as physical tables")]
    [InlineData("COPY v TO STDOUT")]
    [InlineData("COPY INFORMATION_SCHEMA.TABLES TO STDOUT")]
    public async Task CopyToAsync_VirtualTable_ShouldRefuse(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-virtual", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT)");
        await session.ExecuteAsync("CREATE VIEW v AS SELECT id FROM t");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.CopyToAsync(sql, Stream.Null));
        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: application domain casts match SELECT and late failures release the snapshot")]
    public async Task CopyToAsync_ApplicationCastFailure_ShouldKeepPartialOutputAndRelease()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("copy-domain");
        var type = SqlType.CreateDomain("export_value", SqlType.Integer);
        builder.Types.Add(type).AddCast(SqlCast.Create<int, string>(type, SqlType.Text,
            static value => value == 2 ? throw new InvalidOperationException("Injected cast failure.") : "custom," + value,
            SqlCastContext.Assignment));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(v export_value)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2)");
        using var output = new MemoryStream();
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.CopyToAsync("COPY t TO STDOUT (FORMAT csv)", output));
        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05007");
        Encoding.UTF8.GetString(output.ToArray()).ShouldBe("\"custom,1\"\n");
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        await session.ExecuteAsync("DELETE FROM t WHERE v=2");
        (await session.CopyToAsync("COPY t TO STDOUT", Stream.Null)).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: invalid Unicode from a custom cast is a coded result encoding failure")]
    public async Task CopyToAsync_InvalidUnicode_ShouldReleaseSnapshotWithEncodingDiagnostic()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("copy-unicode");
        var type = SqlType.CreateDomain("export_value", SqlType.Integer);
        builder.Types.Add(type).AddCast(SqlCast.Create<int, string>(type, SqlType.Text,
            static _ => "\ud800", SqlCastContext.Assignment));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(v export_value)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1)");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.CopyToAsync("COPY t TO STDOUT", Stream.Null));
        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05045");
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: minimum-of-three allocation per row stays constant at two sizes")]
    public async Task CopyToAsync_TwoSizes_ShouldAllocateConstantBytesPerRow()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-allocation", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        var measured = new List<double>();
        foreach (int size in new[] { 1000, 4000 })
        {
            await session.ExecuteAsync("CREATE TABLE t" + size + "(id INT, v TEXT)");
            await session.ExecuteAsync("INSERT INTO t" + size + " VALUES " + string.Join(",", Enumerable.Range(0, size).Select(i => "(" + i + ",'a,b\\c')")));
            string sql = "COPY t" + size + " TO STDOUT (FORMAT csv)";
            await session.CopyToAsync(sql, Stream.Null);
            long minimum = long.MaxValue;
            // Act
            for (int round = 0; round < 3; round++)
            {
                long before = GC.GetTotalAllocatedBytes(true);
                (await session.CopyToAsync(sql, Stream.Null)).ShouldBe(size);
                minimum = Math.Min(minimum, GC.GetTotalAllocatedBytes(true) - before);
            }
            measured.Add((double)minimum / size);
            _output.WriteLine($"COPY {size}: {minimum} bytes, {measured[^1]:F3} bytes/row.");
        }
        // Assert
        measured[1].ShouldBeLessThan(measured[0] * 1.25);
        measured[1].ShouldBeLessThan(2500);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - COPY: writer and export stress exercises overlap until counters move")]
    public async Task CopyToAsync_ConcurrentStress_ShouldExerciseSnapshotOverlap()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("copy-stress", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        await reader.ExecuteAsync("CREATE TABLE t(id INT)");
        await reader.ExecuteAsync("INSERT INTO t VALUES(1),(2)");
        int overlaps = 0, writes = 0, canceled = 0;
        var timer = Stopwatch.StartNew();
        // Act: every writer is observed inside the export's blocked destination, not inferred
        // from elapsed time. Duration only ensures repeated scheduling; counters decide coverage.
        do
        {
            using var output = new SqlCopyGateStream();
            using var cancellation = new CancellationTokenSource();
            var task = reader.CopyToAsync("COPY t TO STDOUT", output, cancellation.Token).AsTask();
            try
            {
                await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                (await writer.ExecuteAsync("UPDATE t SET id=id+1").AsTask().WaitAsync(TimeSpan.FromSeconds(30))).AffectedCount.ShouldBe(2);
                writes++;
                if ((writes & 1) == 0) { cancellation.Cancel(); canceled++; }
                overlaps++;
            }
            finally { output.Release.TrySetResult(); }
            if (cancellation.IsCancellationRequested) { await Should.ThrowAsync<OperationCanceledException>(async () => await task); }
            else { (await task).ShouldBe(2); }
        } while ((timer.Elapsed < TimeSpan.FromMilliseconds(100) || overlaps < 8 || canceled < 4) && timer.Elapsed < TimeSpan.FromSeconds(30));
        // Assert
        overlaps.ShouldBeGreaterThanOrEqualTo(8); writes.ShouldBe(overlaps); canceled.ShouldBeGreaterThanOrEqualTo(4);
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
    }
}
