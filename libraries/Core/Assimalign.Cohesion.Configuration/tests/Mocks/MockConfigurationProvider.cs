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

    public MockConfigurationProvider(IDictionary<Path, object> data)
    {
        this.data = data;
    }

    public override string Name => nameof(MockConfigurationProvider);

    public override Task LoadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (path, value) in data)
        {
            if (path.Count > 1)
            {
                var subpath = path.Subpath(1);
                var section = new ConfigurationSection(path[0]);

                var entry = ComposeEntry(subpath, value);

                section.Set(entry);

                Set(section);
            }
            else
            {
                Set(new ConfigurationValue(path[0], value));
            }
        }

        IConfigurationEntry ComposeEntry(Path path, object value)
        {
            if (path.Count > 1)
            {
                var subpath = path.Subpath(1);
                var section = new ConfigurationSection(path[0]);

                var entry = ComposeEntry(subpath, value);

                section.Set(entry);

                return section;
            }
            else
            {
                return new ConfigurationValue(path[0], value);
            }
        }


        return Task.CompletedTask;
    }

    public static IConfigurationProvider Create(IDictionary<Path, object> data)
    {
        return new MockConfigurationProvider(data);
    }
}
