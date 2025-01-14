using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Represents a section of application configuration values.
/// </summary>
public class ConfigurationSection : IConfigurationSection
{
    private readonly IConfiguration configuration;
    private readonly Key key;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="root">The configuration root.</param>
    /// <param name="key">The path to this section.</param>
    public ConfigurationSection(IConfiguration configuration, Key key)
    {
        this.configuration = configuration;
        this.key = key;
    }


    public Key Key { get; }
    public KeyPath Path { get; }
    public IConfigurationProvider Provider { get; }

    object? IConfiguration.this[KeyPath path] 
    {
        get => throw new NotImplementedException(); 
        set => throw new NotImplementedException(); 
    }


    private void SetConfigurationValue(Key key)

    public IEnumerable<IConfigurationEntry> EnumerateEntries()
    {
        foreach (var item in )
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

    IEnumerator IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException();
    }

    public object? ToValue()
    {
        throw new NotImplementedException();
    }

    public IConfigurationValue GetValue(Key key)
    {
        throw new NotImplementedException();
    }

    IEnumerable<IConfigurationValue> IConfiguration.EnumerateEntries()
    {
        throw new NotImplementedException();
    }





    //public Key Key
    //{
    //    get { return key; }
    //}
    //public IConfigurationEntry this[Key key] 
    //{ 
    //    get
    //    {
    //        var k = this.key.Combine(key);

    //        return configuration[]
    //    }
    //    set => throw new NotImplementedException(); 
    //}
    //public IConfigurationEntry Value
    //{
    //    get
    //    {
    //        return this.key.Concat()
    //    }
    //}

    //object? IConfigurationEntry.Value { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    //IConfiguration IConfigurationSection.Value => throw new NotImplementedException();

    ///// <summary>
    ///// Gets a configuration sub-section with the specified key.
    ///// </summary>
    ///// <param name="key">The key of the configuration section.</param>
    ///// <returns>The <see cref="IConfigurationSection"/>.</returns>
    ///// <remarks>
    /////     This method will never return <c>null</c>. If no matching sub-section is found with the specified key,
    /////     an empty <see cref="IConfigurationSection"/> will be returned.
    ///// </remarks>
    //public IConfigurationSection GetSection(Key key) => throw new NotImplementedException();

    //public IConfigurationChangeToken GetChangeToken()
    //{
    //    throw new NotImplementedException();
    //}


    //class Enumerator : IEnumerator<IConfigurationEntry>
    //{
    //    private readonly IEnumerator<IConfigurationEntry> enumerator;
    //    private readonly Key key;

    //    public Enumerator(IEnumerator<IConfigurationEntry> enumerator, Key key)
    //    {
    //        this.enumerator = enumerator;
    //        this.key = key;
    //    }

    //    public IConfigurationEntry Current { get; private set; }
    //    object IEnumerator.Current => Current;

    //    public void Dispose()
    //    {
    //        enumerator.Dispose();
    //    }

    //    public bool MoveNext()
    //    {
    //        while (enumerator.MoveNext())
    //        {
    //            var current = enumerator.Current;

    //            if (current.Key.StartsWith(key))
    //            {
    //                return true;
    //            }
    //        }
    //        return false;
    //    }

    //    public void Reset()
    //    {
    //        enumerator.Reset();
    //    }
}
