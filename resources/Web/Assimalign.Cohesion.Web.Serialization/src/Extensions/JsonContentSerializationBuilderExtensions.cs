using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.Web.Serialization.Internal;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// Registers the built-in JSON format on a <see cref="ContentSerializationBuilder"/>. Grafting
/// format verbs onto the builder is the extension seam format packages follow — the JSON pair
/// ships in-package because System.Text.Json is part of the shared framework.
/// </summary>
public static class JsonContentSerializationBuilderExtensions
{
    extension(ContentSerializationBuilder builder)
    {
        /// <summary>
        /// Registers the JSON reader/writer pair for <c>application/json</c> and <c>text/json</c>
        /// over <paramref name="resolver"/>. Serialization uses the
        /// <see cref="JsonTypeInfo"/>-based System.Text.Json entry points exclusively, so any type
        /// outside the resolver's contracts faults instead of falling back to reflection.
        /// </summary>
        /// <param name="resolver">The contract-metadata resolver for every type the application serializes.</param>
        /// <param name="configure">An optional callback to adjust the JSON options (which default to <see cref="JsonSerializerDefaults.Web"/>).</param>
        /// <returns>The builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="resolver"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="configure"/> cleared the options' type-info resolver.</exception>
        public ContentSerializationBuilder AddJson(IJsonTypeInfoResolver resolver, Action<JsonSerializerOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(resolver);

            JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
            {
                TypeInfoResolver = resolver
            };

            configure?.Invoke(options);

            // Freeze the options so the shared reader/writer pair is safely reusable across
            // concurrent exchanges. MakeReadOnly() (parameterless) never populates a reflection
            // resolver; it throws when configure removed the resolver, which keeps the
            // reflection-free guarantee honest.
            options.MakeReadOnly();

            return builder
                .AddReader(new JsonHttpContentReader(options))
                .AddWriter(new JsonHttpContentWriter(options));
        }
    }
}
