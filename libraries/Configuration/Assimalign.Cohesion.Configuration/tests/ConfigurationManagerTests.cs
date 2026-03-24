using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationManagerTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: AddProvider loads provider and gets value")]
    public void Manager_AddProvider_ShouldAllowGetValue()
    {
        var options = new ConfigurationOptions();
        var manager = new ConfigurationManager(options);

        manager.AddProvider(_ => new MockConfigurationProvider(entries =>
        {
            entries["key1"] = "value1";
        }));

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

        var all = manager.ToList();

        Assert.Equal(2, all.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Constructor loads configured providers")]
    public void Manager_Constructor_ShouldLoadConfiguredProviders()
    {
        var options = new ConfigurationOptions();
        options.Providers.Add(new MockConfigurationProvider(entries =>
        {
            entries["key1"] = "value1";
        }));

        var manager = new ConfigurationManager(options);

        Assert.Equal("value1", manager["key1"]);
        Assert.Single(manager.Providers);
        Assert.Single(options.Providers);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Async add loads provider")]
    public void Manager_AddProviderAsync_ShouldLoadProvider()
    {
        var manager = new ConfigurationManager();

        manager.AddProvider(_ => Task.FromResult<IConfigurationProvider>(
            new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "value1";
            })));

        Assert.Equal("value1", manager["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Interface async add loads provider")]
    public async Task Manager_InterfaceAddProviderAsync_ShouldLoadProvider()
    {
        IConfigurationManager manager = new ConfigurationManager();

        manager.AddProvider(_ => Task.FromResult<IConfigurationProvider>(
            new MockConfigurationProvider(entries =>
            {
                entries["key1"] = "value1";
            })));

        Assert.Equal("value1", manager["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Timeout throws TimeoutException")]
    public void Manager_AddProviderAsync_LoadTimeout_ShouldThrowTimeoutException()
    {
        var manager = new ConfigurationManager(new ConfigurationOptions
        {
            LoadTimeout = TimeSpan.FromMilliseconds(20)
        });

        var exception = Assert.Throws<TimeoutException>(() =>
            manager.AddProvider(_ => Task.FromResult<IConfigurationProvider>(
                new DelayedConfigurationProvider(
                    "Provider1",
                    TimeSpan.FromMilliseconds(200),
                    entries => entries["key1"] = "value1"))));

        Assert.Contains("Provider1", exception.Message);
        Assert.Empty(manager.Providers);
    }

    //[Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Cancellation token cancels add")]
    //public async Task Manager_AddProviderAsync_Cancellation_ShouldCancel()
    //{
    //    using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

    //    var manager = new ConfigurationManager();

    //    Assert.Throws<OperationCanceledException>(() =>
    //        manager.AddProvider(
    //            _ => Task.FromResult<IConfigurationProvider>(
    //                new DelayedConfigurationProvider(
    //                    "Provider1",
    //                    TimeSpan.FromMilliseconds(200),
    //                    entries => entries["key1"] = "value1")),
    //            cancellationTokenSource.Token));

    //    Assert.Empty(manager.Providers);
    //}

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Load failure is surfaced and provider is not registered")]
    public void Manager_AddProvider_LoadFailure_ShouldNotRegisterProvider()
    {
        var manager = new ConfigurationManager();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            manager.AddProvider(_ => new ThrowingConfigurationProvider(
                "Provider1",
                new NotSupportedException("boom"))));

        Assert.Contains("Provider1", exception.Message);
        Assert.Empty(manager.Providers);
        Assert.Null(manager["key1"]);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Failed provider is disposed")]
    public void Manager_AddProvider_LoadFailure_ShouldDisposeProvider()
    {
        var manager = new ConfigurationManager();
        var provider = new TrackingThrowingConfigurationProvider(
            "Provider1",
            new NotSupportedException("boom"));

        Assert.Throws<InvalidOperationException>(() => manager.AddProvider(_ => provider));
        Assert.True(provider.WasDisposed);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Duplicate provider name throws")]
    public void Manager_AddProvider_DuplicateProviderName_ShouldThrow()
    {
        var manager = new ConfigurationManager();

        manager.AddProvider(_ => new MockConfigurationProvider("Provider1", _ => { }));

        Assert.Throws<InvalidOperationException>(() =>
            manager.AddProvider(_ => new MockConfigurationProvider("Provider1", _ => { })));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Dispose is safe")]
    public void Manager_Dispose_ShouldBeSafe()
    {
        var manager = new ConfigurationManager();

        manager.Dispose();
        manager.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: DisposeAsync is safe")]
    public async Task Manager_DisposeAsync_ShouldBeSafe()
    {
        var manager = new ConfigurationManager();

        await manager.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Manager: Null options throws")]
    public void Manager_NullOptions_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigurationManager(null!));
    }

    private sealed class ThrowingConfigurationProvider : ConfigurationProvider
    {
        private readonly Exception _exception;

        public ThrowingConfigurationProvider(string name, Exception exception)
        {
            Name = name;
            _exception = exception;
        }

        public override string Name { get; }

        protected override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
        {
            throw _exception;
        }
    }

    private sealed class TrackingThrowingConfigurationProvider : ConfigurationProvider
    {
        private readonly Exception _exception;

        public TrackingThrowingConfigurationProvider(string name, Exception exception)
        {
            Name = name;
            _exception = exception;
        }

        public override string Name { get; }
        public bool WasDisposed { get; private set; }

        protected override Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
        {
            throw _exception;
        }

        protected override ValueTask OnDisposeAsync(IEnumerable<IConfigurationEntry> entries)
        {
            WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedConfigurationProvider : ConfigurationProvider
    {
        private readonly TimeSpan _delay;
        private readonly Action<IDictionary<Path, string?>> _onLoad;

        public DelayedConfigurationProvider(string name, TimeSpan delay, Action<IDictionary<Path, string?>> onLoad)
        {
            Name = name;
            _delay = delay;
            _onLoad = onLoad;
        }

        public override string Name { get; }

        protected override async Task OnLoadAsync(IDictionary<Path, string?> entries, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay).ConfigureAwait(false);
            _onLoad.Invoke(entries);
        }
    }
}
