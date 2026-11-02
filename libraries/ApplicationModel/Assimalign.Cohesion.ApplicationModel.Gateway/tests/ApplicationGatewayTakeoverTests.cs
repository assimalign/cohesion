using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// Two runs of one gateway over one record, such as the old and the new gateway of a rolling update:
/// each run takes the record over, and the newer one wins.
/// </summary>
public sealed class ApplicationGatewayTakeoverTests : IDisposable
{
    private const string DisplayPrefix = "Cohesion Test [ApplicationModel] - ApplicationGateway takeover: ";

    private readonly TemporaryDirectory _directory = new();
    private readonly TestInfrastructure _infrastructure = new();
    private readonly JsonFileApplicationGatewayStore _store;

    public ApplicationGatewayTakeoverTests()
    {
        _store = JsonFileApplicationGatewayStore.Create(_directory.FullPath);
    }

    public void Dispose()
    {
        _directory.Dispose();
    }

    [Fact(DisplayName = DisplayPrefix + "Each run takes the record over with a higher epoch")]
    public async Task RunAsync_EachRun_ShouldTakeTheRecordOverWithAHigherEpoch()
    {
        // Arrange
        IApplicationModel model = CreateModel();

        // Act
        await using (await RunningGateway.StartAsync(_infrastructure, _store, model))
        {
        }

        long first = (await _store.LoadAsync("root"))!.Epoch;
        await using (await RunningGateway.StartAsync(_infrastructure, _store, model))
        {
        }

        long second = (await _store.LoadAsync("root"))!.Epoch;

        // Assert
        first.ShouldBe(1);
        second.ShouldBe(2);
    }

    [Fact(DisplayName = DisplayPrefix + "A run whose record a later run took over stops at its next save, and the later run keeps running")]
    public async Task RunAsync_WhenALaterRunTakesTheRecordOver_ShouldStopWithRecordSuperseded()
    {
        // Arrange: the old gateway runs, then the new one starts over the same record and adopts what
        // the old one deployed, as during a rolling update of the gateway itself.
        IApplicationModel model = CreateModel();
        ApplicationGateway earlier = RunningGateway.Create(_infrastructure, _store, model);
        using var stopEarlier = new CancellationTokenSource();
        Task earlierRun = earlier.RunAsync(stopEarlier.Token, out Task earlierStarted);
        await earlierStarted.WaitAsync(TimeSpan.FromSeconds(10));
        InstanceKey instance = _infrastructure.Instances.Keys.Single();

        ApplicationGateway later = RunningGateway.Create(_infrastructure, _store, model);
        using var stopLater = new CancellationTokenSource();
        Task laterRun = later.RunAsync(stopLater.Token, out Task laterStarted);
        await laterStarted.WaitAsync(TimeSpan.FromSeconds(10));

        // Act: an operator's write reaches the old gateway, which has to save it.
        ApplicationGatewayException refused = await Should.ThrowAsync<ApplicationGatewayException>(
            () => earlier.RecordAsync("Web", "orders-api", instance, TestManifests.LogLevelSnapshot("Debug")).AsTask());
        ApplicationGatewayException stopped = await Should.ThrowAsync<ApplicationGatewayException>(() => earlierRun.WaitAsync(TimeSpan.FromSeconds(10)));

        // Assert: the old run stopped and takes nothing more; the new one runs on and holds the record.
        refused.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.RecordSuperseded);
        stopped.ErrorCode.ShouldBe(ApplicationGatewayErrorCode.RecordSuperseded);
        stopped.Message.ShouldContain("epoch 1", Case.Sensitive);
        Should.Throw<ApplicationGatewayException>(() => earlier.PublishAsync(TestManifests.Instance()))
            .ErrorCode.ShouldBe(ApplicationGatewayErrorCode.PublicationsClosed);
        laterRun.IsCompleted.ShouldBeFalse();
        ApplicationGatewayRecord record = (await _store.LoadAsync("root")).ShouldNotBeNull();
        record.Epoch.ShouldBe(2);
        record.Resources.ShouldHaveSingleItem().State.ShouldBeEmpty();

        await stopLater.CancelAsync();
        await laterRun;
    }

    private static IApplicationModel CreateModel()
    {
        IApplicationModelBuilder builder = ApplicationModel.CreateBuilder("root");
        builder.AddResource("Web", "orders-api");
        return builder.Build();
    }
}
