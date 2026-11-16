using System;
using System.IO;

namespace Assimalign.Cohesion.Database.Sql.Tests;

// Gate-only engine setup, outside every allocation sample. No product environment switch.
internal static class SqlAllocationTemp
{
    private static readonly string? _root = Environment.GetEnvironmentVariable("COHESION_SQL_ALLOCATION_TEMP");
    internal static FileSystemPath? Directory
    {
        get
        {
            if (_root is null) { return default; }
            return (FileSystemPath)Path.Combine(_root, Guid.NewGuid().ToString("N"));
        }
    }
}
