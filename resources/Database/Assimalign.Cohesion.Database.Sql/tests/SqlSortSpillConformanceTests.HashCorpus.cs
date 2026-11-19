using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapPool;
using static Assimalign.Cohesion.Database.Sql.Tests.TestObjects.SqlHeapAcceptance;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    private sealed class CorpusRun : IAsyncDisposable
    {
        internal SqlDatabaseEngine Engine = null!;
        internal SqlDatabaseSession Session = null!;
        internal List<long> Log = new();
        internal long Budget;
        internal long FailAt = -1;
        public async ValueTask DisposeAsync() { await Session.DisposeAsync(); await Engine.DisposeAsync(); }
    }

    private async Task<CorpusRun> CreateCorpusEngineAsync(long budget, bool temp, int count = 1600, bool force = false, long? quota = 536870912)
    {
        var run = new CorpusRun { Budget = budget };
        var builder = SqlDatabaseEngine.CreateBuilder("heap-corpus");
        builder.Options.StatementMemoryBudget = budget;
        builder.Options.StatementTempSpaceQuota = quota;
        builder.Options.TempSpaceQuota = 1073741824;
        if (temp) { builder.Options.TempDirectory = Path.Combine(_root, ".heap-corpus-" + Guid.NewGuid().ToString("N")); }
        builder.Functions.Add(SqlScalarFunction.Create("log_value", (long id) => { run.Log.Add(id); if (id == run.FailAt) { throw new InvalidOperationException("corpus row fault"); } return id; }));
        builder.Functions.Add(SqlScalarFunction.Create<long, decimal>("dec_key", id => new decimal((int)(id % 1000) * (id < 1000 ? 10 : 100), 0, 0, false, (byte)(id < 1000 ? 1 : 2))));
        builder.Functions.Add(SqlScalarFunction.Create<long, double>("nan_key", id => id % 1000 == 900 ? BitConverter.Int64BitsToDouble(id < 1000 ? 0x7ff8000000000001L : unchecked((long)0xfff8000000000042UL)) : id % 1000 == 901 ? (id < 1000 ? -0.0 : 0.0) : id % 1000 + 1));
        run.Engine = await builder.BuildAsync();
        var database = await run.Engine.CreateDatabaseAsync("db");
        database.Definitions.ForceSelectRetentionBasePath = force;
        run.Session = await database.CreateSessionAsync();
        await run.Session.ExecuteAsync("CREATE TABLE t(id INT,k INT,note TEXT COLLATE case_insensitive)");
        for (int first = 0; first < count; first += 200)
        {
            await run.Session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, Math.Min(200, count - first)).Select(id => $"({id},{(id % 10 == 0 ? "NULL" : (id % 73).ToString(CultureInfo.InvariantCulture))},'{(id % 2 == 0 ? "a" : "A")}')")));
        }
        await run.Session.ExecuteAsync("CREATE SEQUENCE s");
        return run;
    }

    private sealed record CorpusOutcome(RowValues[] Rows, string? Error, long Visits, long Peak, int Passes, long Batches, long TempPeak, long[] Calls, long Live = 0);
    private async Task<CorpusOutcome> ExecuteCorpusAsync(CorpusRun run, string sql, long fail = -1, Dictionary<string, object?>? parameters = null, bool sample = false, bool everyTransition = true, WindowPoolLease? images = null, HashPoolEvents? pool = null, Func<long>? readHeap = null)
    {
        run.Log.Clear(); run.FailAt = fail;
        await run.Session.ExecuteAsync("ALTER SEQUENCE s RESTART WITH 1");
        RowValues[] rows = []; string? error = null; long live = 0;
        if (sample) { images?.Drain(pool!); }
        long baseline = sample ? (readHeap ?? LiveHeap.Read)() : 0;
        var phases = new HashSet<string>();
        string? previousPhase = null;
        long depth = 0, population = 0;
        bool replay = false;
        void Sample() { if (replay) { live = Math.Max(live, (readHeap ?? LiveHeap.Read)() - baseline); } }
        if (sample)
        {
            SqlPlanExecutor.ObserveHashSpill = (phase, value) =>
            {
                replay |= phase is "batch" or "partitioned";
                bool selected = everyTransition ? phase != previousPhase :
                    phase == "input" && phases.Add(phase) || phase == "batch" && value > depth ||
                    phase == "batch-live" && value > population || phase is "partitioned" or "ordered" or "groups-live";
                if (selected)
                {
                    previousPhase = phase;
                    if (phase == "batch") { depth = value; }
                    if (phase == "batch-live") { population = value; }
                    Sample();
                }
            };
            run.Session.Database.Definitions.ObserveSelectRetention = (phase, _, _) =>
            { if (phases.Add(phase)) { Sample(); } };
        }
        try
        {
            await using var result = (DatabaseResultSet)await run.Session.ExecuteAsync(sql, parameters);
            if (sample) { Sample(); }
            rows = await Rows(result);
        }
        catch (DatabaseException failure) { error = failure.Diagnostics[0].Code + "|" + failure.Message + "|" + failure.Diagnostics[0].Start + "|" + failure.Diagnostics[0].End; }
        catch (DatabaseTypeException failure) { error = failure.GetType().Name + "|" + failure.Message; }
        finally
        {
            if (sample) { SqlPlanExecutor.ObserveHashSpill = null; run.Session.Database.Definitions.ObserveSelectRetention = null; }
        }
        var metrics = run.Session.LastStatementMetrics!;
        var hash = SqlHashSpillMetrics.For(metrics);
        if (run.Engine.TempSpace is not null) { TempBytes(run.Engine.TempSpace).ShouldBe(0); Files(run.Engine).ShouldBe(0); }
        metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
        return new(rows, error, metrics.RecordsExamined, metrics.PeakHashMemoryBytes, hash?.Passes ?? 0, hash?.Batches ?? 0, hash?.PeakBytes ?? 0, run.Log.ToArray(), live);
    }
    private static bool CorpusEqual(CorpusOutcome a, CorpusOutcome b) => a.Error == b.Error && a.Rows.SequenceEqual(b.Rows) && a.Rows.Zip(b.Rows).All(pair => pair.First.Cells.Zip(pair.Second.Cells).All(cell => cell.First is not DateTimeOffset time || time.EqualsExact((DateTimeOffset)cell.Second!))) && a.Visits == b.Visits && a.Calls.SequenceEqual(b.Calls);

    [Theory(DisplayName = "Cohesion Test [Sql] - Seeded hash corpus: replay samples stay within each grant plus 256 KiB (#1789)")]
    [InlineData(1048576L, 0, 2)]
    [InlineData(1048576L, 1, 2)]
    [InlineData(2097152L, 0, 2)]
    [InlineData(2097152L, 1, 2)]
    [InlineData(4194304L, 0, 1)]
    public async Task ExecuteAsync_SeededHashHeapCorpus_ShouldMatchWithinGrant(long grant, int shard, int shards)
    {
        // Arrange: the review's recorded seed and generation order. The larger run is opt-in.
        const int corpusSeed = 17890507;
        int statements = int.TryParse(Environment.GetEnvironmentVariable("SQL_HASH_CORPUS_STATEMENTS"), out int requested) ? requested : 256;
        long[] grants = grant == 4194304 && statements > 256 ? [grant, 16777216] : [grant];
        int first = (int)((long)statements * shard / shards);
        int last = (int)((long)statements * (shard + 1) / shards);
        var random = new Random(corpusSeed);
        await using var unlimited = await CreateCorpusEngineAsync(67108864, false);
        await using var original = await CreateCorpusEngineAsync(67108864, false, force: true);
        var spilled = new List<CorpusRun>();
        foreach (long selectedGrant in grants) { spilled.Add(await CreateCorpusEngineAsync(selectedGrant, true)); }
        long[] excesses = grants.Select(_ => long.MinValue).ToArray();
        int spills = 0, matches = 0, refusals = 0;
        // The default 4 MiB control never replays, so Sample never reads its baseline.
        bool pinPool = grant != 4194304 || statements > 256;
        using var pool = pinPool ? new HashPoolEvents() : null;
        using var images = pinPool ? new WindowPoolLease() : null;
        if (pinPool) { images!.Drain(pool!);  }
        await using var acceptance = pinPool ? new Batch(pool!, _output, nameof(ExecuteAsync_SeededHashHeapCorpus_ShouldMatchWithinGrant)) : null;
        var recordings = grants.Select(_ => new List<Sample<CorpusHeapReading>>()).ToArray();
        try
        {
            // Act / Assert: rows, exact temporal representatives, errors, visits and calls.
            for (int caseIndex = 0; caseIndex < statements; caseIndex++)
            {
                int test = caseIndex;
                int dimensions = 1 + random.Next(4), modulus = new[] { 1000, 1200, 1600 }[random.Next(3)];
                string[] expressions = [ $"CASE WHEN id%10=0 THEN NULL ELSE id%{modulus} END", "dec_key(id)", "nan_key(id)", "(note COLLATE case_accent_insensitive)", "CAST(id%997 AS TEXT)", "CAST('2026-01-01' AS TIMESTAMPTZ)", "id%2=0" ];
                var keys = new List<string> { expressions[0] };
                for (int i = 1; i < dimensions; i++) { keys.Add(expressions[1 + random.Next(expressions.Length - 1)]); }
                string key = string.Join(',', keys), filter = test % 3 == 0 ? " FILTER(WHERE log_value(id)%3<>0)" : "";
                string[] aggregate = [ "COUNT(*)", "COUNT(k)", "SUM(k)", "AVG(CAST(k AS NUMERIC(12,2)))", "MIN(k)", "MAX(k)", "BOOL_AND(k>3)", "BOOL_OR(k>40)" ];
                bool distinct = test % 7 == 0;
                int aggregates = 1 + random.Next(8);
                string terms = string.Join(',', Enumerable.Range(0, aggregates).Select(i => aggregate[(test + i) % aggregate.Length] + filter));
                if (!distinct && test % 23 == 0) { terms += ",SUM(nextval('s'))"; }
                if (!distinct && test % 31 == 0) { terms += ",COUNT(log_value(id))"; }
                string sql = distinct ? "SELECT DISTINCT " + key + " FROM t" : "SELECT " + key + "," + terms + " FROM t GROUP BY " + key;
                if (!distinct && test % 5 == 0) { sql += " HAVING COUNT(*)>0"; }
                if (test % 4 == 0) { sql += distinct ? " ORDER BY 1" : " ORDER BY COUNT(*)"; }
                if (test % 6 == 0) { sql += $" LIMIT {random.Next(1, 300)} OFFSET {random.Next(30)}"; }
                long fail = test % 31 == 0 ? 1201 : -1;
                // Generate the complete sequence before selecting this range, preserving every SQL shape.
                if (test < first || test >= last) { continue; }
                var expected = await ExecuteCorpusAsync(unlimited, sql, fail);
                CorpusEqual(expected, await ExecuteCorpusAsync(original, sql, fail)).ShouldBeTrue(customMessage: sql);
                for (int index = 0; index < grants.Length; index++)
                {
                    if (pinPool)
                    {
                        int grantIndex = index;
                        CorpusOutcome? expectedForAttempt = expected;
                        var sampled = await acceptance!.MeasureAsync($"seed={corpusSeed}|statements={statements}|range=[{first},{last})|statement={test}|grant={grants[grantIndex]}|shard={shard}|shards={shards}", async readHeap =>
                        {
                            var candidateExpected = expectedForAttempt ?? await ExecuteCorpusAsync(unlimited, sql, fail);
                            expectedForAttempt = null;
                            var outcome = await ExecuteCorpusAsync(spilled[grantIndex], sql, fail, sample: true, images: images, pool: pool, readHeap: readHeap);
                            return new CorpusHeapReading(candidateExpected, outcome);
                        });
                        recordings[grantIndex].Add(sampled);
                        Sample<CorpusHeapReading>[] prefix = recordings[grantIndex].ToArray();
                        await acceptance.CheckAsync(() => [Ceiling("corpus prefix largest excess", prefix.Max(x => x.Value.Live) - grants[grantIndex] - 262144,
                            0, Term.Maximum(prefix))], async () =>
                        {
                            excesses[grantIndex] = prefix.Max(x => x.Value.Live) - grants[grantIndex] - 262144;
                            excesses[grantIndex].ShouldBeLessThanOrEqualTo(0, customMessage: sql);
                            var accepted = sampled.Value.Actual!;
                            accepted.Peak.ShouldBeLessThanOrEqualTo(grants[grantIndex]);
                            if (accepted.Error?.StartsWith("CS05067|", StringComparison.Ordinal) == true)
                            {
                                refusals++;
                                _output.WriteLine($"HASH_CORPUS_REFUSAL|test={test}|grant={grants[grantIndex]}|{sql}");
                                await using var baseline = await CreateCorpusEngineAsync(grants[grantIndex], false);
                                (await ExecuteCorpusAsync(baseline, sql, fail)).Error.ShouldStartWith("CS05067|");
                            }
                            else
                            {
                                CorpusEqual(sampled.Value.Expected!, accepted).ShouldBeTrue(customMessage: sql);
                                matches++;
                            }

                            if (accepted.Passes > 0)
                            {
                                spills++;
                            }

                            sampled.Value.ReleaseRows();
                        });
                        excesses[grantIndex] = prefix.Max(x => x.Value.Live) - grants[grantIndex] - 262144;
                        continue;
                    }
                    var actual = await ExecuteCorpusAsync(spilled[index], sql, fail, sample: true, images: images, pool: pool);
                    excesses[index] = Math.Max(excesses[index], actual.Live - grants[index] - 262144);
                    excesses[index].ShouldBeLessThanOrEqualTo(0, customMessage: sql);
                    actual.Peak.ShouldBeLessThanOrEqualTo(grants[index]);
                    if (actual.Error?.StartsWith("CS05067|", StringComparison.Ordinal) == true)
                    {
                        refusals++;
                        _output.WriteLine($"HASH_CORPUS_REFUSAL|test={test}|grant={grants[index]}|{sql}");
                        await using var baseline = await CreateCorpusEngineAsync(grants[index], false);
                        (await ExecuteCorpusAsync(baseline, sql, fail)).Error.ShouldStartWith("CS05067|");
                    }
                    else { CorpusEqual(expected, actual).ShouldBeTrue(customMessage: sql); matches++; }
                    if (actual.Passes > 0) { spills++; }
                }
                if (test % 200 == 0 && Environment.GetEnvironmentVariable("SQL_HASH_CORPUS_PROGRESS") is { Length: > 0 } progress)
                { File.WriteAllText(progress, $"completed={test + 1}/{statements}|matches={matches}|spills={spills}|largest_excess={string.Join(',', excesses)}"); }
            }
            if (acceptance is not null)
            {
                await acceptance.CompleteAsync();
                for (int index = 0; index < grants.Length; index++)
                { excesses[index] = recordings[index].Max(x => x.Value.Live) - grants[index] - 262144; }
            }
        }
        finally { foreach (var run in spilled) { await run.DisposeAsync(); } }
        // Each replaying range needs a spill witness; the default 4 MiB corpus must fit.
        if (grant < 4194304) { spills.ShouldBeGreaterThan(0); }
        else if (statements == 256) { spills.ShouldBe(0); }
        if (statements == 256 && grant == 1048576 && shards == 2)
        { refusals.ShouldBe(shard == 0 ? 3 : 1); } // four of 256, versus 30 on c9ac0df64
        for (int index = 0; index < grants.Length; index++)
        { _output.WriteLine($"HASH_CORPUS_HEAP|seed={corpusSeed}|statements={statements}|range=[{first},{last})|grant={grants[index]}|largest_excess={excesses[index]}|matches={matches}|spills={spills}|refusals={refusals}"); }
    }
    private sealed class CorpusHeapReading
    {
        internal long Live { get; }
        internal CorpusOutcome? Expected { get; private set; }
        internal CorpusOutcome? Actual { get; private set; }
        internal CorpusHeapReading(CorpusOutcome expected, CorpusOutcome actual)
        { Live = actual.Live; Expected = expected; Actual = actual; }
        internal void ReleaseRows() { Expected = null; Actual = null; }
    }

}
