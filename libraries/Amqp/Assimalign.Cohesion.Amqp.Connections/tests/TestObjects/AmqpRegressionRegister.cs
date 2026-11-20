using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The regression register (#433): every defect the AMQP engine is known to have had, each with the work item
/// that found it, the section of AMQP 1.0 it broke, the compliance-map rule it falls under, and the tests and
/// corpus cases that fail if it returns.
/// </summary>
/// <remarks>
/// A defect is wrong behavior in code that existed: a stall or a hang, data lost or changed, a valid input
/// refused, or invalid output written. Accepting what a peer should not send is a leniency, which its compliance
/// map records instead. Each entry was verified by putting its defect back into the engine and seeing the tests
/// and cases it names fail; the project's <c>DESIGN.md</c>, "Regression register", says how to add one.
/// </remarks>
internal static class AmqpRegressionRegister
{
    /// <summary>
    /// Gets the entries, grouped by category.
    /// </summary>
    public static IReadOnlyList<AmqpRegression> Entries { get; } =
    [
        new("D1", AmqpRegressionCategory.MalformedFrame,
            "An encoder failure in the middle of a frame, such as for a value the type system cannot carry, left the partial frame in the outgoing buffer, corrupting every frame written after it.",
            "#417", "Part 2, sections 2.3.1 and 2.7.3", AmqpComplianceMap.None, null,
            [nameof(AmqpRegressionTests.AttachSenderAsync_WithValueTheEncoderRefuses_ShouldWriteNothingAndKeepTheConnection)],
            []),
        new("D2", AmqpRegressionCategory.MalformedFrame,
            "A delivery state the encoder refused left a partial transfer frame in the outgoing buffer, corrupting every frame written after it.",
            "#417", "Part 2, sections 2.3.1 and 2.7.5", AmqpComplianceMap.None, null,
            [nameof(AmqpRegressionTests.SendAsync_WithStateTheEncoderRefuses_ShouldWriteNothingAndKeepTheLink)],
            []),
        new("D3", AmqpRegressionCategory.MalformedFrame,
            "The handshake read a peer's SASL frames up to the local max-frame-size, which SASL never negotiates, instead of refusing one above 512 bytes from its header.",
            "#1919", "Part 5, section 5.3.1", AmqpComplianceMap.HeaderPhase, "H9",
            [
                nameof(AmqpSaslFrameTests.ConnectAsync_OnSaslFrameAbove512Bytes_ShouldRefuseFromHeader),
                nameof(AmqpSaslFrameTests.AcceptAsync_OnSaslFrameAbove512Bytes_ShouldHangUpFromHeader)
            ],
            [
                "a client's SASL frame that declares 513 bytes is refused from its header",
                "the factory refuses a SASL frame that declares 1024 bytes from its header"
            ]),
        new("D4", AmqpRegressionCategory.MalformedFrame,
            "The handshake wrote SASL frames unchecked, so a long initial response, challenge or outcome went out above 512 bytes.",
            "#1919", "Part 5, section 5.3.1", AmqpComplianceMap.HeaderPhase, "H9",
            [
                nameof(AmqpSaslFrameTests.ConnectAsync_WithInitialResponseAbove512Bytes_ShouldThrowWithoutSending),
                nameof(AmqpSaslFrameTests.AcceptAsync_OnMechanismStepAbove512Bytes_ShouldSendSystemOutcome)
            ],
            []),
        new("D5", AmqpRegressionCategory.MalformedMessage,
            "Arrays were dispatched by type pattern, and CLR array covariance let a uint[] pass as an int[], so it went out as an array of int.",
            "#408", "Part 1, section 1.6.24", AmqpComplianceMap.TypeSystem, "array",
            [nameof(AmqpEncodingTests.ReadValue_WithEncodedArray_ShouldRoundTripTypedArray)],
            ["array8 of uint"]),
        new("D6", AmqpRegressionCategory.MalformedMessage,
            "CLR array covariance let an sbyte[] pass as a byte[], so an array of byte went out as binary.",
            "#408", "Part 1, sections 1.6.24 and 1.6.19", AmqpComplianceMap.TypeSystem, "array",
            [nameof(AmqpEncodingTests.ReadValue_WithEncodedArray_ShouldRoundTripTypedArray)],
            ["array8 of byte"]),
        new("D7", AmqpRegressionCategory.MalformedMessage,
            "The reader decoded no array of short and refused a valid one.",
            "#408", "Part 1, section 1.6.24", AmqpComplianceMap.TypeSystem, "array",
            [nameof(AmqpEncodingTests.ReadValue_WithEncodedArray_ShouldRoundTripTypedArray)],
            ["array8 of short"]),
        new("D8", AmqpRegressionCategory.MalformedMessage,
            "The writer counted each compound twice against the nesting limit, so it refused values the reader accepts.",
            "#408", "Part 1, sections 1.6.22 to 1.6.24", AmqpComplianceMap.None, null,
            [nameof(AmqpEncodingTests.WriteValue_WithDeepNesting_ShouldCapNesting)],
            []),
        new("D9", AmqpRegressionCategory.MalformedMessage,
            "A null string in an array was written as an empty string in its place.",
            "#408", "Part 1, section 1.6.24", AmqpComplianceMap.TypeSystem, "array",
            [nameof(AmqpEncodingTests.WriteValue_WithRefusedArrayElement_ShouldWriteNothing)],
            ["array of string holding null"]),
        new("D10", AmqpRegressionCategory.MalformedMessage,
            "Half of a surrogate pair in a string was encoded as U+FFFD instead of being refused.",
            "#408", "Part 1, section 1.6.20", AmqpComplianceMap.TypeSystem, "string",
            [
                nameof(AmqpEncodingTests.WriteValue_WithUnpairedSurrogateInString_ShouldWriteNothing),
                nameof(AmqpEncodingTests.WriteValue_WithRefusedArrayElement_ShouldWriteNothing)
            ],
            ["string with half of a surrogate pair"]),
        new("D11", AmqpRegressionCategory.MalformedMessage,
            "Malformed UTF-8 decoded with replacement characters, so two different strings, such as two addresses, could decode as one.",
            "#408", "Part 1, section 1.6.20", AmqpComplianceMap.TypeSystem, "string",
            [],
            ["str8 that is not UTF-8", "str8 with an encoded surrogate", "str8 with an overlong encoding"]),
        new("D12", AmqpRegressionCategory.MalformedMessage,
            "CLR array covariance let an sbyte[] pass as a binary message-id: one went out as an array of byte, and an array of byte from a peer came back as an identifier.",
            "#407", "Part 3, sections 3.2.4 and 3.2.11 to 3.2.14", AmqpComplianceMap.MessageFormat, "MF7",
            [nameof(AmqpMessageCodecTests.Decode_WithDisallowedMessageIdType_ShouldThrowDecodeError)],
            []),
        new("D13", AmqpRegressionCategory.MalformedMessage,
            "Finding the bare message checked only that annotations came first and the footer last, so re-annotating a payload forwarded sections out of order, repeated, or with a mixed body.",
            "#402", "Part 3, section 3.2", AmqpComplianceMap.MessageFormat, "MF1",
            [],
            [
                "message-annotations then delivery-annotations",
                "application-properties then properties",
                "body then application-properties",
                "two headers",
                "two delivery-annotations",
                "two properties",
                "two application-properties",
                "data then amqp-sequence",
                "amqp-sequence then amqp-value",
                "amqp-value then data",
                "two amqp-value sections"
            ]),
        new("D14", AmqpRegressionCategory.MalformedMessage,
            "An application property holding a list, map or array was written, although application-properties values are simple types.",
            "#407", "Part 3, section 3.2.5", AmqpComplianceMap.MessageFormat, "MF9",
            [nameof(AmqpMessageCodecTests.Encode_WithCompoundApplicationProperty_ShouldThrowArgumentException)],
            []),
        new("D15", AmqpRegressionCategory.MalformedMessage,
            "An unsigned long annotation key, which AMQP reserves, was dropped on decode with its value instead of being refused.",
            "#407", "Part 3, section 3.2.10", AmqpComplianceMap.MessageFormat, "MF8",
            [],
            ["an unsigned long message-annotations key", "an unsigned long footer key"]),
        new("D16", AmqpRegressionCategory.MalformedMessage,
            "A map writer wrote a key that encodes as null, such as a default symbol, which the reader refuses.",
            "#407", "Part 3, section 3.2.10", AmqpComplianceMap.MessageFormat, "MF8",
            [
                nameof(AmqpMessageCodecTests.Encode_WithDefaultSymbolAnnotationKey_ShouldThrowArgumentException),
                nameof(AmqpEncodingTests.WriteValue_WithNullMapKey_ShouldThrowArgumentException)
            ],
            []),
        new("D17", AmqpRegressionCategory.MalformedMessage,
            "The delivery annotation rejected was written whatever its value, although it holds an error.",
            "#407", "Part 3, section 3.2.2", AmqpComplianceMap.None, null,
            [nameof(AmqpMessageCodecTests.Encode_WithRejectedAnnotationNotAnError_ShouldThrowArgumentException)],
            []),
        new("D18", AmqpRegressionCategory.InvalidState,
            "A failed answer to a peer's attach kept the refused options in the link, so a refusal or a second answer after it carried them.",
            "#417", "Part 2, sections 2.6.3 and 2.7.3", AmqpComplianceMap.None, null,
            [nameof(AmqpAttachRulesTests.AcceptReceiverAsync_WithContradictoryTarget_ShouldThrowAndKeepTheRequestOpen)],
            []),
        new("D19", AmqpRegressionCategory.InvalidState,
            "Disposing a connection whose peer kept its side open and never answered close waited for the peer forever, because the read loop kept a pending read on the carrier.",
            "#415", "Part 2, section 2.4.3", AmqpComplianceMap.None, null,
            [nameof(AmqpRegressionTests.DisposeAsync_OnPeerThatNeverAnswersClose_ShouldReleaseTheCarrier)],
            []),
        new("D20", AmqpRegressionCategory.InvalidNegotiation,
            "Reading a protocol header marked the bytes behind it as examined, so a peer that pipelined its header with the frames after it stalled the exchange until it sent more.",
            "#727", "Part 2, sections 2.2 and 2.4.2", AmqpComplianceMap.HeaderPhase, "H15",
            [],
            [
                "a client that pipelines its header, open and begin is answered in order",
                "a server that pipelines its header, open and begin is answered in order",
                "a server that pipelines its AMQP header and open behind its sasl-outcome is answered in order"
            ]),
        new("D21", AmqpRegressionCategory.InvalidNegotiation,
            "Reading a frame during establishment marked the bytes behind it as examined, so a peer that pipelined frames behind a SASL frame or its open stalled the exchange until it sent more.",
            "#1910", "Part 2, section 2.4.2; Part 5, section 5.3.2", AmqpComplianceMap.HeaderPhase, "H15",
            [],
            [
                "a client that pipelines its header, open and begin is answered in order",
                "a server that pipelines its header, open and begin is answered in order",
                "a client that pipelines its AMQP header and open behind its last SASL frame is answered in order",
                "a server that pipelines its AMQP header and open behind its sasl-outcome is answered in order"
            ]),
        new("D22", AmqpRegressionCategory.InvalidNegotiation,
            "Every sasl-outcome carried an empty additional-data binary instead of omitting the field.",
            "#1919", "Part 5, section 5.3.3.6", AmqpComplianceMap.None, null,
            [nameof(AmqpSaslFrameTests.AcceptAsync_OnMechanismStepAbove512Bytes_ShouldSendSystemOutcome)],
            []),
        new("D23", AmqpRegressionCategory.InvalidNegotiation,
            "A listener whose open or sasl-mechanisms frame exceeds the 512 bytes allowed before the peer's open was created anyway, and every connection then failed.",
            "#1919", "Part 2, section 2.4.1; Part 5, section 5.3.1", AmqpComplianceMap.None, null,
            [
                nameof(AmqpConnectionListenerTests.Create_WithOpenAbove512Bytes_ShouldThrowArgumentException),
                nameof(AmqpConnectionListenerTests.Create_WithMechanismsAbove512Bytes_ShouldThrowArgumentException)
            ],
            []),
        new("D24", AmqpRegressionCategory.InvalidNegotiation,
            "A factory that had authenticated and got the SASL header again failed with authentication required instead of the header error.",
            "#411", "Part 5, section 5.3.2; Part 2, section 2.2", AmqpComplianceMap.HeaderPhase, "H10",
            [],
            ["the factory fails when the server answers with the SASL header again after its outcome"]),
        new("D25", AmqpRegressionCategory.InvalidNegotiation,
            "A peer's idle timeout under 100 ms, which the engine's timer cannot keep, was accepted, so the peer heard from the connection too late and closed it.",
            "#412", "Part 2, sections 2.4.5 and 2.7.1", AmqpComplianceMap.HeaderPhase, "H17",
            [],
            [
                "a client that declares an idle timeout of 50 ms is closed with not-implemented",
                "the factory closes a server that declares an idle timeout of 99 ms with not-implemented"
            ]),
        new("D26", AmqpRegressionCategory.InvalidNegotiation,
            "A max-frame-size under 512 bytes, the AMQP minimum, was accepted in the options and advertised in the open.",
            "#1484", "Part 2, sections 2.7.1 and 2.8.19", AmqpComplianceMap.None, null,
            [
                nameof(AmqpConnectionOptionsTests.ListenerOptions_OnInvalidValues_ShouldThrowArgumentOutOfRangeException),
                nameof(AmqpConnectionOptionsTests.FactoryOptions_OnInvalidValues_ShouldThrowArgumentOutOfRangeException)
            ],
            [])
    ];

    /// <summary>
    /// Finds the entry with the identifier.
    /// </summary>
    public static AmqpRegression Find(string id)
    {
        return Entries.Single(entry => entry.Id == id);
    }
}
