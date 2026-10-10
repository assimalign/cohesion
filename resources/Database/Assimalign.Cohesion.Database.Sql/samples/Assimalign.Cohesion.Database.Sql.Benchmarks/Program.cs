using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Benchmarks;

/// <summary>
/// The SQL engine's NativeAOT statement benchmark (section 10 of
/// <c>docs/programs/DATABASE_ENGINE_EXTENSIBILITY_DESIGN.md</c>). Each case runs once to warm up,
/// then a fixed number of measured iterations; the report gives every iteration and the median
/// and best of them, in nanoseconds per row (Q1 to Q3) or per statement (Q7), wall clock and process
/// processor time, with the bytes the measuring thread allocated per row or statement
/// (<see cref="GC.GetAllocatedBytesForCurrentThread"/>).
/// </summary>
/// <remarks>
/// <para>
/// Only public API is used, so the same file builds against any tree that has the engine; that
/// is how a baseline commit and a phase branch are measured with one harness. The cases that need
/// the E2 function ABI (Q4, Q5, Q7R and Q8C) are compiled out when the project is built with
/// <c>-p:SqlBenchmarkBaseline=true</c>, for a baseline before E2.
/// </para>
/// <para>
/// Q4 is Q1 with a registered <c>Create&lt;string, string&gt;</c> function in place of <c>UPPER</c>;
/// Q5L and Q5T call the same function as a hand-written leaf and as a typed <c>Create</c> leaf; Q6
/// projects <c>ABS(-5)</c>, a call folded at plan time, beside its control Q6C, which projects the
/// constant; Q7R is Q7 with an immutable registered function in the CHECK; Q8 is an engine build over
/// an already-applied 50-table schema in a file-backed root, timed per build, and Q8C the same schema
/// with a CHECK over a registered function on every table.
/// </para>
/// </remarks>
internal static class Program
{
    private const string Q1 = "SELECT UPPER(name), ABS(id), LENGTH(name) FROM t";
    private const string Q2 = "SELECT id FROM t WHERE ABS(id) > 10";
    private const string Q3 = "SELECT g, COUNT(*), SUM(v), AVG(v), MIN(s), MAX(s) FROM t GROUP BY g";
    private const string Q4 = "SELECT upper_r(name), ABS(id), LENGTH(name) FROM t";
    private const string Q5L = "SELECT upper_leaf(name) FROM t";
    private const string Q5T = "SELECT upper_r(name) FROM t";
    private const string Q6 = "SELECT id, ABS(-5) FROM t";
    private const string Q6C = "SELECT id, 5 FROM t";
    private const string Q7Insert = "INSERT INTO {0} (id, name, v) VALUES (@id, @name, @v)";
    private const string Q7Check = "LENGTH(name) > 0 AND ABS(v) < 1000000000";
    private const string Q7RCheck = "has_text(name) AND ABS(v) < 1000000000";

