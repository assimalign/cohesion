using System.Globalization;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Checks the rules on an <c>attach</c> that span more than one of its fields, which decoding a single
/// field cannot see (AMQP 1.0 Part 2, section 2.7.3; Part 3, sections 3.5.3 and 3.5.4).
/// </summary>
/// <remarks>
/// A peer's attach that opens a link and breaks one is refused with <c>amqp:invalid-field</c>, which ends
/// only that link; a local one is refused before it is sent, as the application's mistake. A peer's attach
/// that answers a link this endpoint opened is not checked: a refusal's reason travels in the
/// <c>detach</c> that follows it, and failing the link on the attach would hide that reason.
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

        // In an attach that opens a link, the terminus the other end owns is a request: a receiver's source
        // and a sender's target name the node they want, or ask for a dynamic one.
        if (attach.Source is AmqpSource source)
        {
            string? violation = FindViolation(
                "source",
                source.Dynamic,
                source.Address,
                source.DynamicNodeProperties is not null,
                opening && attach.Role == AmqpRole.Receiver);

            if (violation is not null)
            {
                return violation;
            }
        }

        if (attach.Target is AmqpTarget target)
        {
            return FindViolation(
                "target",
                target.Dynamic,
                target.Address,
                target.DynamicNodeProperties is not null,
                opening && attach.Role == AmqpRole.Sender);
        }

        return null;
    }

    private static string? FindViolation(string terminus, bool dynamic, string? address, bool hasDynamicNodeProperties, bool request)
    {
        // Only a dynamic node is created with properties (Part 3, sections 3.5.3 and 3.5.4).
        if (hasDynamicNodeProperties && !dynamic)
        {
            return string.Format(CultureInfo.CurrentCulture, ErrorMessages.DynamicNodePropertiesUnexpected, terminus);
        }

        // A request for a dynamic node leaves the address to the endpoint that creates the node.
        if (request && dynamic && address is not null)
        {
            return string.Format(CultureInfo.CurrentCulture, ErrorMessages.DynamicAddressUnexpected, terminus);
        }

        return null;
    }
}
