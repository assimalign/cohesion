using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The message-format compliance corpus: encoded messages AMQP 1.0 allows and ones it forbids, each tied to
/// the rule of Part 3, section 3.2 it exercises.
/// </summary>
/// <remarks>
/// A case that is refused names the exact message the codec gives, so a rule that starts refusing for the
/// wrong reason fails as surely as one that stops refusing. Add a case with every layout a peer is found
/// to send that the codec gets wrong.
/// </remarks>
internal static class AmqpMessageFormatCorpus
{
    // One of each section, with the smallest value its type allows.
    private const string header = "00537045";
    private const string deliveryAnnotations = "005371c10100";
    private const string messageAnnotations = "005372c10100";
    private const string properties = "00537345";
    private const string applicationProperties = "005374c10100";
    private const string data = "005375a0020102";
    private const string sequence = "005376c003015401";
    private const string value = "005377a10161";
    private const string footer = "005378c10100";

    /// <summary>
    /// Gets the rules the corpus exercises.
    /// </summary>
    public static IReadOnlyList<AmqpMessageFormatRule> Rules { get; } =
    [
        new("MF1", "Sections follow the order header, delivery-annotations, message-annotations, properties, application-properties, body, footer.", "Part 3, section 3.2", true),
        new("MF2", "Each section other than the body appears at most once.", "Part 3, section 3.2", true),
        new("MF3", "The body is one or more data sections, one or more amqp-sequence sections, or one amqp-value section, never a mix.", "Part 3, section 3.2", true),
        new("MF4", "The footer, when present, is the last section.", "Part 3, sections 3.2 and 3.2.9", true),
        new("MF5", "Each section is a described value whose descriptor, numeric or symbolic, names a message section.", "Part 3, section 3.2; Part 1, section 1.5", true),
        new("MF6", "A data section holds binary and an amqp-sequence section holds a list.", "Part 3, sections 3.2.6 and 3.2.7", false),
        new("MF7", "A message-id or correlation-id is a ulong, uuid, binary or string.", "Part 3, sections 3.2.4 and 3.2.11 to 3.2.14", false),
        new("MF8", "Annotation keys are symbols or unsigned longs, and every unsigned long key is reserved, so a receiver that understands none refuses it with not-implemented.", "Part 3, sections 3.2.2, 3.2.3, 3.2.9 and 3.2.10", false),
        new("MF9", "Application-properties keys are strings; the codec keeps a received value whatever its type.", "Part 3, section 3.2.5", false)
    ];

