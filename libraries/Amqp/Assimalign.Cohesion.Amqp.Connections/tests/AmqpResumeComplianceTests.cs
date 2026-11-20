using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

using static Assimalign.Cohesion.Amqp.Connections.Tests.AmqpPeerScript;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Drives the engine's links through the rules of AMQP 1.0 for resuming deliveries from an earlier attachment of
/// a link: comparing the two unsettled maps, resuming, ignoring, aborting and settling what they list, and
/// holding back new deliveries until both maps are complete, with the worked example of Part 3, section 3.4.6.
/// Each rule in <see cref="Rules"/> names the tests that pin it.
/// </summary>
public class AmqpResumeComplianceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    public static TheoryData<bool, bool> IncompleteFlags => new()
    {
        { true, false },
        { false, true },
        { true, true }
    };

    public static TheoryData<bool, bool> OneIncompleteFlag => new()
    {
        { true, false },
        { false, true }
    };

    /// <summary>
    /// Gets the tags of deliveries no resume may name on a link whose source lists 1 and 2 and whose target lists
    /// 1 and 3: one only the source lists, one only the target lists, and one neither lists.
    /// </summary>
    public static TheoryData<ulong> TagsNotResumable => new() { 2, 3, 9 };

    /// <summary>
    /// Gets the rules for resuming deliveries that the engine holds, each with the tests that pin it.
    /// </summary>
    internal static IReadOnlyList<AmqpTransferRule> Rules { get; } =
    [
        new("R1", "Each end compares the two unsettled maps of the attach frames alike: the sender's map is the source's and the receiver's the target's, whichever end attaches first, and both ends report which map was incomplete.", "Part 2, section 2.6.13", Forbids: false,
        [
            nameof(AttachAsync_WithUnsettledMaps_ShouldCompareThemAlikeAtBothEnds),
            nameof(AttachAsync_WithIncompleteMap_ShouldReportItAtBothEnds)
        ]),
        new("R2", "A sender resumes or aborts only a delivery the comparison leaves resumable, once per attachment, and refuses any other before sending it. The specification lets a sender that has not seen the receiver's map yet pipeline a resume of a delivery only the source lists, which the receiver ignores; the engine sends only once it holds that map, so such a resume could only be ignored and its delivery would never settle.", "Part 2, sections 2.6.13 and 2.7.5", Forbids: true,
        [
            nameof(SendAsync_OnResumeOfDeliveryNotResumable_ShouldRefuse),
            nameof(AbortDeliveryAsync_OnDeliveryNotResumable_ShouldRefuse),
            nameof(SendAsync_OnSecondResumeOfDelivery_ShouldRefuse)
        ]),
        new("R3", "A receiver ignores a resumed delivery its comparison does not leave resumable, or that a transfer already resumed on this attachment: nothing reaches the application, nothing is settled and no partial state shows, but the delivery takes a unit of credit, as the sender counted it, and is measured against max-message-size.", "Part 2, sections 2.6.13 and 2.7.5", Forbids: false,
        [
            nameof(OnTransfer_OnResumeOfDeliveryNotHeld_ShouldIgnoreIt),
            nameof(OnTransfer_OnSplitResumeOfDeliveryNotHeld_ShouldIgnoreEveryFrame),
            nameof(OnTransfer_OnResumeOfDeliveryAlreadyResumed_ShouldIgnoreIt),
            nameof(OnTransfer_OnIgnoredResumeAboveMaxMessageSize_ShouldDetachWithMessageSizeExceeded)
        ]),
        new("R4", "Each delivery of the resumption example goes as its two map entries require: sent again as a new delivery when the receiver has no record of it, resumed from the receiver's position with the rest of its payload, resumed whole, resumed without payload to convey the sender's outcome or settlement, which the receiver echoes when the two outcomes differ, or aborted when it cannot be resumed to completion.", "Part 2, section 2.6.13, and Part 3, section 3.4.6", Forbids: false,
        [
            nameof(SendAsync_OnPart3Example_ShouldResumeEachDeliveryAsItsMapsRequire)
        ]),
        new("R5", "A sender aborts a delivery it cannot resume to completion with one resuming frame that sets aborted and carries no payload, which takes a unit of credit. The receiver hands the aborted delivery to the application, settled and empty, since only the application holds what it kept of the delivery.", "Part 3, section 3.4.6, and Part 2, section 2.6.14", Forbids: false,
        [
            nameof(AbortDeliveryAsync_OnResumableDelivery_ShouldSendOneAbortingResume),
            nameof(OnTransfer_OnAbortedResume_ShouldDeliverItAborted)
        ]),
        new("R6", "A resumed delivery split into frames sets resume on its first frame, and its later frames may repeat it.", "Part 2, section 2.7.5", Forbids: false,
        [
            nameof(SendAsync_OnSplitResumedDelivery_ShouldSetResumeOnFirstFrame),
            nameof(OnTransfer_OnSplitResumeRepeatingFlag_ShouldJoinFrames)
        ]),
        new("R7", "Only the first frame of a resumed delivery carries the received state: a sender refuses it on a delivery it does not resume, and a receiver detaches a link whose sender puts it on a delivery that is not resumed, on a later frame or on a disposition, with amqp:invalid-field.", "Part 3, section 3.4.1", Forbids: true,
        [
            nameof(SendAsync_OnReceivedStateWithoutResume_ShouldRefuse),
            nameof(OnTransfer_OnReceivedStateWithoutResume_ShouldDetachWithInvalidField),
            nameof(OnTransfer_OnReceivedStateOnContinuation_ShouldDetachWithInvalidField),
            nameof(OnDisposition_OnReceivedStateFromSender_ShouldDetachWithInvalidField)
        ]),
        new("R8", "New deliveries wait until both maps are complete: while either is incomplete, a sender refuses a new delivery and still resumes, and a receiver delivers a resume but detaches a link whose sender starts a new delivery with amqp:illegal-state.", "Part 2, sections 2.6.13 and 2.7.3", Forbids: true,
        [
            nameof(SendAsync_OnNewDeliveryWhileMapIncomplete_ShouldRefuseItAndKeepResuming),
            nameof(OnTransfer_OnNewDeliveryWhileMapIncomplete_ShouldDetachWithIllegalState),
            nameof(OnTransfer_OnResumeWhileMapIncomplete_ShouldDeliverIt)
        ]),
        new("R9", "A delivery the comparison leaves resumable keeps its tag until it is resumed: a sender refuses a new delivery under it, and a receiver detaches a link whose sender starts one with amqp:illegal-state. A generated tag skips every tag either map listed.", "Part 2, sections 2.6.12 and 2.6.13", Forbids: true,
        [
            nameof(SendAsync_OnNewDeliveryUnderAwaitedTag_ShouldRefuse),
            nameof(OnTransfer_OnNewDeliveryUnderAwaitedTag_ShouldDetachWithIllegalState),
            nameof(SendAsync_WithGeneratedTags_ShouldSkipCarriedOverTags)
        ]),
        new("R10", "An unsettled map is keyed by delivery tags, binaries of at most 32 bytes, with no null key and no tag twice, as a map's keys are distinct: a decoder refuses any other with amqp:decode-error.", "Part 2, sections 2.7.3 and 2.8.7, and Part 1, section 1.6.23", Forbids: true,
        [
            nameof(Decode_OnMalformedUnsettledMap_ShouldThrowDecodeError)
        ]),
        new("R11", "A later frame of a delivery that is not resumed may set resume: a receiver reads the flag on a delivery's first frame alone, since that frame decides whether the delivery is resumed and a later one cannot reassociate it.", "Part 2, section 2.7.5", Forbids: false,
        [
            nameof(OnTransfer_OnResumeFlagOnContinuationOfNewDelivery_ShouldIgnoreIt)
        ], Leniency: true)
    ];

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Rules: Should name at least one test for every resumption rule, with unique identifiers")]
    public void Rules_OnResumeMap_ShouldNameTestsForEveryRule()
    {
        // Arrange
        IReadOnlyList<AmqpTransferRule> rules = Rules;

        // Act
        string[] identifiers = [.. rules.Select(rule => rule.Id)];

        // Assert
        identifiers.Distinct().Count().ShouldBe(identifiers.Length);
        rules.ShouldAllBe(rule => rule.Tests.Count > 0
            && (rule.Reference.StartsWith("Part 2", StringComparison.Ordinal) || rule.Reference.StartsWith("Part 3", StringComparison.Ordinal)));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - AttachAsync: Should compare the unsettled maps alike at both ends, whichever end attaches first")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttachAsync_WithUnsettledMaps_ShouldCompareThemAlikeAtBothEnds(bool senderAttachesFirst)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);

        // Act
        (IAmqpSender sender, IAmqpReceiver receiver) = await AttachResumedAsync(
            host, Part3Source(), false, Part3Target(), false, senderAttachesFirst, timeout.Token);

        // Assert
        foreach (AmqpUnsettledReconciliation reconciliation in new[] { sender.UnsettledReconciliation, receiver.UnsettledReconciliation })
        {
            reconciliation.IsComplete.ShouldBeTrue();
            Numbers(reconciliation.Resumable).ShouldBe([2, 3, 4, 6, 7, 8, 9, 11, 12, 13, 14]);
            Numbers(reconciliation.UnknownToReceiver).ShouldBe([1, 5, 10]);
            reconciliation.Settled.ShouldBeEmpty();
            reconciliation.InDoubt.ShouldBeEmpty();
        }

        sender.RemoteUnsettled.ShouldNotBeNull().Count.ShouldBe(11);
        receiver.RemoteUnsettled.ShouldNotBeNull().Count.ShouldBe(14);
        sender.RemoteUnsettled[Tag(2)].ShouldBeOfType<AmqpReceived>().SectionNumber.ShouldBe(3u);
        receiver.RemoteUnsettled[Tag(7)].ShouldBeOfType<AmqpReceived>().SectionOffset.ShouldBe(500ul);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - AttachAsync: Should report an incomplete unsettled map at both ends")]
    [MemberData(nameof(IncompleteFlags))]
    public async Task AttachAsync_WithIncompleteMap_ShouldReportItAtBothEnds(bool sourceIncomplete, bool targetIncomplete)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);

        // Act
        (IAmqpSender sender, IAmqpReceiver receiver) = await AttachResumedAsync(
            host, Map((1, null)), sourceIncomplete, Map((1, null), (2, null)), targetIncomplete, true, timeout.Token);

        // Assert
        foreach (AmqpUnsettledReconciliation reconciliation in new[] { sender.UnsettledReconciliation, receiver.UnsettledReconciliation })
        {
            reconciliation.SourceIncomplete.ShouldBe(sourceIncomplete);
            reconciliation.TargetIncomplete.ShouldBe(targetIncomplete);
            reconciliation.IsComplete.ShouldBeFalse();
            Numbers(reconciliation.Resumable).ShouldBe(sourceIncomplete ? [1, 2] : [1]);
        }

        sender.RemoteIncompleteUnsettled.ShouldBe(targetIncomplete);
        receiver.RemoteIncompleteUnsettled.ShouldBe(sourceIncomplete);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should refuse to resume a delivery the comparison does not leave resumable, sending nothing")]
    [MemberData(nameof(TagsNotResumable))]
    public async Task SendAsync_OnResumeOfDeliveryNotResumable_ShouldRefuse(ulong tag)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachResumedSenderAsync(
            host, source: Map((1, null), (2, null)), target: Map((1, null), (3, AmqpAccepted.Instance)), credit: 1, cancellationToken: timeout.Token);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            sender.SendAsync(new byte[] { 1 }, ResumeOptions(tag, settled: true), timeout.Token).AsTask());
        uint credit = sender.Credit;
        await sender.SendAsync(new byte[] { 1 }, ResumeOptions(1, settled: true), timeout.Token);
        AmqpTransferPerformative next = await ReadUntilAsync<AmqpTransferPerformative>(host.Peer, timeout.Token);

        // Assert
        exception.ParamName.ShouldBe("options");
        credit.ShouldBe(1u);
        next.DeliveryTag.ShouldBe(Tag(1));
        next.Resume.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - AbortDeliveryAsync: Should refuse to abort a delivery the comparison does not leave resumable, sending nothing")]
    [MemberData(nameof(TagsNotResumable))]
    public async Task AbortDeliveryAsync_OnDeliveryNotResumable_ShouldRefuse(ulong tag)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachResumedSenderAsync(
            host, source: Map((1, null), (2, null)), target: Map((1, null), (3, AmqpAccepted.Instance)), credit: 1, cancellationToken: timeout.Token);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(sender.AbortDeliveryAsync(Tag(tag), timeout.Token).AsTask());
        uint credit = sender.Credit;
        await sender.AbortDeliveryAsync(Tag(1), timeout.Token);
        AmqpTransferPerformative next = await ReadUntilAsync<AmqpTransferPerformative>(host.Peer, timeout.Token);

        // Assert
        exception.ParamName.ShouldBe("deliveryTag");
        credit.ShouldBe(1u);
        next.DeliveryTag.ShouldBe(Tag(1));
        next.Aborted.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should refuse to resume or abort a delivery a transfer already resumed on this attachment")]
    public async Task SendAsync_OnSecondResumeOfDelivery_ShouldRefuse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachResumedSenderAsync(
            host, source: Map((1, null)), target: Map((1, null)), credit: 2, cancellationToken: timeout.Token);
        await sender.SendAsync(new byte[] { 1 }, ResumeOptions(1, settled: true), timeout.Token);
        AmqpTransferPerformative resumed = await ReadUntilAsync<AmqpTransferPerformative>(host.Peer, timeout.Token);

        // Act
        ArgumentException resend = await Should.ThrowAsync<ArgumentException>(
            sender.SendAsync(new byte[] { 1 }, ResumeOptions(1, settled: true), timeout.Token).AsTask());
        ArgumentException abort = await Should.ThrowAsync<ArgumentException>(sender.AbortDeliveryAsync(Tag(1), timeout.Token).AsTask());

        // Assert
        resumed.Resume.ShouldBeTrue();
        resend.ParamName.ShouldBe("options");
        abort.ParamName.ShouldBe("deliveryTag");
        sender.Credit.ShouldBe(1u);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should ignore a resume of a delivery the comparison does not leave resumable, while it takes a unit of credit")]
    [MemberData(nameof(TagsNotResumable))]
    public async Task OnTransfer_OnResumeOfDeliveryNotHeld_ShouldIgnoreIt(ulong tag)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((1, null), (2, null)), target: Map((1, null), (3, AmqpAccepted.Instance)), prefetch: 0, cancellationToken: timeout.Token);
        receiver.AddCredit(2);

        // Act
        await peer.WriteFrameAsync(0, Resuming(CreateTransfer(deliveryId: 0, deliveryTag: Tag(tag))), timeout.Token, payload: new byte[] { 9 });
        await peer.WriteFrameAsync(0, Resuming(CreateTransfer(deliveryId: 1, deliveryTag: Tag(1))), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        bool more = receiver.TryReceive(out _);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 2, deliveryTag: Tag(5)), timeout.Token, payload: new byte[] { 5 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        delivery.DeliveryTag.ShouldBe(Tag(1));
        delivery.IsResumed.ShouldBeTrue();
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1 });
        more.ShouldBeFalse();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransferLimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should ignore every frame of a split resume of a delivery the receiver does not hold, showing no partial state")]
    public async Task OnTransfer_OnSplitResumeOfDeliveryNotHeld_ShouldIgnoreEveryFrame()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((1, null), (2, null)), target: Map((1, null)), cancellationToken: timeout.Token);
        AmqpTransferPerformative ignored = First(Resuming(CreateTransfer(deliveryId: 0, state: AmqpAccepted.Instance, deliveryTag: Tag(2))));

        // Act
        await peer.WriteFrameAsync(0, ignored, timeout.Token, payload: new byte[] { 9, 9 });
        await peer.WriteFrameAsync(0, Continuation(more: true), timeout.Token, payload: new byte[] { 9 });
        await SyncAsync(peer, timeout.Token);
        AmqpDeliveryState? partial = receiver.PartialDeliveryState;
        await peer.WriteFrameAsync(0, Continuation(), timeout.Token, payload: new byte[] { 9 });
        await peer.WriteFrameAsync(0, Resuming(CreateTransfer(deliveryId: 1, deliveryTag: Tag(1))), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        partial.ShouldBeNull();
        delivery.DeliveryTag.ShouldBe(Tag(1));
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1 });
        receiver.TryReceive(out _).ShouldBeFalse();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should ignore a second resume of a delivery a transfer already resumed on this attachment")]
    public async Task OnTransfer_OnResumeOfDeliveryAlreadyResumed_ShouldIgnoreIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((1, null)), target: Map((1, null)), cancellationToken: timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, Resuming(CreateTransfer(deliveryId: 0, deliveryTag: Tag(1))), timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, Resuming(CreateTransfer(deliveryId: 1, deliveryTag: Tag(1))), timeout.Token, payload: new byte[] { 9 });
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 2, deliveryTag: Tag(5)), timeout.Token, payload: new byte[] { 5 });
        IAmqpDelivery resumed = await receiver.ReceiveAsync(timeout.Token);
        IAmqpDelivery next = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        resumed.DeliveryTag.ShouldBe(Tag(1));
        resumed.Payload.ToArray().ShouldBe(new byte[] { 1 });
        next.DeliveryTag.ShouldBe(Tag(5));
        next.IsResumed.ShouldBeFalse();
        receiver.TryReceive(out _).ShouldBeFalse();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose ignored resume grows past max-message-size with amqp:message-size-exceeded")]
    public async Task OnTransfer_OnIgnoredResumeAboveMaxMessageSize_ShouldDetachWithMessageSizeExceeded()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((1, null), (2, null)), target: Map((1, null)), maxMessageSize: 16, cancellationToken: timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, First(Resuming(CreateTransfer(deliveryId: 0, deliveryTag: Tag(2)))), timeout.Token, payload: new byte[10]);
        await peer.WriteFrameAsync(0, Continuation(), timeout.Token, payload: new byte[10]);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.MessageSizeExceeded);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should resume, send again, settle or abort each delivery of the Part 3 resumption example as its two map entries require")]
    public async Task SendAsync_OnPart3Example_ShouldResumeEachDeliveryAsItsMapsRequire()
    {
        // Arrange: the unsettled maps of AMQP 1.0 Part 3, section 3.4.6, and a message whose last byte the
        // receivers of tags 2 and 6 still miss.
        using CancellationTokenSource timeout = new(_testTimeout);
        CancellationToken cancellationToken = timeout.Token;
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: cancellationToken);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AttachResumedAsync(
            host, Part3Source(), false, Part3Target(), false, true, cancellationToken);
        byte[] message = [0x10, 0x20, 0x30];
        byte[] remainder = [0x30];
        List<string> arrived = [];
        List<AmqpDeliveryState?> outcomes = [];

        // Act
        // Tag 1: the receiver has no record of it, so the message goes again as a new delivery under a new tag.
        outcomes.Add(await TransferAsync(sender, receiver, message, null, false, arrived, cancellationToken));

        // Tag 2: the receiver kept part of the message, so the sender resumes from where the receiver stopped.
        outcomes.Add(await TransferAsync(sender, receiver, remainder, ResumeOptions(2, new AmqpReceived(3, 0)), false, arrived, cancellationToken));

        // Tag 3: the receiver accepted it, so the sender resumes it settled with its own outcome and no payload.
        outcomes.Add(await TransferAsync(sender, receiver, [], ResumeOptions(3, AmqpAccepted.Instance, settled: true), false, arrived, cancellationToken));

        // Tag 4: the receiver knows only that it began, so the sender resumes the whole message.
        outcomes.Add(await TransferAsync(sender, receiver, message, ResumeOptions(4), false, arrived, cancellationToken));

        // Tag 5: as tag 1.
        outcomes.Add(await TransferAsync(sender, receiver, message, null, false, arrived, cancellationToken));

        // Tag 6: as tag 2, from a later point the receiver reached.
        outcomes.Add(await TransferAsync(sender, receiver, remainder, ResumeOptions(6, new AmqpReceived(2, 0)), false, arrived, cancellationToken));

        // Tag 7: the receiver needs data from before where the sender can resume, so the sender aborts it.
        await AbortAsync(sender, receiver, 7, arrived, cancellationToken);

        // Tag 8: as tag 3.
        outcomes.Add(await TransferAsync(sender, receiver, [], ResumeOptions(8, AmqpAccepted.Instance, settled: true), false, arrived, cancellationToken));

        // Tag 9: as tag 7.
        await AbortAsync(sender, receiver, 9, arrived, cancellationToken);

        // Tag 10: the sender reached its outcome and the receiver has no record of it: nothing to send.

        // Tag 11: the sender reached its outcome before the receiver had the whole message, so it aborts it.
        await AbortAsync(sender, receiver, 11, arrived, cancellationToken);

        // Tag 12: both ends accepted it, so the sender settles it.
        outcomes.Add(await TransferAsync(sender, receiver, [], ResumeOptions(12, settled: true), false, arrived, cancellationToken));

        // Tag 13: the outcomes differ, so the sender restates its own, which the receiver echoes as it settles.
        outcomes.Add(await TransferAsync(sender, receiver, [], ResumeOptions(13, AmqpAccepted.Instance), true, arrived, cancellationToken));

        // Tag 14: as tag 11.
        await AbortAsync(sender, receiver, 14, arrived, cancellationToken);

        // Assert
        arrived.ShouldBe(
        [
            "00: resumed=False, aborted=False, settled=False, state=none, payload=102030",
            "02: resumed=True, aborted=False, settled=False, state=received(3,0), payload=30",
            "03: resumed=True, aborted=False, settled=True, state=AmqpAccepted, payload=",
            "04: resumed=True, aborted=False, settled=False, state=none, payload=102030",
            "0f: resumed=False, aborted=False, settled=False, state=none, payload=102030",
            "06: resumed=True, aborted=False, settled=False, state=received(2,0), payload=30",
            "07: resumed=True, aborted=True, settled=True, state=none, payload=",
            "08: resumed=True, aborted=False, settled=True, state=AmqpAccepted, payload=",
            "09: resumed=True, aborted=True, settled=True, state=none, payload=",
            "0b: resumed=True, aborted=True, settled=True, state=none, payload=",
            "0c: resumed=True, aborted=False, settled=True, state=none, payload=",
            "0d: resumed=True, aborted=False, settled=False, state=AmqpAccepted, payload=",
            "0e: resumed=True, aborted=True, settled=True, state=none, payload="
        ]);
        outcomes.Select(outcome => outcome?.GetType().Name ?? "none").ShouldBe(
        [
            "AmqpAccepted", "AmqpAccepted", "none", "AmqpAccepted", "AmqpAccepted", "AmqpAccepted", "none", "none", "AmqpAccepted"
        ]);
        receiver.TryReceive(out _).ShouldBeFalse();
        sender.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AbortDeliveryAsync: Should abort a delivery carried over with one resuming frame that sets aborted and carries no payload, taking a unit of credit")]
    public async Task AbortDeliveryAsync_OnResumableDelivery_ShouldSendOneAbortingResume()
    {
        // Arrange: the sender can resume from offset 500, past the 100 bytes the receiver holds.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachResumedSenderAsync(
            host, source: Map((7, new AmqpReceived(0, 500))), target: Map((7, new AmqpReceived(0, 100))), credit: 1, cancellationToken: timeout.Token);

        // Act
        await sender.AbortDeliveryAsync(Tag(7), timeout.Token);
        AmqpTestFrame frame = await ReadFrameUntilAsync<AmqpTransferPerformative>(host.Peer, timeout.Token);

        // Assert
        AmqpTransferPerformative transfer = (AmqpTransferPerformative)frame.Performative!;
        transfer.DeliveryId.ShouldNotBeNull();
        transfer.DeliveryTag.ShouldBe(Tag(7));
        transfer.Resume.ShouldBeTrue();
        transfer.Aborted.ShouldBeTrue();
        transfer.More.ShouldBeFalse();
        frame.Payload.ShouldBeEmpty();
        sender.Credit.ShouldBe(0u);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should hand an aborted resume to the application settled and empty, and keep receiving")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnTransfer_OnAbortedResume_ShouldDeliverItAborted(bool split)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((7, null)), target: Map((7, new AmqpReceived(0, 100))), cancellationToken: timeout.Token);

        // Act
        if (split)
        {
            await peer.WriteFrameAsync(0, First(Resuming(CreateTransfer(deliveryId: 0, state: new AmqpReceived(0, 100), deliveryTag: Tag(7)))), timeout.Token, payload: new byte[] { 1, 2 });
            await peer.WriteFrameAsync(0, new AmqpTransferPerformative { Handle = 0, Aborted = true }, timeout.Token);
        }
        else
        {
            AmqpTransferPerformative aborting = Resuming(CreateTransfer(deliveryId: 0, deliveryTag: Tag(7)));
            aborting.Aborted = true;
            await peer.WriteFrameAsync(0, aborting, timeout.Token, payload: new byte[] { 9 });
        }

        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1), timeout.Token, payload: new byte[] { 3 });
        IAmqpDelivery next = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.DeliveryTag.ShouldBe(Tag(7));
        delivery.IsResumed.ShouldBeTrue();
        delivery.IsAborted.ShouldBeTrue();
        delivery.IsSettled.ShouldBeTrue();
        delivery.Payload.IsEmpty.ShouldBeTrue();
        next.Payload.ToArray().ShouldBe(new byte[] { 3 });
        next.IsAborted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should set resume on the first frame of a split resumed delivery only")]
    public async Task SendAsync_OnSplitResumedDelivery_ShouldSetResumeOnFirstFrame()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(null, AmqpTestPeer.CreateOpen(maxFrameSize: 512), timeout.Token);
        IAmqpSender sender = await AttachResumedSenderAsync(
            host, source: Map((1, null)), target: Map((1, null)), credit: 1, cancellationToken: timeout.Token);
        byte[] payload = [.. Enumerable.Range(0, 1500).Select(i => (byte)(i % 251))];

        // Act
        Task<AmqpDeliveryState?> send = sender.SendAsync(payload, ResumeOptions(1, settled: true), timeout.Token).AsTask();
        List<AmqpTestFrame> frames = await ReadDeliveryFramesAsync(host.Peer, timeout.Token);
        await send;

        // Assert
        AmqpTransferPerformative[] transfers = [.. frames.Select(frame => (AmqpTransferPerformative)frame.Performative!)];
        transfers.Length.ShouldBeGreaterThan(2);
        transfers[0].Resume.ShouldBeTrue();
        transfers[0].DeliveryTag.ShouldBe(Tag(1));
        transfers.Skip(1).ShouldAllBe(transfer => !transfer.Resume);
        frames.SelectMany(frame => frame.Payload).ToArray().ShouldBe(payload);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should join a split resumed delivery whose later frames repeat resume")]
    public async Task OnTransfer_OnSplitResumeRepeatingFlag_ShouldJoinFrames()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((1, null)), target: Map((1, null)), cancellationToken: timeout.Token);
        AmqpTransferPerformative repeating = Continuation();
        repeating.Resume = true;

        // Act
        await peer.WriteFrameAsync(0, First(Resuming(CreateTransfer(deliveryId: 0, deliveryTag: Tag(1)))), timeout.Token, payload: new byte[] { 1, 2 });
        await peer.WriteFrameAsync(0, repeating, timeout.Token, payload: new byte[] { 3 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.IsResumed.ShouldBeTrue();
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should refuse a received state on a delivery it does not resume, sending nothing")]
    public async Task SendAsync_OnReceivedStateWithoutResume_ShouldRefuse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachResumedSenderAsync(host, source: null, target: null, credit: 1, cancellationToken: timeout.Token);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            sender.SendAsync(new byte[] { 1 }, new AmqpDeliveryOptions { State = new AmqpReceived(0, 10) }, timeout.Token).AsTask());

        // Assert
        exception.ParamName.ShouldBe("options");
        sender.Credit.ShouldBe(1u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose new delivery carries a received state with amqp:invalid-field")]
    public async Task OnTransfer_OnReceivedStateWithoutResume_ShouldDetachWithInvalidField()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(host, source: null, target: null, cancellationToken: timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, state: new AmqpReceived(0, 0)), timeout.Token, payload: new byte[] { 1 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose resumed delivery carries a received state on a later frame with amqp:invalid-field")]
    public async Task OnTransfer_OnReceivedStateOnContinuation_ShouldDetachWithInvalidField()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((1, null)), target: Map((1, new AmqpReceived(0, 0))), cancellationToken: timeout.Token);
        AmqpTransferPerformative later = Continuation();
        later.State = new AmqpReceived(0, 2);
        await peer.WriteFrameAsync(0, First(Resuming(CreateTransfer(deliveryId: 0, state: new AmqpReceived(0, 0), deliveryTag: Tag(1)))), timeout.Token, payload: new byte[] { 1, 2 });

        // Act
        await peer.WriteFrameAsync(0, later, timeout.Token, payload: new byte[] { 3 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should detach a link whose sender's disposition carries a received state with amqp:invalid-field")]
    public async Task OnDisposition_OnReceivedStateFromSender_ShouldDetachWithInvalidField()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(host, source: null, target: null, cancellationToken: timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Sender, First = 0, State = new AmqpReceived(0, 1) }, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        delivery.RemoteState.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should refuse a new delivery while either unsettled map is incomplete, and still resume")]
    [MemberData(nameof(OneIncompleteFlag))]
    public async Task SendAsync_OnNewDeliveryWhileMapIncomplete_ShouldRefuseItAndKeepResuming(bool sourceIncomplete, bool targetIncomplete)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachResumedSenderAsync(
            host, source: Map((1, null)), target: Map((1, null)), sourceIncomplete: sourceIncomplete, targetIncomplete: targetIncomplete, credit: 1, cancellationToken: timeout.Token);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(
            sender.SendAsync(new byte[] { 1 }, new AmqpDeliveryOptions { Settled = true }, timeout.Token).AsTask());
        uint credit = sender.Credit;
        await sender.SendAsync(new byte[] { 1 }, ResumeOptions(1, settled: true), timeout.Token);
        AmqpTransferPerformative next = await ReadUntilAsync<AmqpTransferPerformative>(host.Peer, timeout.Token);

        // Assert
        credit.ShouldBe(1u);
        next.DeliveryTag.ShouldBe(Tag(1));
        next.Resume.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose sender starts a new delivery while either unsettled map is incomplete with amqp:illegal-state")]
    [MemberData(nameof(OneIncompleteFlag))]
    public async Task OnTransfer_OnNewDeliveryWhileMapIncomplete_ShouldDetachWithIllegalState(bool sourceIncomplete, bool targetIncomplete)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((1, null)), target: Map((1, null)), sourceIncomplete: sourceIncomplete, targetIncomplete: targetIncomplete, cancellationToken: timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, deliveryTag: Tag(5)), timeout.Token, payload: new byte[] { 5 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should deliver a resume while either unsettled map is incomplete")]
    [MemberData(nameof(OneIncompleteFlag))]
    public async Task OnTransfer_OnResumeWhileMapIncomplete_ShouldDeliverIt(bool sourceIncomplete, bool targetIncomplete)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((1, null)), target: Map((1, null)), sourceIncomplete: sourceIncomplete, targetIncomplete: targetIncomplete, cancellationToken: timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, Resuming(CreateTransfer(deliveryId: 0, deliveryTag: Tag(1))), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.DeliveryTag.ShouldBe(Tag(1));
        delivery.IsResumed.ShouldBeTrue();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should refuse a new delivery under the tag of one that waits to be resumed, sending nothing")]
    public async Task SendAsync_OnNewDeliveryUnderAwaitedTag_ShouldRefuse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachResumedSenderAsync(
            host, source: Map((1, null)), target: Map((1, null)), credit: 1, cancellationToken: timeout.Token);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            sender.SendAsync(new byte[] { 1 }, new AmqpDeliveryOptions { DeliveryTag = Tag(1), Settled = true }, timeout.Token).AsTask());

        // Assert
        exception.ParamName.ShouldBe("options");
        sender.Credit.ShouldBe(1u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose sender starts a new delivery under the tag of one that waits to be resumed with amqp:illegal-state")]
    public async Task OnTransfer_OnNewDeliveryUnderAwaitedTag_ShouldDetachWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(
            host, source: Map((1, null)), target: Map((1, null)), cancellationToken: timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, deliveryTag: Tag(1)), timeout.Token, payload: new byte[] { 1 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should skip every tag either unsettled map listed when it generates a tag")]
    public async Task SendAsync_WithGeneratedTags_ShouldSkipCarriedOverTags()
    {
        // Arrange: tag 1 only the source lists, tag 2 both, and tag 3 only the target.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSender sender = await AttachResumedSenderAsync(
            host, source: Map((1, null), (2, null)), target: Map((2, null), (3, AmqpAccepted.Instance)), credit: 2, cancellationToken: timeout.Token);

        // Act
        await sender.SendAsync(new byte[] { 1 }, new AmqpDeliveryOptions { Settled = true }, timeout.Token);
        await sender.SendAsync(new byte[] { 2 }, new AmqpDeliveryOptions { Settled = true }, timeout.Token);
        AmqpTransferPerformative first = await ReadUntilAsync<AmqpTransferPerformative>(host.Peer, timeout.Token);
        AmqpTransferPerformative second = await ReadUntilAsync<AmqpTransferPerformative>(host.Peer, timeout.Token);

        // Assert
        first.DeliveryTag.ShouldBe(Tag(0));
        second.DeliveryTag.ShouldBe(Tag(4));
        first.Resume.ShouldBeFalse();
        second.Resume.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse an unsettled map keyed by anything but distinct delivery tags")]
    [InlineData("c103024040", nameof(ErrorMessages.DeliveryTagInvalid))]
    [InlineData("c10904a0010140a0010140", nameof(ErrorMessages.MapKeyDuplicate))]
    [InlineData("c12502a02100000000000000000000000000000000000000000000000000000000000000000040", nameof(ErrorMessages.DeliveryTagInvalid))]
    public void Decode_OnMalformedUnsettledMap_ShouldThrowDecodeError(string hex, string messageName)
    {
        // Arrange: a map with a null key, one naming tag 01 twice, and one keyed by a binary of 33 bytes.
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() =>
        {
            AmqpReader reader = new(encoded);
            _ = AmqpTypeCodec.ReadUnsettled(ref reader);
        });

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.DecodeError);
        exception.Message.ShouldBe(ErrorMessages.ResourceManager.GetString(messageName, ErrorMessages.Culture));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should ignore resume on a later frame of a delivery that is not resumed")]
    public async Task OnTransfer_OnResumeFlagOnContinuationOfNewDelivery_ShouldIgnoreIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachResumedReceiverAsync(host, source: null, target: null, cancellationToken: timeout.Token);
        AmqpTransferPerformative later = Continuation();
        later.Resume = true;

        // Act
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, later, timeout.Token, payload: new byte[] { 2 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.IsResumed.ShouldBeFalse();
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2 });
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    /// <summary>
    /// Attaches a link between the two ends of a test host whose sender and receiver list the specified unsettled
    /// maps, the sender's from the client or, when <paramref name="senderAttaches"/> is <see langword="false"/>, the
    /// receiver's.
    /// </summary>
    private static async Task<(IAmqpSender Sender, IAmqpReceiver Receiver)> AttachResumedAsync(
        AmqpTestHost host,
        IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? source,
        bool sourceIncomplete,
        IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? target,
        bool targetIncomplete,
        bool senderAttaches,
        CancellationToken cancellationToken)
    {
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(null, cancellationToken);
        AmqpSenderOptions senderOptions = new()
        {
            Name = "resumed",
            Source = new AmqpSource { Address = "X" },
            Target = new AmqpTarget { Address = "Y" },
            Unsettled = source,
            IncompleteUnsettled = sourceIncomplete
        };
        AmqpReceiverOptions receiverOptions = new()
        {
            Name = "resumed",
            Source = new AmqpSource { Address = "X" },
            Target = new AmqpTarget { Address = "Y" },
            Unsettled = target,
            IncompleteUnsettled = targetIncomplete
        };

        if (senderAttaches)
        {
            return await AmqpTestHost.AttachAsync(client, server, senderOptions, receiverOptions, cancellationToken);
        }

        Task<IAmqpSender> accept = AcceptSenderAsync(server, senderOptions, cancellationToken);
        IAmqpReceiver receiver = await client.AttachReceiverAsync(receiverOptions, cancellationToken);

        return (await accept, receiver);
    }

    private static async Task<IAmqpSender> AcceptSenderAsync(IAmqpSession session, AmqpSenderOptions options, CancellationToken cancellationToken)
    {
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

        return await request.AcceptSenderAsync(options, cancellationToken);
    }

    /// <summary>
    /// The peer begins a session and attaches a receiving link on handle 0 whose <c>attach</c> lists the target's
    /// unsettled map; the engine accepts it as a sender that lists the source's, and the peer grants it credit.
    /// </summary>
    private static async Task<IAmqpSender> AttachResumedSenderAsync(
        AmqpScriptedHost host,
        IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? source,
        IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? target,
        bool sourceIncomplete = false,
        bool targetIncomplete = false,
        uint credit = 0,
        CancellationToken cancellationToken = default)
    {
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, cancellationToken);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(cancellationToken);
        AmqpAttachPerformative attach = CreateReceiverAttach(new AmqpTarget { Address = "peer" });
        attach.Unsettled = target;
        attach.IncompleteUnsettled = targetIncomplete;

        IAmqpSender sender = await AttachPeerReceiverAsync(
            peer, session, attach, new AmqpSenderOptions { Unsettled = source, IncompleteUnsettled = sourceIncomplete }, cancellationToken);

        if (credit > 0)
        {
            await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit), cancellationToken);
            await sender.WaitForCreditAsync(cancellationToken);
        }

        return sender;
    }

    /// <summary>
    /// The peer begins a session and attaches a sending link on handle 0 whose <c>attach</c> lists the source's
    /// unsettled map; the engine accepts it as a receiver that lists the target's.
    /// </summary>
    private static async Task<IAmqpReceiver> AttachResumedReceiverAsync(
        AmqpScriptedHost host,
        IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? source,
        IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?>? target,
        bool sourceIncomplete = false,
        bool targetIncomplete = false,
        uint prefetch = 100,
        ulong? maxMessageSize = null,
        CancellationToken cancellationToken = default)
    {
        IAmqpSession session = await BeginAsync(host, cancellationToken);
        AmqpAttachPerformative attach = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        attach.Unsettled = source;
        attach.IncompleteUnsettled = sourceIncomplete;

        AmqpReceiverOptions options = new()
        {
            Unsettled = target,
            IncompleteUnsettled = targetIncomplete,
            Prefetch = prefetch,
            MaxMessageSize = maxMessageSize
        };

        return await AttachPeerSenderAsync(host.Peer, session, attach, options, cancellationToken);
    }

    /// <summary>
    /// Sends one delivery and takes it from the receiver, which records how it arrived and then accepts it, or
    /// echoes the sender's state when <paramref name="echo"/> is <see langword="true"/>.
    /// </summary>
    /// <returns>The sender's result: the receiver's outcome, or <see langword="null"/> for a delivery sent settled.</returns>
    private static async Task<AmqpDeliveryState?> TransferAsync(
        IAmqpSender sender,
        IAmqpReceiver receiver,
        byte[] payload,
        AmqpDeliveryOptions? options,
        bool echo,
        List<string> arrived,
        CancellationToken cancellationToken)
    {
        Task<AmqpDeliveryState?> send = sender.SendAsync(payload, options, cancellationToken).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(cancellationToken);

        arrived.Add(Describe(delivery));
        await (echo ? delivery.SettleAsync(delivery.RemoteState, cancellationToken) : delivery.AcceptAsync(cancellationToken));

        return await send;
    }

    /// <summary>
    /// Aborts a delivery carried over from the earlier attachment, and records how it arrived at the receiver.
    /// </summary>
    private static async Task AbortAsync(IAmqpSender sender, IAmqpReceiver receiver, ulong tag, List<string> arrived, CancellationToken cancellationToken)
    {
        await sender.AbortDeliveryAsync(Tag(tag), cancellationToken);
        arrived.Add(Describe(await receiver.ReceiveAsync(cancellationToken)));
    }

    private static string Describe(IAmqpDelivery delivery)
    {
        string state = delivery.RemoteState switch
        {
            null => "none",
            AmqpReceived received => $"received({received.SectionNumber},{received.SectionOffset})",
            AmqpDeliveryState other => other.GetType().Name
        };

        return $"{delivery.DeliveryTag}: resumed={delivery.IsResumed}, aborted={delivery.IsAborted}, settled={delivery.IsSettled}, state={state}, payload={Convert.ToHexStringLower(delivery.Payload.Span)}";
    }

    /// <summary>
    /// Marks a delivery's first frame as resuming a delivery from an earlier attachment.
    /// </summary>
    private static AmqpTransferPerformative Resuming(AmqpTransferPerformative transfer)
    {
        transfer.Resume = true;
        return transfer;
    }

    /// <summary>
    /// Creates the options of a delivery that resumes the delivery under a tag.
    /// </summary>
    private static AmqpDeliveryOptions ResumeOptions(ulong tag, AmqpDeliveryState? state = null, bool settled = false)
    {
        return new AmqpDeliveryOptions { DeliveryTag = Tag(tag), Resume = true, State = state, Settled = settled };
    }

    private static AmqpDeliveryTag Tag(ulong value)
    {
        return AmqpDeliveryTag.Create(value);
    }

    /// <summary>
    /// Gets the sender's unsettled map of AMQP 1.0 Part 3, section 3.4.6.
    /// </summary>
    private static Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> Part3Source()
    {
        return Map(
            (1, null), (2, null), (3, null), (4, null),
            (5, new AmqpReceived(0, 200)), (6, new AmqpReceived(1, 150)), (7, new AmqpReceived(0, 500)),
            (8, new AmqpReceived(3, 5)), (9, new AmqpReceived(2, 0)),
            (10, AmqpAccepted.Instance), (11, AmqpAccepted.Instance), (12, AmqpAccepted.Instance),
            (13, AmqpAccepted.Instance), (14, AmqpAccepted.Instance));
    }

    /// <summary>
    /// Gets the receiver's unsettled map of AMQP 1.0 Part 3, section 3.4.6.
    /// </summary>
    private static Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> Part3Target()
    {
        return Map(
            (2, new AmqpReceived(3, 0)), (3, AmqpAccepted.Instance), (4, null), (6, new AmqpReceived(2, 0)),
            (7, new AmqpReceived(0, 100)), (8, AmqpAccepted.Instance), (9, null), (11, new AmqpReceived(1, 2000)),
            (12, AmqpAccepted.Instance), (13, AmqpReleased.Instance), (14, null));
    }

    private static Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> Map(params (ulong Tag, AmqpDeliveryState? State)[] entries)
    {
        Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> map = [];

        foreach ((ulong tag, AmqpDeliveryState? state) in entries)
        {
            map.Add(Tag(tag), state);
        }

        return map;
    }

    private static ulong[] Numbers(IEnumerable<AmqpDeliveryTag> tags)
    {
        return [.. tags.Select(tag => (ulong)tag.Value.Span[0]).Order()];
    }
}
