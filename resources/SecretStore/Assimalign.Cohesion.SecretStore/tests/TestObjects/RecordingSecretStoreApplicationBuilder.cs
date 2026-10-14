using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.SecretStore.Tests.TestObjects;

internal sealed class RecordingSecretStoreApplicationBuilder : ISecretStoreApplicationBuilder
{
    private readonly List<(string Path, byte[] Value)> _secrets = [];

    internal IReadOnlyList<(string Path, byte[] Value)> Secrets => _secrets;

    internal CertificateAuthorityOptions? CertificateAuthority { get; private set; }

    public ISecretStoreApplicationBuilder AddSecret(SecretStoreSeedPath path, ReadOnlyMemory<byte> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path.Value, nameof(path));
        _secrets.Add((path.Value, value.ToArray()));
        return this;
    }

    public ISecretStoreApplicationBuilder AddCertificateAuthority(
        Action<CertificateAuthorityOptions>? configure = null)
    {
        CertificateAuthorityOptions options = new();
        configure?.Invoke(options);
        CertificateAuthority = options;
        return this;
    }

    public ISecretStoreApplication Build()
    {
        throw new NotSupportedException("The recording builder does not materialize an application.");
    }
}
