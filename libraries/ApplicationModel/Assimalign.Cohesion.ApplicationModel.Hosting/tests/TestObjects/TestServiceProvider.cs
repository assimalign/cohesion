using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Hosting.Tests;

/// <summary>
/// The application's services, as an added action's factory receives them: a map of service types to
/// instances.
/// </summary>
internal sealed class TestServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = [];

    public TestServiceProvider Add<TService>(TService service)
        where TService : notnull
    {
        _services[typeof(TService)] = service;
        return this;
    }

    public object? GetService(Type serviceType)
    {
        return _services.TryGetValue(serviceType, out object? service) ? service : null;
    }
}
