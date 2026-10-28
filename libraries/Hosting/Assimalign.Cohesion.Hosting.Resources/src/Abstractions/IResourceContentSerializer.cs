using System;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Reads and writes the content a resource instance exchanges with its gateway: the manifest it
/// publishes, action inputs and outputs, state snapshots, and action results.
/// </summary>
/// <remarks>
/// <para>
/// One serializer serves an instance's control plane, its manifest publisher, and the transport that
/// serves the control plane, and the gateway that manages the instance reads and writes the same
/// format. The serializer must therefore know every type the instance exchanges: this library's
/// <see cref="ResourceManifest"/>, <see cref="ResourceStateSnapshot"/> list, and
/// <see cref="ResourceActionResult"/>, and the input, output, and state types of each action.
/// </para>
/// <para>
/// <see cref="JsonResourceContentSerializer"/> is the default. A platform that exchanges another
/// format brings its own implementation. Implementations are called concurrently, and must not
/// discover types by reflection at run time, so they stay trimming- and NativeAOT-compatible.
/// </para>
/// </remarks>
public interface IResourceContentSerializer
{
    /// <summary>
    /// Gets the media type of the content the serializer writes, such as <c>application/json</c>.
    /// Transports label the content they send with it.
    /// </summary>
    string MediaType { get; }

    /// <summary>
    /// Writes a value as content.
    /// </summary>
    /// <typeparam name="T">The type to write the value as.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The content.</returns>
    /// <exception cref="NotSupportedException">The serializer does not know <typeparamref name="T"/>.</exception>
    ReadOnlyMemory<byte> Serialize<T>(T value);

    /// <summary>
    /// Reads content as a value.
    /// </summary>
    /// <typeparam name="T">The type to read the content as.</typeparam>
    /// <param name="content">The content.</param>
    /// <returns>The value; <see langword="null"/> when the content is the format's null value.</returns>
    /// <exception cref="NotSupportedException">The serializer does not know <typeparamref name="T"/>.</exception>
    /// <exception cref="ResourceContentException">The content is empty, or is not a value of <typeparamref name="T"/>.</exception>
    T? Deserialize<T>(ReadOnlyMemory<byte> content);
}
