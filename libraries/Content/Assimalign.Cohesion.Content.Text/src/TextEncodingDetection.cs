using System.Text;

namespace Assimalign.Cohesion.Content.Text;

/// <summary>
/// The result of detecting a text encoding from the leading bytes of content.
/// </summary>
public readonly struct TextEncodingDetection
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TextEncodingDetection"/> structure.
    /// </summary>
    /// <param name="encoding">The detected encoding.</param>
    /// <param name="preambleLength">The number of leading byte-order-mark bytes to skip before decoding.</param>
    /// <param name="detectedFromByteOrderMark"><see langword="true"/> when a byte order mark decided the encoding; <see langword="false"/> when a null-byte-pattern heuristic or the UTF-8 default decided it.</param>
    public TextEncodingDetection(Encoding encoding, int preambleLength, bool detectedFromByteOrderMark)
    {
        Encoding = encoding;
        PreambleLength = preambleLength;
        DetectedFromByteOrderMark = detectedFromByteOrderMark;
    }

    /// <summary>Gets the detected encoding. The instance never emits a byte order mark when writing.</summary>
    public Encoding Encoding { get; }

    /// <summary>Gets the number of leading byte-order-mark bytes to skip before decoding.</summary>
    public int PreambleLength { get; }

    /// <summary>Gets a value indicating whether a byte order mark decided the encoding.</summary>
    public bool DetectedFromByteOrderMark { get; }
}
