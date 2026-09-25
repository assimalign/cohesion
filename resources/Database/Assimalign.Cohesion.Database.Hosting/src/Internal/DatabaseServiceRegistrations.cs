using System;
using System.Collections;
using System.Collections.Generic;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.DependencyInjection;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

internal sealed class DatabaseServiceRegistrations : IServiceProviderBuilder, IServiceContainer
{
    private readonly List<ServiceDescriptor> _descriptors = [];
    private readonly Action _ensureMutable;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseServiceRegistrations"/> class.
    /// </summary>
    /// <param name="ensureMutable">The callback that throws when the owning builder no longer accepts registrations.</param>
    public DatabaseServiceRegistrations(Action ensureMutable)
    {
        _ensureMutable = ensureMutable;
    }

    public IServiceContainer Container => this;
    public int Count => _descriptors.Count;
    public ServiceDescriptor this[int index] => _descriptors[index];

    public IServiceProviderBuilder Add(ServiceDescriptor serviceDescriptor)
    {
        Register(serviceDescriptor);
        return this;
    }

    public void Register(ServiceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        _ensureMutable();
        _descriptors.Add(descriptor);
    }

    public bool Unregister(ServiceDescriptor descriptor) { _ensureMutable(); return _descriptors.Remove(descriptor); }
    public void UnregisterAt(int index) { _ensureMutable(); _descriptors.RemoveAt(index); }
    public void Clear() { _ensureMutable(); _descriptors.Clear(); }
    public IEnumerator<ServiceDescriptor> GetEnumerator() => _descriptors.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public IServiceProvider Build() => throw new InvalidOperationException("Only DatabaseApplicationBuilder.Build creates the provider.");

    internal IServiceProvider Materialize(IConfiguration configuration)
    {
        using var builder = new ServiceProviderBuilder(new ServiceProviderOptions
        {
            EnableDynamicCode = false,
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
        foreach (ServiceDescriptor descriptor in _descriptors)
        {
            if (descriptor.ServiceType == typeof(IConfiguration))
            {
                throw new InvalidOperationException("IConfiguration is reserved for the application's configuration.");
            }
            if (descriptor.ImplementationType is not null || descriptor.ServiceType.ContainsGenericParameters)
            {
                throw new InvalidOperationException("Database hosting requires closed factory or instance service registrations.");
            }
            builder.Add(descriptor);
        }
        builder.Add(new ServiceDescriptor(typeof(IConfiguration), configuration));
        return ((IServiceProviderBuilder)builder).Build();
    }
}
