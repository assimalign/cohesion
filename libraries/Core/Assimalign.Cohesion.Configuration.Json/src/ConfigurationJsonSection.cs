using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

internal class ConfigurationJsonSection : ConfigurationJsonEntry, IConfigurationSection
{
    public object? this[KeyPath path] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public KeyPath Path => throw new NotImplementedException();

    public override Key Key => throw new NotImplementedException();

    public IEnumerable<IConfigurationValue> EnumerateEntries()
    {
        throw new NotImplementedException();
    }

    public IConfigurationChangeToken GetChangeToken()
    {
        throw new NotImplementedException();
    }

    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        throw new NotImplementedException();
    }

    public IConfigurationSection GetSection(Key key)
    {
        throw new NotImplementedException();
    }

    public IConfigurationValue GetValue(Key key)
    {
        throw new NotImplementedException();
    }

    public object? GetValue()
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException();
    }
}
