using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// The server side of permessage-deflate negotiation (RFC 7692 §5, §7.1): picks the first offer
/// in <c>Sec-WebSocket-Extensions</c> the server can honor, writes the response element, and maps
/// the agreed parameters onto the BCL's <see cref="WebSocketDeflateOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The header is parsed with RFC 6455 §9.1's grammar: comma-separated extensions, each a token
/// with <c>;</c>-separated parameters whose values are tokens or quoted strings. An offer the
/// server cannot honor is declined, never an error (§5.1): an unknown parameter, a parameter
/// given twice, a value where none belongs or a missing or malformed one, and a request for an
/// 8-bit server window, which zlib cannot produce. Declining every offer leaves the socket
/// uncompressed.
/// </para>
/// <para>
/// The mapping, from the server's point of view: <c>server_no_context_takeover</c> (requested by
/// the client or chosen by the server) clears <see cref="WebSocketDeflateOptions.ServerContextTakeover"/>;
/// <c>server_max_window_bits</c> answers with the smaller of the offer and the server's limit;
/// <c>client_no_context_takeover</c> is accepted and echoed, so the inflater can drop its window
/// between messages; and a <c>client_max_window_bits</c> hint from 9 to 15 is echoed and sizes
/// the inflater. A hint of 8, or the parameter without a value, is not echoed (§7.1.2.2 lets the
/// server ignore it): the inflater keeps a 15-bit window, which reads any smaller one.
/// </para>
/// </remarks>
internal static class HttpWebSocketCompression
{
    /// <summary>The registered name of the extension.</summary>
    public const string ExtensionName = "permessage-deflate";

    private const string serverNoContextTakeover = "server_no_context_takeover";
    private const string clientNoContextTakeover = "client_no_context_takeover";
    private const string serverMaxWindowBits = "server_max_window_bits";
    private const string clientMaxWindowBits = "client_max_window_bits";

    // RFC 7692 allows 8 to 15; zlib, and so the BCL's deflater and inflater, supports 9 to 15.
    private const int minimumWindowBits = 9;
    private const int maximumWindowBits = 15;

    /// <summary>
    /// Accepts the first permessage-deflate offer the server can honor.
    /// </summary>
    /// <param name="offers">The request's <c>Sec-WebSocket-Extensions</c> field lines.</param>
    /// <param name="disableServerContextTakeover">Whether the server resets its compressor after every message.</param>
    /// <param name="serverMaxWindowBits">The largest window the server compresses with, 9 to 15.</param>
    /// <param name="options">The agreed parameters, for <see cref="WebSocketCreationOptions.DangerousDeflateOptions"/>.</param>
    /// <param name="response">The <c>Sec-WebSocket-Extensions</c> response value.</param>
    /// <returns><see langword="true"/> when an offer was accepted; <see langword="false"/> when every offer was declined.</returns>
    public static bool TryNegotiate(
        HttpHeaderValue offers,
        bool disableServerContextTakeover,
        int serverMaxWindowBits,
        [NotNullWhen(true)] out WebSocketDeflateOptions? options,
        [NotNullWhen(true)] out string? response)
    {
        foreach (string? line in offers)
        {
            if (line is null)
            {
                continue;
            }

            int position = 0;

            while (position < line.Length)
            {
                if (TryReadExtension(line, ref position, out string name, out List<KeyValuePair<string, string?>> parameters)
                    && string.Equals(name, ExtensionName, StringComparison.OrdinalIgnoreCase)
                    && TryAccept(parameters, disableServerContextTakeover, serverMaxWindowBits, out options, out response))
                {
                    return true;
                }
            }
        }

        options = null;
        response = null;
        return false;
    }

