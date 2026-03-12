using System;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Options: Default strategy is ExistingOnly")]
    public void Options_DefaultStrategy_ShouldBeExistingOnly()
    {
        var options = new ConfigurationOptions();

        Assert.Equal(ConfigurationSetStrategy.ExistingOnly, options.SetStrategy);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Options: SetStrategy can be changed")]
    public void Options_SetStrategy_ShouldBeChangeable()
    {
        var options = new ConfigurationOptions
        {
            SetStrategy = ConfigurationSetStrategy.Distributed
        };

        Assert.Equal(ConfigurationSetStrategy.Distributed, options.SetStrategy);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Options: LoadTimeout default is zero")]
    public void Options_LoadTimeout_ShouldDefaultToZero()
    {
        var options = new ConfigurationOptions();

        Assert.Equal(TimeSpan.Zero, options.LoadTimeout);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Options: LoadTimeout can be set")]
    public void Options_LoadTimeout_ShouldBeSettable()
    {
        var options = new ConfigurationOptions
        {
            LoadTimeout = TimeSpan.FromSeconds(30)
        };

        Assert.Equal(TimeSpan.FromSeconds(30), options.LoadTimeout);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Options: Providers list is initialized")]
    public void Options_Providers_ShouldBeInitialized()
    {
        var options = new ConfigurationOptions();

        Assert.NotNull(options.Providers);
        Assert.Empty(options.Providers);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Options: Default static instance exists")]
    public void Options_Default_ShouldExist()
    {
        Assert.NotNull(ConfigurationOptions.Default);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Options: Invalid enum throws")]
    public void Options_InvalidSetStrategy_ShouldThrow()
    {
        var options = new ConfigurationOptions();

        Assert.Throws<ArgumentException>(() => options.SetStrategy = (ConfigurationSetStrategy)999);
    }
}
