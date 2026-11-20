using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Checks the rules on an <c>attach</c> that span more than one of its fields (AMQP 1.0 Part 2, section 2.7.3;
/// Part 3, sections 3.5.3 and 3.5.4): a peer's attach that breaks one loses only its link, and a local one
/// is refused before anything is sent.
/// </summary>
public class AmqpAttachRulesTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should refuse only the link when the peer's attach contradicts itself")]
    [InlineData("receiver-names-dynamic-source")]
    [InlineData("sender-names-dynamic-target")]
    [InlineData("sender-without-initial-delivery-count")]
    [InlineData("properties-on-fixed-target")]
    [InlineData("properties-on-fixed-source")]
    public async Task OnAttach_WithContradictoryFields_ShouldRefuseLinkWithInvalidField(string scenario)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        (AmqpAttachPerformative attach, string expected) = CreateContradictoryAttach(scenario);
        await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, attach, timeout.Token);
        AmqpAttachPerformative answer = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = attach.Handle, Closed = true }, timeout.Token);
        await peer.WriteFrameAsync(0, CreateSenderAttach("valid", handle: 1), timeout.Token);
        IAmqpLinkRequest request = await session.AcceptLinkAsync(timeout.Token);

        // Assert
        answer.Name.ShouldBe(attach.Name);
        answer.Role.ShouldNotBe(attach.Role);
        (answer.Role == AmqpRole.Sender ? (object?)answer.Source : answer.Target).ShouldBeNull();
        detach.Handle.ShouldBe(answer.Handle);
        detach.Closed.ShouldBeTrue();
        AmqpError error = detach.Error.ShouldNotBeNull();
        error.Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        error.Description.ShouldBe(expected);
        request.Name.ShouldBe("valid");
        host.Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should accept a sender that names the dynamic source it created")]
    public async Task OnAttach_WithSenderNamingItsDynamicSource_ShouldAttachEchoingTheSource()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        AmqpSenderOptions options = new()
        {
            Name = "dynamic-source",
            Source = new AmqpSource { Address = "tmp-1", Dynamic = true },
            Target = new AmqpTarget { Address = "queue" }
        };

        // Act
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, options, cancellationToken: timeout.Token);

        // Assert
        AmqpSource source = receiver.Source.ShouldNotBeNull();
        source.Dynamic.ShouldBeTrue();
        source.Address.ShouldBe("tmp-1");
        sender.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachReceiverAsync: Should take the address of the dynamic source the sender created")]
    public async Task AttachReceiverAsync_WithDynamicSourceRequest_ShouldTakeTheCreatedAddress()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        AmqpReceiverOptions options = new()
        {
            Name = "replies",
            Source = new AmqpSource { Dynamic = true },
            Target = new AmqpTarget { Address = "client" }
        };

        // Act
        Task<IAmqpReceiver> attach = clientSession.AttachReceiverAsync(options, timeout.Token).AsTask();
        IAmqpLinkRequest request = await serverSession.AcceptLinkAsync(timeout.Token);
        AmqpSource requested = request.Source.ShouldNotBeNull();
        IAmqpSender sender = await request.AcceptSenderAsync(
            new AmqpSenderOptions { Source = new AmqpSource { Dynamic = true, Address = "tmp-7" } },
            timeout.Token);
        IAmqpReceiver receiver = await attach;

        // Assert
        requested.Dynamic.ShouldBeTrue();
        requested.Address.ShouldBeNull();
        AmqpSource created = receiver.Source.ShouldNotBeNull();
        created.Dynamic.ShouldBeTrue();
        created.Address.ShouldBe("tmp-7");
        sender.Source.ShouldNotBeNull().Address.ShouldBe("tmp-7");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - AttachAsync: Should refuse a local attach that asks for a dynamic node and names its address")]
    [InlineData(AmqpRole.Receiver)]
    [InlineData(AmqpRole.Sender)]
    public async Task AttachAsync_WithDynamicRequestAndAddress_ShouldThrowBeforeSending(AmqpRole role)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        string terminus = role == AmqpRole.Receiver ? "source" : "target";

        // Act
        ArgumentException exception = role == AmqpRole.Receiver
            ? await Should.ThrowAsync<ArgumentException>(async () => await clientSession.AttachReceiverAsync(
                new AmqpReceiverOptions { Name = "link", Source = new AmqpSource { Dynamic = true, Address = "chosen" } },
                timeout.Token))
            : await Should.ThrowAsync<ArgumentException>(async () => await clientSession.AttachSenderAsync(
                new AmqpSenderOptions { Name = "link", Target = new AmqpTarget { Dynamic = true, Address = "chosen" } },
                timeout.Token));
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(
            clientSession,
            serverSession,
            new AmqpSenderOptions { Name = "link", Source = new AmqpSource { Address = "client" }, Target = new AmqpTarget { Address = "queue" } },
            cancellationToken: timeout.Token);

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DynamicAddressUnexpected, terminus));
        receiver.Name.ShouldBe("link");
        sender.Handle.ShouldBe(0u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachReceiverAsync: Should refuse a filter that is not described and leave the connection intact")]
    public async Task AttachReceiverAsync_WithFilterNotDescribed_ShouldThrowAndKeepTheConnectionIntact()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        AmqpReceiverOptions options = new()
        {
            Name = "filtered",
            Source = new AmqpSource
            {
                Address = "queue",
                Filter = new Dictionary<AmqpSymbol, object?> { ["selector"] = "color = 'red'" }
            }
        };

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            async () => await clientSession.AttachReceiverAsync(options, timeout.Token));
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, cancellationToken: timeout.Token);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[] { 1, 2, 3 }, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await delivery.AcceptAsync(timeout.Token);
        AmqpDeliveryState? outcome = await send;

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, ErrorMessages.FilterNotDescribed, "selector"));
        outcome.ShouldBeOfType<AmqpAccepted>();
        host.Client.State.ShouldBe(ConnectionState.Open);
        host.Server.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptReceiverAsync: Should refuse a contradictory answer and leave the request as the peer proposed it")]
    public async Task AcceptReceiverAsync_WithContradictoryTarget_ShouldThrowAndKeepTheRequestOpen()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        AmqpSenderOptions senderOptions = new()
        {
            Name = "link",
            Source = new AmqpSource { Address = "client" },
            Target = new AmqpTarget { Address = "queue" }
        };
        AmqpReceiverOptions refused = new()
        {
            Prefetch = 5,
            ReceiverSettleMode = AmqpReceiverSettleMode.Second,
            Properties = new Dictionary<AmqpSymbol, object?> { ["refused"] = true },
            Target = new AmqpTarget
            {
                Address = "queue",
                DynamicNodeProperties = new Dictionary<AmqpSymbol, object?> { ["lifetime-policy"] = null }
            }
        };
        Task<IAmqpSender> attach = clientSession.AttachSenderAsync(senderOptions, timeout.Token).AsTask();
        IAmqpLinkRequest request = await serverSession.AcceptLinkAsync(timeout.Token);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => request.AcceptReceiverAsync(refused, timeout.Token));
        IAmqpReceiver receiver = await request.AcceptReceiverAsync(null, timeout.Token);
        IAmqpSender sender = await attach;
        await sender.WaitForCreditAsync(timeout.Token);

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DynamicNodePropertiesUnexpected, "target"));
        receiver.ReceiverSettleMode.ShouldBe(AmqpReceiverSettleMode.First);
        sender.ReceiverSettleMode.ShouldBe(AmqpReceiverSettleMode.First);
        sender.RemoteProperties.ShouldBeNull();
        sender.Credit.ShouldBe(new AmqpReceiverOptions().Prefetch);
    }

    private static (AmqpAttachPerformative Attach, string Expected) CreateContradictoryAttach(string scenario)
    {
        Dictionary<AmqpSymbol, object?> properties = new() { ["lifetime-policy"] = null };

        return scenario switch
        {
            "receiver-names-dynamic-source" => (
                new AmqpAttachPerformative
                {
                    Name = "invalid",
                    Role = AmqpRole.Receiver,
                    Source = new AmqpSource { Dynamic = true, Address = "chosen" },
                    Target = new AmqpTarget { Address = "peer" }
                },
                Format(ErrorMessages.DynamicAddressUnexpected, "source")),
            "sender-names-dynamic-target" => (
                new AmqpAttachPerformative
                {
                    Name = "invalid",
                    Role = AmqpRole.Sender,
                    Source = new AmqpSource { Address = "peer" },
                    Target = new AmqpTarget { Dynamic = true, Address = "chosen" },
                    InitialDeliveryCount = 0
                },
                Format(ErrorMessages.DynamicAddressUnexpected, "target")),
            "sender-without-initial-delivery-count" => (
                new AmqpAttachPerformative
                {
                    Name = "invalid",
                    Role = AmqpRole.Sender,
                    Source = new AmqpSource { Address = "peer" },
                    Target = new AmqpTarget { Address = "queue" }
                },
                ErrorMessages.InitialDeliveryCountMissing),
            "properties-on-fixed-target" => (
                new AmqpAttachPerformative
                {
                    Name = "invalid",
                    Role = AmqpRole.Sender,
                    Source = new AmqpSource { Address = "peer" },
                    Target = new AmqpTarget { Address = "queue", DynamicNodeProperties = properties },
                    InitialDeliveryCount = 0
                },
                Format(ErrorMessages.DynamicNodePropertiesUnexpected, "target")),
            _ => (
                new AmqpAttachPerformative
                {
                    Name = "invalid",
                    Role = AmqpRole.Receiver,
                    Source = new AmqpSource { Address = "queue", DynamicNodeProperties = properties },
                    Target = new AmqpTarget { Address = "peer" }
                },
                Format(ErrorMessages.DynamicNodePropertiesUnexpected, "source"))
        };
    }

    private static string Format(string format, string terminus)
    {
        return string.Format(CultureInfo.CurrentCulture, format, terminus);
    }

    private static AmqpAttachPerformative CreateSenderAttach(string name, uint handle)
    {
        return new AmqpAttachPerformative
        {
            Name = name,
            Handle = handle,
            Role = AmqpRole.Sender,
            Source = new AmqpSource { Address = "peer" },
            Target = new AmqpTarget { Address = "queue" },
            InitialDeliveryCount = 0
        };
    }

    private static async Task<T> ReadUntilAsync<T>(AmqpTestPeer peer, CancellationToken cancellationToken)
        where T : AmqpPerformative
    {
        while (true)
        {
            AmqpTestFrame frame = await peer.ReadNonEmptyFrameAsync(cancellationToken);

            if (frame.Performative is T performative)
            {
                return performative;
            }

            frame.Performative.ShouldNotBeOfType<AmqpClosePerformative>();
        }
    }
}
