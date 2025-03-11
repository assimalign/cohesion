using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Benchmarks;


[SimpleJob(RuntimeMoniker.Net90)]
public class FileSystemPathBenchmarks
{
    private static char[] _invalidChars = [.. System.IO.Path.GetInvalidPathChars(), '*', '?', '!', '<', '>', '^'];
    private string _value = "//test/users\\aas/fasdf\\myfile.txt";


    public FileSystemPath Path { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Path = _value;
    }

    //[Benchmark]
    //public FileSystemPath Parsing()
    //{
    //    return FileSystemPath.Parse(_value);
    //}

    [Benchmark]
    public string[] GetSegments()
    {
        return Path.GetSegments();
    }

    //[Benchmark]
    //public string Strategy1()
    //{
    //    char[] separators = ['/', '\\'];

    //    // imidiatly check 
    //    if (path.Length == 1 && separators.Contains(path[0]))
    //    {
    //        return "/";
    //    }

    //    int start = 0;
    //    int end = path.Length - 1;
    //    int shift = 1; // To maintain leadin slash

    //    // Check for current directory syntax "./" and skip over
    //    if (path.Length >= 2 && path[0] == '.' && separators.Contains(path[1]))
    //    {
    //        start =+ 2;
    //    }

    //    // Check for leading slash root  '//' or '\\', or if your a werido '/\' '\/'
    //    if (path.Length >= 2 && separators.Contains(path[0]) && separators.Contains(path[1]))
    //    {
    //        shift++;
    //    }

    //    // Check if path has valid drive, if so diregard shift
    //    if (HasDrive(path))
    //    {
    //        shift = 0;
    //    }

    //    // Calculate start of string
    //    for (; start < path.Length; start++)
    //    {
    //        int index = 0;
    //        char c = path[start];
    //        while (index < separators.Length && separators[index] != c)
    //        {
    //            index++;
    //        }
    //        if (index == separators.Length) break;
    //    }

    //    // Calculate end of string
    //    for (; end >= start; end--)
    //    {
    //        int index = 0;
    //        char c = path[end];
    //        while (index < separators.Length && separators[index] != c)
    //        {
    //            index++;
    //        }
    //        if (index == separators.Length) break;
    //    }


    //    var length = (end + 1) - start;

    //    int resize = 0;

    //    string error = string.Empty;

    //    var value = string.Create(((end + 1) - start) + shift, path, (span, value) =>
    //    {
    //        for (int i = 0; i < shift; i++)
    //        {
    //            span[i] = '/';
    //        }

    //        char previous = default;

    //        // Let's convert all backward slashes to forward slashes
    //        for (int i = start; i < (end + 1); i++)
    //        {
    //            var current = value[i];

    //            // Convert back slash to forward slash
    //            if (current == '\\')
    //            {
    //                current = '/';
    //            }

    //            // Check for excessive slashes
    //            if (previous == '/' && current == '/')
    //            {
    //                resize++;
    //                continue;
    //            }

    //            // Check for parent directory globbing ".."
    //            if (previous == '.' && current == '.')
    //            {
    //                // scenario 1: ".." was only passed
    //                // scenario 2: "{directory}/../{directory}"
    //                // scenario 3: "../{directory}"
    //                // scenario 4: "/{directory}/.."

    //                var s = i - 2;
    //                var e = i + 1;

    //                var hasStart = (s > 0 && separators.Contains(value[s])) || s < 0;
    //                var hasEnd = (e < end && separators.Contains(value[e])) || e > end;

    //                if ((s < 0 && e > end) || (hasStart && hasEnd))
    //                {
    //                    error = "Parent directory globbing is not allowed - \"..\". The value must be an absolute or relative path.";
    //                    break;
    //                }
    //            }
    //            if (_invalidChars.Contains(current))
    //            {
    //                error = $"Path contains illegal character '{current}' at index {i}.";
    //                break;
    //            }

    //            previous = current;

    //            span[(i + shift) - start - resize] = current;
    //        }
    //    });

    //    if (error.Length > 0)
    //    {
    //        throw new ArgumentException(error);
    //    }

