using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.FileSystem;

public sealed class FileSystemGlob
{
    public FileSystemGlob(string value)
    {
        Value = value;
    }

    public string Value { get; } 


    public IEnumerable<Glob> GenerateGenericGlobs()
    {
        var span = Value.AsSpan();


        return default;
    }
}
