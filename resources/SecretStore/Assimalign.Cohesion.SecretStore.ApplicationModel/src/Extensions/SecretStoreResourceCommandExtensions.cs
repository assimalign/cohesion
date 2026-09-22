using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Declares secret sources and certificate requests without carrying secret material.</summary>
public static partial class SecretStoreResourceCommandExtensions
{
    extension(ISecretStoreResourceDescriptor descriptor)
    {
        /// <summary>Declares a secret whose value is resolved from a named source during delivery.</summary>
        /// <param name="path">The secret path and ownership key.</param>
        /// <param name="source">A parameter:name or resource:key reference; literal values are forbidden.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">The path or source is invalid or contains a literal.</exception>
        /// <exception cref="InvalidOperationException">The descriptor belongs to a built model.</exception>
        public ISecretStoreResourceDescriptor AddSecret(string path, string source, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentException.ThrowIfNullOrWhiteSpace(source);
            int separator = source.IndexOf(':');
            if (separator <= 0 || separator == source.Length - 1 ||
                string.IsNullOrWhiteSpace(source[(separator + 1)..]) ||
                source[..separator].Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            {
                throw new ArgumentException("A secret source must use parameter:<name> or <resource>:<key>.", nameof(source));
            }
            if (source.StartsWith("literal:", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("secretstore.add-secret forbids literal sources; use parameter:<name> or <resource>:<key> so declarations contain no secret material.", nameof(source));
            }
            descriptor.AddCommand("secretstore.add-secret", path, new AddSecretCommandPayload(path, source),
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
        public ISecretStoreResourceDescriptor IssueCertificate(string name, string subject,
            IEnumerable<string>? subjectAlternativeNames = null, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(subject);
            if (name.Contains('/'))
            {
                throw new ArgumentException("Certificate names must not contain '/'.", nameof(name));
            }
            string[] alternatives = subjectAlternativeNames?.Select(static value =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(subjectAlternativeNames));
                return value;
            }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
            descriptor.AddCommand("secretstore.issue-certificate", name,
                new IssueCertificateCommandPayload(name, subject, alternatives),
                SecretStoreCommandJsonContext.Default.IssueCertificateCommandPayload, optional);
            return descriptor;
        }
    }
}
