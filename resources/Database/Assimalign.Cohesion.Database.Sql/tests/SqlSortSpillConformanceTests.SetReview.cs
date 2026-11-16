using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Fitting set root and CTAS rows match archived base digests (#1789)")]
    public async Task ExecuteAsync_SetBaseRows_ShouldMatchArchivedDigest()
    {
        // Arrange
        await using var engine = await Create("set-base-digests", 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        foreach (bool tableAs in new[] { false, true })
        {
            string query = tableAs ? "SELECT id FROM t UNION ALL SELECT id FROM t ORDER BY id" : "(SELECT id FROM t ORDER BY k) UNION ALL SELECT id FROM t";
            // Act
            if (tableAs) { await session.ExecuteAsync("CREATE TABLE archive AS " + query); query = "SELECT id FROM archive"; }
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(query);
            var rows = await Rows(result);
            // Assert
            rows.Length.ShouldBe(8000);
            SetRowDigest(rows).ShouldBe(tableAs ? "41DFDC9345219DAE0E769B69359413AE8436832851A4268DAF3F0D134CC6743A"
                : "AE0523DF3D7A19DFE593997F3E343CE7DB8011156D7ED70FD0724B57D6D28FB0");
            _output.WriteLine($"SET_BASE_DIGEST|tableAs={tableAs}|{SetRowDigest(rows)}");
        }
    }

    private static string SetRowDigest(RowValues[] rows)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join('\n', rows.Select(row => string.Join(';', row.Cells.Select(cell => cell?.GetType().Name + ":" + Convert.ToString(cell, System.Globalization.CultureInfo.InvariantCulture))))))));

    [Theory(DisplayName = "Cohesion Test [Sql] - Mixed hash build handoffs retain the base refusal and temp cause (#1789)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_MixedBuildFailure_ShouldKeepBaseCode(bool full)
    {
        // Arrange
        await using var engine = await Create("mixed-build-review", 262144, quota: 0);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session, 32768);
        await session.ExecuteAsync("CREATE TABLE p(id INT)"); await session.ExecuteAsync("INSERT INTO p VALUES(0),(1),(2)");
        string sql = full
            ? "SELECT o.id,p.id,b.id FROM (u o CROSS JOIN p) FULL JOIN t b ON p.id=b.id"
            : "SELECT o.id,p.id,b.id FROM u o CROSS JOIN p JOIN t b ON p.id=b.id";
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
        // Assert
        failure.Diagnostics[0].Code.ShouldBe("CS05067");
        WindowFailureData(failure, nameof(SqlOperatorSpillRefusal)).ShouldBeTrue();
        _output.WriteLine($"MIXED_REVIEW|full={full}|code={failure.Diagnostics[0].Code}|data={failure.Data.Count}");
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Identity eligibility stays fixed across a committed operand-boundary DDL (#1789)")]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task ExecuteAsync_SetGateFlip_ShouldKeepStatementDecision(bool toTrue, bool all)
    {
        // Arrange
        await using var engine = await Create("set-gate-review", 262144, quota: 134217728);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await using var ddl = await database.CreateSessionAsync();
        await Seed(session);
        const string definition = "CREATE FUNCTION gate_marker(s TEXT) RETURNS TEXT IMMUTABLE RETURN s";
        if (toTrue) { await ddl.ExecuteAsync(definition); }
        string operation = all ? "UNION ALL" : "UNION";
        string sql = toTrue ? $"SELECT id FROM t WHERE id<2 {operation} SELECT id FROM t"
            : $"SELECT id FROM t {operation} SELECT 999999";
        RowValues[]? expected = null;
        if (!toTrue)
        {
            await using var reference = await Create("set-gate-fitting", 67108864);
            await using var fitting = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(fitting);
            await using var result = (DatabaseResultSet)await fitting.ExecuteAsync(sql);
            expected = await Rows(result);
            expected.ShouldBe(Enumerable.Range(0, 4000).Select(value => new RowValues([(long)value]))
                .Append(new RowValues([999999L])).ToArray());
        }
        bool flipped = false;
        SqlRowCollector.ObserveBuffer = (phase, _) =>
        {
            if (phase != "set-operand" || flipped) { return; }
            flipped = true;
            ddl.ExecuteAsync(toTrue ? "DROP FUNCTION gate_marker(TEXT)" : definition).GetAwaiter().GetResult();
            database.Definitions.CanSpillValues.ShouldBe(toTrue);
        };
        try
        {
            // Act / Assert
            if (toTrue)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
                failure.Diagnostics[0].Code.ShouldBe("CS05067");
            }
            else
            {
                await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
                var rows = await Rows(result);
                rows.ShouldBe(expected);
            }
            flipped.ShouldBeTrue(); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveBuffer = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Pre-existing spilled CTE damage keeps located CK05004 inside a set operand (#1789)")]
    public async Task ExecuteAsync_SetCachedRelationDamage_ShouldKeepBaseCode()
    {
        // Arrange
        await using var engine = await Create("set-cache-review", 262144, quota: 134217728);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        bool injected = false;
        BeforeRead(engine.TempSpace!, () =>
        {
            if (injected) { return; }
            var run = StatementRuns(engine.TempSpace!).First(run => run.IsSealed && run.RecordCount > 0);
            RandomAccess.Write(Handle(run), new byte[] { 255 }, 0); injected = true;
        });
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "WITH c AS (SELECT id,note FROM t) SELECT id FROM c UNION ALL SELECT id FROM c"));
            // Assert
            injected.ShouldBeTrue(); failure.Diagnostics[0].Code.ShouldBe("CK05004");
            failure.Diagnostics[0].Start.ShouldNotBeNull(); failure.Diagnostics[0].End.ShouldNotBeNull();
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { BeforeRead(engine.TempSpace!, null); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DISTINCT join output permits nested-loop buffer spooling (#1789)")]
    public async Task ExecuteAsync_DistinctNestedBuffer_ShouldSpool()
    {
        // Arrange
        await using var engine = await Create("distinct-inner-review", 262144);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync(); await Seed(session);
        int appends = 0;
        SqlRowCollector.ObserveBuffer = (phase, _) => { if (phase == "inner-append") { appends++; } };
        try
        {
            // Act
            await using (var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT DISTINCT u.id FROM u CROSS JOIN t"))
            {
                // Assert
                (await Rows(result)).ShouldBe(new[] { new RowValues([1]) });
            }
            appends.ShouldBe(4000); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveBuffer = null; }
    }
}