    private static async Task<int> Main(string[] args)
    {
        int rows = 100_000;
        int iterations = 5;
        string label = "run";
#if SQL_BENCHMARK_BASELINE
        var cases = new HashSet<string>(["Q1", "Q2", "Q3", "Q6", "Q6C", "Q7", "Q8"], StringComparer.OrdinalIgnoreCase);
#else
        var cases = new HashSet<string>(["Q1", "Q2", "Q3", "Q4", "Q5L", "Q5T", "Q6", "Q6C", "Q7", "Q7R", "Q8", "Q8C"],
            StringComparer.OrdinalIgnoreCase);
#endif
        for (int index = 0; index < args.Length - 1; index++)
        {
            switch (args[index])
            {
                case "--rows":
                    rows = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--iterations":
                    iterations = int.Parse(args[++index], CultureInfo.InvariantCulture);
                    break;
                case "--label":
                    label = args[++index];
                    break;
                case "--cases":
                    cases = new HashSet<string>(args[++index].Split(','), StringComparer.OrdinalIgnoreCase);
                    break;
            }
        }

        Console.WriteLine($"label={label} rows={rows} iterations={iterations} aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported} " +
            $"gc={(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} os={Environment.OSVersion.VersionString} cpus={Environment.ProcessorCount}");

        var builder = SqlDatabaseEngine.CreateBuilder("sql-benchmark");
#if !SQL_BENCHMARK_BASELINE
        RegisterFunctions(builder.Functions);
#endif
        await using (var engine = await builder.BuildAsync())
        {
            var database = await engine.CreateDatabaseAsync("benchmark");
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            await LoadAsync(session, rows);

            foreach (var (name, sql) in new[] { ("Q1", Q1), ("Q2", Q2), ("Q3", Q3), ("Q4", Q4), ("Q5L", Q5L), ("Q5T", Q5T), ("Q6", Q6), ("Q6C", Q6C) })
            {
                if (cases.Contains(name))
                {
                    Report(name, "row", await MeasureAsync(iterations, () => QueryAsync(session, sql, rows)));
                }
            }

            foreach (var (name, check) in new[] { ("Q7", Q7Check), ("Q7R", Q7RCheck) })
            {
                if (cases.Contains(name))
                {
                    int table = 0;
                    Report(name, "statement", await MeasureAsync(iterations, () => InsertAsync(session, $"{name}_{table++}", check, rows)));
                }
            }
        }

        foreach (var (name, checks) in new[] { ("Q8", false), ("Q8C", true) })
        {
            if (cases.Contains(name))
            {
                Report(name, "build", await MeasureBuildsAsync(iterations, checks));
            }
        }

        return 0;
    }

#if !SQL_BENCHMARK_BASELINE
    /// <summary>
    /// The registered functions Q4, Q5, Q7R and Q8C call: <c>upper_r</c> as a typed
    /// <c>Create&lt;string, string&gt;</c> leaf and <c>upper_leaf</c> as a hand-written leaf doing the same
    /// work as the built-in <c>UPPER</c> over text, and <c>has_text</c> for the CHECKs.
    /// </summary>
    private static void RegisterFunctions(SqlFunctionCollection functions)
        => functions
            .Add(SqlScalarFunction.Create("upper_r", static (string text) => text.ToUpperInvariant(), SqlFunctionVolatility.Immutable))
            .Add(new UpperLeafFunction())
            .Add(SqlScalarFunction.Create("has_text", static (string text) => text.Length > 0, SqlFunctionVolatility.Immutable));

    /// <summary>Q5L's hand-written leaf: <c>upper_leaf(TEXT)</c>, the same work as <c>upper_r</c>.</summary>
    private sealed class UpperLeafFunction : SqlScalarFunction
    {
        public UpperLeafFunction()
            : base("upper_leaf", [SqlType.Text], SqlType.Text, SqlFunctionVolatility.Immutable)
        {
        }

        protected override SqlValue InvokeCore(scoped in SqlArguments arguments)
            => SqlValue.FromString(arguments.GetString(0).ToUpperInvariant());
    }
#endif

    /// <summary>
    /// Q8 and Q8C: an engine build over a file-backed root whose 50-table schema is already applied,
    /// so each build compiles the schema, creates the engine, opens and recovers the database, skips
    /// the applied schema and returns. The first build applies the schema and is not measured; the
    /// engine's disposal is outside the timed region.
    /// </summary>
    private static async Task<IReadOnlyList<Sample>> MeasureBuildsAsync(int iterations, bool checks)
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-sql-benchmark-q8", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var first = await Q8Schema.CreateBuilder(root, checks).BuildAsync())
            {
            }

