using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationProviderTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: Load populates entries")]
    public void Provider_Load_ShouldPopulateEntries()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key1"] = "value1";
            entries["key2"] = "value2";
        });

        provider.Load();

        Assert.True(provider.TryGet("key1", out string? v1));
        Assert.Equal("value1", v1);
        Assert.True(provider.TryGet("key2", out string? v2));
        Assert.Equal("value2", v2);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: LoadAsync populates entries")]
    public async Task Provider_LoadAsync_ShouldPopulateEntries()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key1"] = "value1";
        });

        await provider.LoadAsync();

        Assert.True(provider.TryGet("key1", out string? value));
        Assert.Equal("value1", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: TryGet returns false for missing key")]
    public void Provider_TryGet_MissingKey_ShouldReturnFalse()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key1"] = "value1";
        });

        provider.Load();

        Assert.False(provider.TryGet("nonexistent", out _));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: Exists returns true for existing key")]
    public void Provider_Exists_ShouldReturnTrue()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key1"] = "value1";
        });

        provider.Load();

        Assert.True(provider.Exists("key1"));
        Assert.False(provider.Exists("missing"));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: TrySet adds new value")]
    public void Provider_TrySet_ShouldAddNewValue()
    {
        var provider = CreateProvider(_ => { });

        provider.Load();

        Assert.True(provider.TrySet("newKey", "newValue"));
        Assert.True(provider.TryGet("newKey", out string? value));
        Assert.Equal("newValue", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: TrySet updates existing value")]
    public void Provider_TrySet_ShouldUpdateExistingValue()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key1"] = "original";
        });

        provider.Load();

        Assert.True(provider.TrySet("key1", "updated"));
        Assert.True(provider.TryGet("key1", out string? value));
        Assert.Equal("updated", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: Hierarchical keys create sections")]
    public void Provider_HierarchicalKeys_ShouldCreateSections()
    {
        var provider = CreateProvider(entries =>
        {
            entries["Azure:Identity:ClientId"] = "abc123";
            entries["Azure:Identity:ClientSecret"] = "secret";
        });

        provider.Load();

        Assert.True(provider.TryGet("Azure:Identity:ClientId", out string? value));
        Assert.Equal("abc123", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: GetEntry returns value for leaf")]
    public void Provider_GetEntry_ShouldReturnValueForLeaf()
    {
        var provider = CreateProvider(entries =>
        {
            entries["Version"] = "1.0";
        });

        provider.Load();

        var entry = provider.GetEntry("Version");

        Assert.NotNull(entry);
        Assert.IsAssignableFrom<IConfigurationValue>(entry);
        Assert.Equal("1.0", ((IConfigurationValue)entry).Value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: GetEntry returns section for composite")]
    public void Provider_GetEntry_ShouldReturnSectionForComposite()
    {
        var provider = CreateProvider(entries =>
        {
            entries["Azure:Identity:ClientId"] = "abc123";
        });

        provider.Load();

        var entry = provider.GetEntry("Azure");

        Assert.NotNull(entry);
        Assert.IsAssignableFrom<IConfigurationSection>(entry);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: GetEntry returns null for missing")]
    public void Provider_GetEntry_ShouldReturnNullForMissing()
    {
        var provider = CreateProvider(_ => { });
        provider.Load();

        var entry = provider.GetEntry("missing");

        Assert.Null(entry);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: GetEntries returns all top-level entries")]
    public void Provider_GetEntries_ShouldReturnTopLevel()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key1"] = "value1";
            entries["key2"] = "value2";
            entries["section:sub"] = "value3";
        });

        provider.Load();

        var allEntries = provider.GetEntries().ToList();

        Assert.Equal(3, allEntries.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: Reload clears and reloads data")]
    public async Task Provider_Reload_ShouldClearAndReload()
    {
        int loadCount = 0;
        var provider = CreateProvider(entries =>
        {
            loadCount++;
            entries["key1"] = $"value{loadCount}";
        });

        await provider.LoadAsync();
        Assert.True(provider.TryGet("key1", out string? v1));
        Assert.Equal("value1", v1);

        await provider.LoadAsync();
        Assert.True(provider.TryGet("key1", out string? v2));
        Assert.Equal("value2", v2);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: Dispose prevents further operations")]
    public async Task Provider_Dispose_ShouldPreventOperations()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key1"] = "value1";
        });

        provider.Load();
        await provider.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => provider.TryGet("key1", out _));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: TrySet creates nested sections")]
    public void Provider_TrySet_ShouldCreateNestedSections()
    {
        var provider = CreateProvider(_ => { });
        provider.Load();

        Assert.True(provider.TrySet("level1:level2:level3", "deep"));
        Assert.True(provider.TryGet("level1:level2:level3", out string? value));
        Assert.Equal("deep", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: TrySet replaces value with section")]
    public void Provider_TrySet_ShouldReplaceValueWithSection()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key"] = "simple";
        });

        provider.Load();

        Assert.True(provider.TrySet("key:sub", "nested"));
        Assert.True(provider.TryGet("key:sub", out string? value));
        Assert.Equal("nested", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: TrySet replaces section with value")]
    public void Provider_TrySet_ShouldReplaceSectionWithValue()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key:sub"] = "nested";
        });

        provider.Load();

        Assert.True(provider.TrySet("key", "simple"));
        Assert.True(provider.TryGet("key", out string? value));
        Assert.Equal("simple", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: Name property returns correct name")]
    public void Provider_Name_ShouldReturnCorrectName()
    {
        var provider = CreateProvider(_ => { });

        Assert.Equal(nameof(MockConfigurationProvider), provider.Name);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Provider: TryGet composite path on value returns false")]
    public void Provider_TryGet_CompositePathOnValue_ShouldReturnFalse()
    {
        var provider = CreateProvider(entries =>
        {
            entries["key1"] = "value1";
        });

        provider.Load();

        Assert.False(provider.TryGet("key1:sub", out _));
    }

    private static MockConfigurationProvider CreateProvider(Action<IDictionary<Path, string?>> onLoad)
    {
        return new MockConfigurationProvider(onLoad);
    }
}