    //    return value.Remove(value.Length - resize);
    //}

    //[Benchmark]
    //public string Strategy2()
    //{
    //    char[] separators = ['/', '\\'];

    //    // imidiatly check 
    //    if (path.Length == 1 && separators.Contains(path[0]))
    //    {
    //        return "/";
    //    }

    //    int start = 0;
    //    int end = path.Length - 1;
    //    int shift = 1; // To maintain leadin slash

    //    // Check for current directory syntax "./" and skip over
    //    if (path.Length >= 2 && path[0] == '.' && separators.Contains(path[1]))
    //    {
    //        start =+ 2;
    //    }

    //    // Check for leading slash root  '//' or '\\', or if your a werido '/\' '\/'
    //    if (path.Length >= 2 && separators.Contains(path[0]) && separators.Contains(path[1]))
    //    {
    //        shift++;
    //    }

    //    // Check if path has valid drive, if so diregard shift
    //    if (HasDrive(path))
    //    {
    //        shift = 0;
    //    }

    //    // Calculate start of string
    //    for (; start < path.Length; start++)
    //    {
    //        int index = 0;
    //        char c = path[start];
    //        while (index < separators.Length && separators[index] != c)
    //        {
    //            index++;
    //        }
    //        if (index == separators.Length) break;
    //    }

    //    // Calculate end of string
    //    for (; end >= start; end--)
    //    {
    //        int index = 0;
    //        char c = path[end];
    //        while (index < separators.Length && separators[index] != c)
    //        {
    //            index++;
    //        }
    //        if (index == separators.Length) break;
    //    }


    //    int length = (end + 1) - start + shift;
    //    char previous = default;

    //    // Calculate length
    //    for (int i = start; i < (end + 1); i++)
    //    {
    //        var current = path[i];

    //        // Convert back slash to forward slash
    //        if (current == '\\') current = '/';

    //        // Check for excessive slashes
    //        if (previous == '/' && current == '/') length--;

    //        previous = current;
    //    }

    //    string error = string.Empty;

    //    var value = string.Create(length, path, (span, value) =>
    //    {
    //        for (int i = 0; i < shift; i++)
    //        {
    //            span[i] = '/';
    //        }

    //        int reduce = 0;
    //        char previous = default;

    //        // Let's convert all backward slashes to forward slashes
    //        for (int i = start; i < (end + 1); i++)
    //        {
    //            var current = value[i];

    //            // Convert back slash to forward slash
    //            if (current == '\\')
    //            {
    //                current = '/';
    //            }

    //            // Check for excessive slashes
    //            if (previous == '/' && current == '/')
    //            {
    //                reduce++;
    //                continue;
    //            }

    //            // Check for parent directory globbing ".."
    //            if (previous == '.' && current == '.')
    //            {
    //                // scenario 1: ".." was only passed
    //                // scenario 2: "{directory}/../{directory}"
    //                // scenario 3: "../{directory}"
    //                // scenario 4: "/{directory}/.."

    //                var s = i - 2;
    //                var e = i + 1;

    //                var hasStart = (s > 0 && separators.Contains(value[s])) || s < 0;
    //                var hasEnd = (e < end && separators.Contains(value[e])) || e > end;

    //                if ((s < 0 && e > end) || (hasStart && hasEnd))
    //                {
    //                    error = "Parent directory globbing is not allowed - \"..\". The value must be an absolute or relative path.";
    //                    break;
    //                }
    //            }
    //            if (_invalidChars.Contains(current))
    //            {
    //                error = $"Path contains illegal character '{current}' at index {i}.";
    //                break;
    //            }

    //            previous = current;

    //            var index = (i + shift) - start - reduce;

    //            span[index] = current;
    //        }
    //    });

    //    if (error.Length > 0)
    //    {
    //        throw new ArgumentException(error);
    //    }

    //    return value;

    //}


    //private static bool HasDrive(string value)
    //{
    //    if (value.Length >= 2)
    //    {
    //        if ((uint)((value[0] | 0x20) - 97) <= 25u && value[1] == ':')
    //        {
    //            return true;
    //        }
    //    }

    //    return false;
    //}
}
