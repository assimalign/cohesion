using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;


using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Server.Tests;

/// <summary>
/// The default server sizes each exchange's feature collection (#1381). It counts the application
/// features the pipeline stamps plus the features the host always installs, sets the listener's
/// <c>ExchangeFeatureCapacity</c> to that count before the listener configurations run, and rounds what
/// they leave up to a size an unsized collection grows through. These tests pin the count against a real
/// exchange, so a feature the host starts installing on every exchange without counting it fails here,
/// and they pin the rounding: an exchange carrying more features than counted, such as routing's route
/// match, must never allocate more than an unsized collection would.
/// </summary>
public class WebApplicationExchangeFeatureCapacityTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Server] - Feature capacity: Rounding should pick the smallest size an unsized collection grows through")]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 7)]
    [InlineData(7, 7)]
    [InlineData(8, 17)]
    [InlineData(11, 17)]
    [InlineData(17, 17)]
    [InlineData(18, 37)]
    [InlineData(20, 37)]
    [InlineData(38, 89)]
    [InlineData(919, 919)]
    [InlineData(920, 920)]
    public void RoundExchangeFeatureCapacity_Count_ShouldReturnTheSmallestGrowthSizeThatHoldsIt(int featureCount, int expected)
    {
        // Arrange — nothing beyond the inputs.

        // Act
        int capacity = WebApplicationServer.RoundExchangeFeatureCapacity(featureCount);

        // Assert
        capacity.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Server] - Feature capacity: An exchange carrying more features than counted should allocate no more than an unsized collection")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(30)]
    public void RoundExchangeFeatureCapacity_FeaturesPastTheCount_ShouldNotAllocateMoreThanAnUnsizedCollection(int applicationFeatureCount)
    {
        // Arrange — the host's count for this many application features, and every overflow up to twice
        // the rounded capacity: one feature past the count is routing's route match on a matched request
        // (seven application features plus it was the case that cost 144 B more than no presizing), and
        // more are Use* middleware features the count leaves out.
        int counted = WebApplicationServer.HostFeatureCount + applicationFeatureCount;
        int capacity = WebApplicationServer.RoundExchangeFeatureCapacity(counted);
        IHttpFeature[] features = CreateFeatures(2 * capacity);
        Stamp(new HttpFeatureCollection(capacity), features);
        Stamp(new HttpFeatureCollection(), features);

        for (int carried = counted + 1; carried <= features.Length; carried++)
        {
            IHttpFeature[] stamped = features[..carried];

            // Act
            long presized = CreateAndStamp(capacity, stamped);
            long unsized = CreateAndStamp(0, stamped);

            // Assert
            presized.ShouldBeLessThanOrEqualTo(unsized, $"{carried} features on a collection sized for {capacity}");
        }
    }

    // ------------------------------------------------------------------ helpers

    private static IHttpFeature[] CreateFeatures(int count)
    {
        IHttpFeature[] features = new IHttpFeature[count];

        for (int i = 0; i < count; i++)
        {
            features[i] = new NamedFeature($"Cohesion.Tests.StampedFeature{i}");
        }

        return features;
    }

    /// <summary>
    /// Sets every feature and returns the bytes the current thread allocated doing it.
    /// </summary>
    private static long Stamp(IHttpFeatureCollection collection, IHttpFeature[] features)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        foreach (IHttpFeature feature in features)
        {
            collection.Set(feature);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// Creates a collection sized for <paramref name="capacity"/> (<c>0</c> leaves it unsized), sets every
    /// feature, and returns the bytes the current thread allocated doing both.
    /// </summary>
    private static long CreateAndStamp(int capacity, IHttpFeature[] features)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        HttpFeatureCollection collection = new(capacity);

        foreach (IHttpFeature feature in features)
        {
            collection.Set(feature);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(collection);
        return allocated;
    }

    private sealed class NamedFeature : IHttpFeature
    {
        public NamedFeature(string name)
        {
            Name = name;
        }

        public string Name { get; }
    }

    private sealed class FeatureCountingPipeline : IWebApplicationPipeline
    {
        public int Carried { get; private set; } = -1;

        public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
        {
            Carried = context.Features.Count();
            return Task.CompletedTask;
        }
    }
}
