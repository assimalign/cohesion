using System;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>An externally supplied mapping action.</summary>
public sealed class ExecutionCallback : IMapperAction
{
    private readonly Action<IMapperContext> _callback;

    public ExecutionCallback(Action<IMapperContext> callback)
    {
        _callback = callback;
    }

    public void Invoke(IMapperContext context) => _callback(context);
}
