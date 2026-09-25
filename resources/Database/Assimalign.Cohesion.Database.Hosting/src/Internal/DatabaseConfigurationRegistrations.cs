using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Configuration;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

internal sealed class DatabaseConfigurationRegistrations : IConfigurationBuilder
{
    private readonly List<Action<IConfigurationBuilder>> _registrations = [];
    private readonly Action _ensureMutable;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseConfigurationRegistrations"/> class.
    /// </summary>
    /// <param name="ensureMutable">The callback that throws when the owning builder no longer accepts registrations.</param>
    public DatabaseConfigurationRegistrations(Action ensureMutable)
    {
        _ensureMutable = ensureMutable;
    }

    public IConfigurationBuilder AddProvider(IConfigurationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _ensureMutable();
        _registrations.Add(builder => builder.AddProvider(provider));
        return this;
    }

    public IConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, IConfigurationProvider> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _ensureMutable();
        _registrations.Add(builder => builder.AddProvider(provider));
        return this;
    }

    public IConfigurationBuilder AddProvider(Func<IConfigurationBuilderContext, Task<IConfigurationProvider>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _ensureMutable();
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
