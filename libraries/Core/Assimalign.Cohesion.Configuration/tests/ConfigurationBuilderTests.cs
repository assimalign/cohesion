using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Build returns configuration")]
    public void Builder_Build_ShouldReturnConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "value1";
            }))
            .Build();

        Assert.NotNull(config);
        Assert.Equal("value1", config["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: BuildAsync returns configuration")]
    public async Task Builder_BuildAsync_ShouldReturnConfiguration()
    {
        var config = await new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "value1";
            }))
            .BuildAsync();

        Assert.NotNull(config);
        Assert.Equal("value1", config["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Multiple providers")]
    public void Builder_MultipleProviders_ShouldWork()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider("Provider1", entries =>
            {
                entries["key1"] = "value1";
            }))
            .AddProvider(_ => new MockConfigurationProvider("Provider2", entries =>
            {
                entries["key2"] = "value2";
            }))
            .Build();

        Assert.Equal("value1", config["key1"]);
        Assert.Equal("value2", config["key2"]);
        Assert.Equal(2, config.Providers.Count());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Last provider wins for same key")]
    public void Builder_LastProviderWins_ShouldOverride()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider("Provider1", entries =>
            {
                entries["key1"] = "first";
            }))
            .AddProvider(_ => new MockConfigurationProvider("Provider2", entries =>
            {
                entries["key1"] = "second";
            }))
            .Build();

        Assert.Equal("second", config["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Create with options")]
    public void Builder_CreateWithOptions_ShouldWork()
    {
        var builder = ConfigurationBuilder.Create(options =>
        {
            options.SetStrategy = ConfigurationSetStrategy.Distributed;
        });

        Assert.NotNull(builder);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Context timeout matches options")]
    public void Builder_ContextTimeout_ShouldMatchOptions()
    {
        TimeSpan timeout = TimeSpan.Zero;

        var options = new ConfigurationOptions
        {
            LoadTimeout = TimeSpan.FromSeconds(5)
        };

        new ConfigurationBuilder(options)
            .AddProvider(context =>
            {
                timeout = context.Timeout;
                return new MockConfigurationProvider("Provider1", _ => { });
            })
            .Build();

        Assert.Equal(TimeSpan.FromSeconds(5), timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Context properties persist across registrations")]
    public void Builder_ContextProperties_ShouldPersistAcrossRegistrations()
    {
        bool received = false;

        var options = new ConfigurationOptions
        {
            LoadTimeout = TimeSpan.FromSeconds(5)
        };

        new ConfigurationBuilder(options)
            .AddProvider(context =>
            {
                context.Properties["MyKey"] = "MyValue";
                return new MockConfigurationProvider("Provider1", _ => { });
            })
            .AddProvider(context =>
            {
                received = context.GetProperty<string>("MyKey") == "MyValue";
                return new MockConfigurationProvider("Provider2", _ => { });
            })
            .Build();

        Assert.True(received);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: GetProperty extension reads internal context")]
    public void Builder_GetPropertyExtension_ShouldReadInternalContext()
    {
        string? value = null;

        new ConfigurationBuilder(new ConfigurationOptions
        {
            LoadTimeout = TimeSpan.FromSeconds(5)
        })
            .AddProvider(context =>
            {
                context.Properties["MyKey"] = "MyValue";
                value = context.GetProperty<string>("MyKey");
                return new MockConfigurationProvider("Provider1", _ => { });
            })
            .Build();

        Assert.Equal("MyValue", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: GetProperty extension reads external context")]
    public void Builder_GetPropertyExtension_ShouldReadExternalContext()
    {
        IConfigurationBuilderContext context = new ExternalBuilderContext(
            TimeSpan.FromSeconds(5),
            new Dictionary<string, object>
            {
                ["MyKey"] = "MyValue"
            });

        string? value = context.GetProperty<string>("MyKey");

        Assert.Equal("MyValue", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Build snapshots configured providers")]
    public void Builder_Build_ShouldSnapshotConfiguredProviders()
    {
        var options = new ConfigurationOptions
        {
            LoadTimeout = TimeSpan.FromSeconds(5)
        };

        options.Providers.Add(new MockConfigurationProvider("Provider1", entries =>
        {
            entries["key1"] = "value1";
        }));

        var config = new ConfigurationBuilder(options)
            .AddProvider(_ => new MockConfigurationProvider("Provider2", entries =>
            {
                entries["key2"] = "value2";
            }))
            .Build();

        Assert.Single(options.Providers);
        Assert.Equal("value1", config["key1"]);
        Assert.Equal("value2", config["key2"]);
        Assert.Equal(2, config.Providers.Count());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Duplicate provider name throws")]
    public void Builder_DuplicateProviderName_ShouldThrow()
    {
        var builder = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "value1";
            }))
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key2"] = "value2";
            }));

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Interface AddProvider works")]
    public void Builder_InterfaceAddProvider_ShouldWork()
    {
        IConfigurationBuilder builder = new ConfigurationBuilder();
        var provider = new MockConfigurationProvider(_ => { });

        var result = builder.AddProvider(provider);

        Assert.NotNull(result);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Interface Build returns IConfiguration")]
    public void Builder_InterfaceBuild_ShouldReturnIConfiguration()
    {
        IConfigurationBuilder builder = new ConfigurationBuilder();
        builder.AddProvider(new MockConfigurationProvider(_ => { }));

        IConfiguration config = builder.Build();

        Assert.NotNull(config);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Interface async AddProvider works")]
    public async Task Builder_InterfaceAsyncAddProvider_ShouldWork()
    {
        IConfigurationBuilder builder = new ConfigurationBuilder(new ConfigurationOptions
        {
            LoadTimeout = TimeSpan.FromSeconds(5)
        });

        builder.AddProvider(_ => Task.FromResult<IConfigurationProvider>(
            new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "value1";
            })));

        var config = await builder.BuildAsync();

        Assert.Equal("value1", config["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Builder: Null provider action throws")]
    public void Builder_NullProviderAction_ShouldThrow()
    {
        var builder = new ConfigurationBuilder();

        Assert.Throws<ArgumentNullException>(() =>
            builder.AddProvider((Func<IConfigurationBuilderContext, IConfigurationProvider>)null!));
    }

    private sealed class ExternalBuilderContext : IConfigurationBuilderContext
    {
        public ExternalBuilderContext(TimeSpan timeout, IDictionary<string, object> properties)
        {
            Timeout = timeout;
            Properties = properties;
        }

        public TimeSpan Timeout { get; }
        public IDictionary<string, object> Properties { get; }
        public IEnumerable<IConfigurationProvider> Providers => [];
    }
}
