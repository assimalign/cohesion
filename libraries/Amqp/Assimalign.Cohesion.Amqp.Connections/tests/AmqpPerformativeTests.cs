using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Encodes and decodes every performative against the rules of AMQP 1.0 for a composite type and for the fields
/// the performatives define. Each rule in <see cref="Rules"/> names the tests that pin it.
/// </summary>
public class AmqpPerformativeTests
{
    /// <summary>
    /// Gets the rules for how a performative is encoded and what its fields may hold, each with the tests that
    /// pin it.
    /// </summary>
    internal static IReadOnlyList<AmqpComplianceRule> Rules { get; } =
    [
        new("P1", "A performative is a described list whose elements follow the field order of its definition: the engine omits trailing fields that hold their defaults, reads an absent or null field as its default, and refuses a body that is not a described list with amqp:decode-error.", "Part 1, section 1.4",
            Tests:
            [
                nameof(Encode_WithDefaultFields_ShouldTrimTrailingFields),
                nameof(Decode_WithMinimalOpen_ShouldApplyDefaults),
                nameof(Decode_WithFullOpen_ShouldRoundTrip),
                nameof(Decode_WithFullBegin_ShouldRoundTrip),
                nameof(Decode_WithFullAttach_ShouldRoundTrip),
                nameof(Decode_WithFullFlow_ShouldRoundTrip),
                nameof(Decode_WithSaslPerformatives_ShouldRoundTrip)
            ],
            Refusals: [nameof(Decode_WithUndescribedBody_ShouldThrowDecodeError)]),
        new("P2", "A mandatory field is never null: the engine writes every mandatory field, an empty one included, and refuses a performative that leaves one out with amqp:decode-error.", "Part 1, section 1.4",
            Tests: [nameof(Encode_WithEmptySaslFields_ShouldWriteMandatoryFields)],
            Refusals: [nameof(Decode_WithMissingMandatoryField_ShouldThrowDecodeError)]),
        new("P3", "A performative is named by its descriptor, the numeric code or the symbolic name: the engine reads either and names each performative as the specification does, and a descriptor it does not know is refused with amqp:not-implemented, which closes the connection.", "Part 1, section 1.5; Part 2, sections 2.3.2 and 2.7",
            Tests:
            [
                nameof(Decode_WithSymbolicDescriptor_ShouldDecodePerformative),
                nameof(PerformativeName_OnEachPerformative_ShouldMatchSpecification)
            ],
            Refusals:
            [
                nameof(Decode_WithUnknownDescriptor_ShouldThrowNotImplemented),
                nameof(AmqpEngineViolationTests.ProcessFrame_OnUnknownPerformative_ShouldCloseWithNotImplemented)
            ]),
        new("P4", "A restricted type takes only the choices its definition lists: the engine refuses an undefined sender or receiver settle mode, terminus durability, expiry policy or SASL code with amqp:invalid-field.", "Part 1, section 1.1.4; Part 2, sections 2.8.2 and 2.8.3; Part 3, sections 3.5.5 and 3.5.6; Part 5, section 5.3.3.6",
            Tests: [],
            Refusals:
            [
                nameof(Decode_WithUndefinedSettleMode_ShouldThrowInvalidField),
                nameof(Decode_WithUndefinedTerminusDurability_ShouldThrowInvalidField),
                nameof(Decode_WithUndefinedExpiryPolicy_ShouldThrowInvalidField),
                nameof(Decode_WithUndefinedSaslCode_ShouldThrowInvalidField)
            ]),
        new("P5", "A field that allows multiple values holds null, one value, or an array of values of its type: the engine reads each form and refuses any other type with amqp:decode-error.", "Part 1, section 1.4",
            Tests: [nameof(AmqpEncodingTests.ReadSymbolMultiple_WithAcceptedForms_ShouldDecode)],
            Refusals: [nameof(AmqpEncodingTests.ReadSymbolMultiple_WithOtherTypes_ShouldThrowDecodeError)]),
        new("P6", "A delivery tag is at most 32 bytes: the engine reads a transfer's tag and refuses a longer one with amqp:invalid-field.", "Part 2, section 2.8.7",
            Tests: [nameof(Decode_WithFullTransfer_ShouldRoundTripAndLeavePayload)],
            Refusals: [nameof(Decode_WithLongDeliveryTag_ShouldThrowInvalidField)]),
        new("P7", "An error names its condition: the engine refuses to write an error without one, and refuses one that arrives without it with amqp:decode-error.", "Part 2, section 2.8.14",
            Tests: [nameof(Decode_WithErrors_ShouldRoundTrip)],
            Refusals:
            [
                nameof(Encode_WithErrorWithoutCondition_ShouldThrowArgumentException),
                nameof(Decode_WithErrorWithoutCondition_ShouldThrowDecodeError)
            ]),
        new("P8", "A max-message-size of zero, like an absent one, sets no limit on the messages a link takes.", "Part 2, section 2.7.3",
            Tests: [nameof(Decode_WithZeroMaxMessageSize_ShouldReadNoLimit)],
            Refusals: [])
    ];