    /// <summary>
    /// Gets the cases.
    /// </summary>
    public static IReadOnlyList<AmqpMessageFormatCase> Cases { get; } =
    [
        // MF1: order.
        new("every section in order", "MF1", header + deliveryAnnotations + messageAnnotations + properties + applicationProperties + value + footer, null),
        new("properties then header", "MF1", properties + header, OrderInvalid("header", "properties")),
        new("message-annotations then delivery-annotations", "MF1", messageAnnotations + deliveryAnnotations, OrderInvalid("delivery-annotations", "message-annotations")),
        new("application-properties then properties", "MF1", applicationProperties + properties, OrderInvalid("properties", "application-properties")),
        new("body then application-properties", "MF1", value + applicationProperties, OrderInvalid("application-properties", "amqp-value")),

        // MF2: multiplicity.
        new("one of each annotated section around a data body", "MF2", header + messageAnnotations + data + footer, null),
        new("two headers", "MF2", header + header, Repeated("header")),
        new("two delivery-annotations", "MF2", deliveryAnnotations + deliveryAnnotations, Repeated("delivery-annotations")),
        new("two properties", "MF2", properties + properties + value, Repeated("properties")),
        new("two application-properties", "MF2", applicationProperties + applicationProperties, Repeated("application-properties")),

        // MF3: body kinds.
        new("three data sections", "MF3", data + data + data, null),
        new("two amqp-sequence sections", "MF3", sequence + sequence, null),
        new("one amqp-value section", "MF3", properties + value, null),
        new("no body section, which widely used libraries send", "MF3", header + properties, null),
        new("two amqp-value sections", "MF3", value + value, Repeated("amqp-value")),
        new("data then amqp-sequence", "MF3", data + sequence, Mixed("data", "amqp-sequence")),
        new("amqp-sequence then amqp-value", "MF3", sequence + value, Mixed("amqp-sequence", "amqp-value")),
        new("amqp-value then data", "MF3", value + data, Mixed("amqp-value", "data")),

        // MF4: footer placement.
        new("footer after a data body", "MF4", data + data + footer, null),
        new("footer then body", "MF4", footer + value, OrderInvalid("amqp-value", "footer")),
        new("footer then header", "MF4", footer + header, OrderInvalid("header", "footer")),
        new("footer between data sections", "MF4", data + footer + data, OrderInvalid("data", "footer")),
        new("two footers", "MF4", value + footer + footer, Repeated("footer")),

        // MF5: descriptors.
        new("symbolic descriptors", "MF5",
            "00a310616d71703a6865616465723a6c69737445"
            + "00a314616d71703a70726f706572746965733a6c69737445"
            + "00a310616d71703a646174613a62696e617279a0020102"
            + "00a30f616d71703a666f6f7465723a6d6170c10100",
            null),
        new("a section that is not described", "MF5", "40", ErrorMessages.SectionNotDescribed),
        new("an unknown numeric descriptor", "MF5", "00537945", ErrorMessages.SectionDescriptorUnsupported),
        new("a performative's descriptor", "MF5", "00531045", ErrorMessages.SectionDescriptorUnsupported),
        new("an unknown symbolic descriptor", "MF5", "00a311616d71703a756e6b6e6f776e3a6c69737445", ErrorMessages.SectionDescriptorUnsupported),

        // MF6: body values.
        new("an empty data section", "MF6", "005375a000", null),
        new("a data section holding null", "MF6", "00537540", ErrorMessages.ValueNotBinary),
        new("an amqp-sequence section holding a string", "MF6", "005376a10161", ErrorMessages.ValueNotList),

        // MF7: message identifiers.
        new("a ulong message-id", "MF7", "005373c003015305", null),
        new("a uuid message-id", "MF7", "005373c012019800112233445566778899aabbccddeeff", null),
        new("a binary message-id", "MF7", "005373c00501a0020102", null),
        new("a string message-id", "MF7", "005373c00401a10161", null),
        new("an int message-id", "MF7", "005373c003015405", ErrorMessages.MessageIdInvalid),
        new("a list correlation-id", "MF7", "005373c00706404040404045", ErrorMessages.MessageIdInvalid),

        // MF8: annotation keys.
        new("reserved, extension and optional symbolic annotation keys", "MF8", "005372c11506a3016140a303782d6240a307782d6f70742d6340", null),
        new("an unsigned long message-annotations key", "MF8", "005372c10904530540a301615401",
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.AnnotationKeyReserved, 5), AmqpErrorCondition.NotImplemented),
        new("an unsigned long footer key", "MF8", value + "005378c103024440",
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.AnnotationKeyReserved, 0), AmqpErrorCondition.NotImplemented),
        new("a string delivery-annotations key", "MF8", "005371c10602a101615401", ErrorMessages.AnnotationKeyInvalid),

        // MF9: application-properties keys.
        new("a string application-properties key", "MF9", "005374c10602a101615401", null),
        new("an application property holding a list, as other libraries send", "MF9", "005374c10802a1047461677345", null),
        new("a symbol application-properties key", "MF9", "005374c10602a301615401", ErrorMessages.MapKeyNotString),
        new("a null application-properties key", "MF9", "005374c10402405401", ErrorMessages.MapKeyNotString)
    ];

    /// <summary>
    /// Finds a case by its name.
    /// </summary>
    public static AmqpMessageFormatCase Find(string name)
    {
        return Cases.Single(entry => entry.Name == name);
    }

    /// <summary>
    /// Finds a rule by its identifier.
    /// </summary>
    public static AmqpMessageFormatRule FindRule(string id)
    {
        return Rules.Single(rule => rule.Id == id);
    }

    private static string OrderInvalid(string section, string previous)
    {
        return string.Format(CultureInfo.CurrentCulture, ErrorMessages.SectionOrderInvalid, section, previous);
    }

    private static string Repeated(string section)
    {
        return string.Format(CultureInfo.CurrentCulture, ErrorMessages.SectionRepeated, section);
    }

    private static string Mixed(string first, string second)
    {
        return string.Format(CultureInfo.CurrentCulture, ErrorMessages.BodyMixed, first, second);
    }
}
