using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationManagerTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: AddProvider and get value")]
    public void Manager_AddProvider_ShouldAllowGetValue()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        manager.AddProvider(_ => new MockConfigurationProvider(entries =>
        {
            entries["key1"] = "value1";
        }));

        // Load the provider
        foreach (var provider in manager.Providers)
        {
            provider.Load();
        }

        Assert.Equal("value1", manager["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Indexer set updates value")]
    public void Manager_IndexerSet_ShouldUpdateValue()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        manager.AddProvider(_ => new MockConfigurationProvider(entries =>
        {
            entries["key1"] = "original";
        }));

        foreach (var provider in manager.Providers)
        {
            provider.Load();
        }

        manager["key1"] = "updated";

        Assert.Equal("updated", manager["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: GetEntry returns entry")]
    public void Manager_GetEntry_ShouldReturnEntry()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        manager.AddProvider(_ => new MockConfigurationProvider(entries =>
        {
            entries["key1"] = "value1";
        }));

        foreach (var provider in manager.Providers)
        {
            provider.Load();
        }

        var entry = manager.GetEntry("key1");

        Assert.NotNull(entry);
        Assert.IsAssignableFrom<IConfigurationValue>(entry);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: GetValue returns IConfigurationValue")]
    public void Manager_GetValue_ShouldReturnValue()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        manager.AddProvider(_ => new MockConfigurationProvider(entries =>
        {
            entries["key1"] = "value1";
        }));

        foreach (var provider in manager.Providers)
        {
            provider.Load();
        }

        var value = manager.GetValue("key1");

        Assert.NotNull(value);
        Assert.Equal("value1", value!.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: GetSection returns section")]
    public void Manager_GetSection_ShouldReturnSection()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        manager.AddProvider(_ => new MockConfigurationProvider(entries =>
        {
            entries["section:key1"] = "value1";
        }));

        foreach (var provider in manager.Providers)
        {
            provider.Load();
        }

        var section = manager.GetSection("section");

        Assert.NotNull(section);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: GetSection throws for missing")]
    public void Manager_GetSection_MissingPath_ShouldThrow()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        Assert.Throws<ConfigurationException>(() => manager.GetSection("missing"));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Providers returns registered providers")]
    public void Manager_Providers_ShouldReturnAll()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        manager.AddProvider(_ => new MockConfigurationProvider("P1", _ => { }));
        manager.AddProvider(_ => new MockConfigurationProvider("P2", _ => { }));

        Assert.Equal(2, manager.Providers.Count());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Enumeration returns entries")]
    public void Manager_Enumeration_ShouldReturnEntries()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        manager.AddProvider(_ => new MockConfigurationProvider(entries =>
        {
            entries["key1"] = "value1";
            entries["key2"] = "value2";
        }));

        foreach (var provider in manager.Providers)
        {
            provider.Load();
        }

        var all = manager.ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Dispose is safe")]
    public void Manager_Dispose_ShouldBeSafe()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        manager.Dispose();

        // Should not throw on double dispose
        manager.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: DisposeAsync is safe")]
    public async Task Manager_DisposeAsync_ShouldBeSafe()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        await manager.DisposeAsync();

        // Should not throw on double dispose
        await manager.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Null options throws")]
    public void Manager_NullOptions_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigurationManager(null!));
    }
}
