using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Serialization.Internal;

namespace Assimalign.Cohesion.Web.Serialization;

/// <summary>
/// Read access to the System.Text.Json contracts of the built-in JSON format, for components that
/// describe the application's payloads rather than serialize them (an OpenAPI document generator, for
/// example).
/// </summary>
/// <remarks>
/// <para>
/// The JSON pair registered by <c>AddJson</c>/<c>AddJsonSerialization</c> serializes through frozen
/// options built over the application's resolver, with the web defaults (camelCase names, numbers
/// readable from strings) and whatever the registration callback configured. A describer has to see
/// exactly those contracts, or its property names and shapes drift from the wire. This seam hands out
/// the <see cref="JsonTypeInfo"/> the writer would use and nothing else: the options stay internal and
/// read-only, and no reader or writer state is exposed.
/// </para>
/// </remarks>
public static class JsonContentSerializationFeatureExtensions
{
    extension(IHttpContentSerializationFeature feature)
    {
        /// <summary>
        /// Resolves the System.Text.Json contract the built-in JSON format serializes
        /// <paramref name="type"/> with, from the writer the registry selects for
        /// <c>application/json</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The writer is the one <see cref="IHttpContentSerializationFeature.GetWriter"/> returns for
        /// <c>application/json</c>. The built-in reader and writer that one <c>AddJson</c> call registers
        /// share their options, so the same contract describes request bodies of the type as well.
        /// </para>
        /// <para>
        /// Returns <see langword="false"/> when <c>application/json</c> resolves to no writer, to a writer
        /// other than the built-in JSON writer, or when the registered resolver has no contract for the
        /// type. The lookup is reflection-free: it asks the registered resolver, exactly as
        /// <see cref="IHttpContentWriter.CanWrite"/> does.
        /// </para>
        /// </remarks>
        /// <param name="type">The declared CLR type to resolve.</param>
        /// <param name="typeInfo">The contract, when one is available; otherwise <see langword="null"/>.</param>
        /// <returns><see langword="true"/> when the built-in JSON writer has a contract for the type.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="feature"/> or <paramref name="type"/> is <see langword="null"/>.</exception>
        public bool TryGetJsonTypeInfo(Type type, [NotNullWhen(true)] out JsonTypeInfo? typeInfo)
        {
            ArgumentNullException.ThrowIfNull(feature);
            ArgumentNullException.ThrowIfNull(type);

            if (feature.GetWriter(HttpMediaType.ApplicationJson) is JsonHttpContentWriter writer)
            {
                return writer.TryGetTypeInfo(type, out typeInfo);
            }

            typeInfo = null;
            return false;
        }
    }
}
