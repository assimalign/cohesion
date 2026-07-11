using System.IO;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Serializes a <see cref="ProblemDetails"/> to <c>application/problem+json</c> (RFC 9457).
/// </summary>
/// <remarks>
/// Implementations are AOT- and trimming-safe: they walk the model's members explicitly rather than
/// reflecting over its shape, following the <c>OpenApiJsonWriter</c> precedent. Obtain the built-in
/// writer through <see cref="ProblemDetailsWriter.Default"/>.
/// </remarks>
public interface IProblemDetailsWriter
{
    /// <summary>
    /// Writes <paramref name="problem"/> as problem+json into <paramref name="stream"/>.
    /// </summary>
    /// <param name="problem">The problem details to serialize.</param>
    /// <param name="stream">The destination stream.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="problem"/> or <paramref name="stream"/> is <see langword="null"/>.</exception>
    void Write(ProblemDetails problem, Stream stream);

    /// <summary>
    /// Serializes <paramref name="problem"/> to a freshly allocated UTF-8 problem+json byte array.
    /// </summary>
    /// <param name="problem">The problem details to serialize.</param>
    /// <returns>The UTF-8 encoded problem+json payload.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="problem"/> is <see langword="null"/>.</exception>
    byte[] WriteToUtf8Bytes(ProblemDetails problem);

    /// <summary>
    /// Serializes <paramref name="problem"/> to a problem+json string.
    /// </summary>
    /// <param name="problem">The problem details to serialize.</param>
    /// <returns>The problem+json representation.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="problem"/> is <see langword="null"/>.</exception>
    string WriteToString(ProblemDetails problem);
}
