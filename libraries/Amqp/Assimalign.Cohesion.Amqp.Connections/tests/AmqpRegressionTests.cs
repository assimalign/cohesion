using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Checks the regression register (#433) in <see cref="AmqpRegressionRegister"/>, and holds the regression tests
/// for defects that no other test pins.
/// </summary>
public class AmqpRegressionTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public AmqpRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Register: Should hold a regression of every category, each citing the specification and naming what fails if its defect returns")]
    public void Register_OnEveryEntry_ShouldCiteTheSpecificationAndNameItsTests()
    {
        // Arrange
        IReadOnlyList<AmqpRegression> entries = AmqpRegressionRegister.Entries;
        AmqpRegressionCategory[] categories = Enum.GetValues<AmqpRegressionCategory>();

        // Act
        string[] identifiers = [.. entries.Select(entry => entry.Id)];

        foreach (AmqpRegressionCategory category in categories)
        {
            AmqpRegression[] members = [.. entries.Where(entry => entry.Category == category)];

            _output.WriteLine($"{category}: {members.Length} regressions.");

            foreach (AmqpRegression entry in members)
            {
                string rule = entry.Rule is null ? string.Empty : $"; {entry.Map} {entry.Rule}";

                _output.WriteLine($"  {entry.Id} ({entry.Origin}; {entry.Reference}{rule}): {entry.Tests.Count} tests, {entry.Cases.Count} corpus cases. {entry.Defect}");
            }
        }

        // Assert
        identifiers.Distinct().Count().ShouldBe(identifiers.Length);
        categories.ShouldAllBe(category => entries.Any(entry => entry.Category == category));

        foreach (AmqpRegression entry in entries)
        {
            entry.Reference.ShouldStartWith("Part ", Case.Sensitive, entry.Id);
            entry.Origin.ShouldStartWith("#", Case.Sensitive, entry.Id);
            (entry.Tests.Count + entry.Cases.Count).ShouldBeGreaterThan(0, entry.Id);
            entry.Tests.Distinct().Count().ShouldBe(entry.Tests.Count, entry.Id);
            HasRule(entry).ShouldBeTrue(entry.Id);
            entry.Cases.ShouldAllBe(name => HasCase(entry.Map, name), entry.Id);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachSenderAsync: Should leave no partial frame behind when the encoder refuses a value in the attach")]
    public async Task AttachSenderAsync_WithValueTheEncoderRefuses_ShouldWriteNothingAndKeepTheConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        AmqpSenderOptions refused = new()
        {
            Name = "refused",
            Source = new AmqpSource { Address = "client" },
            Target = new AmqpTarget { Address = "queue" },
            Properties = new Dictionary<AmqpSymbol, object?> { ["timeout"] = TimeSpan.FromSeconds(1) }
        };

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            async () => await clientSession.AttachSenderAsync(refused, timeout.Token));
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[] { 1, 2, 3 }, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await delivery.AcceptAsync(timeout.Token);
        AmqpDeliveryState? outcome = await send;

        // Assert
        exception.Message.ShouldContain(typeof(TimeSpan).ToString(), Case.Sensitive);
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        outcome.ShouldBeOfType<AmqpAccepted>();
        host.Client.State.ShouldBe(ConnectionState.Open);
        host.Server.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should leave no partial frame behind when the encoder refuses the delivery state")]
    public async Task SendAsync_WithStateTheEncoderRefuses_ShouldWriteNothingAndKeepTheLink()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);
        await sender.WaitForCreditAsync(timeout.Token);
        AmqpDeliveryOptions refused = new()
        {
            State = new AmqpModified(messageAnnotations: new Dictionary<AmqpSymbol, object?> { ["x-opt-wait"] = TimeSpan.FromSeconds(1) })
        };

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            async () => await sender.SendAsync(new byte[] { 9 }, refused, timeout.Token));
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[] { 1, 2, 3 }, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await delivery.AcceptAsync(timeout.Token);
        AmqpDeliveryState? outcome = await send;

        // Assert
        exception.Message.ShouldContain(typeof(TimeSpan).ToString(), Case.Sensitive);
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        outcome.ShouldBeOfType<AmqpAccepted>();
        sender.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        host.Client.State.ShouldBe(ConnectionState.Open);
        host.Server.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DisposeAsync: Should release the carrier when the peer keeps its side open and never answers close")]
    public async Task DisposeAsync_OnPeerThatNeverAnswersClose_ShouldReleaseTheCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new()
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(500),
            IdleTimeout = TimeSpan.Zero
        };
        (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(options, timeout.Token);
        await using AmqpConnectionListener owned = listener;
        await peer.OpenAsClientAsync(null, timeout.Token);
        IAmqpConnection connection = await accept;

        // Act
        await connection.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        error.ShouldBeNull();
        peer.Carrier.IsDisposed.ShouldBeTrue();
        connection.State.ShouldBe(ConnectionState.Closed);
    }

    /// <summary>
    /// Whether the entry's rule is one its compliance map holds, or, for an entry outside every map, absent.
    /// </summary>
    private static bool HasRule(AmqpRegression entry)
    {
        return entry.Map switch
        {
            AmqpComplianceMap.None => entry.Rule is null,
            AmqpComplianceMap.HeaderPhase => AmqpHeaderPhaseCorpus.Rules.Any(rule => rule.Id == entry.Rule),
            AmqpComplianceMap.TypeSystem => AmqpTypeSystemCorpus.Types.Any(type => type.Name == entry.Rule),
            AmqpComplianceMap.MessageFormat => AmqpMessageFormatCorpus.Rules.Any(rule => rule.Id == entry.Rule),
            AmqpComplianceMap.Terminus => AmqpTerminusCorpus.Rules.Any(rule => rule.Id == entry.Rule),
            AmqpComplianceMap.Transfer => AmqpTransferComplianceTests.Rules.Any(rule => rule.Id == entry.Rule),
            AmqpComplianceMap.Transaction => AmqpTransactionComplianceTests.Rules.Any(rule => rule.Id == entry.Rule),
            AmqpComplianceMap.Resume => AmqpResumeComplianceTests.Rules.Any(rule => rule.Id == entry.Rule),
            _ => false
        };
    }

    /// <summary>
    /// Whether the map's corpus holds a case with the name. The transfer, transaction and resume maps name tests,
    /// not corpus cases, so they hold none.
    /// </summary>
    private static bool HasCase(AmqpComplianceMap map, string name)
    {
        return map switch
        {
            AmqpComplianceMap.HeaderPhase => AmqpHeaderPhaseCorpus.Cases.Any(item => item.Name == name),
            AmqpComplianceMap.TypeSystem => AmqpTypeSystemCorpus.Cases.Any(item => item.Name == name),
            AmqpComplianceMap.MessageFormat => AmqpMessageFormatCorpus.Cases.Any(item => item.Name == name),
            AmqpComplianceMap.Terminus => AmqpTerminusCorpus.Cases.Any(item => item.Name == name),
            _ => false
        };
    }
}
