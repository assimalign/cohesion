using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Hosting.Tests;

internal class TestServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        Func<CancellationToken, Task> func = cancellationToken =>
        {
            return Task.CompletedTask;
        };
        if (serviceType == func.GetType())
        {
            return func;
        }
        throw new InvalidOperationException();
    }
}
