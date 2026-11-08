using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>A direct graph-store index build exceeded its memory or work grant before allocating its root.</summary>
/// <remarks>Session builds use their configured grants. Direct store builds allow 256 MiB and 1,000,000 work units.</remarks>
public sealed class GraphIndexBuildLimitException : StorageException
{
    internal GraphIndexBuildLimitException(string code, string message) : base(message) { Code = code; }

    /// <summary>Gets CG05020 for retained memory or CG05004 for work admission.</summary>
    public string Code { get; }
}
