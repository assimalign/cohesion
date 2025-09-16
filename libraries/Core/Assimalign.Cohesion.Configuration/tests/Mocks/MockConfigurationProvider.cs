using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class MockConfigurationProvider : ConfigurationProvider
{
    private readonly IDictionary<Path, object> data;

    public MockConfigurationProvider() : base()
    {
        this.data = data;
    }

    public override string Name => nameof(MockConfigurationProvider);
    public override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
    {
        entries["Azure:Identity:ClientSecret"] = "asdflkajdsf";
        entries["Azure:Identity:ClientId"] = Guid.NewGuid().ToString();
        entries["Azure:Identity:Endpoint"] = "https://auth.com/config";


        return Task.CompletedTask;
    }
}
