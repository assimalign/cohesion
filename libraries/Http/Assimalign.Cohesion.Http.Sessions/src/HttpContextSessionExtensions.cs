using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Extension methods that attach an <see cref="IHttpSession"/> to an
/// <see cref="IHttpContext"/> through its <see cref="IHttpContext.Items"/> bag.
/// </summary>
/// <remarks>
/// <para>
/// The Cohesion HTTP protocol core deliberately omits a <c>Session</c> property from
/// <see cref="IHttpContext"/> &#8211; HTTP sessions are an application-layer concept, not
/// part of the wire protocol. Consumers that want session semantics reference the
/// <c>Assimalign.Cohesion.Http.Sessions</c> package and use these extensions to install /
/// fetch the session against the context's <see cref="IHttpContext.Items"/> dictionary.
/// </para>
/// <para>
/// The key is namespace-prefixed to avoid clashes with caller-owned items.
/// </para>
/// </remarks>
public static class HttpContextSessionExtensions
{
    /// <summary>Key used to store the session in <see cref="IHttpContext.Items"/>.</summary>
    public const string SessionItemKey = "Assimalign.Cohesion.Http.Sessions::Session";

    /// <summary>
    /// Returns the session attached to <paramref name="context"/> via
    /// <see cref="SetSession"/>, or <see langword="null"/> when none has been attached.
    /// </summary>
    public static IHttpSession? GetSession(this IHttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(SessionItemKey, out object? value)
            ? value as IHttpSession
            : null;
    }

    /// <summary>
    /// Attaches <paramref name="session"/> to <paramref name="context"/>. Overwrites any
    /// previously attached session.
    /// </summary>
    public static void SetSession(this IHttpContext context, IHttpSession session)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(session);
        context.Items[SessionItemKey] = session;
    }

    /// <summary>
    /// Returns the session attached to <paramref name="context"/>, throwing when none is
    /// present. Use when the caller treats the session as a required dependency.
    /// </summary>
    /// <exception cref="InvalidOperationException">No session has been attached.</exception>
    public static IHttpSession RequireSession(this IHttpContext context)
    {
        IHttpSession? session = context.GetSession();
        if (session is null)
        {
            throw new InvalidOperationException(
                "No IHttpSession has been attached to the HTTP context. Call SetSession(...) before requiring it.");
        }
        return session;
    }
}
