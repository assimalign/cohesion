using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Connections.Tests;

public class ConnectionFeaturesTests
{
    [Theory(DisplayName = "Cohesion Test [Connections] - Features: All guided bases defer collection creation until first access")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Features_BeforeFirstAccess_ShouldNotReadInnerOrConfigure(int shape)
    {
        // Arrange
        var probe = new FeatureConnectionProbe();

        // Act
        (object connection, Func<IConnectionFeatureCollection> getFeatures) = Create(shape, probe);

        // Assert
        probe.InnerReads.ShouldBe(0);
        probe.ConfigurationCount.ShouldBe(0);
        (connection is IConnectionFeatureCollection).ShouldBeFalse();
        IConnectionFeatureCollection features = getFeatures();
        features.ShouldBeOfType<ConnectionFeatureCollection>();
        getFeatures().ShouldBeSameAs(features);
        probe.InnerReads.ShouldBe(1);
        probe.ConfigurationCount.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Connections] - Features: All guided bases configure their published collection over InnerFeatures")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Features_WithInnerAndOwnRegistrations_ShouldPublishConfiguredLayer(int shape)
    {
        // Arrange
        var inherited = new TestConnectionFeature("Inherited");
        var own = new TestConnectionFeature("Own");
        var inner = new ConnectionFeatureCollection();
        inner.Set(inherited);
        IConnectionFeatureCollection? configured = null;
        var probe = new FeatureConnectionProbe
        {
            Inner = inner,
            Configure = features =>
            {
                configured = features;
                features.Set(own);
            }
        };
        (_, Func<IConnectionFeatureCollection> getFeatures) = Create(shape, probe);

        // Act
        IConnectionFeatureCollection features = getFeatures();

        // Assert
        features.ShouldBeSameAs(configured);
        features.Get("Inherited").ShouldBeSameAs(inherited);
        features.Get<TestConnectionFeature>().ShouldBeSameAs(own);
        features.ToArray().ShouldBe(new IConnectionFeature[] { own, inherited });
    }

    [Theory(DisplayName = "Cohesion Test [Connections] - Features: Concurrent first access configures once and waits for the completed collection")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Features_WithConcurrentFirstAccess_ShouldConfigureOnceBeforePublication(int shape)
    {
        // Arrange
        using var ready = new CountdownEvent(8);
        using var start = new ManualResetEventSlim();
        using var configuring = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var own = new TestConnectionFeature("Own");
        var probe = new FeatureConnectionProbe
        {
            Configure = features =>
            {
                configuring.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release feature configuration.");
                }
                features.Set(own);
            }
        };
        (_, Func<IConnectionFeatureCollection> getFeatures) = Create(shape, probe);
        Task<IConnectionFeatureCollection>[] readers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            if (!start.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not start feature readers.");
            }
            IConnectionFeatureCollection features = getFeatures();
            features.Get("Own").ShouldBeSameAs(own);
            return features;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        // Act
        try
        {
            ready.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            start.Set();
            configuring.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            readers.All(reader => !reader.IsCompleted).ShouldBeTrue();
        }
        finally
        {
            start.Set();
            release.Set();
        }
        IConnectionFeatureCollection[] results = await Task.WhenAll(readers);

        // Assert
        probe.ConfigurationCount.ShouldBe(1);
        probe.InnerReads.ShouldBe(1);
        results.All(features => ReferenceEquals(features, results[0])).ShouldBeTrue();
        getFeatures().ShouldBeSameAs(results[0]);
    }

    [Theory(DisplayName = "Cohesion Test [Connections] - Features: Failed configuration is not invoked again or partially published")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Features_WithFailedConfiguration_ShouldRetainFailure(int shape)
    {
        // Arrange
        var failure = new InvalidOperationException("Feature configuration failed.");
        var probe = new FeatureConnectionProbe
        {
            Configure = features =>
            {
                features.Set(new TestConnectionFeature("Partial"));
                throw failure;
            }
        };
        (_, Func<IConnectionFeatureCollection> getFeatures) = Create(shape, probe);

        // Act and assert
        Should.Throw<InvalidOperationException>(() => getFeatures()).ShouldBeSameAs(failure);
        Should.Throw<InvalidOperationException>(() => getFeatures()).ShouldBeSameAs(failure);
        probe.ConfigurationCount.ShouldBe(1);
    }

    private static (object Connection, Func<IConnectionFeatureCollection> GetFeatures) Create(int shape, FeatureConnectionProbe probe)
    {
        switch (shape)
        {
            case 0:
                IConnection stream = new FeatureProbeConnection(probe);
                return (stream, () => stream.Features);
            case 1:
                IMultiplexedConnection multiplexed = new FeatureProbeMultiplexedConnection(probe);
                return (multiplexed, () => multiplexed.Features);
            default:
                IDatagramConnection datagram = new FeatureProbeDatagramConnection(probe);
                return (datagram, () => datagram.Features);
        }
    }
}
