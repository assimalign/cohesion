using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Extension methods that read or attach an <see cref="IHttpFormCollection"/> against an
/// <see cref="IHttpRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// Form-body parsing is application code: it interprets a request body under one of two
/// content types (<c>application/x-www-form-urlencoded</c>, <c>multipart/form-data</c>) and
/// produces a typed collection. The Cohesion HTTP protocol core
/// (<see cref="IHttpRequest"/>) deliberately exposes only the raw <c>Body</c> stream;
/// consumers that need parsed forms reference this package and call
/// <see cref="ReadFormAsync"/> against the request.
/// </para>
/// <para>
/// <strong>PR-1 scope:</strong> the parser itself is not yet ported into this package &#8211;
/// the prior implementation lived inside <c>Assimalign.Cohesion.Http.Transports</c>'s
/// per-version readers (Http1MessageReader / Http2Stream / Http3HeaderCodec) and was removed
/// together with the dead <c>Form</c> property. <see cref="ReadFormAsync"/> currently
/// installs an empty collection when none has been pre-attached and is intentionally a
/// scaffold for the follow-up that ports / rewrites the form-body parser. Use
/// <see cref="SetForm"/> to inject a parsed collection in the meantime.
/// </para>
/// </remarks>
public static class HttpRequestFormExtensions
{
    /// <summary>Key used to store the form collection in <see cref="IHttpRequest"/>-scoped state.</summary>
    public const string FormItemKey = "Assimalign.Cohesion.Http.Forms::Form";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IHttpRequest, IHttpFormCollection> _forms = new();

    /// <summary>
    /// Returns the form collection attached to <paramref name="request"/> via
    /// <see cref="SetForm"/>, or <see langword="null"/> when none has been attached.
    /// </summary>
    public static IHttpFormCollection? GetForm(this IHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _forms.TryGetValue(request, out IHttpFormCollection? form) ? form : null;
    }

    /// <summary>
    /// Attaches <paramref name="form"/> to <paramref name="request"/>. Overwrites any
    /// previously attached collection.
    /// </summary>
    public static void SetForm(this IHttpRequest request, IHttpFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(form);
        _forms.AddOrUpdate(request, form);
    }

    /// <summary>
    /// Returns the form collection attached to <paramref name="request"/>, parsing the
    /// body and caching the result on first call.
    /// </summary>
    /// <remarks>
    /// PR-1 scaffold: returns an empty <see cref="HttpFormCollection"/> when no collection
    /// has been pre-attached and the request does not advertise a form content-type. A
    /// future PR ports the multipart / urlencoded parser into this package; until then,
    /// callers that already have parsed form data should attach it via <see cref="SetForm"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static Task<IHttpFormCollection> ReadFormAsync(
        this IHttpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        IHttpFormCollection? attached = request.GetForm();
        if (attached is not null)
        {
            return Task.FromResult(attached);
        }

        IHttpFormCollection empty = new HttpFormCollection();
        request.SetForm(empty);
        return Task.FromResult(empty);
    }
}
