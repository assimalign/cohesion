using Assimalign.Cohesion.Benchmarks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using System.Reflection;



var summary = BenchmarkRunner.Run<FileSystemPathBenchmarks>();