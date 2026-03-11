using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class MockConfigurationProvider : ConfigurationProvider
{
    private readonly Action<IDictionary<Path, string?>> _onLoad;

    public MockConfigurationProvider(Action<IDictionary<Path, string?>> onLoad): base()
    {
        _onLoad = onLoad;
    }

    public override string Name => nameof(MockConfigurationProvider);
    protected override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
    {
        _onLoad.Invoke(entries);


        return Task.CompletedTask;
    }
}