    public static TheoryData<string, string, string> MissingMandatoryFields()
    {
        return new TheoryData<string, string, string>
        {
            { "00531045", "open", "container-id" },
            { "00531145", "begin", "next-outgoing-id" },
            { "00531245", "attach", "name" },
            { "00531345", "flow", "incoming-window" },
            { "00531445", "transfer", "handle" },
            { "00531545", "disposition", "role" },
            { "00531645", "detach", "handle" },
            { "00534045", "sasl-mechanisms", "sasl-server-mechanisms" },
            { "00534145", "sasl-init", "mechanism" },
            { "00534245", "sasl-challenge", "challenge" },
            { "00534345", "sasl-response", "response" },
            { "00534445", "sasl-outcome", "code" }
        };
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip an open with every field")]
    public void Decode_WithFullOpen_ShouldRoundTrip()
    {
        // Arrange
        AmqpOpenPerformative open = new()
        {
            ContainerId = "container",
            HostName = "amqp.test",
            MaxFrameSize = 65536,
            ChannelMax = 31,
            IdleTimeout = 30_000,
            OutgoingLocales = ["en-US"],
            IncomingLocales = ["en-US", "fr-FR"],
            OfferedCapabilities = ["ANONYMOUS-RELAY"],
            DesiredCapabilities = ["DELAYED_DELIVERY"],
            Properties = new Dictionary<AmqpSymbol, object?> { ["product"] = "cohesion" }
        };

        // Act
        AmqpOpenPerformative decoded = RoundTrip(open);

        // Assert
        decoded.ContainerId.ShouldBe("container");
        decoded.HostName.ShouldBe("amqp.test");
        decoded.MaxFrameSize.ShouldBe(65536u);
        decoded.ChannelMax.ShouldBe((ushort)31);
        decoded.IdleTimeout.ShouldBe(30_000u);
        Names(decoded.OutgoingLocales).ShouldBe(new[] { "en-US" });
        Names(decoded.IncomingLocales).ShouldBe(new[] { "en-US", "fr-FR" });
        Names(decoded.OfferedCapabilities).ShouldBe(new[] { "ANONYMOUS-RELAY" });
        Names(decoded.DesiredCapabilities).ShouldBe(new[] { "DELAYED_DELIVERY" });
        decoded.Properties.ShouldNotBeNull()[new AmqpSymbol("product")].ShouldBe("cohesion");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should leave out trailing fields that hold their defaults")]
    public void Encode_WithDefaultFields_ShouldTrimTrailingFields()
    {
        // Act
        string open = Hex(new AmqpOpenPerformative());
        string close = Hex(new AmqpClosePerformative());
        string end = Hex(new AmqpEndPerformative());
        string detach = Hex(new AmqpDetachPerformative { Handle = 1 });

        // Assert
        open.ShouldBe("005310c00301a100");
        close.ShouldBe("00531845");
        end.ShouldBe("00531745");
        detach.ShouldBe("005316c003015201");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should read omitted open fields as their defaults")]
    public void Decode_WithMinimalOpen_ShouldApplyDefaults()
    {
        // Act
        AmqpOpenPerformative decoded = Decode<AmqpOpenPerformative>(Convert.FromHexString("005310c00301a100"));

        // Assert
        decoded.ContainerId.ShouldBe(string.Empty);
        decoded.HostName.ShouldBeNull();
        decoded.MaxFrameSize.ShouldBe(uint.MaxValue);
        decoded.ChannelMax.ShouldBe(ushort.MaxValue);
        decoded.IdleTimeout.ShouldBeNull();
        decoded.OfferedCapabilities.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip a begin with every field")]
    public void Decode_WithFullBegin_ShouldRoundTrip()
    {
        // Arrange
        AmqpBeginPerformative begin = new()
        {
            RemoteChannel = 2,
            NextOutgoingId = 10,
            IncomingWindow = 2048,
            OutgoingWindow = 1024,
            HandleMax = 63,
            OfferedCapabilities = ["a"],
            DesiredCapabilities = ["b"],
            Properties = new Dictionary<AmqpSymbol, object?> { ["k"] = 1u }
        };

        // Act
        AmqpBeginPerformative decoded = RoundTrip(begin);

        // Assert
        decoded.RemoteChannel.ShouldBe((ushort)2);
        decoded.NextOutgoingId.ShouldBe(10u);
        decoded.IncomingWindow.ShouldBe(2048u);
        decoded.OutgoingWindow.ShouldBe(1024u);
        decoded.HandleMax.ShouldBe(63u);
        Names(decoded.OfferedCapabilities).ShouldBe(new[] { "a" });
        Names(decoded.DesiredCapabilities).ShouldBe(new[] { "b" });
        decoded.Properties.ShouldNotBeNull()[new AmqpSymbol("k")].ShouldBe(1u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip an attach with termini and unsettled state")]
    public void Decode_WithFullAttach_ShouldRoundTrip()
    {
        // Arrange
        AmqpAttachPerformative attach = new()
        {
            Name = "link-1",
            Handle = 3,
            Role = AmqpRole.Receiver,
            SenderSettleMode = AmqpSenderSettleMode.Settled,
            ReceiverSettleMode = AmqpReceiverSettleMode.Second,
            Source = new AmqpSource
            {
                Address = "queue/a",
                Durable = AmqpTerminusDurability.UnsettledState,
                ExpiryPolicy = AmqpTerminusExpiryPolicy.Never,
                Timeout = 60,
                DistributionMode = AmqpDistributionMode.Copy,
                Filter = new Dictionary<AmqpSymbol, object?>
                {
                    ["selector"] = new AmqpDescribedValue(new AmqpSymbol("apache.org:selector-filter:string"), "color = 'red'")
                },
                DefaultOutcome = new AmqpModified(deliveryFailed: true),
                Outcomes = ["amqp:accepted:list", "amqp:rejected:list"],
                Capabilities = ["queue"]
            },
            Target = new AmqpTarget
            {
                Address = "queue/b",
                Durable = AmqpTerminusDurability.Configuration,
                ExpiryPolicy = AmqpTerminusExpiryPolicy.LinkDetach,
                Dynamic = true,
                DynamicNodeProperties = new Dictionary<AmqpSymbol, object?> { ["supported-dist-modes"] = new AmqpSymbol("move") },
                Capabilities = ["topic"]
            },
            Unsettled = new Dictionary<AmqpDeliveryTag, AmqpDeliveryState?>
            {
                [AmqpDeliveryTag.Create(1)] = AmqpAccepted.Instance,
                [AmqpDeliveryTag.Create(2)] = null,
                [AmqpDeliveryTag.Create(3)] = new AmqpReceived(0, 100)
            },
            IncompleteUnsettled = true,
            InitialDeliveryCount = 10,
            MaxMessageSize = 1_048_576,
            OfferedCapabilities = ["SHARED-SUBS"],
            DesiredCapabilities = ["SHARED-SUBS"],
            Properties = new Dictionary<AmqpSymbol, object?> { ["x-opt-priority"] = 5 }
        };

        // Act
        AmqpAttachPerformative decoded = RoundTrip(attach);

        // Assert
        decoded.Name.ShouldBe("link-1");
        decoded.Handle.ShouldBe(3u);
        decoded.Role.ShouldBe(AmqpRole.Receiver);
        decoded.SenderSettleMode.ShouldBe(AmqpSenderSettleMode.Settled);
        decoded.ReceiverSettleMode.ShouldBe(AmqpReceiverSettleMode.Second);

        AmqpSource source = decoded.Source.ShouldNotBeNull();
        source.Address.ShouldBe("queue/a");
        source.Durable.ShouldBe(AmqpTerminusDurability.UnsettledState);
        source.ExpiryPolicy.ShouldBe(AmqpTerminusExpiryPolicy.Never);
        source.Timeout.ShouldBe(60u);
        source.DistributionMode.ShouldBe(AmqpDistributionMode.Copy);
        AmqpDescribedValue filter = source.Filter.ShouldNotBeNull()[new AmqpSymbol("selector")].ShouldBeOfType<AmqpDescribedValue>();
        filter.Descriptor.ShouldBe(new AmqpSymbol("apache.org:selector-filter:string"));
        filter.Value.ShouldBe("color = 'red'");
        source.DefaultOutcome.ShouldBeOfType<AmqpModified>().DeliveryFailed.ShouldBeTrue();
        Names(source.Outcomes).ShouldBe(new[] { "amqp:accepted:list", "amqp:rejected:list" });
        Names(source.Capabilities).ShouldBe(new[] { "queue" });

        AmqpTarget target = decoded.Target.ShouldBeOfType<AmqpTarget>();
        target.Address.ShouldBe("queue/b");
        target.Durable.ShouldBe(AmqpTerminusDurability.Configuration);
        target.ExpiryPolicy.ShouldBe(AmqpTerminusExpiryPolicy.LinkDetach);
        target.Dynamic.ShouldBeTrue();
        target.DynamicNodeProperties.ShouldNotBeNull()[new AmqpSymbol("supported-dist-modes")].ShouldBe(new AmqpSymbol("move"));
        Names(target.Capabilities).ShouldBe(new[] { "topic" });

        IReadOnlyDictionary<AmqpDeliveryTag, AmqpDeliveryState?> unsettled = decoded.Unsettled.ShouldNotBeNull();
        unsettled.Count.ShouldBe(3);
        unsettled[AmqpDeliveryTag.Create(1)].ShouldBeOfType<AmqpAccepted>();
        unsettled[AmqpDeliveryTag.Create(2)].ShouldBeNull();
        unsettled[AmqpDeliveryTag.Create(3)].ShouldBeOfType<AmqpReceived>().SectionOffset.ShouldBe(100ul);
        decoded.IncompleteUnsettled.ShouldBeTrue();
        decoded.InitialDeliveryCount.ShouldBe(10u);
        decoded.MaxMessageSize.ShouldBe(1_048_576ul);
        Names(decoded.OfferedCapabilities).ShouldBe(new[] { "SHARED-SUBS" });
        Names(decoded.DesiredCapabilities).ShouldBe(new[] { "SHARED-SUBS" });
        decoded.Properties.ShouldNotBeNull()[new AmqpSymbol("x-opt-priority")].ShouldBe(5);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip an attach to a transaction coordinator")]
    public void Decode_WithCoordinatorTarget_ShouldRoundTrip()
    {
        // Arrange
        AmqpAttachPerformative attach = new()
        {
            Name = "txn-controller",
            Role = AmqpRole.Sender,
            Source = new AmqpSource(),
            Target = new AmqpCoordinator { Capabilities = [AmqpTransactionCapability.LocalTransactions] }
        };

        // Act
        AmqpAttachPerformative decoded = RoundTrip(attach);

        // Assert
        AmqpCoordinator coordinator = decoded.Target.ShouldBeOfType<AmqpCoordinator>();
        Names(coordinator.Capabilities).ShouldBe(new[] { AmqpTransactionCapability.LocalTransactions.Value! });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should read a zero max-message-size as no limit")]
    public void Decode_WithZeroMaxMessageSize_ShouldReadNoLimit()
    {
        // Arrange
        AmqpAttachPerformative attach = new() { Name = "a", MaxMessageSize = 0 };

        // Act
        AmqpAttachPerformative decoded = RoundTrip(attach);

        // Assert
        decoded.MaxMessageSize.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a settle mode the specification does not define")]
    [InlineData("005312c00804a1016143425003", nameof(ErrorMessages.SenderSettleModeInvalid))]
    [InlineData("005312c00905a101614342405002", nameof(ErrorMessages.ReceiverSettleModeInvalid))]
    public void Decode_WithUndefinedSettleMode_ShouldThrowInvalidField(string hex, string messageName)
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode<AmqpAttachPerformative>(encoded));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        exception.Message.ShouldBe(ErrorMessages.ResourceManager.GetString(messageName, ErrorMessages.Culture));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a terminus durability the specification does not define")]
    [InlineData("005312c01106a1016143414040005328c00402405203")]
    [InlineData("005312c01207a101614341404040005329c00402405203")]
    public void Decode_WithUndefinedTerminusDurability_ShouldThrowInvalidField(string hex)
    {
        // Arrange: a source or target whose durable field is 3.
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode<AmqpAttachPerformative>(encoded));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        exception.Message.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.TerminusDurabilityInvalid, 3u));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a terminus expiry policy the specification does not define")]
    [InlineData("005312c01706a1016143414040005328c00a034040a3056c61746572")]
    [InlineData("005312c01807a101614341404040005329c00a034040a3056c61746572")]
    public void Decode_WithUndefinedExpiryPolicy_ShouldThrowInvalidField(string hex)
    {
        // Arrange: a source or target whose expiry-policy is the symbol "later".
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode<AmqpAttachPerformative>(encoded));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        exception.Message.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.TerminusExpiryPolicyInvalid, "later"));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should keep a filter whose value is neither described nor null, for the attach rules to refuse with its link")]
    public void Decode_WithFilterNotDescribed_ShouldKeepTheValue()
    {
        // Arrange: a source whose filter set maps the symbol "f" to the string "x".
        byte[] encoded = Convert.FromHexString("005312c01e06a1016143414040005328c0110840404040404040c10702a30166a10178");

        // Act
        AmqpAttachPerformative attach = Decode<AmqpAttachPerformative>(encoded);

        // Assert
        attach.Source.ShouldBeOfType<AmqpSource>().Filter.ShouldNotBeNull()[new AmqpSymbol("f")].ShouldBe("x");
        AmqpAttachRules.FindViolation(attach, opening: true)
            .ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.FilterNotDescribed, "f"));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a filter set keyed by anything but distinct symbols")]
    [InlineData("005312c01b06a1016143414040005328c00e0840404040404040c10402530540", nameof(ErrorMessages.MapKeyNotSymbol))]
    [InlineData("005312c02006a1016143414040005328c0130840404040404040c10904a3016640a3016640", nameof(ErrorMessages.MapKeyDuplicate))]
    public void Decode_WithMalformedFilterSet_ShouldThrowDecodeError(string hex, string messageName)
    {
        // Arrange: a filter set keyed by the ulong 5, or naming the filter "f" twice.
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode<AmqpAttachPerformative>(encoded));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.DecodeError);
        exception.Message.ShouldBe(ErrorMessages.ResourceManager.GetString(messageName, ErrorMessages.Culture));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should keep a filter whose value is null")]
    public void Decode_WithNullFilter_ShouldRoundTrip()
    {
        // Arrange
        AmqpAttachPerformative attach = new()
        {
            Name = "a",
            Role = AmqpRole.Receiver,
            Source = new AmqpSource { Filter = new Dictionary<AmqpSymbol, object?> { ["f"] = null } }
        };

        // Act
        AmqpAttachPerformative decoded = RoundTrip(attach);

        // Assert
        IReadOnlyDictionary<AmqpSymbol, object?> filter = decoded.Source.ShouldNotBeNull().Filter.ShouldNotBeNull();
        filter.Count.ShouldBe(1);
        filter[new AmqpSymbol("f")].ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should refuse a filter whose value is neither described nor null")]
    public void Encode_WithFilterNotDescribed_ShouldThrowArgumentException()
    {
        // Arrange
        using AmqpBufferWriter writer = new();
        AmqpAttachPerformative attach = new()
        {
            Name = "a",
            Role = AmqpRole.Receiver,
            Source = new AmqpSource { Filter = new Dictionary<AmqpSymbol, object?> { ["f"] = "color = 'red'" } }
        };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => attach.Encode(writer));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, ErrorMessages.FilterNotDescribed, "f"));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a SASL outcome code the specification does not define")]
    [InlineData("005344c003015005", (byte)5)]
    [InlineData("005344c0030150ff", (byte)255)]
    public void Decode_WithUndefinedSaslCode_ShouldThrowInvalidField(string hex, byte code)
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode<AmqpSaslOutcomePerformative>(encoded));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        exception.Message.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.SaslCodeInvalid, code));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip a flow with every field")]
    public void Decode_WithFullFlow_ShouldRoundTrip()
    {
        // Arrange
        AmqpFlowPerformative flow = new()
        {
            NextIncomingId = 1,
            IncomingWindow = 2,
            NextOutgoingId = 3,
            OutgoingWindow = 4,
            Handle = 5,
            DeliveryCount = 6,
            LinkCredit = 7,
            Available = 8,
            Drain = true,
            Echo = true,
            Properties = new Dictionary<AmqpSymbol, object?> { ["k"] = "v" }
        };

        // Act
        AmqpFlowPerformative decoded = RoundTrip(flow);

        // Assert
        decoded.NextIncomingId.ShouldBe(1u);
        decoded.IncomingWindow.ShouldBe(2u);
        decoded.NextOutgoingId.ShouldBe(3u);
        decoded.OutgoingWindow.ShouldBe(4u);
        decoded.Handle.ShouldBe(5u);
        decoded.DeliveryCount.ShouldBe(6u);
        decoded.LinkCredit.ShouldBe(7u);
        decoded.Available.ShouldBe(8u);
        decoded.Drain.ShouldBeTrue();
        decoded.Echo.ShouldBeTrue();
        decoded.Properties.ShouldNotBeNull()[new AmqpSymbol("k")].ShouldBe("v");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip a session flow without link fields")]
    public void Decode_WithSessionFlow_ShouldLeaveLinkFieldsNull()
    {
        // Arrange
        AmqpFlowPerformative flow = new() { IncomingWindow = 100, NextOutgoingId = 0, OutgoingWindow = 100 };

        // Act
        AmqpFlowPerformative decoded = RoundTrip(flow);

        // Assert
        decoded.NextIncomingId.ShouldBeNull();
        decoded.Handle.ShouldBeNull();
        decoded.LinkCredit.ShouldBeNull();
        decoded.Drain.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip a transfer and leave the reader at its payload")]
    public void Decode_WithFullTransfer_ShouldRoundTripAndLeavePayload()
    {
        // Arrange
        AmqpTransferPerformative transfer = new()
        {
            Handle = 1,
            DeliveryId = 2,
            DeliveryTag = AmqpDeliveryTag.Create(new byte[] { 0xaa, 0xbb }),
            MessageFormat = 0,
            Settled = false,
            More = true,
            ReceiverSettleMode = AmqpReceiverSettleMode.Second,
            State = new AmqpReceived(1, 512),
            Resume = true,
            Aborted = true,
            Batchable = true
        };
        byte[] payload = [0x00, 0x53, 0x77, 0xa1, 0x01, 0x61];
        using AmqpBufferWriter writer = new();

        transfer.Encode(writer);
        writer.Write(payload);

        // Act
        AmqpReader reader = new(writer.WrittenSpan);
        AmqpTransferPerformative decoded = AmqpPerformative.Decode(ref reader).ShouldBeOfType<AmqpTransferPerformative>();
        byte[] remaining = reader.RemainingSpan.ToArray();

        // Assert
        decoded.Handle.ShouldBe(1u);
        decoded.DeliveryId.ShouldBe(2u);
        decoded.DeliveryTag.ShouldBe(AmqpDeliveryTag.Create(new byte[] { 0xaa, 0xbb }));
        decoded.MessageFormat.ShouldBe(0u);
        decoded.Settled.ShouldBe(false);
        decoded.More.ShouldBeTrue();
        decoded.ReceiverSettleMode.ShouldBe(AmqpReceiverSettleMode.Second);
        decoded.State.ShouldBeOfType<AmqpReceived>().SectionOffset.ShouldBe(512ul);
        decoded.Resume.ShouldBeTrue();
        decoded.Aborted.ShouldBeTrue();
        decoded.Batchable.ShouldBeTrue();
        remaining.ShouldBe(payload);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a delivery tag longer than 32 bytes")]
    public void Decode_WithLongDeliveryTag_ShouldThrowInvalidField()
    {
        // Arrange
        byte[] encoded = [0x00, 0x53, 0x14, 0xc0, 38, 0x03, 0x43, 0x43, 0xa0, 33, .. new byte[33]];

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode<AmqpTransferPerformative>(encoded));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        exception.Message.ShouldBe(ErrorMessages.DeliveryTagInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip a disposition over a range of deliveries")]
    public void Decode_WithDispositionRange_ShouldRoundTrip()
    {
        // Arrange
        AmqpDispositionPerformative disposition = new()
        {
            Role = AmqpRole.Receiver,
            First = 4,
            Last = 9,
            Settled = true,
            State = new AmqpRejected(new AmqpError(AmqpErrorCondition.NotAllowed, "no")),
            Batchable = true
        };

        // Act
        AmqpDispositionPerformative decoded = RoundTrip(disposition);

        // Assert
        decoded.Role.ShouldBe(AmqpRole.Receiver);
        decoded.First.ShouldBe(4u);
        decoded.Last.ShouldBe(9u);
        decoded.Settled.ShouldBeTrue();
        decoded.State.ShouldBeOfType<AmqpRejected>().Error.ShouldNotBeNull().Description.ShouldBe("no");
        decoded.Batchable.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should send a disposition of one delivery without its last id")]
    public void Encode_WithSingleDeliveryDisposition_ShouldOmitLast()
    {
        // Arrange
        AmqpDispositionPerformative disposition = new() { Role = AmqpRole.Sender, First = 4, Last = 4 };

        // Act
        string encoded = Hex(disposition);
        AmqpDispositionPerformative decoded = RoundTrip(disposition);

        // Assert
        encoded.ShouldBe("005315c00402425204");
        decoded.Last.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip detach, end and close with their errors")]
    public void Decode_WithErrors_ShouldRoundTrip()
    {
        // Arrange
        AmqpError error = new(AmqpErrorCondition.ResourceLimitExceeded, "too many", new Dictionary<AmqpSymbol, object?> { ["limit"] = 10 });

        // Act
        AmqpDetachPerformative detach = RoundTrip(new AmqpDetachPerformative { Handle = 7, Closed = true, Error = error });
        AmqpEndPerformative end = RoundTrip(new AmqpEndPerformative { Error = error });
        AmqpClosePerformative close = RoundTrip(new AmqpClosePerformative { Error = error });

        // Assert
        detach.Handle.ShouldBe(7u);
        detach.Closed.ShouldBeTrue();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.ResourceLimitExceeded);
        detach.Error.Info.ShouldNotBeNull()[new AmqpSymbol("limit")].ShouldBe(10);
        end.Error.ShouldNotBeNull().Description.ShouldBe("too many");
        close.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.ResourceLimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should refuse an error without a condition")]
    public void Encode_WithErrorWithoutCondition_ShouldThrowArgumentException()
    {
        // Arrange
        AmqpClosePerformative close = new() { Error = new AmqpError() };

        // Act and Assert
        Should.Throw<ArgumentException>(() => Hex(close));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse an error without a condition")]
    public void Decode_WithErrorWithoutCondition_ShouldThrowDecodeError()
    {
        // Arrange
        byte[] encoded = Convert.FromHexString("005318c00501" + "00531d45");

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode<AmqpClosePerformative>(encoded));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.DecodeError);
        exception.Message.ShouldContain("condition", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should round-trip every SASL frame body")]
    public void Decode_WithSaslPerformatives_ShouldRoundTrip()
    {
        // Act
        AmqpSaslMechanismsPerformative mechanisms = RoundTrip(new AmqpSaslMechanismsPerformative { Mechanisms = ["PLAIN", "ANONYMOUS"] });
        AmqpSaslInitPerformative init = RoundTrip(new AmqpSaslInitPerformative
        {
            Mechanism = "PLAIN",
            InitialResponse = Encoding.UTF8.GetBytes("\0alice\0secret"),
            HostName = "amqp.test"
        });
        AmqpSaslChallengePerformative challenge = RoundTrip(new AmqpSaslChallengePerformative { Challenge = new byte[] { 1, 2 } });
        AmqpSaslResponsePerformative response = RoundTrip(new AmqpSaslResponsePerformative { Response = new byte[] { 3 } });
        AmqpSaslOutcomePerformative outcome = RoundTrip(new AmqpSaslOutcomePerformative { Code = AmqpSaslCode.SysTemp, AdditionalData = new byte[] { 4 } });

        // Assert
        Names(mechanisms.Mechanisms).ShouldBe(new[] { "PLAIN", "ANONYMOUS" });
        init.Mechanism.ShouldBe(new AmqpSymbol("PLAIN"));
        init.InitialResponse.ShouldNotBeNull().ToArray().ShouldBe(Encoding.UTF8.GetBytes("\0alice\0secret"));
        init.HostName.ShouldBe("amqp.test");
        challenge.Challenge.ToArray().ShouldBe(new byte[] { 1, 2 });
        response.Response.ToArray().ShouldBe(new byte[] { 3 });
        outcome.Code.ShouldBe(AmqpSaslCode.SysTemp);
        outcome.AdditionalData.ShouldNotBeNull().ToArray().ShouldBe(new byte[] { 4 });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should write mandatory SASL fields even when they hold zero values")]
    public void Encode_WithEmptySaslFields_ShouldWriteMandatoryFields()
    {
        // Act
        string mechanisms = Hex(new AmqpSaslMechanismsPerformative());
        string outcome = Hex(new AmqpSaslOutcomePerformative { Code = AmqpSaslCode.Ok });
        AmqpSaslMechanismsPerformative decodedMechanisms = RoundTrip(new AmqpSaslMechanismsPerformative());

        // Assert
        mechanisms.ShouldBe("005340c00501e00200a3");
        outcome.ShouldBe("005344c003015000");
        decodedMechanisms.Mechanisms.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should name a mandatory field the peer left out")]
    [MemberData(nameof(MissingMandatoryFields))]
    public void Decode_WithMissingMandatoryField_ShouldThrowDecodeError(string hex, string performative, string field)
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode<AmqpPerformative>(encoded));

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.DecodeError);
        exception.Message.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.MandatoryFieldMissing, field, performative));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should accept a symbolic performative descriptor")]
    public void Decode_WithSymbolicDescriptor_ShouldDecodePerformative()
    {
        // Arrange
        byte[] name = Encoding.ASCII.GetBytes("amqp:close:list");
        byte[] encoded = [0x00, 0xa3, (byte)name.Length, .. name, 0x45];

        // Act
        AmqpClosePerformative close = Decode<AmqpClosePerformative>(encoded);

        // Assert
        close.Error.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a frame body that is not a described list")]
    public void Decode_WithUndescribedBody_ShouldThrowDecodeError()
    {
        // Act
        AmqpException notDescribed = Should.Throw<AmqpException>(() => Decode<AmqpPerformative>([0x45]));
        AmqpException notList = Should.Throw<AmqpException>(() => Decode<AmqpPerformative>(Convert.FromHexString("005318a10161")));

        // Assert
        notDescribed.Message.ShouldBe(ErrorMessages.PerformativeNotDescribed);
        notList.Message.ShouldBe(ErrorMessages.ValueNotList);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a performative this library does not know as not implemented")]
    public void Decode_WithUnknownDescriptor_ShouldThrowNotImplemented()
    {
        // Arrange
        byte[] encoded = Convert.FromHexString("00537f45");

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => Decode<AmqpPerformative>(encoded));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotImplemented);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - PerformativeName: Should spell each performative as the specification does")]
    public void PerformativeName_OnEachPerformative_ShouldMatchSpecification()
    {
        // Arrange
        AmqpPerformative[] performatives =
        [
            new AmqpOpenPerformative(),
            new AmqpBeginPerformative(),
            new AmqpAttachPerformative(),
            new AmqpFlowPerformative(),
            new AmqpTransferPerformative(),
            new AmqpDispositionPerformative(),
            new AmqpDetachPerformative(),
            new AmqpEndPerformative(),
            new AmqpClosePerformative(),
            new AmqpSaslMechanismsPerformative(),
            new AmqpSaslInitPerformative(),
            new AmqpSaslChallengePerformative(),
            new AmqpSaslResponsePerformative(),
            new AmqpSaslOutcomePerformative()
        ];

        // Act
        string[] names = performatives.Select(performative => performative.PerformativeName).ToArray();

        // Assert
        names.ShouldBe(new[]
        {
            "open", "begin", "attach", "flow", "transfer", "disposition", "detach", "end", "close",
            "sasl-mechanisms", "sasl-init", "sasl-challenge", "sasl-response", "sasl-outcome"
        });
    }

    private static T RoundTrip<T>(T performative)
        where T : AmqpPerformative
    {
        using AmqpBufferWriter writer = new();

        performative.Encode(writer);
        return Decode<T>(writer.ToArray());
    }

    private static T Decode<T>(byte[] encoded)
        where T : AmqpPerformative
    {
        AmqpReader reader = new(encoded);
        AmqpPerformative performative = AmqpPerformative.Decode(ref reader);

        reader.IsEnd.ShouldBeTrue();
        return performative.ShouldBeAssignableTo<T>()!;
    }

    private static string Hex(AmqpPerformative performative)
    {
        using AmqpBufferWriter writer = new();

        performative.Encode(writer);
        return Convert.ToHexStringLower(writer.WrittenSpan);
    }

    private static string[] Names(IEnumerable<AmqpSymbol>? symbols)
    {
        return symbols.ShouldNotBeNull().Select(symbol => symbol.Value ?? string.Empty).ToArray();
    }
}
