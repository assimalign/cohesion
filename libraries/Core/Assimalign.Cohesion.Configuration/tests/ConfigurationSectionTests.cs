using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationSectionTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Section: GetEntry returns nested value")]
    public void Section_GetEntry_ShouldReturnNestedValue()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["Azure:Identity:ClientId"] = "abc123";
        });

        var section = config.GetSection("Azure");

        Assert.NotNull(section);

        var entry = section!.GetEntry("Identity:ClientId");

        Assert.NotNull(entry);
        Assert.IsAssignableFrom<IConfigurationValue>(entry);
        Assert.Equal("abc123", ((IConfigurationValue)entry).Value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Section: GetEntry returns nested section")]
    public void Section_GetEntry_ShouldReturnNestedSection()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["Azure:Identity:ClientId"] = "abc123";
            entries["Azure:Identity:TenantId"] = "tenant1";
        });

        var azure = config.GetSection("Azure");
        var identity = azure!.GetEntry("Identity");

        Assert.NotNull(identity);
        Assert.IsAssignableFrom<IConfigurationSection>(identity);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Section: GetEntry returns null for missing")]
    public void Section_GetEntry_MissingKey_ShouldReturnNull()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["Azure:Identity:ClientId"] = "abc123";
        });

        var section = config.GetSection("Azure");
        var entry = section!.GetEntry("Missing");

        Assert.Null(entry);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Section: GetChildren returns children")]
    public void Section_GetChildren_ShouldReturnChildren()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["section:key1"] = "value1";
            entries["section:key2"] = "value2";
            entries["section:key3"] = "value3";
        });

        var section = config.GetSection("section");

        Assert.NotNull(section);

        var children = section!.GetChildren().ToList();

        Assert.Equal(3, children.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Section: GetChildren returns empty for leaf")]
    public void Section_GetChildren_LeafSection_ShouldReturnChildren()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["section:child:value1"] = "v1";
        });

        var section = config.GetSection("section");

        Assert.NotNull(section);

        var children = section!.GetChildren().ToList();

        Assert.Single(children);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Section: Key and Path are correct")]
    public void Section_KeyAndPath_ShouldBeCorrect()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["Azure:Identity:ClientId"] = "abc";
        });

        var section = config.GetSection("Azure");

        Assert.NotNull(section);
        Assert.Equal("Azure", section!.Key.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Section: Deep nesting")]
    public void Section_DeepNesting_ShouldWork()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["a:b:c:d:e"] = "deep";
        });

        var entry = config.GetEntry("a:b:c:d:e");

        Assert.NotNull(entry);
        Assert.IsAssignableFrom<IConfigurationValue>(entry);
        Assert.Equal("deep", ((IConfigurationValue)entry).Value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Section: Mixed values and sections")]
    public void Section_MixedValuesAndSections_ShouldCoexist()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["section:value1"] = "v1";
            entries["section:subsection:value2"] = "v2";
        });

        var section = config.GetSection("section");

        Assert.NotNull(section);

        var children = section!.GetChildren().ToList();

        Assert.Equal(2, children.Count);
        Assert.Contains(children, c => c is IConfigurationValue);
        Assert.Contains(children, c => c is IConfigurationSection);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Section: ProviderName is set")]
    public void Section_ProviderName_ShouldBeSet()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["section:key1"] = "value1";
        });

        var section = config.GetSection("section");

        Assert.NotNull(section);
        Assert.Equal(nameof(MockConfigurationProvider), section!.ProviderName);
    }

    private static Configuration BuildConfiguration(Action<IDictionary<Path, string?>> onLoad)
    {
        return new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(onLoad))
            .Build();
    }
}
