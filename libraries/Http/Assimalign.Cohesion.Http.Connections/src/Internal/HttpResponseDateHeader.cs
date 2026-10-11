using System;
using System.Text;

namespace Assimalign.Cohesion.Http.Connections.Internal;

// One immutable timestamp and its wire fields, shared by all responses originated in this second.
internal sealed class HttpResponseDateHeader
{
    public HttpResponseDateHeader(long utcSecond, string value)
    {
        UtcSecond = utcSecond;
        Value = value;
        Http1Field = Encoding.ASCII.GetBytes("Date: " + value + "\r\n");
        Span<byte> hpack = stackalloc byte[64];
        if (!HPackEncoder.EncodeLiteralHeaderFieldWithoutIndexing(HPackStaticTable.Date, value, Encoding.ASCII, hpack, out int written))
        {
            throw new InvalidOperationException("The cached HTTP date did not fit its HPACK field buffer.");
        }
        Http2Field = hpack[..written].ToArray();
        Http3Field = QPackFieldSectionEncoder.EncodeField("date", value);
    }

    public long UtcSecond { get; }
    public string Value { get; }
    public ReadOnlyMemory<byte> Http1Field { get; }
    public ReadOnlyMemory<byte> Http2Field { get; }
    public ReadOnlyMemory<byte> Http3Field { get; }

    // Use the existing lookup contract to avoid boxing the enumerator of a Date-only head.
    public bool IsOnlyHeader(IHttpHeaderCollection headers) => headers.Count == 1
        && headers.TryGetValue(HttpHeaderKey.Date, out HttpHeaderValue date)
        && date.Count == 1
        && string.Equals(date.Value, Value, StringComparison.Ordinal);
}
