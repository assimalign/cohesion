using System;

using Assimalign.Cohesion.Content.Pdf.Internal;

namespace Assimalign.Cohesion.Content.Pdf;

public sealed partial class PdfStream
{
    private PdfReaderOptions _options = new();
    private Func<IPdfObject, IPdfObject>? _resolve;
    internal void Configure(PdfReaderOptions options, Func<IPdfObject, IPdfObject>? resolve)
    {
        _options = options; _resolve = resolve;
    }
    /// <summary>Decodes structural filters and predictors on demand into bounded memory (§7.4).</summary>
    /// <param name="options">Optional replacement limits; otherwise uses the load-time options.</param>
    /// <returns>The decoded bytes; each call reflects the current dictionary.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is invalid.</exception>
    /// <exception cref="ContentFormatException">Data is invalid, truncated, exceeds a limit, or requires an unsupported filter or encryption.</exception>
    public ReadOnlyMemory<byte> Decode(PdfReaderOptions? options = null)
    {
        var limits = options ?? _options;
        limits.Validate();
        return PdfFilters.Decode(this, limits, _resolve);
    }
}
