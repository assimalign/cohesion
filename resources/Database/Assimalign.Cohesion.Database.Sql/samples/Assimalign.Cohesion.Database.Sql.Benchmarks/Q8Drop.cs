using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Benchmarks;

/// <summary>Decision 77: full Build and its catalog DROP phase, empty versus populated.</summary>
internal static class Q8Drop
{
    internal static async Task RunAsync(int rows, int iterations)
    {
        string root = Path.Combine(Environment.CurrentDirectory, "_out", "q8-drop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (int count in new[] { 0, rows })
            {
                string seed = Path.Combine(root, "seed" + count);
                var original = SqlSchema.Create("q8d", schema => schema.Table<Item>("retired", table =>
                {
                    table.Column(row => row.Id);
                    table.Column(row => row.Payload);
                }));
                var seedBuilder = SqlDatabaseEngine.CreateBuilder("q8d-seed");
                seedBuilder.Options.RootPath = seed;
                seedBuilder.AddDatabase(original);
                await using (var engine = await seedBuilder.BuildAsync())
                {
                    var database = await engine.OpenDatabaseAsync("q8d");
                    await using var session = await database.CreateSessionAsync();
                    var values = new Dictionary<string, object?> { ["payload"] = new string('x', 1800) };
                    for (int offset = 0; offset < count; offset += 1000)
                    {
                        await using var transaction = await session.BeginTransactionAsync();
                        for (int row = offset; row < Math.Min(count, offset + 1000); row++)
                        {
                            values["id"] = row;
                            await session.ExecuteAsync("INSERT INTO retired VALUES (@id, @payload)", values);
                        }
                        await transaction.CommitAsync();
                    }
                }

                long bytes = new FileInfo(Path.Combine(seed, "q8d", "sql.dat")).Length;
                var builds = new List<double>();
                var drops = new List<double>();
                // One warm-up, then the requested samples. Each starts with the same clean image.
                for (int iteration = 0; iteration <= iterations; iteration++)
                {
                    string copy = Path.Combine(root, "copy");
                    foreach (string file in Directory.EnumerateFiles(seed, "*", SearchOption.AllDirectories))
                    {
                        string destination = Path.Combine(copy, Path.GetRelativePath(seed, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(file, destination);
                    }
                    var builder = SqlDatabaseEngine.CreateBuilder("q8d-drop");
                    builder.Options.RootPath = copy;
                    builder.AddDatabase(SqlSchema.Create("q8d", schema => schema.AllowDestructiveChanges()));
                    using var trace = new DropTrace();
                    long started = Stopwatch.GetTimestamp();
                    await using (var engine = await builder.BuildAsync())
                    {
                        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        if (double.IsNaN(trace.Milliseconds)) { throw new InvalidOperationException("Q8D requires provisioning EventSource support."); }
                        if (iteration > 0)
                        {
                            builds.Add(elapsed);
                            drops.Add(trace.Milliseconds);
                            Console.WriteLine(FormattableString.Invariant($"Q8D rows={count} iteration={iteration} build_ms={elapsed:F6} drop_ms={trace.Milliseconds:F6} data_bytes={bytes}"));
                        }
                    }
                    Directory.Delete(copy, recursive: true);
                }
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Q8D rows={count} build_median_ms={Median(builds):F6} build_best_ms={builds.Min():F6} drop_median_ms={Median(drops):F6} drop_best_ms={drops.Min():F6} data_bytes={bytes}"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    private sealed record Item(int Id, string Payload);

    private sealed class DropTrace : EventListener
    {
        private double _milliseconds = double.NaN;
        internal double Milliseconds => Volatile.Read(ref _milliseconds);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Assimalign.Cohesion.Database.Sql") { EnableEvents(eventSource, EventLevel.Informational); }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventId == 14 && (string)eventData.Payload![0]! == "q8d-drop")
            {
                Volatile.Write(ref _milliseconds, (double)eventData.Payload[6]!);
            }
        }
    }
}
