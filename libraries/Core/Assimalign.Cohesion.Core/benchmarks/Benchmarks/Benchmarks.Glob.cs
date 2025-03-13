using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;

namespace Assimalign.Cohesion.Benchmarks;

using Utilities;

//[SimpleJob(RuntimeMoniker.Net90)]
//[SimpleJob(RuntimeMoniker.Net80)]
//[SimpleJob(RuntimeMoniker.Net60)]
[MemoryDiagnoser, MarkdownExporter, MinColumn, MaxColumn]
public class GlobBenchmarks
{

    private Glob _glob;

    private List<string> _matches = new List<string>();
    private List<string> _noneMatches = new List<string>();

    [GlobalSetup]
    public void SetupData()
    {
        _glob = Glob.Parse(GlobPattern!);

        var generator = new RandomGlobGenerator(_glob);

        for (int i = 0; i < 10000; i++)
        {
            _matches.Add(generator.GetRandomMatch());
            _noneMatches.Add(generator.GetRandomNoneMatch());
        }
    }

    [Params(1, 10, 100, 200, 500, 1000, 10000)]
    public int NumberOfMatches { get; set; }

    [Params("p?th/a[e-g].txt",
            "p?th/a[bcd]b[e-g].txt",
            "p?th/a[bcd]b[e-g]a[1-4][!wxyz][!a-c][!1-3].txt")]
    public string? GlobPattern { get; set; }

    [Benchmark]
    public bool IsMatchTrue()
    {
        // we collect all results in a list and return it to prevent dead code elimination (optimisation)
        var result = false;
        for (int i = 0; i < NumberOfMatches; i++)
        {
            var testString = _matches[i];
            result ^= _glob.IsMatch(testString);
        }
        return result;
    }

    [Benchmark]
    public bool IsMatchFalse()
    {
        var result = false;
        for (int i = 0; i < NumberOfMatches; i++)
        {
            var testString = _noneMatches[i];
            result ^= _glob.IsMatch(testString);
        }
        return result;
    }

}
