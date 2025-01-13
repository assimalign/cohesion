using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Benchmarks;


[SimpleJob(RuntimeMoniker.Net90)]
public class KeySegmentParsingStrategy
{
    private string value = "myKey4$label2[34]";

    [Benchmark]
    //[Arguments("myKey1")]
    //[Arguments("myKey2[34]")]
    //[Arguments("myKey3$label1")]
    //[Arguments("myKey4$label2[34]")]
    public void Strategy1()
    {
        Key.Parse(value);
    }

    [Benchmark]
    //[Arguments("myKey1")]
    //[Arguments("myKey2[34]")]
    //[Arguments("myKey3$label1")]
   // [Arguments("myKey4$label2[34]")]
    public void Strategy2()
    {
        int? Index;
        string? Label;
        string Value;

        if (string.IsNullOrEmpty(value))
        {
            throw new Exception();
        }
        if (value.ContainsAny(Key.Delimiters))
        {
            throw new Exception();
        }

        var name = new char[value.Length];

        for (int i = 0; i < value.Length; i++)
        {
            // Parse Index
            if (value[i].Equals('['))
            {
                Array.Resize(ref name, i);

                var num = new char[value.Length - 2 - i];

                for (; value[i] != ']'; i++)
                {
                    if (value.Length == i)
                    {
                        throw new Exception();
                    }
                }
                if (!int.TryParse(num, out var index))
                {
                    // TODO: Invalid index
                }
                Index = index;
            }
            // Parse Label
            else if (value[i].Equals('$'))
            {
                Array.Resize(ref name, i);

                Label = value.Substring(i + 1);
                break;
            }
            else
            {
                name[i] = value[i];
            }
        }

        Value = new string(name);
    }
}
