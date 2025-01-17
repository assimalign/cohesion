using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Diagnostics.Tracing.Parsers;
using Nelibur.ObjectMapper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Am = AutoMapper;

namespace Assimalign.Cohesion.ObjectMapping.Benchmarks;



[SimpleJob(RuntimeMoniker.Net90)]
public class SimpleObjectToObjectMapping
{
    private Am.IMapper autoMapper;
    private ObjectMapping.IMapper cohesion;
    private Obj1 object1;


    [GlobalSetup]
    public void Setup()
    {
        SetupCohesion();
        SetupAutoMapper();
        SetupTinyMapper();

        object1 = new()
        {
            Str = "TestString1"
        };
    }

    void SetupCohesion()
    {
        var factory = MapperFactory.Configure(factory =>
        {
            factory.AddMapper("default", builder =>
            {
                builder.CreateProfile<Obj2, Obj1>(descriptor =>
                {
                    descriptor.MapMember(target => target.Str, source => source.Str);
                    descriptor.MapMember(target => target.Num1, source => source.Num1);
                    descriptor.MapMember(target => target.Num2, source => source.Num2);
                    descriptor.MapMember(target => target.Num3, source => source.Num3);
                });
            });
        });

        cohesion = factory.CreateMapper("default");
    }
    void SetupAutoMapper()
    {
        var config = new Am.MapperConfiguration(config =>
        {
            config.CreateMap<Obj1, Obj2>();
        });

        autoMapper = config.CreateMapper();
    }

    void SetupTinyMapper()
    {
        Nelibur.ObjectMapper.TinyMapper.Bind<Obj1, Obj2>();
    }



    [Benchmark]
    public Obj2 Cohesion() => cohesion.Map<Obj2>(object1);
    [Benchmark]
    public Obj2 AutoMapper() => autoMapper.Map<Obj2>(object1);
    [Benchmark]
    public Obj2 TinyMapper() => Nelibur.ObjectMapper.TinyMapper.Map<Obj2>(object1);


    public partial class Obj1
    {
        public string? Str { get; set; }
        public short? Num1 { get; set; }
        public int? Num2 { get; set; }
        public long? Num3 { get; set; }
    }

    public partial class Obj2
    {
        public string? Str { get; set; }
        public short? Num1 { get; set; }
        public int? Num2 { get; set; }
        public long? Num3 { get; set; }
    }
}
