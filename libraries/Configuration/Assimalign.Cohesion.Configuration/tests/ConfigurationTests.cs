using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: Indexer get returns value")]
    public void Configuration_IndexerGet_ShouldReturnValue()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key1"] = "value1";
        });

        Assert.Equal("value1", config["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: Indexer get returns null for missing")]
    public void Configuration_IndexerGet_MissingKey_ShouldReturnNull()
    {
        var config = BuildConfiguration(_ => { });

        Assert.Null(config["missing"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: Indexer get nested value")]
    public void Configuration_IndexerGet_NestedValue_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["section:key"] = "nested";
        });

        Assert.Equal("nested", config["section:key"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: GetEntry returns value entry")]
    public void Configuration_GetEntry_ShouldReturnValue()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key1"] = "value1";
        });

        var entry = config.GetEntry("key1");

        Assert.NotNull(entry);
        Assert.IsAssignableFrom<IConfigurationValue>(entry);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: GetEntry returns section")]
    public void Configuration_GetEntry_ShouldReturnSection()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["section:key"] = "value";
        });

        var entry = config.GetEntry("section");

        Assert.NotNull(entry);
        Assert.IsAssignableFrom<IConfigurationSection>(entry);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: GetEntry returns null for missing")]
    public void Configuration_GetEntry_MissingPath_ShouldReturnNull()
    {
        var config = BuildConfiguration(_ => { });

        Assert.Null(config.GetEntry("missing"));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: GetValue returns value")]
    public void Configuration_GetValue_ShouldReturnValue()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key1"] = "value1";
        });

        var value = config.GetValue("key1");

        Assert.NotNull(value);
        Assert.Equal("value1", value!.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: GetSection returns section")]
    public void Configuration_GetSection_ShouldReturnSection()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["section:key"] = "value";
        });

        var section = config.GetSection("section");

        Assert.NotNull(section);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: Enumeration returns all entries")]
    public void Configuration_Enumeration_ShouldReturnEntries()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key1"] = "value1";
            entries["key2"] = "value2";
        });

        var all = config.ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: Providers returns registered providers")]
    public void Configuration_Providers_ShouldReturnAll()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider("P1", _ => { }))
            .AddProvider(_ => new MockConfigurationProvider("P2", _ => { }))
            .Build();

        Assert.Equal(2, config.Providers.Count());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: Dispose disposes providers")]
    public void Configuration_Dispose_ShouldDisposeProviders()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key1"] = "value1";
        });

        config.Dispose();

        Assert.Throws<ObjectDisposedException>(() => config["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: DisposeAsync disposes providers")]
    public async Task Configuration_DisposeAsync_ShouldDisposeProviders()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key1"] = "value1";
        });

        await config.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => config["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: Multiple providers last wins")]
    public void Configuration_MultipleProviders_LastWins()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider("P1", entries =>
            {
                entries["key1"] = "from-p1";
            }))
            .AddProvider(_ => new MockConfigurationProvider("P2", entries =>
            {
                entries["key1"] = "from-p2";
            }))
            .Build();

        Assert.Equal("from-p2", config["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Configuration: Deep nested path access")]
    public void Configuration_DeepNestedPath_ShouldWork()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["level1:level2:level3:level4"] = "deep";
        });

        Assert.Equal("deep", config["level1:level2:level3:level4"]);
    }

    private static Configuration BuildConfiguration(Action<IDictionary<Path, string?>> onLoad)
    {
        return new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(onLoad))
            .Build();
    }
}
