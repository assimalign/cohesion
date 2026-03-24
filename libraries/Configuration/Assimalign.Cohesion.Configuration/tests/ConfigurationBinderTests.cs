using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationBinderTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Binder: Get binds nested object graph")]
    public void Binder_Get_ShouldBindNestedObjectGraph()
    {
        Configuration config = BuildConfiguration(entries =>
        {
            entries["Enabled"] = "true";
            entries["Api:Endpoint"] = "https://example.test";
            entries["Api:Timeout"] = "00:00:30";
        });

        RootOptions options = config.Get<RootOptions>();

        Assert.NotNull(options);
        Assert.True(options.Enabled);
        Assert.NotNull(options.Api);
        Assert.Equal("https://example.test", options.Api!.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Api.Timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Binder: Attribute creates concrete implementation")]
    public void Binder_Attribute_ShouldCreateConcreteImplementation()
    {
        Configuration config = BuildConfiguration(entries =>
        {
            entries["Storage:Kind"] = "Blob";
        });

        InterfaceContainerOptions options = config.Get<InterfaceContainerOptions>();

        Assert.NotNull(options);
        Assert.NotNull(options.Storage);
        Assert.IsType<StorageOptions>(options.Storage);
        Assert.Equal("Blob", options.Storage!.Kind);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Binder: Bind populates existing getter-only collection")]
    public void Binder_Bind_ShouldPopulateGetterOnlyCollection()
    {
        Configuration config = BuildConfiguration(entries =>
        {
            entries["Tags:0"] = "alpha";
            entries["Tags:1"] = "beta";
        });

        var options = new CollectionOptions();

        config.Bind(options);

        Assert.Equal(["alpha", "beta"], options.Tags);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Binder: Bind supports non-public setters")]
    public void Binder_Bind_ShouldSupportNonPublicSetters()
    {
        Configuration config = BuildConfiguration(entries =>
        {
            entries["Secret"] = "classified";
        });

        var options = new PrivateSetterOptions();

        config.Bind(options, static options => options.BindNonPublicProperties = true);

        Assert.Equal("classified", options.Secret);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Binder: ErrorOnUnknownConfiguration throws")]
    public void Binder_ErrorOnUnknownConfiguration_ShouldThrow()
    {
        Configuration config = BuildConfiguration(entries =>
        {
            entries["Known"] = "value";
            entries["Unknown"] = "value";
        });

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = config.Get<KnownOptions>(static options => options.ErrorOnUnknownConfiguration = true);
        });
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Binder: GetValue converts Guid")]
    public void Binder_GetValueGuid_ShouldConvert()
    {
        Guid identifier = Guid.NewGuid();
        Configuration config = BuildConfiguration(entries =>
        {
            entries["Id"] = identifier.ToString();
        });

        Guid value = config.GetValue<Guid>("Id");

        Assert.Equal(identifier, value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Binder: Entry Get binds section")]
    public void Binder_EntryGet_ShouldBindSection()
    {
        Configuration config = BuildConfiguration(entries =>
        {
            entries["Api:Endpoint"] = "https://example.test";
            entries["Api:Timeout"] = "00:00:45";
        });

        IConfigurationSection? section = config.GetSection("Api");
        ApiOptions options = section!.Get<ApiOptions>();

        Assert.Equal("https://example.test", options.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Timeout);
    }

    private static Configuration BuildConfiguration(Action<IDictionary<Path, string?>> onLoad)
    {
        return new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(onLoad))
            .Build();
    }

    public class RootOptions
    {
        public bool Enabled { get; set; }

        public ApiOptions? Api { get; set; }
    }

    public class ApiOptions
    {
        public string? Endpoint { get; set; }

        public TimeSpan Timeout { get; set; }
    }

    public class InterfaceContainerOptions
    {
        [ConfigurationBinding<StorageOptions>]
        public IStorageOptions? Storage { get; set; }
    }

    public interface IStorageOptions
    {
        string? Kind { get; set; }
    }

    public class StorageOptions : IStorageOptions
    {
        public string? Kind { get; set; }
    }

    public class CollectionOptions
    {
        public List<string> Tags { get; } = [];
    }

    public class PrivateSetterOptions
    {
        public string? Secret { get; private set; }
    }

    public class KnownOptions
    {
        public string? Known { get; set; }
    }
}
