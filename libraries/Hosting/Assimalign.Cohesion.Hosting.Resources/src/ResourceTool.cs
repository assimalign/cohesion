using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>
/// Declares an operator tool a resource offers on one of its endpoints, such as a SQL session for a
/// management portal's query editor.
/// </summary>
/// <remarks>
/// A tool is metadata: it tells a portal what it can open and on which endpoint. The gateway brokers the
/// connection. A tool that reaches the resource's data requires a data-plane credential; control-plane
/// permission alone never grants it.
/// </remarks>
[DebuggerDisplay("{Name,nq} ({Protocol}) on {Endpoint,nq}")]
public sealed class ResourceTool
{
    private ResourceTool(ResourceToolName name, string endpoint, ResourceToolProtocol protocol, string? subprotocol, bool requiresDataAccess)
    {
        Name = name;
        Endpoint = endpoint;
        Protocol = protocol;
        Subprotocol = subprotocol;
        RequiresDataAccess = requiresDataAccess;
    }

    /// <summary>Gets the tool name.</summary>
    public ResourceToolName Name { get; }

    /// <summary>Gets the name of the resource endpoint that serves the tool.</summary>
    public string Endpoint { get; }

    /// <summary>Gets the transport the tool uses.</summary>
    public ResourceToolProtocol Protocol { get; }

    /// <summary>Gets the WebSocket subprotocol the tool speaks, if any.</summary>
    public string? Subprotocol { get; }

    /// <summary>Gets whether opening the tool requires a data-plane credential.</summary>
    public bool RequiresDataAccess { get; }

    /// <summary>Creates a tool declaration.</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="endpoint">The resource endpoint that serves the tool.</param>
    /// <param name="protocol">The transport the tool uses.</param>
    /// <param name="subprotocol">The WebSocket subprotocol, an HTTP token, or null.</param>
    /// <param name="requiresDataAccess">Whether the tool reaches the resource's data.</param>
    /// <returns>The tool declaration.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is the default value, <paramref name="endpoint"/> is blank, or
    /// <paramref name="subprotocol"/> is not an HTTP token or is set for a protocol other than WebSocket.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocol"/> is undefined.</exception>
    public static ResourceTool Create(
        ResourceToolName name,
        string endpoint,
        ResourceToolProtocol protocol,
        string? subprotocol = null,
        bool requiresDataAccess = true)
    {
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.ResourceToolNameInvalid, nameof(name));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (!Enum.IsDefined(protocol))
        {
            throw new ArgumentOutOfRangeException(nameof(protocol));
        }

        if (subprotocol is not null && (protocol != ResourceToolProtocol.WebSocket || !IsToken(subprotocol)))
        {
            throw new ArgumentException(ErrorMessages.ResourceToolSubprotocolInvalid, nameof(subprotocol));
        }

        return new ResourceTool(name, endpoint, protocol, subprotocol, requiresDataAccess);
    }

    // RFC 9110 token: the grammar of a WebSocket subprotocol name (RFC 6455 section 4.1).
    private static bool IsToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && "!#$%&'*+-.^_`|~".IndexOf(character) < 0)
            {
                return false;
            }
        }

        return true;
    }
}
