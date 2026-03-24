using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<string> returns string")]
    public void Extensions_GetValueString_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key1"] = "hello";
        });

        string result = config.GetValue<string>("key1");

        Assert.Equal("hello", result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<int> returns int")]
    public void Extensions_GetValueInt_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["port"] = "8080";
        });

        int result = config.GetValue<int>("port");

        Assert.Equal(8080, result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<bool> returns bool")]
    public void Extensions_GetValueBool_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["enabled"] = "true";
        });

        bool result = config.GetValue<bool>("enabled");

        Assert.True(result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<long> returns long")]
    public void Extensions_GetValueLong_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["bignum"] = "9999999999";
        });

        long result = config.GetValue<long>("bignum");

        Assert.Equal(9999999999L, result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<double> returns double")]
    public void Extensions_GetValueDouble_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["rate"] = "3.14";
        });

        double result = config.GetValue<double>("rate");

        Assert.Equal(3.14, result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<float> returns float")]
    public void Extensions_GetValueFloat_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["rate"] = "2.5";
        });

        float result = config.GetValue<float>("rate");

        Assert.Equal(2.5f, result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<decimal> returns decimal")]
    public void Extensions_GetValueDecimal_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["price"] = "19.99";
        });

        decimal result = config.GetValue<decimal>("price");

        Assert.Equal(19.99m, result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<short> returns short")]
    public void Extensions_GetValueShort_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["small"] = "42";
        });

        short result = config.GetValue<short>("small");

        Assert.Equal((short)42, result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<DateTime> returns DateTime")]
    public void Extensions_GetValueDateTime_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["date"] = "2026-01-15";
        });

        DateTime result = config.GetValue<DateTime>("date");

        Assert.Equal(new DateTime(2026, 1, 15), result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<TimeSpan> returns TimeSpan")]
    public void Extensions_GetValueTimeSpan_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["timeout"] = "00:05:00";
        });

        TimeSpan result = config.GetValue<TimeSpan>("timeout");

        Assert.Equal(TimeSpan.FromMinutes(5), result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue<DateTimeOffset> returns DateTimeOffset")]
    public void Extensions_GetValueDateTimeOffset_ShouldReturn()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["timestamp"] = "2026-01-15T10:30:00+00:00";
        });

        DateTimeOffset result = config.GetValue<DateTimeOffset>("timestamp");

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero), result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue for missing key throws")]
    public void Extensions_GetValue_MissingKey_ShouldThrow()
    {
        var config = BuildConfiguration(_ => { });

        Assert.Throws<ArgumentNullException>(() => config.GetValue<string>("missing"));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue unsupported type throws")]
    public void Extensions_GetValue_UnsupportedType_ShouldThrow()
    {
        var config = BuildConfiguration(entries =>
        {
            entries["key"] = "value";
        });

        Assert.Throws<InvalidCastException>(() => config.GetValue<UnsupportedType>("key"));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetSection returns section")]
    public void Extensions_GetSection_ShouldReturnSection()
    {
        IConfiguration config = BuildConfiguration(entries =>
        {
            entries["section:key"] = "value";
        });

        var section = config.GetSection("section");

        Assert.NotNull(section);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetSection returns null for missing")]
    public void Extensions_GetSection_Missing_ShouldReturnNull()
    {
        IConfiguration config = BuildConfiguration(_ => { });

        var section = config.GetSection("missing");

        Assert.Null(section);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetValue returns IConfigurationValue")]
    public void Extensions_GetValue_ShouldReturnIConfigurationValue()
    {
        IConfiguration config = BuildConfiguration(entries =>
        {
            entries["key1"] = "value1";
        });

        var value = config.GetValue("key1");

        Assert.NotNull(value);
        Assert.Equal("value1", value!.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetProvider returns provider")]
    public void Extensions_GetProvider_ShouldReturnProvider()
    {
        IConfiguration config = BuildConfiguration(_ => { });

        var provider = config.GetProvider(nameof(MockConfigurationProvider));

        Assert.NotNull(provider);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetProvider returns null for missing")]
    public void Extensions_GetProvider_Missing_ShouldReturnNull()
    {
        IConfiguration config = BuildConfiguration(_ => { });

        var provider = config.GetProvider("NonExistent");

        Assert.Null(provider);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Extensions: GetProvider case-insensitive")]
    public void Extensions_GetProvider_CaseInsensitive_ShouldMatch()
    {
        IConfiguration config = BuildConfiguration(_ => { });

        var provider = config.GetProvider("mockconfigurationprovider", StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(provider);
    }

    private static Configuration BuildConfiguration(Action<IDictionary<Path, string?>> onLoad)
    {
        return new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(onLoad))
            .Build();
    }

    private sealed class UnsupportedType
    {
    }
}
