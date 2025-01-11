using System;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Collections;

namespace Assimalign.Cohesion.Configuration;

using Assimalign.Cohesion.Internal;
using System.Threading.Tasks;

public class ConfigurationRootOld : IConfigurationRoot, IDisposable
{
    private readonly IList<IConfigurationProvider> providers;
    private readonly IList<IDisposable> changeTokenRegistrations;
    private ConfigurationReloadToken changeToken = new ConfigurationReloadToken();

    /// <summary>
    /// Initializes a Configuration root with a list of providers.
    /// </summary>
    /// <param name="providers">The <see cref="IConfigurationProvider"/>s for this configuration.</param>
    public ConfigurationRootOld(IList<IConfigurationProvider> providers)
    {
        if (providers == null)
        {
            throw new ArgumentNullException(nameof(providers));
        }

        this.providers = providers;
        this.changeTokenRegistrations = new List<IDisposable>(providers.Count);
        foreach (IConfigurationProvider provider in providers)
        {
            provider.Load();
            changeTokenRegistrations.Add(ChangeToken.OnChange(() => provider.GetReloadToken(), () => RaiseChanged()));
        }
    }

    /// <summary>
    /// The <see cref="IConfigurationProvider"/>s for this configuration.
    /// </summary>
    public IEnumerable<IConfigurationProvider> Providers => providers;

    public object this[ConfigurationPath path] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    /// <summary>
    /// Gets or sets the value corresponding to a configuration key.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configuration value.</returns>
    public string? this[string key]
    {
        get => GetConfiguration(providers, key);
        set => SetConfiguration(providers, key, value!);
    }

    /// <summary>
    /// Gets the immediate children sub-sections.
    /// </summary>
    /// <returns>The children.</returns>
    public IEnumerable<IConfigurationSection> GetChildren() => this.GetChildrenImplementation(null);

    /// <summary>
    /// Returns a <see cref="IChangeToken"/> that can be used to observe when this configuration is reloaded.
    /// </summary>
    /// <returns>The <see cref="IChangeToken"/>.</returns>
    public IChangeToken GetReloadToken() => changeToken;

    /// <summary>
    /// Gets a configuration sub-section with the specified key.
    /// </summary>
    /// <param name="key">The key of the configuration section.</param>
    /// <returns>The <see cref="IConfigurationSection"/>.</returns>
    /// <remarks>
    ///     This method will never return <c>null</c>. If no matching sub-section is found with the specified key,
    ///     an empty <see cref="IConfigurationSection"/> will be returned.
    /// </remarks>
    public IConfigurationSection GetSection(string key)
        => new ConfigurationSection(this, key);

    /// <summary>
    /// Force the configuration values to be reloaded from the underlying sources.
    /// </summary>
    public void Reload()
    {
        foreach (IConfigurationProvider provider in providers)
        {
            provider.Load();
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        ConfigurationReloadToken previousToken = Interlocked.Exchange(ref changeToken, new ConfigurationReloadToken());
        previousToken.OnReload();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // dispose change token registrations
        foreach (IDisposable registration in changeTokenRegistrations)
        {
            registration.Dispose();
        }

        // dispose providers
        foreach (IConfigurationProvider provider in providers)
        {
            (provider as IDisposable)?.Dispose();
        }
    }

    internal static string? GetConfiguration(IList<IConfigurationProvider> providers, string key)
    {
        for (int i = providers.Count - 1; i >= 0; i--)
        {
            IConfigurationProvider provider = providers[i];

            if (provider.TryGet(key, out string value))
            {
                return value;
            }
        }

        return null;
    }

    internal static void SetConfiguration(IList<IConfigurationProvider> providers, string key, string value)
    {
        if (providers.Count == 0)
        {
            throw new InvalidOperationException();// SR.Error_NoSources);
        }

        foreach (IConfigurationProvider provider in providers)
        {
            provider.Set(key, value);
        }
    }

    public IConfigurationProvider GetProvider(string name)
    {
        throw new NotImplementedException();
    }

    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        throw new NotImplementedException();
    }
}


public class ConfigurationRoot : IConfigurationRoot
{
    private readonly IEnumerable<IConfigurationProvider> providers;

    public ConfigurationRoot(IEnumerable<IConfigurationProvider> providers)
    {
        if (providers is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(providers));
        }
        if (!providers.Any())
        {
            ThrowHelper.ThrowArgumentException("");
        }
        this.providers = providers;
    }


    public IConfigurationEntry this[Key key] 
    { 
        get => GetConfigurationEntry(key);
        set => SetConfigurationEntry(key, value);
    }
    public IEnumerable<IConfigurationProvider> Providers => this.providers;
    public IConfigurationProvider GetProvider(string name)
    {
        foreach (var provider in providers)
        {
            if (provider.Name == name)
            {
                return provider;
            }
        }

        throw new InvalidOperationException();

       // ThrowHelper.ThrowInvalidOperationException($"The configuration provider '{name}' was not found.");
    }
    public IConfigurationChangeToken GetChangeToken()
    {
        throw new NotImplementedException();
    }
    public void Reload()
    {
        throw new NotImplementedException();
    }

    public async Task ReloadAsync()
    {
        var tasks = providers.Select(p => p.RefreshAsync()).ToList();

        while (tasks.Any())
        {
            var finished = await Task.WhenAny(tasks);

            tasks.Remove(finished);

            
        }
    }


    public IEnumerator<IConfigurationEntry> GetEnumerator()
    {
        return Enumerate().GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();
    IEnumerable<IConfigurationEntry> Enumerate()
    {
        foreach (var provider in providers)
        {
            foreach (var entry in provider.EnumerateEntries())
            {
                yield return entry;
            }
        }
    }
    internal IConfigurationEntry GetConfigurationEntry(Key key)
    {
        foreach (var provider in Providers)
        {
            foreach (var entry in provider.EnumerateEntries())
            {
                if (entry.Key == key)
                {
                    return entry;
                }
            }
        }
        throw new Exception();
    }
    internal void SetConfigurationEntry(Key key, IConfigurationEntry entry)
    {
        throw new Exception();
    }
}