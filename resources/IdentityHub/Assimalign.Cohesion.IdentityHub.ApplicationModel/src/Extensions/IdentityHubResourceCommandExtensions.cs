using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Declares IdentityHub audiences and clients owned by the claiming application.</summary>
public static partial class IdentityHubResourceCommandExtensions
{
    extension(IIdentityHubResourceDescriptor descriptor)
    {
        /// <summary>Declares an audience accepted by the identity provider.</summary>
        /// <param name="name">The audience name and ownership key.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">The descriptor or name is null.</exception>
        /// <exception cref="ArgumentException">The name is blank or contains a slash.</exception>
        /// <exception cref="InvalidOperationException">The descriptor belongs to a built model.</exception>
        public IIdentityHubResourceDescriptor AddAudience(string name, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ValidateSegment(name, nameof(name));
            descriptor.AddCommand("identityhub.add-audience", name, new AddAudienceCommandPayload(name),
                IdentityHubCommandJsonContext.Default.AddAudienceCommandPayload, optional);
            return descriptor;
        }

        /// <summary>Declares a confidential client using a named credential source.</summary>
        /// <param name="clientId">The client identifier and ownership key.</param>
        /// <param name="audiences">The nonempty set of audiences the client may request.</param>
        /// <param name="credentialSource">The name of a secret mount available to the IdentityHub resource.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">An identifier, audience, or credential source is invalid.</exception>
        /// <exception cref="InvalidOperationException">The descriptor belongs to a built model.</exception>
        public IIdentityHubResourceDescriptor AddClient(string clientId, IEnumerable<string> audiences,
            string credentialSource, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(audiences);
            ValidateSegment(clientId, nameof(clientId));
            ArgumentException.ThrowIfNullOrWhiteSpace(credentialSource);
            if (credentialSource.StartsWith("literal:", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Client declarations require a named credential source, never literal credential material.", nameof(credentialSource));
            }
            string[] allowed = audiences.Select(static value =>
            {
                ValidateSegment(value, nameof(audiences));
                return value;
            }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (allowed.Length == 0)
            {
                throw new ArgumentException("A client must name at least one audience.", nameof(audiences));
            }
            descriptor.AddCommand("identityhub.add-client", clientId,
                new AddClientCommandPayload(clientId, allowed, credentialSource),
                IdentityHubCommandJsonContext.Default.AddClientCommandPayload, optional);
            return descriptor;
        }
    }

    private static void ValidateSegment(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Contains('/'))
        {
            throw new ArgumentException("Identity command identifiers must not contain '/'.", parameter);
        }
    }
}
