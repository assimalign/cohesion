using System;
using System.Text;

namespace Assimalign.Cohesion.Net.Http.Internal;

// Ref: https://httpwg.org/specs/rfc7541.html#rfc.section.2.3.1
// The static table consists of a predefined static list of header fields for HTTP/2
internal static partial class HPackStaticTable
{
    // Values for encoding.
    // Unused values are omitted.
    public const int Authority = 1;
    public const int MethodGet = 2;
    public const int MethodPost = 3;
    public const int PathSlash = 4;
    public const int SchemeHttp = 6;
    public const int SchemeHttps = 7;
    public const int Status200 = 8;
    public const int AcceptCharset = 15;
    public const int AcceptEncoding = 16;
    public const int AcceptLanguage = 17;
    public const int AcceptRanges = 18;
    public const int Accept = 19;
    public const int AccessControlAllowOrigin = 20;
    public const int Age = 21;
    public const int Allow = 22;
    public const int Authorization = 23;
    public const int CacheControl = 24;
    public const int ContentDisposition = 25;
    public const int ContentEncoding = 26;
    public const int ContentLanguage = 27;
    public const int ContentLength = 28;
    public const int ContentLocation = 29;
    public const int ContentRange = 30;
    public const int ContentType = 31;
    public const int Cookie = 32;
    public const int Date = 33;
    public const int ETag = 34;
    public const int Expect = 35;
    public const int Expires = 36;
    public const int From = 37;
    public const int Host = 38;
    public const int IfMatch = 39;
    public const int IfModifiedSince = 40;
    public const int IfNoneMatch = 41;
    public const int IfRange = 42;
    public const int IfUnmodifiedSince = 43;
    public const int LastModified = 44;
    public const int Link = 45;
    public const int Location = 46;
    public const int MaxForwards = 47;
    public const int ProxyAuthenticate = 48;
    public const int ProxyAuthorization = 49;
    public const int Range = 50;
    public const int Referer = 51;
    public const int Refresh = 52;
    public const int RetryAfter = 53;
    public const int Server = 54;
    public const int SetCookie = 55;
    public const int StrictTransportSecurity = 56;
    public const int TransferEncoding = 57;
    public const int UserAgent = 58;
    public const int Vary = 59;
    public const int Via = 60;
    public const int WwwAuthenticate = 61;


    public static int Count => s_staticDecoderTable.Length;

    public static ref readonly HPackHeaderField Get(int index) => ref s_staticDecoderTable[index];

    public static bool TryGetStatusIndex(int status, out int index)
    {
        index = status switch
        {
            200 => 8,
            204 => 9,
            206 => 10,
            304 => 11,
            400 => 12,
            404 => 13,
            500 => 14,
            _ => -1
        };

        return index != -1;
    }

    private static readonly HPackHeaderField[] s_staticDecoderTable = new HPackHeaderField[]
    {
            CreateHeaderField(1, ":authority", ""),
            CreateHeaderField(2, ":method", "GET"),
            CreateHeaderField(3, ":method", "POST"),
            CreateHeaderField(4, ":path", "/"),
            CreateHeaderField(5, ":path", "/index.html"),
            CreateHeaderField(6, ":scheme", "http"),
            CreateHeaderField(7, ":scheme", "https"),
            CreateHeaderField(8, ":status", "200"),
            CreateHeaderField(9, ":status", "204"),
            CreateHeaderField(10, ":status", "206"),
            CreateHeaderField(11, ":status", "304"),
            CreateHeaderField(12, ":status", "400"),
            CreateHeaderField(13, ":status", "404"),
            CreateHeaderField(14, ":status", "500"),
            CreateHeaderField(15, "accept-charset", ""),
            CreateHeaderField(16, "accept-encoding", "gzip, deflate"),
            CreateHeaderField(17, "accept-language", ""),
            CreateHeaderField(18, "accept-ranges", ""),
            CreateHeaderField(19, "accept", ""),
            CreateHeaderField(20, "access-control-allow-origin", ""),
            CreateHeaderField(21, "age", ""),
            CreateHeaderField(22, "allow", ""),
            CreateHeaderField(23, "authorization", ""),
            CreateHeaderField(24, "cache-control", ""),
            CreateHeaderField(25, "content-disposition", ""),
            CreateHeaderField(26, "content-encoding", ""),
            CreateHeaderField(27, "content-language", ""),
            CreateHeaderField(28, "content-length", ""),
            CreateHeaderField(29, "content-location", ""),
            CreateHeaderField(30, "content-range", ""),
            CreateHeaderField(31, "content-type", ""),
            CreateHeaderField(32, "cookie", ""),
            CreateHeaderField(33, "date", ""),
            CreateHeaderField(34, "etag", ""),
            CreateHeaderField(35, "expect", ""),
            CreateHeaderField(36, "expires", ""),
            CreateHeaderField(37, "from", ""),
            CreateHeaderField(38, "host", ""),
            CreateHeaderField(39, "if-match", ""),
            CreateHeaderField(40, "if-modified-since", ""),
            CreateHeaderField(41, "if-none-match", ""),
            CreateHeaderField(42, "if-range", ""),
            CreateHeaderField(43, "if-unmodified-since", ""),
            CreateHeaderField(44, "last-modified", ""),
            CreateHeaderField(45, "link", ""),
            CreateHeaderField(46, "location", ""),
            CreateHeaderField(47, "max-forwards", ""),
            CreateHeaderField(48, "proxy-authenticate", ""),
            CreateHeaderField(49, "proxy-authorization", ""),
            CreateHeaderField(50, "range", ""),
            CreateHeaderField(51, "referer", ""),
            CreateHeaderField(52, "refresh", ""),
            CreateHeaderField(53, "retry-after", ""),
            CreateHeaderField(54, "server", ""),
            CreateHeaderField(55, "set-cookie", ""),
            CreateHeaderField(56, "strict-transports-security", ""),
            CreateHeaderField(57, "transfer-encoding", ""),
            CreateHeaderField(58, "user-agent", ""),
            CreateHeaderField(59, "vary", ""),
            CreateHeaderField(60, "via", ""),
            CreateHeaderField(61, "www-authenticate", "")
    };

    // TODO: The HeaderField constructor will allocate and copy again. We should avoid this.
    // Tackle as part of header table allocation strategy in general (see note in HeaderField constructor).

    private static HPackHeaderField CreateHeaderField(int staticTableIndex, string name, string value) =>
        new HPackHeaderField(
            staticTableIndex,
            Encoding.ASCII.GetBytes(name),
            value.Length != 0 ? Encoding.ASCII.GetBytes(value) : Array.Empty<byte>());
}
