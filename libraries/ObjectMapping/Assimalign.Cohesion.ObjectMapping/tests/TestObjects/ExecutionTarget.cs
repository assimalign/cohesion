using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>A target graph used to check invocation and collection isolation.</summary>
public sealed class ExecutionTarget
{
    public int Value { get; set; }
    public ExecutionTarget? Child { get; set; }
    public List<ExecutionTarget> Items { get; set; } = new List<ExecutionTarget>();
}
