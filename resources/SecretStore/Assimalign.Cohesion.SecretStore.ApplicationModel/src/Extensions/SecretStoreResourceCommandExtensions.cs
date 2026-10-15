using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Declares secret sources and certificate requests without carrying secret material.</summary>
public static partial class SecretStoreResourceCommandExtensions
{
    extension(ISecretStoreResourceDescriptor descriptor)
    {
        /// <summary>Declares a secret whose value is resolved from a named source during delivery.</summary>
        /// <remarks>
        /// The SecretStore manifest marks <c>secretstore.add-secret</c> as requiring an input resolver: the
        /// gateway replaces <paramref name="source"/> with the resolved value, before delivery, through an
        /// <see cref="IResourceCommandInputResolver"/> registered for that kind in the declaring application's
        /// <c>Providers.CommandInputs</c>. For the application's own store, <c>builder.UseSecretStore(...)</c>
        /// (package <c>Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration</c>) registers
        /// <c>SecretStoreAddSecretInputResolver</c>. A store of another application cannot be bound with
        /// <c>UseSecretStore</c>, so add that resolver to <c>Providers.CommandInputs</c> directly. Without a
        /// resolver for the kind, <see cref="IApplicationBuilder.Build"/> fails before anything is delivered,
        /// and so does an application set when it starts the member (register it in that member's
        /// <c>AddApplication</c> callback). The error names the package and verb for the application's own
        /// store, and <c>Providers.CommandInputs</c> for another application's store.
        /// </remarks>
        /// <param name="path">The secret path and ownership key.</param>
        /// <param name="source">A parameter:name or resource:key reference; literal values are forbidden.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">The path or source is invalid or contains a literal.</exception>
        /// <exception cref="InvalidOperationException">The descriptor belongs to a built model.</exception>
        public ISecretStoreResourceDescriptor AddSecret(SecretStoreCommandPath path, SecretStoreCommandSource source, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (path.Value is null)
            {
                throw new ArgumentException(ErrorMessages.CommandPathInvalid, nameof(path));
            }
            if (source.Value is null)
            {
                throw new ArgumentException(ErrorMessages.CommandSourceInvalid, nameof(source));
            }
            descriptor.AddCommand("secretstore.add-secret", path.Value, new AddSecretCommandPayload(path.Value, source.Value),
                SecretStoreCommandJsonContext.Default.AddSecretCommandPayload, optional);
            return descriptor;
        }

        /// <summary>Declares a private certificate with an explicit subject and alternative names.</summary>
        /// <param name="name">The single-segment certificate name and ownership key.</param>
        /// <param name="subject">The requested certificate subject.</param>
        /// <param name="subjectAlternativeNames">The requested DNS or IP alternative names.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">The name, subject, or an alternative name is invalid.</exception>
        /// <exception cref="InvalidOperationException">The descriptor belongs to a built model.</exception>
        public ISecretStoreResourceDescriptor IssueCertificate(SecretStoreCertificateName name, string subject,
            IEnumerable<SecretStoreSubjectAlternativeName>? subjectAlternativeNames = null, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.CertificateNameInvalid, nameof(name));
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(subject);
            string[] alternatives = subjectAlternativeNames?.Select(static value =>
            {
                if (value.Value is null)
                {
                    throw new ArgumentException(ErrorMessages.SubjectAlternativeNameInvalid, nameof(subjectAlternativeNames));
                }
                return value.Value;
            }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
            descriptor.AddCommand("secretstore.issue-certificate", name.Value,
                new IssueCertificateCommandPayload(name.Value, subject, alternatives),
                SecretStoreCommandJsonContext.Default.IssueCertificateCommandPayload, optional);
            return descriptor;
        }
    }
}
