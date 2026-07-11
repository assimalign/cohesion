using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Serialization.Internal;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// Builder-time registration of the content-serialization registry. Registration is
/// dependency-free per the area's composition model: the registry is attached to the application
/// as a typed feature (<see cref="IHttpContentSerializationFeature"/>) and holds plain
/// reader/writer values — no service container, no configuration binding, no request-time
/// service location.
/// </summary>
public static class SerializationWebApplicationExtensions
{
    extension(IWebApplicationBuilder builder)
    {
        /// <summary>
        /// Adds an empty content-serialization registry to the application and returns the
        /// builder used to register format readers and writers.
        /// </summary>
        /// <returns>A <see cref="ContentSerializationBuilder"/> for registering formats.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// Call once per application: each call composes a fresh registry whose feature replaces
        /// the previously-registered one (features are name-keyed). Register additional formats
        /// by chaining on the returned builder.
        /// </remarks>
        public ContentSerializationBuilder AddContentSerialization()
        {
            ArgumentNullException.ThrowIfNull(builder);

            HttpContentSerializationFeature feature = new();
            builder.AddFeature(feature);

            return new ContentSerializationBuilder(feature);
        }

        /// <summary>
        /// Adds the content-serialization registry with the built-in JSON reader/writer pair
        /// registered over <paramref name="resolver"/> — typically an application's
        /// source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>
        /// (e.g. <c>AddJsonSerialization(AppJsonContext.Default)</c>), which keeps typed body IO
        /// reflection-free under NativeAOT.
        /// </summary>
        /// <param name="resolver">The contract-metadata resolver for every type the application serializes.</param>
        /// <param name="configure">An optional callback to adjust the JSON options (which default to <see cref="JsonSerializerDefaults.Web"/>).</param>
        /// <returns>A <see cref="ContentSerializationBuilder"/> for registering further formats.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="resolver"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// Call once per application (see <see cref="AddContentSerialization"/>). Equivalent to
        /// <c>AddContentSerialization().AddJson(resolver, configure)</c>.
        /// </remarks>
        public ContentSerializationBuilder AddJsonSerialization(IJsonTypeInfoResolver resolver, Action<JsonSerializerOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(resolver);

            return builder.AddContentSerialization().AddJson(resolver, configure);
        }
    }
}
