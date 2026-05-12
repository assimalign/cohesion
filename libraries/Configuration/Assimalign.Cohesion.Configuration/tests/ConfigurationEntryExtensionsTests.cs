using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationEntryExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: IsValue returns true for value")]
    public void Entry_IsValue_ShouldReturnTrueForValue()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key1"] = "value1";
        });

        var entry = config.GetEntry("key1");

        Assert.NotNull(entry);
        Assert.True(entry!.IsValue(out IConfigurationValue? value));
        Assert.NotNull(value);
        Assert.Equal("value1", value!.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: IsValue returns false for section")]
    public void Entry_IsValue_ShouldReturnFalseForSection()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["section:key"] = "value";
        });

        var entry = config.GetEntry("section");

        Assert.NotNull(entry);
        Assert.False(entry!.IsValue(out _));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: IsSection returns true for section")]
    public void Entry_IsSection_ShouldReturnTrueForSection()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["section:key"] = "value";
        });

        var entry = config.GetEntry("section");

        Assert.NotNull(entry);
        Assert.True(entry!.IsSection(out IConfigurationSection? section));
        Assert.NotNull(section);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: IsSection returns false for value")]
    public void Entry_IsSection_ShouldReturnFalseForValue()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key1"] = "value1";
        });

        var entry = config.GetEntry("key1");

        Assert.NotNull(entry);
        Assert.False(entry!.IsSection(out _));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: ToInt16 converts value")]
    public void Entry_ToInt16_ShouldConvert()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["num"] = "42";
        });

        var entry = config.GetEntry("num") as IConfigurationValue;

        Assert.NotNull(entry);
        Assert.Equal((short)42, entry!.ToInt16());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: TryToInt16 converts value")]
    public void Entry_TryToInt16_ShouldConvert()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["num"] = "42";
        });

        var entry = config.GetEntry("num") as IConfigurationValue;

        Assert.NotNull(entry);
        Assert.True(entry!.TryToInt16(out short value));
        Assert.Equal((short)42, value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: TryToInt16 fails for non-numeric")]
    public void Entry_TryToInt16_NonNumeric_ShouldReturnFalse()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["text"] = "hello";
        });

        var entry = config.GetEntry("text") as IConfigurationValue;

        Assert.NotNull(entry);
        Assert.False(entry!.TryToInt16(out _));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: ToInt32 converts value")]
    public void Entry_ToInt32_ShouldConvert()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["num"] = "12345";
        });

        var entry = config.GetEntry("num") as IConfigurationValue;

        Assert.NotNull(entry);
        Assert.Equal(12345, entry!.ToInt32());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: TryToInt32 converts value")]
    public void Entry_TryToInt32_ShouldConvert()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["num"] = "12345";
        });

        var entry = config.GetEntry("num") as IConfigurationValue;

        Assert.NotNull(entry);
        Assert.True(entry!.TryToInt32(out int value));
        Assert.Equal(12345, value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: TryToInt32 fails for non-numeric")]
    public void Entry_TryToInt32_NonNumeric_ShouldReturnFalse()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["text"] = "hello";
        });

        var entry = config.GetEntry("text") as IConfigurationValue;

        Assert.NotNull(entry);
        Assert.False(entry!.TryToInt32(out _));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: ToInt64 converts value")]
    public void Entry_ToInt64_ShouldConvert()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["num"] = "9999999999";
        });

        var entry = config.GetEntry("num") as IConfigurationValue;

        Assert.NotNull(entry);
        Assert.Equal(9999999999L, entry!.ToInt64());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: TryToInt64 converts value")]
    public void Entry_TryToInt64_ShouldConvert()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["num"] = "9999999999";
        });

        var entry = config.GetEntry("num") as IConfigurationValue;

        Assert.NotNull(entry);
        Assert.True(entry!.TryToInt64(out long value));
        Assert.Equal(9999999999L, value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Entry: TryToInt64 fails for non-numeric")]
    public void Entry_TryToInt64_NonNumeric_ShouldReturnFalse()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["text"] = "hello";
        });

        var entry = config.GetEntry("text") as IConfigurationValue;

        Assert.NotNull(entry);
        Assert.False(entry!.TryToInt64(out _));
    }

    private static Configuration BuildConfiguration(Action<IDictionary<Path, string?>> onLoad)
    {
        return new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(onLoad))
            .Build();
    }
}
