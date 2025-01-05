using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Text.Encoding;

namespace Assimalign.Cohesion.Net.Http.Internal;

internal class HttpValues
{
    internal static ReadOnlySpan<byte> Version1 => new Span<byte>(new byte[] { (byte)'H', (byte)'T', (byte)'T', (byte)'P', (byte)'/', (byte)'1', (byte)'.', (byte)'1' });

    internal class Separators
    {
        internal static ReadOnlySpan<byte> CarriageReturn => new ReadOnlySpan<byte>(new byte[] { (byte)'\r' });
        internal static ReadOnlySpan<byte> LineFeed => new ReadOnlySpan<byte>(new byte[] { (byte)'\n' });
        internal static ReadOnlySpan<byte> Space => new ReadOnlySpan<byte>(new byte[] { (byte)' ' });
        internal static ReadOnlySpan<byte> Colon => new ReadOnlySpan<byte>(new byte[] { (byte)':' });
        internal static ReadOnlySpan<byte> Tab => new ReadOnlySpan<byte>(new byte[] { (byte)'\t' });
        internal static ReadOnlySpan<byte> QuestionMark => new ReadOnlySpan<byte>(new byte[] { (byte)'?' });
        internal static ReadOnlySpan<byte> Percentage => new ReadOnlySpan<byte>(new byte[] { (byte)'%' });        
        internal static ReadOnlySpan<byte> NewLine => new ReadOnlySpan<byte>(new byte[] { (byte)'\r', (byte)'\n' });
    }

    internal class StatusCodes
    {
        // 1xx
        internal static ReadOnlySpan<byte> Continue => UTF8.GetBytes("100 Continue");
        internal static ReadOnlySpan<byte> SwitchProtocols => UTF8.GetBytes("101 Switching Protocols");

        // 2xx
        internal static ReadOnlySpan<byte> Ok => UTF8.GetBytes("200 OK");
        internal static ReadOnlySpan<byte> Created => UTF8.GetBytes("201 Created");
        internal static ReadOnlySpan<byte> Accepted => UTF8.GetBytes("202 Accepted");
        internal static ReadOnlySpan<byte> NonAuthoritativeInformation => UTF8.GetBytes("203 Non-Authoritative Information");
        internal static ReadOnlySpan<byte> NoContent => UTF8.GetBytes("204 No Content");
        internal static ReadOnlySpan<byte> ResetContent => UTF8.GetBytes("205 Reset Content");
        internal static ReadOnlySpan<byte> PartialContent => UTF8.GetBytes("206 Partial Content");

        // 3xx
        internal static ReadOnlySpan<byte> MultipleChoices => UTF8.GetBytes("300 Multiple Choices");
        internal static ReadOnlySpan<byte> MovedPermanently => UTF8.GetBytes("301 Moved Permanently");
        internal static ReadOnlySpan<byte> Found => UTF8.GetBytes("302 Found");
        internal static ReadOnlySpan<byte> SeeOther => UTF8.GetBytes("303 See Other");
        internal static ReadOnlySpan<byte> NotModified => UTF8.GetBytes("304 Not Modified");
        internal static ReadOnlySpan<byte> UseProxy => UTF8.GetBytes("305 Use Proxy");
        internal static ReadOnlySpan<byte> TemporaryRedirect => UTF8.GetBytes("307 Temporary Redirect");
        internal static ReadOnlySpan<byte> PermanentRedirect => UTF8.GetBytes("308 Permanent Redirect");

        // 4xx
        internal static ReadOnlySpan<byte> BadRequest => UTF8.GetBytes("400 Bad Request");
        internal static ReadOnlySpan<byte> Unauthorized => UTF8.GetBytes("401 Unauthorized");
        internal static ReadOnlySpan<byte> MethodNotAllowed => UTF8.GetBytes("403 Method Not Allowed");
        internal static ReadOnlySpan<byte> NotFound => UTF8.GetBytes("404 Not Found");
        internal static ReadOnlySpan<byte> NotAcceptable => UTF8.GetBytes("406 Not Acceptable");
    }
}