            return await MeasureAsync(iterations, async () =>
            {
                SqlDatabaseEngine? engine = null;
                var (elapsed, cpu, threadBytes, processBytes, hopped) = await TimeAsync(async () =>
                {
                    engine = await Q8Schema.CreateBuilder(root, checks).BuildAsync();
                });

                await engine!.DisposeAsync();
                return new Sample(elapsed, cpu, threadBytes, processBytes, 1, hopped, Q8Schema.Tables);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a scratch directory under the temp folder.
            }
        }
    }

    /// <summary>Fills <c>t</c>: ids straddle zero, 1,000 groups, a decimal and a string per row.</summary>
    private static async Task LoadAsync(SqlDatabaseSession session, int rows)
    {
        await ExecuteAsync(session, "CREATE TABLE t (id BIGINT NOT NULL, name VARCHAR(32) NOT NULL, g INT NOT NULL, v DECIMAL(18, 2), s VARCHAR(16))");
        const int batch = 500;
        var text = new StringBuilder();
        for (int start = 0; start < rows; start += batch)
        {
            text.Clear().Append("INSERT INTO t (id, name, g, v, s) VALUES ");
            for (int i = start; i < Math.Min(rows, start + batch); i++)
            {
                if (i > start)
                {
                    text.Append(", ");
                }

                text.Append(CultureInfo.InvariantCulture,
                    $"({i - rows / 2}, 'name{i:D6}', {i % 1000}, {(i % 10_000) * 0.25m:0.00}, 's{(i * 7919L) % 10_007:D5}')");
            }

            await ExecuteAsync(session, text.ToString());
        }
    }

    private static async Task<Sample> QueryAsync(SqlDatabaseSession session, string sql, int rows)
    {
        long count = 0;
        var (elapsed, cpu, threadBytes, processBytes, hopped) = await TimeAsync(async () =>
        {
            await using var result = (QueryResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                for (int field = 0; field < row.FieldCount; field++)
                {
                    _ = row.GetValue(field);
                }

                count++;
            }
        });

        if (count == 0)
        {
            throw new InvalidOperationException($"'{sql}' returned no rows.");
        }

        // Q1 to Q3 are reported per row of t, the rows each statement reads.
        return new Sample(elapsed, cpu, threadBytes, processBytes, rows, hopped, count);
    }

    /// <summary>
    /// Q7 and Q7R: one autocommitted INSERT per row into a fresh table whose CHECK calls the built-in
    /// LENGTH and ABS (Q7), or the registered immutable <c>has_text</c> and ABS (Q7R).
    /// </summary>
    private static async Task<Sample> InsertAsync(SqlDatabaseSession session, string table, string check, int rows)
    {
        await ExecuteAsync(session,
            $"CREATE TABLE {table} (id BIGINT NOT NULL, name VARCHAR(32) NOT NULL, v BIGINT, " +
            $"CONSTRAINT ck_{table} CHECK ({check}))");

        // Every value is created before the clock starts, so the harness allocates nothing per statement.
        string sql = string.Format(CultureInfo.InvariantCulture, Q7Insert, table);
        var ids = new object[rows];
        var names = new object[rows];
        var values = new object[rows];
        for (int i = 0; i < rows; i++)
        {
            ids[i] = (long)i;
            names[i] = $"name{i:D6}";
            values[i] = (long)(i - rows / 2);
        }

        var parameters = new Dictionary<string, object?> { ["id"] = null, ["name"] = null, ["v"] = null };
        var (elapsed, cpu, threadBytes, processBytes, hopped) = await TimeAsync(async () =>
        {
            for (int i = 0; i < rows; i++)
            {
                parameters["id"] = ids[i];
                parameters["name"] = names[i];
                parameters["v"] = values[i];
                var result = await session.ExecuteAsync(sql, parameters, CancellationToken.None);
                if (result.Status != QueryResultStatus.Success)
                {
                    throw new InvalidOperationException($"INSERT {i} failed.");
                }
            }
        });

        await ExecuteAsync(session, $"DROP TABLE {table}");
        return new Sample(elapsed, cpu, threadBytes, processBytes, rows, hopped, rows);
    }

    private static async Task<(TimeSpan Elapsed, TimeSpan Cpu, long ThreadBytes, long ProcessBytes, bool Hopped)> TimeAsync(Func<Task> work)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        using var process = Process.GetCurrentProcess();
        int thread = Environment.CurrentManagedThreadId;
        long processBefore = GC.GetTotalAllocatedBytes(precise: true);
        long threadBefore = GC.GetAllocatedBytesForCurrentThread();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var pauseBefore = GC.GetTotalPauseDuration();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long started = Stopwatch.GetTimestamp();
        await work();
        long ended = Stopwatch.GetTimestamp();
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        LastCollections = $"{GC.CollectionCount(0) - gen0}/{GC.CollectionCount(1) - gen1}/{GC.CollectionCount(2) - gen2}" +
            $"@{(GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds:F0}ms";
        long threadAfter = GC.GetAllocatedBytesForCurrentThread();
        long processAfter = GC.GetTotalAllocatedBytes(precise: true);

        // An await that completed asynchronously moves the rest of the work to another thread,
        // whose allocations the thread counter would miss; the report flags such an iteration.
        // Processor time is reported beside wall time: on a shared machine it leaves out the time
        // the process waited for a core, which is most of the run-to-run variance.
        bool hopped = Environment.CurrentManagedThreadId != thread;
        return (Stopwatch.GetElapsedTime(started, ended), cpuAfter - cpuBefore, threadAfter - threadBefore, processAfter - processBefore, hopped);
    }

    /// <summary>The collections (gen0/gen1/gen2) and total GC pause of the last measured iteration.</summary>
    private static string LastCollections = string.Empty;

    private static async Task<IReadOnlyList<Sample>> MeasureAsync(int iterations, Func<Task<Sample>> run)
    {
        await run(); // warm-up, not reported
        var samples = new List<Sample>(iterations);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            var sample = await run();
            samples.Add(sample with { Collections = LastCollections });
        }

        return samples;
    }

    private static void Report(string name, string unit, IReadOnlyList<Sample> samples)
    {
        var nanoseconds = samples.Select(sample => sample.NanosecondsPerUnit).ToArray();
        var cpu = samples.Select(sample => sample.CpuNanosecondsPerUnit).ToArray();
        var threadBytes = samples.Select(sample => sample.ThreadBytesPerUnit).ToArray();
        var processBytes = samples.Select(sample => sample.ProcessBytesPerUnit).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name} unit={unit} units={samples[0].Units} output_rows={samples[0].OutputRows} " +
            $"median_ns={Median(nanoseconds):F1} best_ns={nanoseconds.Min():F1} " +
            $"median_cpu_ns={Median(cpu):F1} best_cpu_ns={cpu.Min():F1} " +
            $"median_bytes={Median(threadBytes):F1} min_bytes={threadBytes.Min():F1} " +
            $"median_process_bytes={Median(processBytes):F1} hopped={samples.Count(sample => sample.Hopped)} " +
            $"ns=[{Join(nanoseconds)}] cpu_ns=[{Join(cpu)}] bytes=[{Join(threadBytes)}] " +
            $"gc=[{string.Join(", ", samples.Select(sample => sample.Collections))}]"));

        static string Join(double[] values) => string.Join(", ", values.Select(value => value.ToString("F1", CultureInfo.InvariantCulture)));
    }

    private static double Median(double[] values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        if (result is QueryResultSet set)
        {
            await set.DisposeAsync();
        }
        else if (result.Status != QueryResultStatus.Success)
        {
            throw new InvalidOperationException($"'{sql}' failed: {string.Join("; ", result.Diagnostics?.Select(diagnostic => diagnostic.Message) ?? [])}");
        }
    }

    /// <summary>One measured iteration.</summary>
    private readonly record struct Sample(TimeSpan Elapsed, TimeSpan Cpu, long ThreadBytes, long ProcessBytes, int Units, bool Hopped, long OutputRows)
    {
        public string Collections { get; init; } = string.Empty;

        public double NanosecondsPerUnit => Elapsed.Ticks * 100d / Units;

        public double CpuNanosecondsPerUnit => Cpu.Ticks * 100d / Units;

        public double ThreadBytesPerUnit => (double)ThreadBytes / Units;

        public double ProcessBytesPerUnit => (double)ProcessBytes / Units;
    }
}
