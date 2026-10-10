using System;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// The factory behind <c>builder.Services.AddJsonSerialization(...)</c>. Applications call that verb, not
/// this type.
/// </summary>
/// <remarks>
/// <para>
/// The verb is a component integration (<c>Properties/ComponentIntegrations.cs</c>): every compilation
/// that references both this package and <c>Assimalign.Cohesion.DependencyInjection</c>, as every Web
/// application does through <c>Web.Hosting</c>, receives <c>AddJsonSerialization(resolver, configure)</c>
/// on <c>IServiceProviderBuilder</c>. The verb registers the registry <see cref="CreateJsonFeature"/>
/// returns as an <see cref="IHttpFeature"/> singleton (owner decision 34, #1380). It is shorthand for
/// <c>builder.Services.AddContentSerialization(serialization => serialization.AddJson(resolver, configure))</c>,
/// the builder-template verb over <see cref="ContentSerializationBuilder"/>.
/// </para>
/// <para>
/// The type is public only because the generated verb, compiled into the application, calls it. It is
/// the static-factory shape because the verb takes the resolver as an argument, which the builder
/// template's single configure callback cannot carry.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SerializationComponents
{
    /// <summary>
    /// Creates a content-serialization registry with the built-in JSON reader/writer pair registered over
    /// <paramref name="resolver"/>, typically an application's source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>
    /// (<c>AddJsonSerialization(AppJsonContext.Default)</c>), which keeps typed body IO reflection-free
    /// under NativeAOT.
    /// </summary>
    /// <remarks>
    /// Registering again replaces the earlier registry: an exchange carries one.
    /// </remarks>
    /// <param name="resolver">The contract-metadata resolver for every type the application serializes.</param>
    /// <param name="configure">An optional callback to adjust the JSON options (which default to <see cref="JsonSerializerDefaults.Web"/>).</param>
    /// <returns>The registry, registered by the verb as an <see cref="IHttpFeature"/> singleton.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="configure"/> cleared the options' type-info resolver.</exception>
    public static IHttpFeature CreateJsonFeature(IJsonTypeInfoResolver resolver, Action<JsonSerializerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        return new ContentSerializationBuilder()
            .AddJson(resolver, configure)
            .Build();
    }
}