    private static bool TryAccept(
        List<KeyValuePair<string, string?>> parameters,
        bool disableServerContextTakeover,
        int serverLimit,
        [NotNullWhen(true)] out WebSocketDeflateOptions? options,
        [NotNullWhen(true)] out string? response)
    {
        options = null;
        response = null;

        bool serverNoTakeover = false;
        bool clientNoTakeover = false;
        bool hasServerBits = false;
        bool hasClientBits = false;
        int offeredServerBits = maximumWindowBits;
        int? clientBitsHint = null;

        foreach (KeyValuePair<string, string?> parameter in parameters)
        {
            if (IsParameter(parameter.Key, serverNoContextTakeover))
            {
                if (serverNoTakeover || parameter.Value is not null)
                {
                    return false;
                }

                serverNoTakeover = true;
            }
            else if (IsParameter(parameter.Key, clientNoContextTakeover))
            {
                if (clientNoTakeover || parameter.Value is not null)
                {
                    return false;
                }

                clientNoTakeover = true;
            }
            else if (IsParameter(parameter.Key, serverMaxWindowBits))
            {
                if (hasServerBits || !TryParseWindowBits(parameter.Value, out offeredServerBits))
                {
                    return false;
                }

                hasServerBits = true;
            }
            else if (IsParameter(parameter.Key, clientMaxWindowBits))
            {
                if (hasClientBits)
                {
                    return false;
                }

                hasClientBits = true;

                if (parameter.Value is not null)
                {
                    if (!TryParseWindowBits(parameter.Value, out int hint))
                    {
                        return false;
                    }

                    clientBitsHint = hint;
                }
            }
            else
            {
                // §5.1: a parameter not defined for use in an offer.
                return false;
            }
        }

        // §7.1.2.1: answer with the same or a smaller window than the client asked for. A request for
        // 8 bits cannot be met, so the offer is declined.
        int serverBits = Math.Min(offeredServerBits, serverLimit);
        if (serverBits < minimumWindowBits)
        {
            return false;
        }

        bool serverTakeover = !serverNoTakeover && !disableServerContextTakeover;
        bool limitClientWindow = clientBitsHint is int usable && usable >= minimumWindowBits;
        int clientBits = limitClientWindow ? clientBitsHint!.Value : maximumWindowBits;

        StringBuilder builder = new(ExtensionName);

        if (!serverTakeover)
        {
            builder.Append("; ").Append(serverNoContextTakeover);
        }

        if (clientNoTakeover)
        {
            builder.Append("; ").Append(clientNoContextTakeover);
        }

        // Required when the client asked (§7.1.2.1); otherwise only to announce a smaller window.
        if (hasServerBits || serverBits != maximumWindowBits)
        {
            builder.Append("; ").Append(serverMaxWindowBits).Append('=').Append(serverBits.ToString(CultureInfo.InvariantCulture));
        }

        // Never sent unless the client offered the parameter (§7.1.2.2).
        if (limitClientWindow)
        {
            builder.Append("; ").Append(clientMaxWindowBits).Append('=').Append(clientBits.ToString(CultureInfo.InvariantCulture));
        }

        options = new WebSocketDeflateOptions
        {
            ServerContextTakeover = serverTakeover,
            ServerMaxWindowBits = serverBits,
            ClientContextTakeover = !clientNoTakeover,
            ClientMaxWindowBits = clientBits,
        };
        response = builder.ToString();
        return true;
    }

    // RFC 7692 §7.1.2: a decimal integer from 8 to 15 without leading zeros.
    private static bool TryParseWindowBits(string? value, out int bits)
    {
        bits = 0;

        if (value is null || value.Length is 0 or > 2 || value[0] == '0')
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        bits = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        return bits is >= 8 and <= maximumWindowBits;
    }

    private static bool IsParameter(string name, string expected)
    {
        return string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads one extension element starting at <paramref name="position"/> and leaves
    /// <paramref name="position"/> past its terminating comma. A malformed element is skipped and
    /// reported as <see langword="false"/>.
    /// </summary>
    private static bool TryReadExtension(
        string line,
        ref int position,
        out string name,
        out List<KeyValuePair<string, string?>> parameters)
    {
        parameters = new List<KeyValuePair<string, string?>>();

        SkipWhitespace(line, ref position);

        if (!TryReadToken(line, ref position, out name))
        {
            SkipElement(line, ref position);
            return false;
        }

        while (true)
        {
            SkipWhitespace(line, ref position);

            if (position >= line.Length)
            {
                return true;
            }

            if (line[position] == ',')
            {
                position++;
                return true;
            }

            if (line[position] != ';')
            {
                SkipElement(line, ref position);
                return false;
            }

            position++;
            SkipWhitespace(line, ref position);

            if (!TryReadToken(line, ref position, out string parameterName))
            {
                SkipElement(line, ref position);
                return false;
            }

            SkipWhitespace(line, ref position);
            string? parameterValue = null;

            if (position < line.Length && line[position] == '=')
            {
                position++;
                SkipWhitespace(line, ref position);

                bool read = position < line.Length && line[position] == '"'
                    // RFC 6455 §9.1: an unescaped quoted-string value must still be a token.
                    ? TryReadQuotedString(line, ref position, out parameterValue) && HttpWebSocketHandshake.IsToken(parameterValue)
                    : TryReadToken(line, ref position, out parameterValue);

                if (!read)
                {
                    SkipElement(line, ref position);
                    return false;
                }
            }

            parameters.Add(new KeyValuePair<string, string?>(parameterName, parameterValue));
        }
    }

    private static bool TryReadToken(string line, ref int position, out string token)
    {
        int start = position;

        while (position < line.Length && HttpWebSocketHandshake.IsTokenCharacter(line[position]))
        {
            position++;
        }

        token = line[start..position];
        return token.Length > 0;
    }

    private static bool TryReadQuotedString(string line, ref int position, [NotNullWhen(true)] out string? value)
    {
        value = null;
        StringBuilder builder = new();

        // Past the opening quote.
        position++;

        while (position < line.Length)
        {
            char character = line[position++];

            if (character == '"')
            {
                value = builder.ToString();
                return true;
            }

            if (character == '\\')
            {
                if (position >= line.Length)
                {
                    return false;
                }

                character = line[position++];
            }

            builder.Append(character);
        }

        // Unterminated.
        return false;
    }

    private static void SkipWhitespace(string line, ref int position)
    {
        while (position < line.Length && line[position] is ' ' or '\t')
        {
            position++;
        }
    }

    /// <summary>
    /// Moves <paramref name="position"/> past the next comma that is not inside a quoted string, or
    /// to the end of the line.
    /// </summary>
    private static void SkipElement(string line, ref int position)
    {
        bool quoted = false;

        while (position < line.Length)
        {
            char character = line[position++];

            if (quoted)
            {
                if (character == '\\')
                {
                    position++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
            }
            else if (character == '"')
            {
                quoted = true;
            }
            else if (character == ',')
            {
                return;
            }
        }
    }
}
