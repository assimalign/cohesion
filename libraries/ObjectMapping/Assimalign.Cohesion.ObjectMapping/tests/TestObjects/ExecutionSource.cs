using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>A source graph used to check invocation and collection isolation.</summary>
public sealed class ExecutionSource
{
    public int Value { get; set; }
    public ExecutionSource? Child { get; set; }
    public List<ExecutionSource> Items { get; set; } = new List<ExecutionSource>();
}
