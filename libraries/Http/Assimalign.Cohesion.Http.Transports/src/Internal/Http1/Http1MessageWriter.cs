using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

internal static class Http1MessageWriter
{
    public static async ValueTask WriteResponseAsync(Stream stream, Http1Context context, CancellationToken cancellationToken)
    {
        byte[] bodyBytes = await ReadBodyAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
        HttpHeaderCollection headers = context.Response.Headers;

        if (!headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            headers[HttpHeaderKey.ContentLength] = bodyBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (!context.KeepAlive)
        {
            headers[HttpHeaderKey.Connection] = "close";
        }

        await WriteAsciiAsync(stream, $"HTTP/1.1 {context.Response.StatusCode}\r\n", cancellationToken).ConfigureAwait(false);

        foreach (System.Collections.Generic.KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            // RFC 6265 §3 — Set-Cookie MUST be emitted as one field line per
            // value; combining cookies into a single comma-separated value is
            // forbidden. Every other header is comma-folded as usual.
            if (header.Key == HttpHeaderKey.SetCookie)
            {
                foreach (string? value in header.Value)
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        await WriteAsciiAsync(stream, $"{header.Key}: {value}\r\n", cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                await WriteAsciiAsync(stream, $"{header.Key}: {header.Value}\r\n", cancellationToken).ConfigureAwait(false);
            }
        }

        await WriteAsciiAsync(stream, "\r\n", cancellationToken).ConfigureAwait(false);

        if (context.Request.Method != HttpMethod.Head && bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadBodyAsync(Stream body, CancellationToken cancellationToken)
    {
        if (body is MemoryStream memoryStream)
        {
            return memoryStream.ToArray();
        }

        if (body.CanSeek)
        {
            long originalPosition = body.Position;
            body.Position = 0;

            using MemoryStream buffer = new();
            await body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            body.Position = originalPosition;

            return buffer.ToArray();
        }

        using MemoryStream copy = new();
        await body.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
        return copy.ToArray();
    }

    private static ValueTask WriteAsciiAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        byte[] buffer = System.Text.Encoding.ASCII.GetBytes(value);
        return new ValueTask(stream.WriteAsync(buffer, 0, buffer.Length, cancellationToken));
    }
}
