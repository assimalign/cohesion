using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// The terminus compliance corpus: encoded <c>attach</c> performatives whose source or target AMQP 1.0
/// allows or forbids, each tied to the rule of Part 3, section 3.5 it exercises.
/// </summary>
/// <remarks>
/// <para>
/// Every case is an attach from a link named <c>link</c> on handle 0. A receiver's attach carries the target
/// <c>peer</c>; a sender's carries the source <c>peer</c> and the target <c>queue</c>, and names the
/// delivery-count its deliveries start from, so only the terminus under test can break a rule.
/// </para>
/// <para>
/// A case that is refused names the exact description, so a rule that starts refusing for the wrong
/// reason fails as surely as one that stops refusing. Add a case with every terminus a peer is found to
/// send that the engine gets wrong.
/// </para>
/// </remarks>
internal static class AmqpTerminusCorpus
{
    /// <summary>
    /// Gets the rules the corpus exercises.
    /// </summary>
    public static IReadOnlyList<AmqpTerminusRule> Rules { get; } =
    [
        new("T1", "A terminus whose fields are absent or null takes the defaults: no address, durability none, expiry policy session-end, timeout 0, not dynamic.", "Part 3, sections 3.5.3 to 3.5.6", false, false),
        new("T2", "An attach that opens a link and asks for a dynamic node names no address.", "Part 3, sections 3.5.3 and 3.5.4", true, false),
        new("T3", "The endpoint that owns a dynamic terminus, the sender for a source and the receiver for a target, names the address of the node it created.", "Part 3, sections 3.5.3 and 3.5.4", true, false),
        new("T4", "Only a dynamic terminus carries dynamic-node-properties.", "Part 3, sections 3.5.3 and 3.5.4", true, false),
        new("T5", "The lifetime-policy node property is a lifetime policy.", "Part 3, sections 3.5.9 and 3.5.10 to 3.5.13", true, true),
        new("T6", "The supported-dist-modes node property is one or more symbols.", "Part 3, sections 3.5.7 and 3.5.9", true, true),
        new("T7", "A filter is a described value or null.", "Part 3, section 3.5.8", true, true),
        new("T8", "Terminus durability is none, configuration or unsettled-state (0, 1 or 2).", "Part 3, section 3.5.5", true, false),
        new("T9", "Terminus expiry policy is link-detach, session-end, connection-close or never.", "Part 3, section 3.5.6", true, false),
        new("T10", "A default-outcome is an outcome.", "Part 3, section 3.5.3", true, false)
    ];

