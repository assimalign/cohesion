using System;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2.HPack;

internal sealed class HPackDecodedHeaders
{
    public HPackDecodedHeaders()
    {
        Headers = new HttpHeaderCollection();
    }

    public string? Authority { get; private set; }

    public string? Method { get; private set; }

    public string? Path { get; private set; }

    public string? Scheme { get; private set; }

    public HttpHeaderCollection Headers { get; }

    public void Add(string name, string value)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new HPackDecodingException("The HPACK header name cannot be empty.");
        }

        if (name[0] == ':')
        {
            switch (name)
            {
                case ":authority":
                    Authority = value;
                    return;
                case ":method":
                    Method = value;
                    return;
                case ":path":
                    Path = value;
                    return;
                case ":scheme":
                    Scheme = value;
                    return;
                default:
                    return;
            }
        }

        HttpHeaderKey key = new(name);

        if (Headers.TryGetValue(key, out HttpHeaderValue existingValue))
        {
            Headers[key] = string.Equals(name, "cookie", StringComparison.OrdinalIgnoreCase)
                ? string.Concat(existingValue.Value, "; ", value)
                : HttpHeaderValue.Concat(existingValue, value);
        }
        else
        {
            Headers[key] = value;
        }
    }
}
