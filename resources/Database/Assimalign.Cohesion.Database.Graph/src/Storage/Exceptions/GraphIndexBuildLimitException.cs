using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>An index build exceeded a supplied memory or work grant.</summary>
/// <remarks>Session builds use configured grants. Direct store builds retain caller-owned accounting.</remarks>
public sealed class GraphIndexBuildLimitException : StorageException
{
    internal GraphIndexBuildLimitException(string code, string message) : base(message) { Code = code; }

    /// <summary>Gets CG05020 for retained memory or CG05004 for work admission.</summary>
    public string Code { get; }
}