    /// <summary>
    /// Gets the cases.
    /// </summary>
    public static IReadOnlyList<AmqpTerminusCase> Cases { get; } =
    [
        // T1: defaults.
        new("a minimal source", "T1", "005312c01b07a1046c696e6b4341404000532845005329c00701a10470656572", true, null),
        new("a source whose every field is null", "T1", "005312c02807a1046c696e6b43414040005328c00c0b4040404040404040404040005329c00701a10470656572", true, null),
        new("a minimal target", "T1", "005312c01e0aa1046c696e6b43424040005328c00701a1047065657200532945404043", true, null),

        // T2: requesting a dynamic node.
        new("a receiver that asks for a dynamic source", "T2", "005312c02207a1046c696e6b43414040005328c006054040404041005329c00701a10470656572", true, null),
        new("a sender that asks for a dynamic target", "T2", "005312c0250aa1046c696e6b43424040005328c00701a10470656572005329c006054040404041404043", true, null),
        new("a receiver that asks for a dynamic source and names its address", "T2", "005312c02907a1046c696e6b43414040005328c00d05a10663686f73656e40404041005329c00701a10470656572", true, Format(ErrorMessages.DynamicAddressUnexpected, "source")),
        new("a sender that asks for a dynamic target and names its address", "T2", "005312c02c0aa1046c696e6b43424040005328c00701a10470656572005329c00d05a10663686f73656e40404041404043", true, Format(ErrorMessages.DynamicAddressUnexpected, "target")),
        new("a receiver's answer that echoes the dynamic source a sender created", "T2", "005312c02907a1046c696e6b43414040005328c00d05a10663686f73656e40404041005329c00701a10470656572", false, null),

        // T3: creating a dynamic node.
        new("a sender that opens with the dynamic source it created", "T3", "005312c02c0aa1046c696e6b43424040005328c00c05a105746d702d3140404041005329c00801a1057175657565404043", true, null),
        new("a sender's answer that names the dynamic source it created", "T3", "005312c02c0aa1046c696e6b43424040005328c00c05a105746d702d3140404041005329c00801a1057175657565404043", false, null),
        new("a sender that opens with a dynamic source and no address", "T3", "005312c0260aa1046c696e6b43424040005328c006054040404041005329c00801a1057175657565404043", true, Format(ErrorMessages.DynamicAddressMissing, "source")),
        new("a sender's answer that creates a dynamic source and names no address", "T3", "005312c0260aa1046c696e6b43424040005328c006054040404041005329c00801a1057175657565404043", false, Format(ErrorMessages.DynamicAddressMissing, "source")),
        new("a receiver that opens with a dynamic target and no address", "T3", "005312c02307a1046c696e6b43414040005328c00801a1057175657565005329c006054040404041", true, Format(ErrorMessages.DynamicAddressMissing, "target")),

        // T4: dynamic-node-properties.
        new("dynamic-node-properties on a dynamic target request", "T4", "005312c03d0aa1046c696e6b43424040005328c00701a10470656572005329c01e064040404041c11602a30f6c69666574696d652d706f6c69637900532e45404043", true, null),
        new("the properties a sender reports for the dynamic source it created", "T4", "005312c04b0aa1046c696e6b43424040005328c02b06a105746d702d3140404041c11d02a314737570706f727465642d646973742d6d6f646573a3046d6f7665005329c00801a1057175657565404043", false, null),
        new("dynamic-node-properties on a fixed source", "T4", "005312c03d07a1046c696e6b43414040005328c02106a105717565756540404040c11302a30f6c69666574696d652d706f6c69637940005329c00701a10470656572", true, Format(ErrorMessages.DynamicNodePropertiesUnexpected, "source")),
        new("dynamic-node-properties on a fixed target", "T4", "005312c0400aa1046c696e6b43424040005328c00701a10470656572005329c02106a105717565756540404040c11302a30f6c69666574696d652d706f6c69637940404043", true, Format(ErrorMessages.DynamicNodePropertiesUnexpected, "target")),

        // T5: lifetime policies.
        new("the delete-on-close lifetime policy", "T5", "005312c03a07a1046c696e6b43414040005328c01e064040404041c11602a30f6c69666574696d652d706f6c69637900532b45005329c00701a10470656572", true, null),
        new("the delete-on-no-links lifetime policy", "T5", "005312c03a07a1046c696e6b43414040005328c01e064040404041c11602a30f6c69666574696d652d706f6c69637900532c45005329c00701a10470656572", true, null),
        new("the delete-on-no-messages lifetime policy", "T5", "005312c03a07a1046c696e6b43414040005328c01e064040404041c11602a30f6c69666574696d652d706f6c69637900532d45005329c00701a10470656572", true, null),
        new("the delete-on-no-links-or-messages lifetime policy", "T5", "005312c03a07a1046c696e6b43414040005328c01e064040404041c11602a30f6c69666574696d652d706f6c69637900532e45005329c00701a10470656572", true, null),
        new("a lifetime policy under its symbolic descriptor", "T5", "005312c05507a1046c696e6b43414040005328c039064040404041c13102a30f6c69666574696d652d706f6c69637900a319616d71703a64656c6574652d6f6e2d636c6f73653a6c697374c00100005329c00701a10470656572", true, null),
        new("a lifetime policy an extension defines", "T5", "005312c05907a1046c696e6b43414040005328c03d064040404041c13502a30f6c69666574696d652d706f6c69637900a31f636f6d2e6578616d706c653a64656c6574652d61742d6e6f6f6e3a6c69737445005329c00701a10470656572", true, null),
        new("a null lifetime policy", "T5", "005312c03707a1046c696e6b43414040005328c01b064040404041c11302a30f6c69666574696d652d706f6c69637940005329c00701a10470656572", true, null),
        new("a lifetime policy given as a string", "T5", "005312c04707a1046c696e6b43414040005328c02b064040404041c12302a30f6c69666574696d652d706f6c696379a10f64656c6574652d6f6e2d636c6f7365005329c00701a10470656572", true, Format(ErrorMessages.LifetimePolicyInvalid, "source")),
        new("a lifetime policy given as a symbol on a target", "T5", "005312c04a0aa1046c696e6b43424040005328c00701a10470656572005329c02b064040404041c12302a30f6c69666574696d652d706f6c696379a30f64656c6574652d6f6e2d636c6f7365404043", true, Format(ErrorMessages.LifetimePolicyInvalid, "target")),

        // T6: supported distribution modes.
        new("one supported distribution mode", "T6", "005312c04107a1046c696e6b43414040005328c025064040404041c11d02a314737570706f727465642d646973742d6d6f646573a3046d6f7665005329c00701a10470656572", true, null),
        new("two supported distribution modes", "T6", "005312c04907a1046c696e6b43414040005328c02d064040404041c12502a314737570706f727465642d646973742d6d6f646573e00c02a3046d6f766504636f7079005329c00701a10470656572", true, null),
        new("a supported distribution mode given as a string", "T6", "005312c04107a1046c696e6b43414040005328c025064040404041c11d02a314737570706f727465642d646973742d6d6f646573a1046d6f7665005329c00701a10470656572", true, Format(ErrorMessages.SupportedDistributionModesInvalid, "source")),
        new("an empty array of supported distribution modes", "T6", "005312c03f07a1046c696e6b43414040005328c023064040404041c11b02a314737570706f727465642d646973742d6d6f646573e00200a3005329c00701a10470656572", true, Format(ErrorMessages.SupportedDistributionModesInvalid, "source")),
        new("a list of supported distribution modes", "T6", "005312c04407a1046c696e6b43414040005328c028064040404041c12002a314737570706f727465642d646973742d6d6f646573c00701a3046d6f7665005329c00701a10470656572", true, Format(ErrorMessages.SupportedDistributionModesInvalid, "source")),

        // T7: filters.
        new("a described filter", "T7", "005312c06307a1046c696e6b43414040005328c04708a1057175657565404040404040c13702a3016600a3216170616368652e6f72673a73656c6563746f722d66696c7465723a737472696e67a10d636f6c6f72203d202772656427005329c00701a10470656572", true, null),
        new("a null filter", "T7", "005312c03107a1046c696e6b43414040005328c01508a1057175657565404040404040c10502a3016640005329c00701a10470656572", true, null),
        new("a filter given as a string", "T7", "005312c03307a1046c696e6b43414040005328c01708a1057175657565404040404040c10702a30166a10178005329c00701a10470656572", true, Format(ErrorMessages.FilterNotDescribed, "f")),
        new("a lifetime policy given as a filter", "T7", "005312c03407a1046c696e6b43414040005328c01808a1057175657565404040404040c10802a3016600532b45005329c00701a10470656572", true, Format(ErrorMessages.FilterNotDescribed, "f")),

        // T8: durability.
        new("unsettled-state durability", "T8", "005312c02607a1046c696e6b43414040005328c00a02a10571756575655202005329c00701a10470656572", true, null),
        new("durability 3", "T8", "005312c02607a1046c696e6b43414040005328c00a02a10571756575655203005329c00701a10470656572", true, Format(ErrorMessages.TerminusDurabilityInvalid, 3), Decoding: true),

        // T9: expiry policy.
        new("the never expiry policy", "T9", "005312c02c07a1046c696e6b43414040005328c01003a105717565756540a3056e65766572005329c00701a10470656572", true, null),
        new("the expiry policy later", "T9", "005312c02c07a1046c696e6b43414040005328c01003a105717565756540a3056c61746572005329c00701a10470656572", true, Format(ErrorMessages.TerminusExpiryPolicyInvalid, "later"), Decoding: true),

        // T10: default outcome.
        new("the released default outcome", "T10", "005312c02f07a1046c696e6b43414040005328c01309a10571756575654040404040404000532645005329c00701a10470656572", true, null),
        new("a received state as the default outcome", "T10", "005312c03307a1046c696e6b43414040005328c01709a105717565756540404040404040005323c003024344005329c00701a10470656572", true, ErrorMessages.OutcomeInvalid, Decoding: true, Condition: AmqpErrorCondition.DecodeError)
    ];

    /// <summary>
    /// Finds a case by its name.
    /// </summary>
    public static AmqpTerminusCase Find(string name)
    {
        return Cases.Single(entry => entry.Name == name);
    }

    /// <summary>
    /// Finds a rule by its identifier.
    /// </summary>
    public static AmqpTerminusRule FindRule(string id)
    {
        return Rules.Single(rule => rule.Id == id);
    }

    private static string Format(string format, object argument)
    {
        return string.Format(CultureInfo.CurrentCulture, format, argument);
    }
}
