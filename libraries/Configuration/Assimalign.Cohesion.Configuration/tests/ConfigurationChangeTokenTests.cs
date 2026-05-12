using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationChangeTokenTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - ChangeToken: OnChange fires on value update")]
    public void ChangeToken_OnChange_ShouldFireOnValueUpdate()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "original";
            }))
            .Build();

        var entry = config.GetEntry("key1");

        Assert.NotNull(entry);

        bool changed = false;
        var token = entry!.GetChangeToken();
        token.OnChange(_ => changed = true, null);

        // Modify the value
        var value = entry as IConfigurationValue;
        Assert.NotNull(value);
        value!.Value = "updated";

        Assert.True(changed);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - ChangeToken: OnChange fires multiple subscribers")]
    public void ChangeToken_OnChange_ShouldFireMultipleSubscribers()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "original";
            }))
            .Build();

        var entry = config.GetEntry("key1") as IConfigurationValue;

        Assert.NotNull(entry);

        int callCount = 0;
        var token = entry!.GetChangeToken();
        token.OnChange(_ => callCount++, null);
        token.OnChange(_ => callCount++, null);

        entry.Value = "updated";

        Assert.Equal(2, callCount);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - ChangeToken: Dispose unsubscribes")]
    public void ChangeToken_Dispose_ShouldUnsubscribe()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "original";
            }))
            .Build();

        var entry = config.GetEntry("key1") as IConfigurationValue;

        Assert.NotNull(entry);

        bool changed = false;
        var token = entry!.GetChangeToken();
        var subscription = token.OnChange(_ => changed = true, null);

        subscription.Dispose();

        entry.Value = "updated";

        Assert.False(changed);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - ChangeToken: Passes state to callback")]
    public void ChangeToken_OnChange_ShouldPassState()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "original";
            }))
            .Build();

        var entry = config.GetEntry("key1") as IConfigurationValue;

        Assert.NotNull(entry);

        object? receivedState = null;
        var expectedState = "my-state";

        var token = entry!.GetChangeToken();
        token.OnChange(state => receivedState = state, expectedState);

        entry.Value = "updated";

        Assert.Equal(expectedState, receivedState);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - ChangeToken: OnChange fires parent section subscribers")]
    public void ChangeToken_OnChange_ShouldFireParentSectionSubscribers()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["section:key1"] = "original";
            }))
            .Build();

        var section = config.GetSection("section");
        var entry = section!.GetEntry("key1") as IConfigurationValue;

        Assert.NotNull(entry);

        bool changed = false;
        var token = section.GetChangeToken();
        token.OnChange(_ => changed = true, null);

        entry!.Value = "updated";

        Assert.True(changed);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - ChangeToken: OnChange ignores unchanged value")]
    public void ChangeToken_OnChange_UnchangedValue_ShouldNotFire()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["section:key1"] = "original";
            }))
            .Build();

        var section = config.GetSection("section");
        var entry = section!.GetEntry("key1") as IConfigurationValue;

        Assert.NotNull(entry);

        bool changed = false;
        var token = section.GetChangeToken();
        token.OnChange(_ => changed = true, null);

        entry!.Value = "original";

        Assert.False(changed);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - ChangeToken: Dispose during notification does not skip remaining subscribers")]
    public void ChangeToken_DisposeDuringNotify_ShouldNotSkipRemainingSubscribers()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "original";
            }))
            .Build();

        var entry = config.GetEntry("key1") as IConfigurationValue;

        Assert.NotNull(entry);

        int callCount = 0;
        var token = entry!.GetChangeToken();
        IDisposable? subscription = null;

        subscription = token.OnChange(_ =>
        {
            callCount++;
            subscription!.Dispose();
        }, null);

        token.OnChange(_ => callCount++, null);

        entry.Value = "updated";

        Assert.Equal(2, callCount);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - ChangeToken: GetChangeToken returns same instance")]
    public void ChangeToken_GetChangeToken_ShouldReturnSameInstance()
    {
        var config = new ConfigurationBuilder()
            .AddProvider(_ => new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "value1";
            }))
            .Build();

        var entry = config.GetEntry("key1");

        Assert.NotNull(entry);

        var token1 = entry!.GetChangeToken();
        var token2 = entry.GetChangeToken();

        Assert.Same(token1, token2);
    }
}
