using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.Hosting.Resources.Internal;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Declares one configuration section that a resource's deployment record may set and, when its
/// <see cref="Mutability"/> allows, the management API may change after deployment.
/// </summary>
/// <remarks>
/// <para>
/// A section binds through the feature's own source-generated <see cref="JsonTypeInfo{T}"/>, which also
/// produces the section's JSON schema, so the feature keeps one description of its options. Register a
/// section on the control plane before the host is built; it then appears in the runtime surface that the
/// SDK's describe step writes beside <c>resource.json</c>.
/// </para>
/// <para>
/// A section holds no desired state. Each time a deployment record is applied, the effective value is the
/// section's <em>baseline</em> (the value the resource configured itself with) with the record's
/// deployment, claim and override layers merged over it as RFC 7396 merge patches, in that order.
/// </para>
/// </remarks>
[DebuggerDisplay("{Path,nq} ({Mutability})")]
[DebuggerTypeProxy(typeof(DebugView))]
public sealed class ResourceConfigurationSection
{
    private readonly byte[] _baseline;
    private readonly Func<JsonElement, string?> _normalize;
    private readonly Func<JsonElement, string?> _apply;

    private ResourceConfigurationSection(
        ResourceConfigurationPath path,
        ResourceConfigurationMutability mutability,
        ResourceConfigurationScope scope,
        bool claimable,
        JsonElement schema,
        byte[] baseline,
        Func<JsonElement, string?> normalize,
        Func<JsonElement, string?> apply)
    {
        Path = path;
        Mutability = mutability;
        Scope = scope;
        Claimable = claimable;
        Schema = schema;
        _baseline = baseline;
        _normalize = normalize;
        _apply = apply;
    }

    /// <summary>Gets the configuration path the deployment record uses for this section.</summary>
    public ResourceConfigurationPath Path { get; }

    /// <summary>Gets when the section may change after deployment.</summary>
    public ResourceConfigurationMutability Mutability { get; }

    /// <summary>Gets what applying the section changes, which decides which instances report it.</summary>
    public ResourceConfigurationScope Scope { get; }

    /// <summary>Gets whether other applications may contribute keyed entries to this section.</summary>
    /// <remarks>A claimable section is a JSON object whose members are entries owned by the application that contributed them.</remarks>
    public bool Claimable { get; }

    /// <summary>Gets the JSON schema generated from the section's options type, including titles and descriptions.</summary>
    public JsonElement Schema { get; }

    /// <summary>Creates a section over a feature's own options type.</summary>
    /// <typeparam name="T">The feature's options type.</typeparam>
    /// <param name="path">The configuration path, such as <c>Web:Cors</c>.</param>
    /// <param name="typeInfo">The source-generated metadata that binds the section and produces its schema.</param>
    /// <param name="mutability">When the section may change after deployment.</param>
    /// <param name="baseline">The value the resource configured itself with, from its appsettings and code.</param>
    /// <param name="apply">
    /// Validates a value and, when it is valid, replaces the feature's value atomically. Returns
    /// <see langword="null"/> when the value is in force, or a short error description when the feature
    /// refused it and kept its previous value. The description is reported on the resource's authenticated
    /// control plane, so it must not contain secrets.
    /// </param>
    /// <param name="scope">What applying the section changes.</param>
    /// <param name="claimable">Whether other applications may contribute keyed entries.</param>
    /// <returns>The section.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is the default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/>, <paramref name="baseline"/> or <paramref name="apply"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mutability"/> or <paramref name="scope"/> is undefined.</exception>
    /// <exception cref="InvalidOperationException">A claimable section's schema does not describe a JSON object.</exception>
    public static ResourceConfigurationSection Create<T>(
        ResourceConfigurationPath path,
        JsonTypeInfo<T> typeInfo,
        ResourceConfigurationMutability mutability,
        T baseline,
        Func<T, string?> apply,
        ResourceConfigurationScope scope = ResourceConfigurationScope.Process,
        bool claimable = false)
    {
        if (path.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceConfigurationPathInvalid, nameof(path));
        }

        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(apply);
        if (!Enum.IsDefined(mutability))
        {
            throw new ArgumentOutOfRangeException(nameof(mutability));
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        JsonElement schema = ResourceSurfaceSchema.Create(typeInfo);
        if (claimable && !ResourceSurfaceSchema.DescribesObject(schema))
        {
            throw new InvalidOperationException(ErrorMessages.ResourceConfigurationClaimableNotObject);
        }

        byte[] baselineDocument = JsonSerializer.SerializeToUtf8Bytes(baseline, typeInfo);
        return new ResourceConfigurationSection(
            path,
            mutability,
            scope,
            claimable,
            schema,
            baselineDocument,
            value => value.Deserialize(typeInfo) is T candidate
                ? CanonicalJson.Write(JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(candidate, typeInfo)))
                : null,
            value =>
            {
                T? candidate;
                try
                {
                    candidate = value.Deserialize(typeInfo);
                }
                catch (JsonException exception)
                {
                    return ResourceConfigurationErrors.InvalidValue(exception);
                }

                return candidate is null ? ResourceConfigurationErrors.NullValue : apply(candidate);
            });
    }

    /// <summary>Returns a fresh, mutable copy of the baseline the record's layers merge over.</summary>
    internal JsonNode CreateBaseline() => JsonNode.Parse(_baseline) ?? new JsonObject();

    /// <summary>
    /// Binds a value and writes it back through the section's own metadata, so values that bind alike
    /// (an absent member and its default, members in another order) compare equal.
    /// </summary>
    /// <param name="value">The effective value.</param>
    /// <param name="normalized">The normalized text, when the value binds.</param>
    /// <param name="error">Why the value does not bind, when it does not.</param>
    /// <returns>Whether the value binds.</returns>
    internal bool TryNormalize(JsonElement value, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error)
    {
        try
        {
            normalized = _normalize(value);
            error = normalized is null ? ResourceConfigurationErrors.NullValue : null;
        }
        catch (JsonException exception)
        {
            normalized = null;
            error = ResourceConfigurationErrors.InvalidValue(exception);
        }

        return normalized is not null;
    }

    /// <summary>Binds and applies an effective value; returns null when it is in force, else the error.</summary>
    internal string? Apply(JsonElement value) => _apply(value);

    private sealed class DebugView
    {
        private readonly ResourceConfigurationSection _target;

        public DebugView(ResourceConfigurationSection value)
        {
            _target = value;
        }

        public ResourceConfigurationPath Path => _target.Path;

        public ResourceConfigurationMutability Mutability => _target.Mutability;

        public ResourceConfigurationScope Scope => _target.Scope;

        public bool Claimable => _target.Claimable;
    }
}
