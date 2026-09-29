using System;
using System.Buffers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

// Deviates from the repo interface-first rule per the owner-approved BYO design (2026-09-25): the
// design names this concrete resolver as the package's public type so an application can register
// it in builder.Providers.CommandInputs itself; the contract a gateway depends on is
// IResourceCommandInputResolver.

/// <summary>
/// Resolves the declared source of a <c>secretstore.add-secret</c> command into the value the
/// SecretStore stores, at delivery time.
/// </summary>
/// <remarks>
/// <para>
/// A declared <c>secretstore.add-secret</c> payload is portable desired state and carries no secret
/// material: <c>{"path":"&lt;path&gt;","source":"&lt;source&gt;"}</c>, where the source is
/// <c>parameter:&lt;name&gt;</c> or <c>&lt;resource&gt;:&lt;key&gt;</c>. The resolver resolves the
/// source through the gateway's <see cref="IResourceSourceResolver"/> as a
/// <see cref="ResourceMountKind.Secret"/> value and returns the delivered payload
/// <c>{"path":"&lt;command key&gt;","source":"&lt;source&gt;","resolvedValue":"&lt;base64&gt;"}</c>.
/// The resolved bytes exist only in the delivery envelope; the model and the gateway's ledger keep
/// the source-only declaration.
/// </para>
/// <para>
/// A refusal is an <see cref="InvalidOperationException"/> whose message is the refusal detail the
/// gateway records on the command: a blank or missing source, a <c>literal:</c> source (literal
/// values would put secret material in the declaration), a source of any other shape, an unbound
/// parameter, or a store source that does not resolve.
/// </para>
/// <para>
/// <c>builder.UseSecretStore(store)</c> registers one instance in
/// <see cref="ApplicationProviders.CommandInputs"/>. The resolver holds no state.
/// </para>
/// </remarks>
public sealed class SecretStoreAddSecretInputResolver : IResourceCommandInputResolver
{
    private const string parameterPrefix = "parameter:";
    private const string literalPrefix = "literal:";

    /// <summary>
    /// Gets <c>secretstore.add-secret</c>.
    /// </summary>
    public string CommandKind => SecretStoreProtocol.AddSecretCommandKind;

    /// <summary>
    /// Produces the delivered <c>secretstore.add-secret</c> payload for one declared command.
    /// </summary>
    /// <param name="declared">
    /// The declared command. Its <see cref="ResourceCommandInput.Payload"/> is the
    /// <c>{"path":...,"source":...}</c> declaration, its <see cref="ResourceCommandInput.Key"/> the
    /// secret path, and its <see cref="ResourceCommandInput.Owner"/> the declaring application.
    /// </param>
    /// <param name="sources">Resolves the declared source expression.</param>
    /// <param name="cancellationToken">Signals that resolution should be abandoned.</param>
    /// <returns>
    /// The UTF-8 JSON payload <c>{"path":...,"source":...,"resolvedValue":...}</c>, with the
    /// resolved bytes base64-encoded.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="declared"/> or <paramref name="sources"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="declared"/> is not a <c>secretstore.add-secret</c> command.
    /// </exception>
    /// <exception cref="JsonException">The declared payload is not JSON.</exception>
    /// <exception cref="InvalidOperationException">
    /// The command is refused: the source is missing or blank, is a <c>literal:</c> source or not of
    /// the form <c>parameter:&lt;name&gt;</c> or <c>&lt;resource&gt;:&lt;key&gt;</c>, names an unbound
    /// parameter, or does not resolve.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is cancelled.</exception>
    public async ValueTask<ReadOnlyMemory<byte>> ResolveAsync(
        ResourceCommandInput declared,
        IResourceSourceResolver sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(sources);

        if (!string.Equals(declared.Kind, SecretStoreProtocol.AddSecretCommandKind, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Command '{declared.Id}' is kind '{declared.Kind}', but this resolver handles " +
                $"'{SecretStoreProtocol.AddSecretCommandKind}' commands only.",
                nameof(declared));
        }

        string source = ReadDeclaredSource(declared.Payload);
        ReadOnlyMemory<byte> content;
        if (source.StartsWith(parameterPrefix, StringComparison.Ordinal))
        {
            string name = source[parameterPrefix.Length..];
            ResourceMountInput input = await sources
                .ResolveAsync(source, ResourceMountKind.Secret, cancellationToken)
                .ConfigureAwait(false);
            if (!input.IsResolved)
            {
                throw new InvalidOperationException(
                    $"{SecretStoreProtocol.AddSecretCommandKind} parameter '{name}' is not bound for " +
                    $"application '{declared.Owner}'.");
            }

            content = input.Content;
        }
        else
        {
            if (!IsResourceSource(source) || source.StartsWith(literalPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{SecretStoreProtocol.AddSecretCommandKind} sources must use parameter:<name> or " +
                    "<resource>:<key>; literal sources are forbidden.");
            }

            ResourceMountInput input = await sources
                .ResolveAsync(source, ResourceMountKind.Secret, cancellationToken)
                .ConfigureAwait(false);
            if (!input.IsResolved)
            {
                throw new InvalidOperationException(
                    $"{SecretStoreProtocol.AddSecretCommandKind} source '{source}' is unresolved: " +
                    $"{input.UnresolvedReason}");
            }

            content = input.Content;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("path", declared.Key);
            writer.WriteString("source", source);
            writer.WriteBase64String("resolvedValue", content.Span);
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static string ReadDeclaredSource(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        string? source = document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("source", out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException(
                $"{SecretStoreProtocol.AddSecretCommandKind} requires a nonblank source.");
        }

        return source;
    }

    // The gateway's '<resource>:<key>' shape: exactly one ':' with a nonblank name before it and a
    // nonblank key after it.
    private static bool IsResourceSource(string source)
    {
        int separator = source.IndexOf(':');
        return separator > 0 &&
            separator != source.Length - 1 &&
            source.IndexOf(':', separator + 1) < 0 &&
            !string.IsNullOrWhiteSpace(source[..separator]) &&
            !string.IsNullOrWhiteSpace(source[(separator + 1)..]);
    }
}
