using System;
using System.Collections.Generic;
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
    private const int generatedStatementCount = 2048;

    [Fact(DisplayName = "Cohesion Test [Sql] - Seeded staged-source projections execute once and generated application binary values refuse spill (#1789)")]
    public async Task ExecuteAsync_SeededStagedProjections_ShouldPreserveCallsAndBinaryRefusals()
    {
        // Arrange: independent volatile projections have no ORDER BY output slot. Wide source
        // retention forces MaterializeStagedSort's deferProjection branch before Transform.
        var logs = new[] { new List<long>(), new List<long>() };
        await using var reference = await CreateSecondGeneratedAsync("staged-reference", 67108864, logs[0]);
        await using var engine = await CreateSecondGeneratedAsync("staged-spill", 262144, logs[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b }) { await Seed(session); }
        var random = new Random(seed);
        int staged = 0;
        SqlRowCollector.ObserveSpill = phase => { if (phase == "staged-sort") { staged++; } };
        try
        {
            for (int statement = 0; statement < 12; statement++)
            {
                string from = (statement % 3) switch
                {
                    1 => "t LEFT JOIN u ON u.id=t.id%4",
                    2 => "t CROSS JOIN u",
                    _ => "t",
                };
                string direction = random.Next(2) == 0 ? "ASC" : "DESC";
                string window = statement % 4 == 0 ? " LIMIT 3000 OFFSET 11" : "";
                string sql = $"SELECT stamp(t.id),wide(t.id) FROM {from} WHERE stamp(t.id)>=0 ORDER BY t.k {direction},t.id{window}";
                logs[0].Clear(); logs[1].Clear();
                // Act
                await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql);
                await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql);
                // Assert: both the predicate phase and the projection phase are exact.
                right.ShouldBeOfType<SqlSpilledResultSet>();
                logs[1].ShouldBe(logs[0], customMessage: $"seed={seed}; statement={statement}; {sql}");
                (await Rows(right)).ShouldBe(await Rows(left));
            }
            staged.ShouldBe(12);
            for (int statement = 0; statement < 6; statement++)
            {
                string expression = statement % 2 == 0 ? "changing_key(id)" : "CASE WHEN id>=0 THEN changing_key(id) ELSE X'00' END";
                string sql = statement < 4 ? $"SELECT id FROM t ORDER BY {expression}" : $"SELECT {expression} FROM t ORDER BY id";
                await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql);
                var error = await Should.ThrowAsync<DatabaseException>(async () => await b.ExecuteAsync(sql));
                error.Diagnostics[0].Code.ShouldBe("CS05067");
                TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
            }
            _output.WriteLine($"GENERATOR_REVIEW|seed={seed}|statements=18|staged_spills=12|staged_starts={staged}|binary_refusals=6");
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }

    private async Task<SqlDatabaseEngine> CreateSecondGeneratedAsync(string name, long budget, List<long> log)
    {
        var builder = SqlDatabaseEngine.CreateBuilder(name);
        builder.Options.TempDirectory = Path.Combine(_root, name);
        builder.Options.StatementMemoryBudget = budget;
        builder.Options.StatementTempSpaceQuota = 67108864;
        byte[] shared = [0]; var gate = new object();
        builder.Functions.Add(SqlScalarFunction.Create("stamp", (long id) => { log.Add(id); return id; }));
        builder.Functions.Add(SqlScalarFunction.Create("wide", (long id) => { log.Add(-1000000-id); return new string('a', 1100); }));
        builder.Functions.Add(SqlScalarFunction.Create("changing_key", (long id) =>
        { lock (gate) { shared[0] = (byte)(id % 251); return shared; } }));
        return await builder.BuildAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Seeded statement generator crosses sources, typed keys, volatile calls, sequences and empty/error windows (#1789)")]
    public async Task ExecuteAsync_SeededStatements_ShouldMatchTypesValuesCallsAndFailures()
    {
        // Arrange: in-memory engines with explicit directories, including the reference.
        _output.WriteLine($"GENERATOR|seed={seed}|statements={generatedStatementCount}");
        var logs = new[] { new List<long>(), new List<long>() };
        var counters = new[] { new GeneratorCounter(), new GeneratorCounter() };
        await using var reference = await CreateGeneratedAsync("generated-reference", 67108864, logs[0], counters[0]);
        await using var engine = await CreateGeneratedAsync("generated-spill", 2097152, logs[1], counters[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        foreach (var session in new[] { a, b })
        {
            await Seed(session,1500);
            await session.ExecuteAsync("INSERT INTO u VALUES(1),(2)");
            await session.ExecuteAsync("CREATE VIEW v AS SELECT id FROM t");
        }
        var random = new Random(seed);
        int spilled = 0, failures = 0, empty = 0;
        var spillBoundaries = new int[2]; int active = 0;
        SqlRowCollector.ObserveSpill = phase => { if (phase is "arena" or "staged-sort" or "output-spool") { spillBoundaries[active]++; } };
        try
        {
        for (int statement = 0; statement < generatedStatementCount; statement++)
        {
            int source = statement % 8, family = statement / 8 % 16, variant = statement / 128;
            bool grouped = source == 3, distinct = variant % 5 == 4 && family != 15;
            string inputId = source == 5 ? "LENGTH(t.function_name)" : "t.id";
            string id = grouped ? distinct ? "(t.id%256)%7" : "t.id%256" : distinct ? $"({inputId})%7" : inputId;
            string key = GeneratedKey(family, id);
            string from = source switch
            {
                1 => "t INNER JOIN u ON u.id=t.id%3",
                2 => "t LEFT JOIN u ON u.id=t.id%4",
                4 => "v t",
                5 => "COHESION_SCHEMA.FUNCTIONS t",
                _ => "t",
            };
            string extra = source == 2 ? ",u.id AS nullable_inner" : source == 7 ? ",(SELECT 1) AS scalar" : "";
            string where = "";
            if (variant == 5 && !grouped) { where += $" WHERE stamp({inputId})>=0"; }
            string group = grouped ? " GROUP BY t.id%256 HAVING COUNT(*)>0" : "";
            string failureCall = $"fails_on({id},{(grouped ? 200 : 1000)})";
            string observation = variant % 4 == 1 ? $",stamp({id}) AS observed" : variant == 11 ? $",{failureCall} AS failure" : "";
            string sequence = variant is 2 or 10 && !distinct ? ",nextval('s') AS serial" : "";
            string order = sequence.Length > 0 ? "serial," : variant is 6 or 14 ? "nextval('s')," : "";
            order += variant % 4 == 3 && variant != 11 ? $"{failureCall}," : variant == 13 ? $"stamp({id})," : "";
            string direction = random.Next(2) == 0 ? " ASC NULLS FIRST" : " DESC NULLS LAST";
            string window = variant switch
            {
                0 or 7 => " LIMIT 0",
                1 or 8 => " OFFSET 20000",
                2 or 9 => " LIMIT 3000 OFFSET 11",
                _ => "",
            };
            if (family == 15)
            {
                // Invariant text is deliberately ineligible for spilling; exercise its fitting
                // sort and calls without overflowing a staged system-view join source.
                window = " LIMIT 1"; where += (where.Length == 0 ? " WHERE " : " AND ") + inputId + "<64";
            }
            string sql = (source == 6 ? "WITH one AS (SELECT 1 AS n) " : "")
                + $"SELECT {(distinct ? "DISTINCT " : "")}{id} AS id,{key} AS typed_key,{(grouped ? "large_wide" : "wide")}({id}) AS w{observation}{sequence}{extra} FROM {from}{where}{group} ORDER BY {order}typed_key{direction},id{window}";
            _output.WriteLine($"GENERATED|{statement}|{sql}");
            logs[0].Clear(); logs[1].Clear(); counters[0].Calls = counters[1].Calls = 0;
            spillBoundaries[0] = spillBoundaries[1] = 0; active = 0;
            DatabaseResultSet? left = null, right = null;
            try
            {
                // Act: compare partial-failure codes as well as the complete evaluation log.
                var leftError = await Record.ExceptionAsync(async () => left = (DatabaseResultSet)await a.ExecuteAsync(sql));
                active = 1;
                var rightError = await Record.ExceptionAsync(async () => right = (DatabaseResultSet)await b.ExecuteAsync(sql));
                // Assert
                logs[1].ShouldBe(logs[0], customMessage: $"seed={seed}; statement={statement}; {sql}");
                SqlSortSpillMetrics.For(a.LastStatementMetrics!).ShouldBeNull();
                spillBoundaries[0].ShouldBe(0,customMessage: sql);
                if (leftError is not null)
                {
                    var expected = leftError.ShouldBeAssignableTo<DatabaseException>();
                    var actual = rightError.ShouldBeAssignableTo<DatabaseException>();
                    expected.Diagnostics.Select(d => d.Code).ShouldBe(new[] { "CS05007" },customMessage: sql);
                    actual.Diagnostics.Select(d => d.Code).ShouldBe(expected.Diagnostics.Select(d => d.Code), customMessage: sql);
                    if (family != 15) { spillBoundaries[1].ShouldBeGreaterThan(0,customMessage: sql); }
                    failures++;
                }
                else
                {
                    rightError.ShouldBeNull(customMessage: sql);
                    left.ShouldBeOfType<SqlMaterializedResultSet>();
                    (await Rows(right!)).ShouldBe(await Rows(left!), customMessage: sql);
                    right!.Columns.Select(c => (c.Name,c.Type,c.IsNullable,c.MaxLength)).ShouldBe(left!.Columns.Select(c => (c.Name,c.Type,c.IsNullable,c.MaxLength)));
                    if (window == " LIMIT 0" && !distinct) { empty++; }
                    else if (family == 15) { right.ShouldBeOfType<SqlMaterializedResultSet>(); SqlSortSpillMetrics.For(b.LastStatementMetrics!).ShouldBeNull(); }
                    else
                    {
                        right.ShouldBeOfType<SqlSpilledResultSet>(customMessage: sql);
                        SqlSortSpillMetrics.For(b.LastStatementMetrics!).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0); spilled++;
                    }
                }
            }
            finally { if (left is not null) { await left.DisposeAsync(); } if (right is not null) { await right.DisposeAsync(); } }
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        }
        finally { SqlRowCollector.ObserveSpill = null; }
        await using var sa = (DatabaseResultSet)await a.ExecuteAsync("SELECT currval('s')");
        await using var sb = (DatabaseResultSet)await b.ExecuteAsync("SELECT currval('s')");
        (await Rows(sb)).ShouldBe(await Rows(sa));
        spilled.ShouldBe(1320); failures.ShouldBe(364); empty.ShouldBe(240);
        _output.WriteLine($"GENERATOR_RESULT|seed={seed}|statements={generatedStatementCount}|spills={spilled}|errors={failures}|empty={empty}");
    }

    private async Task<SqlDatabaseEngine> CreateGeneratedAsync(string name, long budget, List<long> log, GeneratorCounter counter)
    {
        var builder = SqlDatabaseEngine.CreateBuilder(name);
        builder.Options.StatementMemoryBudget = budget;
        builder.Options.TempDirectory = Path.Combine(_root, name);
        builder.Options.StatementTempSpaceQuota = 67108864;
        for (int index=0;index<1000;index++)
        { builder.Functions.Add(SqlScalarFunction.Create("catalog_spill_"+index, (long value) => value)); }
        builder.Functions.Add(SqlScalarFunction.Create("large_wide", (long value) => { log.Add(-1000000-value); return new string((char)('a'+value%23),5500); }));
        builder.Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { log.Add(value); return value; }));
        builder.Functions.Add(SqlScalarFunction.Create("wide", (long value) => { log.Add(-1000000-value); return new string((char)('a'+value%23),1100); }));
        builder.Functions.Add(SqlScalarFunction.Create("fails_on", (long value, long at) => { log.Add(value); if(++counter.Calls==at) { throw new InvalidOperationException("injected row failure"); } return value; }));
        builder.Functions.Add(SqlScalarFunction.Create("date_key", (long value) => new DateOnly(2020,1,1).AddDays((int)(value%31))));
        builder.Functions.Add(SqlScalarFunction.Create("timestamp_key", (long value) => new DateTime(2020,1,1).AddTicks(value%31)));
        builder.Functions.Add(SqlScalarFunction.Create("time_key", (long value) => new TimeOnly(value%31)));
        builder.Functions.Add(SqlScalarFunction.Create("guid_key", (long value) => new Guid((int)(value%31),0,0,new byte[8])));
        builder.Functions.Add(SqlScalarFunction.Create("real_key", (long value) => value%3==0 ? float.NaN : value%3==1 ? -0.0f : 0.0f));
        return await builder.BuildAsync();
    }

    private sealed class GeneratorCounter
    {
        internal int Calls;
    }

    private static string GeneratedKey(int family, string id)
    {
        string text = $"CASE WHEN ({id})%4=0 THEN 'a' WHEN ({id})%4=1 THEN 'A' WHEN ({id})%4=2 THEN 'á' ELSE 'Á' END";
        return family switch
        {
            0 => $"date_key({id})",
            1 => $"timestamp_key({id})",
            2 => $"time_key({id})",
            3 => $"guid_key({id})",
            4 => $"({id})%2=0",
            5 => $"real_key({id})",
            6 => $"CAST(({text}) AS CHAR(4)) COLLATE binary",
            7 => $"({text}) COLLATE binary",
            8 => $"({text}) COLLATE case_insensitive",
            9 => $"(({text}) || CASE WHEN ({id})%3=0 THEN '́' ELSE '' END) COLLATE case_accent_insensitive",
            10 => $"({text}) COLLATE case_accent_insensitive",
            11 => $"CASE WHEN ({id})%2=0 THEN X'00FF' ELSE X'00FE' END",
            12 => $"CAST(CASE WHEN ({id})%2=0 THEN '{{\"x\":1}}' ELSE '{{\"x\":2}}' END AS JSONB)",
            13 => $"CAST(({id})%31 AS DECIMAL(10,2))",
            14 => $"CASE WHEN ({id})%2=0 THEN NULL ELSE ({id})%31 END",
            _ => $"({text}) COLLATE invariant",
        };
    }
}
