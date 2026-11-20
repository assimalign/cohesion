using System.Globalization;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Checks the rules on an <c>attach</c> that decoding a single field cannot see: those that span more than
/// one of its fields, and the values inside its termini that AMQP 1.0 does not allow (Part 2, section 2.7.3;
/// Part 3, sections 3.5.3, 3.5.4, 3.5.8 and 3.5.9).
/// </summary>
/// <remarks>
/// <para>
/// A peer's attach that opens a link and breaks one is refused with <c>amqp:invalid-field</c>, which ends
/// only that link; a local one is refused before it is sent, as the application's mistake. A peer's attach
/// that answers a link this endpoint opened is not checked: a refusal's reason travels in the
/// <c>detach</c> that follows it, and failing the link on the attach would hide that reason.
/// </para>
/// <para>
/// A dynamic terminus follows the role of the endpoint that sends it. The endpoint that owns the terminus
/// (the sender for a source, the receiver for a target) creates the node and names its address. The other
/// endpoint's attach that opens the link asks for the node and names no address; in an answer it echoes the
/// owner's terminus, so the request rule does not apply there.
/// </para>
/// </remarks>
internal static class AmqpAttachRules
{
    /// <summary>
    /// Finds the first rule an <c>attach</c> breaks, judged by the role of the endpoint that sends it.
    /// </summary>
    /// <param name="attach">The attach.</param>
    /// <param name="opening">
    /// <see langword="true"/> for an attach that opens a link; <see langword="false"/> for one that answers
    /// the peer's, which carries back the terminus the peer owns as the peer sent it.
    /// </param>
    /// <returns>A description of the broken rule, or <see langword="null"/> when the attach breaks none.</returns>
    public static string? FindViolation(AmqpAttachPerformative attach, bool opening)
    {
        // A sender names the delivery-count its deliveries start from (Part 2, section 2.7.3).
        if (attach.Role == AmqpRole.Sender && attach.InitialDeliveryCount is null)
        {
            return ErrorMessages.InitialDeliveryCountMissing;
        }

        if (attach.Source is AmqpSource source)
        {
            string? violation = FindDynamicViolation(
                "source",
                source.Dynamic,
                source.Address,
                source.DynamicNodeProperties is not null,
                owner: attach.Role == AmqpRole.Sender,
                opening)
                ?? AmqpTerminusRules.FindViolation("source", source.DynamicNodeProperties, source.Filter);

            if (violation is not null)
            {
                return violation;
            }
        }

        if (attach.Target is AmqpTarget target)
        {
            return FindDynamicViolation(
                "target",
                target.Dynamic,
                target.Address,
                target.DynamicNodeProperties is not null,
                owner: attach.Role == AmqpRole.Receiver,
                opening)
                ?? AmqpTerminusRules.FindViolation("target", target.DynamicNodeProperties, null);
        }

        return null;
    }

    private static string? FindDynamicViolation(string terminus, bool dynamic, string? address, bool hasDynamicNodeProperties, bool owner, bool opening)
    {
        // Only a dynamic node is created with properties (Part 3, sections 3.5.3 and 3.5.4).
        if (hasDynamicNodeProperties && !dynamic)
        {
            return string.Format(CultureInfo.CurrentCulture, ErrorMessages.DynamicNodePropertiesUnexpected, terminus);
        }

        if (!dynamic)
        {
            return null;
        }

        // The endpoint that owns the terminus created the node, so it names the address it assigned.
        if (owner && address is null)
        {
            return string.Format(CultureInfo.CurrentCulture, ErrorMessages.DynamicAddressMissing, terminus);
        }

        // A request for a dynamic node leaves the address to the endpoint that creates the node.
        if (!owner && opening && address is not null)
        {
            return string.Format(CultureInfo.CurrentCulture, ErrorMessages.DynamicAddressUnexpected, terminus);
        }

        return null;
    }
}
