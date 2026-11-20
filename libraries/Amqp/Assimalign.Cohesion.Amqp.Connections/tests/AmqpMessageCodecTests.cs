using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpMessageCodecTests
{
    private static readonly byte[] _emptyHeader = Convert.FromHexString("00537045");
    private static readonly byte[] _emptyProperties = Convert.FromHexString("00537345");
    private static readonly byte[] _emptyFooter = Convert.FromHexString("005378c10100");

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should round-trip a message with every section")]
    public void Encode_WithEverySection_ShouldRoundTrip()
    {
        // Arrange
        AmqpMessage message = CreateFullMessage();

        // Act
        AmqpMessage decoded = AmqpMessageCodec.Decode(AmqpMessageCodec.Encode(message));

        // Assert
        AmqpHeader header = decoded.Header.ShouldNotBeNull();
        header.Durable.ShouldBeTrue();
        header.Priority.ShouldBe((byte)9);
        header.TimeToLive.ShouldBe(30_000u);
        header.FirstAcquirer.ShouldBeTrue();
        header.DeliveryCount.ShouldBe(3u);
        decoded.DeliveryAnnotations.ShouldNotBeNull()[new AmqpSymbol("x-opt-trace")].ShouldBe("hop-1");
        decoded.MessageAnnotations.ShouldNotBeNull()[new AmqpSymbol("x-opt-sequence")].ShouldBe(42L);

        AmqpProperties properties = decoded.Properties.ShouldNotBeNull();
        properties.MessageId.ShouldBe("message-1");
        properties.UserId.ShouldNotBeNull().ToArray().ShouldBe(Encoding.ASCII.GetBytes("alice"));
        properties.To.ShouldBe("queue/orders");
        properties.Subject.ShouldBe("order-created");
        properties.ReplyTo.ShouldBe("queue/replies");
        properties.CorrelationId.ShouldBe(7ul);
        properties.ContentType.ShouldBe(new AmqpSymbol("application/json"));
        properties.ContentEncoding.ShouldBe(new AmqpSymbol("gzip"));
        properties.AbsoluteExpiryTime.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        properties.CreationTime.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        properties.GroupId.ShouldBe("group-a");
        properties.GroupSequence.ShouldBe(5u);
        properties.ReplyToGroupId.ShouldBe("group-b");

        IReadOnlyDictionary<string, object?> applicationProperties = decoded.ApplicationProperties.ShouldNotBeNull();
        applicationProperties["region"].ShouldBe("us-east");
        applicationProperties["attempt"].ShouldBe(2);
        decoded.Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBe("payload");
        decoded.Footer.ShouldNotBeNull()[new AmqpSymbol("x-opt-digest")].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 0xde, 0xad });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should write default header and properties fields as empty lists")]
    public void Encode_WithDefaultSections_ShouldWriteEmptyLists()
    {
        // Arrange
        AmqpMessage message = new()
        {
            Header = new AmqpHeader(),
            Properties = new AmqpProperties()
        };

        // Act
        byte[] encoded = AmqpMessageCodec.Encode(message);
        AmqpMessage decoded = AmqpMessageCodec.Decode(encoded);

        // Assert
        encoded.ShouldBe(Concat(_emptyHeader, _emptyProperties));
        decoded.Header.ShouldNotBeNull().Priority.ShouldBe((byte)4);
        decoded.Header.Durable.ShouldBeFalse();
        decoded.Properties.ShouldNotBeNull().MessageId.ShouldBeNull();
        decoded.Body.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should write the same bytes into a buffer writer")]
    public void Encode_IntoBufferWriter_ShouldMatchArray()
    {
        // Arrange
        AmqpMessage message = CreateFullMessage();
        ArrayBufferWriter<byte> destination = new();

        // Act
        AmqpMessageCodec.Encode(message, destination);

        // Assert
        destination.WrittenSpan.ToArray().ShouldBe(AmqpMessageCodec.Encode(message));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should reject null arguments")]
    public void Encode_WithNullArguments_ShouldThrowArgumentNullException()
    {
        // Act and Assert
        Should.Throw<ArgumentNullException>(() => AmqpMessageCodec.Encode(null!)).ParamName.ShouldBe("message");
        Should.Throw<ArgumentNullException>(() => AmqpMessageCodec.Encode(new AmqpMessage(), null!)).ParamName.ShouldBe("destination");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should return data sections as slices of the payload")]
    public void Decode_WithDataSections_ShouldSlicePayload()
    {
        // Arrange
        AmqpMessage message = new()
        {
            Properties = new AmqpProperties { UserId = new byte[] { 0x75 } },
            Body = new AmqpDataBody([new byte[] { 1, 2 }, new byte[] { 3 }])
        };
        byte[] payload = AmqpMessageCodec.Encode(message);

        // Act
        AmqpMessage decoded = AmqpMessageCodec.Decode(payload);

        // Assert
        AmqpDataBody body = decoded.Body.ShouldBeOfType<AmqpDataBody>();
        body.Sections.Count.ShouldBe(2);
        body.Sections[0].ToArray().ShouldBe(new byte[] { 1, 2 });
        body.Sections[1].ToArray().ShouldBe(new byte[] { 3 });
        MemoryMarshal.TryGetArray(body.Sections[0], out ArraySegment<byte> section).ShouldBeTrue();
        section.Array.ShouldBeSameAs(payload);
        MemoryMarshal.TryGetArray(decoded.Properties.ShouldNotBeNull().UserId.ShouldNotBeNull(), out ArraySegment<byte> userId).ShouldBeTrue();
        userId.Array.ShouldBeSameAs(payload);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should keep every sequence section of a sequence body")]
    public void Decode_WithSequenceSections_ShouldKeepEach()
    {
        // Arrange
        AmqpMessage message = new()
        {
            Body = new AmqpSequenceBody([new List<object?> { 1, "a" }, new List<object?>()])
        };

        // Act
        AmqpMessage decoded = AmqpMessageCodec.Decode(AmqpMessageCodec.Encode(message));

        // Assert
        AmqpSequenceBody body = decoded.Body.ShouldBeOfType<AmqpSequenceBody>();
        body.Sequences.Count.ShouldBe(2);
        body.Sequences[0].ShouldBe(new object?[] { 1, "a" });
        body.Sequences[1].ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should decode a value body holding a compound value")]
    public void Decode_WithCompoundValueBody_ShouldRoundTrip()
    {
        // Arrange
        AmqpMessage message = new()
        {
            Body = new AmqpValueBody(new Dictionary<string, object?> { ["id"] = 7L, ["tags"] = new List<object?> { "a", "b" } })
        };

        // Act
        AmqpMessage decoded = AmqpMessageCodec.Decode(AmqpMessageCodec.Encode(message));

        // Assert
        Dictionary<object, object?> map = decoded.Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBeOfType<Dictionary<object, object?>>();
        map["id"].ShouldBe(7L);
        map["tags"].ShouldBeOfType<List<object?>>().ShouldBe(new object?[] { "a", "b" });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should decode each kind of message identifier")]
    public void Decode_WithEachMessageIdKind_ShouldRoundTrip()
    {
        // Arrange
        Guid uuid = new("0f8fad5b-d9cb-469f-a165-70867728950e");
        object[] identifiers = [42ul, uuid, "id-1", new byte[] { 1, 2 }];

        foreach (object identifier in identifiers)
        {
            AmqpMessage message = new() { Properties = new AmqpProperties { MessageId = identifier, CorrelationId = identifier } };

            // Act
            AmqpProperties decoded = AmqpMessageCodec.Decode(AmqpMessageCodec.Encode(message)).Properties.ShouldNotBeNull();

            // Assert
            if (identifier is byte[] bytes)
            {
                decoded.MessageId.ShouldBeOfType<byte[]>().ShouldBe(bytes);
                decoded.CorrelationId.ShouldBeOfType<byte[]>().ShouldBe(bytes);
            }
            else
            {
                decoded.MessageId.ShouldBe(identifier);
                decoded.CorrelationId.ShouldBe(identifier);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should decode a binary identifier set from memory as a byte array")]
    public void Decode_WithMemoryMessageId_ShouldReturnByteArray()
    {
        // Arrange
        AmqpMessage message = new() { Properties = new AmqpProperties { MessageId = new ReadOnlyMemory<byte>([9, 8]) } };

        // Act
        AmqpProperties decoded = AmqpMessageCodec.Decode(AmqpMessageCodec.Encode(message)).Properties.ShouldNotBeNull();

        // Assert
        decoded.MessageId.ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 9, 8 });
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a message identifier of a type the specification does not allow")]
    [InlineData("an int", "005373c003015405")]
    [InlineData("an array of byte, which decodes as sbyte[]", "005373c00701e00402510102")]
    public void Decode_WithDisallowedMessageIdType_ShouldThrowDecodeError(string scenario, string hex)
    {
        // Arrange
        byte[] payload = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => AmqpMessageCodec.Decode(payload), scenario);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        exception.Message.ShouldBe(ErrorMessages.MessageIdInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should accept a symbol address some peers send in place of a string")]
    public void Decode_WithSymbolAddress_ShouldReturnText()
    {
        // Arrange
        byte[] payload = Convert.FromHexString("005373c0070340" + "40a3027131");

        // Act
        AmqpMessage decoded = AmqpMessageCodec.Decode(payload);

        // Assert
        decoded.Properties.ShouldNotBeNull().To.ShouldBe("q1");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should accept a symbolic section descriptor")]
    public void Decode_WithSymbolicSectionDescriptor_ShouldDecodeSection()
    {
        // Arrange
        byte[] name = Encoding.ASCII.GetBytes("amqp:amqp-value:*");
        byte[] payload = [0x00, 0xa3, (byte)name.Length, .. name, 0xa1, 0x01, 0x61];

        // Act
        AmqpMessage decoded = AmqpMessageCodec.Decode(payload);

        // Assert
        decoded.Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBe("a");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should decode an empty payload as a message without sections")]
    public void Decode_WithEmptyPayload_ShouldReturnEmptyMessage()
    {
        // Act
        AmqpMessage decoded = AmqpMessageCodec.Decode(ReadOnlyMemory<byte>.Empty);

        // Assert
        decoded.Header.ShouldBeNull();
        decoded.Properties.ShouldBeNull();
        decoded.Body.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should refuse a section that is not a message section")]
    [InlineData("40", nameof(ErrorMessages.SectionNotDescribed))]
    [InlineData("00537945", nameof(ErrorMessages.SectionDescriptorUnsupported))]
    [InlineData("00537540", nameof(ErrorMessages.ValueNotBinary))]
    [InlineData("005376a10161", nameof(ErrorMessages.ValueNotList))]
    [InlineData("005377a105", nameof(ErrorMessages.ValueTruncated))]
    public void Decode_WithInvalidSection_ShouldThrowDecodeError(string hex, string messageName)
    {
        // Arrange
        byte[] payload = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => AmqpMessageCodec.Decode(payload));

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        exception.Message.ShouldBe(ErrorMessages.ResourceManager.GetString(messageName, ErrorMessages.Culture));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should refuse an application property with no AMQP representation")]
    public void Encode_WithUnsupportedApplicationProperty_ShouldThrowArgumentException()
    {
        // Arrange
        AmqpMessage message = new()
        {
            ApplicationProperties = new Dictionary<string, object?> { ["elapsed"] = TimeSpan.FromSeconds(1) }
        };

        // Act and Assert
        Should.Throw<ArgumentException>(() => AmqpMessageCodec.Encode(message));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should refuse an application property that holds a list, map or array")]
    [InlineData("list")]
    [InlineData("map")]
    [InlineData("int array")]
    [InlineData("sbyte array")]
    [InlineData("described list")]
    [InlineData("delivery state")]
    public void Encode_WithCompoundApplicationProperty_ShouldThrowArgumentException(string kind)
    {
        // Arrange
        object value = kind switch
        {
            "list" => new List<object?> { 1, 2 },
            "map" => new Dictionary<string, object?> { ["nested"] = 1 },
            "int array" => new[] { 1, 2 },
            "sbyte array" => new sbyte[] { 1, 2 },
            "described list" => new AmqpDescribedValue(new AmqpSymbol("x-opt-wrapped"), new List<object?> { 1 }),
            _ => AmqpAccepted.Instance
        };
        AmqpMessage message = new()
        {
            ApplicationProperties = new Dictionary<string, object?> { ["simple"] = "ok", ["compound"] = value }
        };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpMessageCodec.Encode(message));

        // Assert
        exception.ParamName.ShouldBe("message");
        exception.Message.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ApplicationPropertyNotSimple, "compound", value.GetType()));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should keep every simple application property value")]
    public void Encode_WithSimpleApplicationProperties_ShouldRoundTrip()
    {
        // Arrange
        Guid id = Guid.NewGuid();
        DateTimeOffset sent = new(2026, 11, 20, 8, 30, 0, TimeSpan.Zero);
        AmqpMessage message = new()
        {
            ApplicationProperties = new Dictionary<string, object?>
            {
                ["null"] = null,
                ["flag"] = true,
                ["count"] = 7,
                ["total"] = 9_000_000_000L,
                ["ratio"] = 0.5,
                ["letter"] = 'x',
                ["sent"] = sent,
                ["id"] = id,
                ["name"] = "orders",
                ["kind"] = new AmqpSymbol("created"),
                ["digest"] = new byte[] { 1, 2, 3 },
                ["price"] = new AmqpDecimal64(0x3040000000000001),
                ["tagged"] = new AmqpDescribedValue(new AmqpSymbol("x-opt-unit"), "kg")
            }
        };

        // Act
        IReadOnlyDictionary<string, object?> decoded = AmqpMessageCodec.Decode(AmqpMessageCodec.Encode(message)).ApplicationProperties.ShouldNotBeNull();

        // Assert
        decoded.Count.ShouldBe(13);
        decoded["null"].ShouldBeNull();
        decoded["flag"].ShouldBe(true);
        decoded["count"].ShouldBe(7);
        decoded["total"].ShouldBe(9_000_000_000L);
        decoded["ratio"].ShouldBe(0.5);
        decoded["letter"].ShouldBe(new Rune('x'));
        decoded["sent"].ShouldBe(sent);
        decoded["id"].ShouldBe(id);
        decoded["name"].ShouldBe("orders");
        decoded["kind"].ShouldBe(new AmqpSymbol("created"));
        decoded["digest"].ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 1, 2, 3 });
        decoded["price"].ShouldBe(new AmqpDecimal64(0x3040000000000001));
        decoded["tagged"].ShouldBeOfType<AmqpDescribedValue>().Value.ShouldBe("kg");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should keep an application property that holds a list, as other libraries do")]
    public void Decode_WithCompoundApplicationProperty_ShouldKeepIt()
    {
        // Arrange: application properties mapping "tags" to an empty list.
        byte[] payload = Convert.FromHexString("005374c10802a1047461677345");

        // Act
        AmqpMessage decoded = AmqpMessageCodec.Decode(payload);

        // Assert
        decoded.ApplicationProperties.ShouldNotBeNull()["tags"].ShouldBeOfType<List<object?>>().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - GetBareMessage: Should return the properties, application properties and body as encoded")]
    public void GetBareMessage_WithAnnotatedMessage_ShouldReturnBareSections()
    {
        // Arrange
        AmqpMessage message = CreateFullMessage();
        AmqpMessage bare = new()
        {
            Properties = message.Properties,
            ApplicationProperties = message.ApplicationProperties,
            Body = message.Body
        };
        byte[] payload = AmqpMessageCodec.Encode(message);

        // Act
        ReadOnlyMemory<byte> bareMessage = AmqpMessageCodec.GetBareMessage(payload);

        // Assert
        bareMessage.ToArray().ShouldBe(AmqpMessageCodec.Encode(bare));
        MemoryMarshal.TryGetArray(bareMessage, out ArraySegment<byte> segment).ShouldBeTrue();
        segment.Array.ShouldBeSameAs(payload);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - GetBareMessage: Should return nothing for a message with only annotated sections")]
    public void GetBareMessage_WithoutBareSections_ShouldReturnEmpty()
    {
        // Arrange
        byte[] payload = Concat(_emptyHeader, _emptyFooter);

        // Act
        ReadOnlyMemory<byte> bareMessage = AmqpMessageCodec.GetBareMessage(payload);

        // Assert
        bareMessage.IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Reannotate: Should replace the annotated sections and copy the bare message unchanged")]
    public void Reannotate_WithNewAnnotations_ShouldKeepBareMessage()
    {
        // Arrange
        byte[] original = AmqpMessageCodec.Encode(CreateFullMessage());
        AmqpHeader header = new() { DeliveryCount = 4 };
        Dictionary<AmqpSymbol, object?> deliveryAnnotations = new() { ["x-opt-hop"] = 2 };

        // Act
        byte[] reannotated = AmqpMessageCodec.Reannotate(original, header, deliveryAnnotations, messageAnnotations: null, footer: null);
        AmqpMessage decoded = AmqpMessageCodec.Decode(reannotated);

        // Assert
        AmqpMessageCodec.GetBareMessage(reannotated).ToArray().ShouldBe(AmqpMessageCodec.GetBareMessage(original).ToArray());
        decoded.Header.ShouldNotBeNull().DeliveryCount.ShouldBe(4u);
        decoded.Header.Durable.ShouldBeFalse();
        decoded.DeliveryAnnotations.ShouldNotBeNull()[new AmqpSymbol("x-opt-hop")].ShouldBe(2);
        decoded.MessageAnnotations.ShouldBeNull();
        decoded.Footer.ShouldBeNull();
        decoded.Properties.ShouldNotBeNull().MessageId.ShouldBe("message-1");
        decoded.Body.ShouldBeOfType<AmqpValueBody>().Value.ShouldBe("payload");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Reannotate: Should reduce a message to its bare message when every annotation is removed")]
    public void Reannotate_WithoutAnnotations_ShouldReturnBareMessage()
    {
        // Arrange
        byte[] original = AmqpMessageCodec.Encode(CreateFullMessage());

        // Act
        byte[] reannotated = AmqpMessageCodec.Reannotate(original, header: null, deliveryAnnotations: null, messageAnnotations: null, footer: null);

        // Assert
        reannotated.ShouldBe(AmqpMessageCodec.GetBareMessage(original).ToArray());
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Reannotate: Should add a footer after the bare message")]
    public void Reannotate_WithFooter_ShouldWriteFooterLast()
    {
        // Arrange
        byte[] original = AmqpMessageCodec.Encode(new AmqpMessage { Body = new AmqpValueBody("a") });
        Dictionary<AmqpSymbol, object?> footer = new() { ["x-opt-digest"] = "sha256" };

        // Act
        byte[] reannotated = AmqpMessageCodec.Reannotate(original, header: null, deliveryAnnotations: null, messageAnnotations: null, footer);
        AmqpMessage decoded = AmqpMessageCodec.Decode(reannotated);

        // Assert
        reannotated.AsSpan(0, original.Length).ToArray().ShouldBe(original);
        decoded.Footer.ShouldNotBeNull()[new AmqpSymbol("x-opt-digest")].ShouldBe("sha256");
    }

    private static AmqpMessage CreateFullMessage()
    {
        return new AmqpMessage
        {
            Header = new AmqpHeader
            {
                Durable = true,
                Priority = 9,
                TimeToLive = 30_000,
                FirstAcquirer = true,
                DeliveryCount = 3
            },
            DeliveryAnnotations = new Dictionary<AmqpSymbol, object?> { ["x-opt-trace"] = "hop-1" },
            MessageAnnotations = new Dictionary<AmqpSymbol, object?> { ["x-opt-sequence"] = 42L },
            Properties = new AmqpProperties
            {
                MessageId = "message-1",
                UserId = Encoding.ASCII.GetBytes("alice"),
                To = "queue/orders",
                Subject = "order-created",
                ReplyTo = "queue/replies",
                CorrelationId = 7ul,
                ContentType = new AmqpSymbol("application/json"),
                ContentEncoding = new AmqpSymbol("gzip"),
                AbsoluteExpiryTime = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000),
                CreationTime = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
                GroupId = "group-a",
                GroupSequence = 5,
                ReplyToGroupId = "group-b"
            },
            ApplicationProperties = new Dictionary<string, object?> { ["region"] = "us-east", ["attempt"] = 2 },
            Body = new AmqpValueBody("payload"),
            Footer = new Dictionary<AmqpSymbol, object?> { ["x-opt-digest"] = new byte[] { 0xde, 0xad } }
        };
    }

    private static byte[] Concat(params byte[][] sections)
    {
        return sections.SelectMany(section => section).ToArray();
    }
}
