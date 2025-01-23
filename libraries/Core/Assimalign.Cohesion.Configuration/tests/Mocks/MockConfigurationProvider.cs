using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class MockConfigurationProvider : ConfigurationProvider
{
    private readonly IDictionary<KeyPath, object> data;

    public MockConfigurationProvider(IDictionary<KeyPath, object> data)
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
                var subpath = path.GetSubpath(1);
                var section = new ConfigurationSection(path.GetFirstKey());

                var entry = ComposeEntry(subpath, value);

                section.Add(entry);

                Set(section);
            }
            else
            {
                Set(new ConfigurationValue(path.GetFirstKey(), value));
            }
        }

        IConfigurationEntry ComposeEntry(KeyPath path, object value)
        {
            if (path.Count > 1)
            {
                var subpath = path.GetSubpath(1);
                var section = new ConfigurationSection(path.GetFirstKey());

                var entry = ComposeEntry(subpath, value);

                section.Add(entry);

                return section;
            }
            else
            {
                return new ConfigurationValue(path.GetFirstKey(), value);
            }
        }


        return Task.CompletedTask;
    }

    public static IConfigurationProvider Create(IDictionary<KeyPath, object> data)
    {
        return new MockConfigurationProvider(data);
    }
}
