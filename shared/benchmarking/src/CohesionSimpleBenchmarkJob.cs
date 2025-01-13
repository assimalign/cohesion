
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;


namespace BenchmarkDotNet.Cohesion;


[SimpleJob(RuntimeMoniker.Net60)]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
[SimpleJob(RuntimeMoniker.NativeAot80)]
[SimpleJob(RuntimeMoniker.NativeAot90)]
public abstract class CohesionSimpleBenchmarkJob
{
    public static Type[] GetBenchmarkTypes()
    {
        var assembly = Assembly.GetEntryAssembly();
        var types = assembly.GetTypes();
        var benchmarks = types.Where(type => type.IsAssignableTo(typeof(CohesionSimpleBenchmarkJob)));

        return [.. benchmarks];
    }
}
