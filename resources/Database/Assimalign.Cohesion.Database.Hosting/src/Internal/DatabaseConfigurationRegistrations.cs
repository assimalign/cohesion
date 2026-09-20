using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Database.Hosting;

internal sealed class DatabaseConfigurationRegistrations(Action ensureMutable) : IConfigurationBuilder
{
    private readonly List<Action<IConfigurationBuilder>> _registrations = [];

    public IConfigurationBuilder AddProvider(IConfigurationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ensureMutable();
        _registrations.Add(builder => builder.AddProvider(provider));
        return this;
    }

    public IConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ensureMutable();
        _registrations.Add(builder => builder.AddProvider(provider));
        return this;
    }

    public IConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ensureMutable();
        _registrations.Add(builder => builder.AddProvider(provider));
        return this;
    }

    public IConfiguration Build() => throw new InvalidOperationException("Only DatabaseApplicationBuilder.Build creates configuration.");
    public ValueTask<IConfiguration> BuildAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Only DatabaseApplicationBuilder.Build creates configuration.");

    internal void Apply(IConfigurationBuilder builder)
    {
        foreach (Action<IConfigurationBuilder> registration in _registrations)
        {
            registration(builder);
        }
    }
}
