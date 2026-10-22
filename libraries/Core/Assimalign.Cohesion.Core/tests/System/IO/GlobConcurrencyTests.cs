using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace System.IO.Tests;

using static Glob;

/// <summary>
/// Pins <see cref="Glob.Parse(string)"/> as safe to call from several threads at once. Every parse
/// used to share one tokenizer buffer, so two parses at once could splice their text together:
/// <c>/file[0-9].txt</c> came back as a range that matched <c>/filea.txt</c> (PR #1168, run
/// 38792058025). Each parse now owns its tokenizer state, and the process-wide pattern cache takes
/// concurrent lookups, additions and clears.
/// </summary>
public class GlobConcurrencyTests
{
    // Parses that began while another parse was in flight. The test runs until it has seen this
    // many instead of for a fixed time, so a slow or oversubscribed machine still exercises the race.
    private const long requiredOverlaps = 2_000;

    // Bounds a run that never reaches requiredOverlaps. Reaching it fails the test rather than
    // passing without having exercised the race.
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);

    [Fact(DisplayName = "Cohesion Test [Core] - Glob.Parse: Should keep patterns parsed on several threads at once apart")]
    public async Task Parse_WhenThreadsParseDistinctPatternsAtOnce_ShouldMatchAndRoundTripEachPattern()
    {
        // Arrange
        string runId = Guid.NewGuid().ToString("N");
        int workerCount = Math.Clamp(Environment.ProcessorCount, 4, 8);
        var failures = new ConcurrentQueue<string>();
        var elapsed = Stopwatch.StartNew();
        using var start = new Barrier(workerCount);
        long nextCase = -1;
        long overlaps = 0;
        int inFlight = 0;
        bool stop = false;

        void Work()
        {
            try
            {
                if (!start.SignalAndWait(_timeout))
                {
                    failures.Enqueue("The workers never all started.");
                    return;
                }

                while (!Volatile.Read(ref stop)
                    && failures.IsEmpty
                    && Interlocked.Read(ref overlaps) < requiredOverlaps
                    && elapsed.Elapsed < _timeout)
                {
                    // A pattern nothing has parsed yet: a cache miss that tokenizes while the other
                    // workers tokenize theirs.
                    GlobCase fresh = CreateCase(runId, Interlocked.Increment(ref nextCase));

                    if (Interlocked.Increment(ref inFlight) > 1)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    Glob glob;

                    try
                    {
                        glob = Glob.Parse(fresh.Pattern);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref inFlight);
                    }

                    Verify(fresh, glob, failures);

                    // A pattern parsed earlier: usually a cache hit, sometimes a writer racing the
                    // first one, or a parse after the cache was cleared.
                    GlobCase earlier = CreateCase(runId, fresh.Number / 2);

                    Verify(earlier, Glob.Parse(earlier.Pattern), failures);
                }
            }
            finally
            {
                // A worker that fails or throws stops the others.
                Volatile.Write(ref stop, true);
            }
        }

        // Act
        var workers = new Task[workerCount];

        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        await Task.WhenAll(workers);

        // Assert
        failures.ShouldBeEmpty();
        Interlocked.Read(ref overlaps).ShouldBeGreaterThanOrEqualTo(
            requiredOverlaps,
            $"Only {Interlocked.Read(ref overlaps)} parses overlapped another within {_timeout}, so the race was not exercised.");
    }

    [Fact(DisplayName = "Cohesion Test [Core] - Glob.Parse: Should allocate only the Glob for a cached pattern")]
    public void Parse_WhenPatternIsCached_ShouldAllocateOnlyTheGlob()
    {
        // Arrange
        string pattern = $"/d{Guid.NewGuid():N}/**/file[0-9].{{cs,txt}}";
        var tokens = Parser.Tokenize(pattern);
        long globBytes = MeasureAllocations(() => new Glob(tokens));

        // Act: the cache is process-wide and another test's parses can clear it between the warm-up
        // and the measurement, so keep the best of a few attempts. A hit path that allocates more
        // than the Glob fails every attempt.
        long parseBytes = long.MaxValue;

        for (int attempt = 0; attempt < 5 && parseBytes > globBytes; attempt++)
        {
            parseBytes = Math.Min(parseBytes, MeasureAllocations(() => Glob.Parse(pattern)));
        }

        // Assert
        parseBytes.ShouldBe(globBytes);
    }

    private static long MeasureAllocations(Func<Glob> create)
    {
        // Warms the JIT and, for Glob.Parse, the cache.
        create();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Glob glob = create();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        GC.KeepAlive(glob);

        return allocated;
    }

    private static GlobCase CreateCase(string runId, long number)
    {
        // The run and case number make every directory, and so every pattern, unique.
        string directory = $"/d{runId}{number}";

        return (number % 7) switch
        {
            0 => new GlobCase(number, $"{directory}/file[0-9].txt", [$"{directory}/file5.txt"], [$"{directory}/filea.txt", $"{directory}/file55.txt"]),
            1 => new GlobCase(number, $"{directory}/log[!a-c].txt", [$"{directory}/logd.txt"], [$"{directory}/logb.txt"]),
            2 => new GlobCase(number, $"{directory}/item[xyz].cs", [$"{directory}/itemy.cs"], [$"{directory}/itemw.cs"]),
            3 => new GlobCase(number, $"{directory}/app.{{json,yaml}}", [$"{directory}/app.json", $"{directory}/app.yaml"], [$"{directory}/app.xml"]),
            4 => new GlobCase(number, $"{directory}/*.md", [$"{directory}/readme.md"], [$"{directory}/docs/readme.md", $"{directory}/readme.txt"]),
            5 => new GlobCase(number, $"{directory}/**/*.cs", [$"{directory}/src/app/Program.cs"], [$"{directory}/src/app/Program.txt"]),
            _ => new GlobCase(number, $"{directory}/v?.dll", [$"{directory}/v1.dll"], [$"{directory}/v12.dll"]),
        };
    }

    private static void Verify(GlobCase globCase, Glob glob, ConcurrentQueue<string> failures)
    {
        string text = glob.ToString();

        if (text != globCase.Pattern)
        {
            failures.Enqueue($"'{globCase.Pattern}' round-tripped as '{text}'.");
        }

        foreach (string path in globCase.Matches)
        {
            if (!glob.IsMatch(path))
            {
                failures.Enqueue($"'{globCase.Pattern}' did not match '{path}'.");
            }
        }

        foreach (string path in globCase.NonMatches)
        {
            if (glob.IsMatch(path))
            {
                failures.Enqueue($"'{globCase.Pattern}' matched '{path}'.");
            }
        }
    }

    private sealed record GlobCase(long Number, string Pattern, string[] Matches, string[] NonMatches);
}
