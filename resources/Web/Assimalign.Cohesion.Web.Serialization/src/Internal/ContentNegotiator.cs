using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization.Internal;

/// <summary>
/// The negotiation algorithm behind the <see cref="IHttpContentSerializationFeature"/> negotiation
/// seam (#149): given a request's <c>Accept</c> header and the writers a registry can offer, it
/// picks the concrete media type the response should carry. The q-value / precedence core is the
/// shared <see cref="HttpContentNegotiation"/> primitive (#771) — this type only collects the
/// registry's emittable representations and layers the structured-suffix fallback on top, so the
/// registry's <see cref="HttpMediaType.Includes(HttpMediaType)"/> matching and negotiation can
/// never disagree.
/// </summary>
internal static class ContentNegotiator
{
    /// <summary>
    /// Selects the best concrete media type for a request from the registered writers.
    /// </summary>
    /// <param name="writers">The registry's writers, in registration (server-preference) order.</param>
    /// <param name="acceptHeader">The raw request <c>Accept</c> header, or <see langword="null"/>.</param>
    /// <param name="mediaType">On success, the negotiated concrete media type.</param>
    /// <returns>
    /// <see langword="true"/> when an acceptable representation exists; <see langword="false"/>
    /// when nothing is acceptable (the caller's <c>406</c> signal) — including when no writers are
    /// registered.
    /// </returns>
    internal static bool TryNegotiate(
        IReadOnlyList<IHttpContentWriter> writers,
        string? acceptHeader,
        out HttpMediaType mediaType)
    {
        mediaType = default;

        List<HttpMediaType> serverOptions = CollectServerOptions(writers);
        if (serverOptions.Count == 0)
        {
            return false;
        }

        IReadOnlyList<HttpMediaTypeQuality> accept = HttpAcceptParser.ParseAccept(acceptHeader);

        // Exact RFC 9110 §12.5.1 matching first — the same Includes/Specificity rules the registry
        // uses, delegated to the shared primitive so negotiation and lookup stay in lock-step.
        if (HttpContentNegotiation.TryNegotiateMediaType(accept, serverOptions, out mediaType))
        {
            return true;
        }

        // Structured-suffix fallback (#149): a bare base-type Accept range (application/json) is
        // satisfiable by a registered writer whose media type carries that base as its structured
        // suffix (application/problem+json). Strictly lower precedence than the exact pass, so it
        // only ever turns a would-be 406 into a served, honestly-typed response.
        return TryNegotiateBySuffix(accept, serverOptions, out mediaType);
    }

    /// <summary>
    /// Gathers the concrete (emittable) media types the writers can produce, in registration order
    /// and de-duplicated. Wildcard entries are a writer's match targets, not representations it can
    /// serialize <em>as</em>, so they are excluded.
    /// </summary>
    private static List<HttpMediaType> CollectServerOptions(IReadOnlyList<IHttpContentWriter> writers)
    {
        var options = new List<HttpMediaType>();
        if (writers is null)
        {
            return options;
        }

        foreach (IHttpContentWriter writer in writers)
        {
            foreach (HttpMediaType candidate in writer.MediaTypes)
            {
                if (candidate.IsEmpty || candidate.HasWildcard)
                {
                    continue;
                }
                if (!Contains(options, candidate))
                {
                    options.Add(candidate);
                }
            }
        }

        return options;
    }

    private static bool Contains(List<HttpMediaType> options, HttpMediaType candidate)
    {
        foreach (HttpMediaType option in options)
        {
            if (option.Equals(candidate))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The structured-suffix pass: matches a positively-weighted, wildcard-free, non-suffixed
    /// base-type Accept range against a server option whose subtype carries that base as its
    /// <see cref="HttpMediaType.Suffix"/>. Ranges are visited in client-preference order (the
    /// parser sorts descending quality, then descending specificity); the first server option that
    /// matches and is not explicitly rejected wins.
    /// </summary>
    private static bool TryNegotiateBySuffix(
        IReadOnlyList<HttpMediaTypeQuality> accept,
        IReadOnlyList<HttpMediaType> serverOptions,
        out HttpMediaType mediaType)
    {
        mediaType = default;

        foreach (HttpMediaTypeQuality entry in accept)
        {
            HttpMediaType range = entry.MediaType;

            // Only a bare base type broadens to its suffix family. An already-suffixed range
            // (application/vnd.foo+json) asks for a specific schema and must not be widened;
            // wildcard ranges (*/*, application/*) are the exact pass's job.
            if (!entry.Quality.IsAcceptable || range.HasWildcard || range.Suffix.Length != 0)
            {
                continue;
            }

            foreach (HttpMediaType option in serverOptions)
            {
                if (string.Equals(option.Type, range.Type, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(option.Suffix, range.SubType, StringComparison.OrdinalIgnoreCase)
                    && !IsExplicitlyRejected(accept, option))
                {
                    mediaType = option;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Reports whether a <c>q=0</c> range explicitly refuses <paramref name="option"/> (RFC 9110
    /// §12.5.1). The exact pass already skips such options; the suffix fallback must honor the
    /// refusal too, so it never serves a representation the client crossed out.
    /// </summary>
    private static bool IsExplicitlyRejected(IReadOnlyList<HttpMediaTypeQuality> accept, HttpMediaType option)
    {
        foreach (HttpMediaTypeQuality entry in accept)
        {
            if (!entry.Quality.IsAcceptable && entry.MediaType.Includes(option))
            {
                return true;
            }
        }
        return false;
    }
}
