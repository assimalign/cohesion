using System;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.Web.Hosting.Management.Internal;

namespace Assimalign.Cohesion.Web.Hosting.Management;

/// <summary>References an immutable certificate bundle version without embedding private key material.</summary>
public sealed record ResourceCertificateReference
{
    /// <summary>Creates a reference to one explicitly named immutable certificate version.</summary>
    /// <param name="provider">The configured provider identity.</param>
    /// <param name="key">The provider's certificate bundle key.</param>
    /// <param name="version">The immutable version.</param>
    /// <exception cref="ArgumentException">An identifier is default.</exception>
    public ResourceCertificateReference(ResourceCertificateProvider provider, ResourceCertificateKey key, ResourceCertificateVersion version)
    {
        if (provider.Value is null || key.Value is null || version.Value is null) { throw new ArgumentException(ErrorMessages.CertificateReferenceInvalid); }
        Provider = provider;
        Key = key;
        Version = version;
    }

    /// <summary>Gets the explicitly configured provider.</summary>
    [JsonConverter(typeof(CertificateProviderJsonConverter))]
    public ResourceCertificateProvider Provider { get; }
    /// <summary>Gets the provider's certificate bundle key.</summary>
    [JsonConverter(typeof(CertificateKeyJsonConverter))]
    public ResourceCertificateKey Key { get; }
    /// <summary>Gets the exact immutable provider version.</summary>
    [JsonConverter(typeof(CertificateVersionJsonConverter))]
    public ResourceCertificateVersion Version { get; }
}
