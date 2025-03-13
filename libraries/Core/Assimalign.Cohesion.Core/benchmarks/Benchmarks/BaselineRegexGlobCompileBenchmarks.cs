using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using System.IO;

namespace Assimalign.Cohesion.Benchmarks;


[SimpleJob(RuntimeMoniker.Net90)]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net60)]
[MemoryDiagnoser, MinColumn, MaxColumn]
public class BaselineRegexGlobCompileBenchmarks
{
    private string _pattern;
    private string _regexString;


    [Params(
        "p?th/a[e-g].txt",
        "p?th/a[bcd]b[e-g].txt",
        "p?th/a[bcd]b[e-g]a[1-4][!wxyz][!a-c][!1-3].txt")]
    public string Pattern
    {
        get => _pattern;
        set
        {
            _pattern = value;
            var tokens = Glob.Parse(_pattern).ToString();
        }
    }

    [Benchmark(Baseline = true)]
    public Regex New_Compiled_Regex_Glob()
    {
        var result = new Regex(_regexString, RegexOptions.Compiled | RegexOptions.Singleline);
        return result;
    }

    [Benchmark()]
    public Glob New_DotNet_Glob()
    {
        var result = Glob.Parse(Pattern);
        return result;
    }

    //[Benchmark()]
    //public global::Glob.Glob New_Glob()
    //{
    //    var result = new global::Glob.Glob(Pattern);
    //    return result;

    //}


}
