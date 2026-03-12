using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class MockConfigurationProvider : ConfigurationProvider
{
    private readonly string _name;
    private readonly Action<IDictionary<Path, string?>> _onLoad;

    public MockConfigurationProvider(Action<IDictionary<Path, string?>> onLoad)
        : this(nameof(MockConfigurationProvider), onLoad)
    {
    }

    public MockConfigurationProvider(string name, Action<IDictionary<Path, string?>> onLoad)
        : base()
    {
        _name = name;
        _onLoad = onLoad;
    }

    public override string Name => _name;

    protected override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
    {
        _onLoad.Invoke(entries);
        return Task.CompletedTask;
    }
}
