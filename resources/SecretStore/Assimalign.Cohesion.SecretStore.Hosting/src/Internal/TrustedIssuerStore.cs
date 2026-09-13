using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.SecretStore.Hosting;

internal sealed class TrustedIssuerStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, TrustedIssuer> _issuers = new(StringComparer.Ordinal);
    private string? _ambientIssuer;
    private bool _initialized;

    internal TrustedIssuerStore(string dataPath, IDataProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentNullException.ThrowIfNull(protector);

        _path = Path.Combine(Path.GetFullPath(dataPath), "trust", "trusted-issuers.protected");
        _protector = protector;
    }

    internal async Task InitializeAsync(
        ResourceContext? context,
        bool requireAuthentication,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            byte[]? content = await ProtectedFileStore.ReadAsync(
                    _path,
                    _protector,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (content is not null)
                {
                    Load(content);
                }
            }
            finally
            {
                if (content is not null)
                {
                    CryptographicOperations.ZeroMemory(content);
                }
            }

            bool changed = false;
            if (requireAuthentication)
            {
                if (context is null ||
                    string.IsNullOrWhiteSpace(context.ApplicationName) ||
                    string.IsNullOrWhiteSpace(context.ResourceName) ||
                    context.BootstrapCredential.IsEmpty ||
                    context.ApplicationTrustKey.IsEmpty)
                {
                    throw new InvalidOperationException(
                        "A gateway-managed SecretStore requires application and resource names, " +
                        "a bootstrap credential, and a public application trust key.");
                }

                TrustedIssuer applicationIssuer;
                try
                {
                    using JsonDocument keyDocument = JsonDocument.Parse(context.ApplicationTrustKey);
                    applicationIssuer = new TrustedIssuer(
                        context.ApplicationName,
                        context.ApplicationName,
                        keyDocument.RootElement);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException(
                        "The ambient application trust key is not a valid public JWK document.",
                        exception);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        $"The ambient application trust key is invalid: {exception.Message}",
                        exception);
                }

                lock (_sync)
                {
                    changed = !_issuers.TryGetValue(
                            applicationIssuer.Issuer,
                            out TrustedIssuer? existing) ||
                        !string.Equals(
                            existing.PublicKey.GetRawText(),
                            applicationIssuer.PublicKey.GetRawText(),
                            StringComparison.Ordinal);
                    _issuers[applicationIssuer.Issuer] = applicationIssuer;
                    _ambientIssuer = applicationIssuer.Issuer;
                }

                string compactToken = Encoding.ASCII.GetString(context.BootstrapCredential.Span);
                BootstrapTokenValidation bootstrap = new BootstrapTokenVerifier(this).Validate(
                    compactToken,
                    context.ResourceName,
                    DateTimeOffset.UtcNow);
                if (bootstrap.Status is not BootstrapTokenValidationStatus.Authorized ||
                    !string.Equals(
                        bootstrap.Issuer,
                        context.ApplicationName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The ambient SecretStore bootstrap credential does not match the " +
                        "application trust key and resource identity.");
                }
            }

            if (changed || (content is null && requireAuthentication))
            {
                await PersistAsync(Snapshot(), cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal TrustedIssuer? Find(string issuer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);

        lock (_sync)
        {
            return _issuers.TryGetValue(issuer, out TrustedIssuer? value)
                ? value
                : null;
        }
    }

    internal async Task<TrustedIssuerUpsertResult> UpsertAsync(
        string owner,
        string issuer,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);

        TrustedIssuer candidate;
        try
        {
            using JsonDocument document = JsonDocument.Parse(publicKey);
            JsonElement root = document.RootElement;
            candidate = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("trustKey", out JsonElement key)
                ? new TrustedIssuer(owner, issuer, key, TrustedIssuer.ReadAllowedCommandKinds(root))
                : new TrustedIssuer(owner, issuer, root);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A trusted issuer payload is not valid JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"A trusted issuer payload is invalid: {exception.Message}",
                exception);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            TrustedIssuer[] pendingSnapshot;
            lock (_sync)
            {
                if (string.Equals(issuer, _ambientIssuer, StringComparison.Ordinal))
                {
                    return TrustedIssuerUpsertResult.AmbientIssuerConflict;
                }

                if (_issuers.TryGetValue(issuer, out TrustedIssuer? existing) &&
                    !string.Equals(existing.Owner, owner, StringComparison.Ordinal))
                {
                    return TrustedIssuerUpsertResult.OwnerConflict;
                }

                var pending = new Dictionary<string, TrustedIssuer>(_issuers, StringComparer.Ordinal)
                {
                    [issuer] = candidate,
                };
                pendingSnapshot = pending.Values
                    .OrderBy(static value => value.Issuer, StringComparer.Ordinal)
                    .ToArray();
            }

            await PersistAsync(pendingSnapshot, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _issuers[issuer] = pendingSnapshot.Single(value =>
                    string.Equals(value.Issuer, issuer, StringComparison.Ordinal));
            }

            return TrustedIssuerUpsertResult.Stored;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal ReadOnlyMemory<byte> Export()
    {
        lock (_sync)
        {
            EnsureInitialized();
            return Serialize(_issuers.Values
                .OrderBy(static issuer => issuer.Issuer, StringComparer.Ordinal)
                .ToArray());
        }
    }

    private void Load(ReadOnlyMemory<byte> content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind is not JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("issuers", out JsonElement issuers) ||
                issuers.ValueKind is not JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "The protected trusted-issuer document must contain an 'issuers' array.");
            }

            var loaded = new Dictionary<string, TrustedIssuer>(StringComparer.Ordinal);
            foreach (JsonElement item in issuers.EnumerateArray())
            {
                if (item.ValueKind is not JsonValueKind.Object ||
                    !item.TryGetProperty("owner", out JsonElement ownerProperty) ||
                    ownerProperty.ValueKind is not JsonValueKind.String ||
                    !item.TryGetProperty("issuer", out JsonElement issuerProperty) ||
                    issuerProperty.ValueKind is not JsonValueKind.String ||
                    !item.TryGetProperty("trustKey", out JsonElement keyProperty))
                {
                    throw new InvalidDataException(
                        "Every protected trusted issuer requires owner, issuer, and trustKey members.");
                }

                TrustedIssuer issuer = new(
                    ownerProperty.GetString()!,
                    issuerProperty.GetString()!,
                    keyProperty,
                    TrustedIssuer.ReadAllowedCommandKinds(item));
                if (!loaded.TryAdd(issuer.Issuer, issuer))
                {
                    throw new InvalidDataException(
                        $"Trusted issuer '{issuer.Issuer}' occurs more than once.");
                }
            }

            lock (_sync)
            {
                _issuers.Clear();
                foreach ((string name, TrustedIssuer issuer) in loaded)
                {
                    _issuers.Add(name, issuer);
                }
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The protected trusted-issuer document is not valid JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The protected trusted-issuer document is invalid: {exception.Message}",
                exception);
        }
    }

    private TrustedIssuer[] Snapshot()
    {
        lock (_sync)
        {
            return _issuers.Values
                .OrderBy(static issuer => issuer.Issuer, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private async Task PersistAsync(
        IReadOnlyList<TrustedIssuer> snapshot,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> payload = Serialize(snapshot);
        await ProtectedFileStore.WriteAsync(
                _path,
                _protector,
                payload,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> Serialize(IReadOnlyList<TrustedIssuer> snapshot)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("issuers");
            writer.WriteStartArray();
            for (int index = 0; index < snapshot.Count; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("owner", snapshot[index].Owner);
                writer.WriteString("issuer", snapshot[index].Issuer);
                writer.WritePropertyName("trustKey");
                snapshot[index].PublicKey.WriteTo(writer);
                if (snapshot[index].AllowedCommandKinds.Count > 0)
                {
                    writer.WriteStartArray("allowedCommandKinds");
                    foreach (string kind in snapshot[index].AllowedCommandKinds) { writer.WriteStringValue(kind); }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The trusted issuer store has not been initialized.");
        }
    }
}

internal enum TrustedIssuerUpsertResult
{
    Stored,
    OwnerConflict,
    AmbientIssuerConflict,
}
