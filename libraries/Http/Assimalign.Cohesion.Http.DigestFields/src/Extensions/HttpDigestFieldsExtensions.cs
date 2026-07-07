using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Convenience members for reading RFC 9530 digest fields off a request and stamping
/// <c>Content-Digest</c> onto a response.
/// </summary>
public static class HttpDigestFieldsExtensions
{
    extension(IHttpRequest request)
    {
        /// <summary>
        /// Attempts to read and parse the request's <c>Content-Digest</c> field.
        /// </summary>
        /// <param name="field">When this method returns <see langword="true"/>, the parsed field.</param>
        /// <returns><see langword="true"/> if the header was present and well-formed; otherwise <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
        public bool TryGetContentDigest(out HttpDigestField field)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Headers.TryGetValue(HttpHeaderKey.ContentDigest, out HttpHeaderValue value) && !value.IsEmpty)
            {
                return HttpDigestField.TryParse(value, out field);
            }
            field = default;
            return false;
        }

        /// <summary>
        /// Attempts to read and parse the request's <c>Want-Content-Digest</c> preference field.
        /// </summary>
        /// <param name="field">When this method returns <see langword="true"/>, the parsed field.</param>
        /// <returns><see langword="true"/> if the header was present and well-formed; otherwise <see langword="false"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
        public bool TryGetWantContentDigest(out HttpWantDigestField field)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Headers.TryGetValue(HttpHeaderKey.WantContentDigest, out HttpHeaderValue value) && !value.IsEmpty)
            {
                return HttpWantDigestField.TryParse(value, out field);
            }
            field = default;
            return false;
        }
    }

    extension(IHttpResponse response)
    {
        /// <summary>
        /// Stamps the response with a precomputed <c>Content-Digest</c> field, replacing any existing
        /// value.
        /// </summary>
        /// <param name="field">The digest field to write.</param>
        /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
        public void SetContentDigest(HttpDigestField field)
        {
            ArgumentNullException.ThrowIfNull(response);
            response.Headers[HttpHeaderKey.ContentDigest] = field.Serialize();
        }

        /// <summary>
        /// Computes <c>Content-Digest</c> over <paramref name="content"/> with an explicit algorithm
        /// and stamps it onto the response.
        /// </summary>
        /// <param name="content">The response content to hash.</param>
        /// <param name="algorithm">The algorithm to compute with; must be supported.</param>
        /// <returns>The digest field written to the response.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="algorithm"/> is not supported for computation.</exception>
        public HttpDigestField SetContentDigest(ReadOnlySpan<byte> content, HttpDigestAlgorithm algorithm)
        {
            ArgumentNullException.ThrowIfNull(response);
            HttpDigestField field = HttpDigestField.ForContent(content, algorithm);
            response.Headers[HttpHeaderKey.ContentDigest] = field.Serialize();
            return field;
        }

        /// <summary>
        /// Computes <c>Content-Digest</c> over <paramref name="content"/> and stamps it onto the
        /// response, honoring the request's <c>Want-Content-Digest</c> preference when present. Falls
        /// back to <see cref="HttpDigestAlgorithm.Sha256"/> when the request expressed no supported,
        /// acceptable preference.
        /// </summary>
        /// <param name="content">The response content to hash.</param>
        /// <returns>The digest field written to the response.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="response"/> is <see langword="null"/>.</exception>
        public HttpDigestField SetContentDigest(ReadOnlySpan<byte> content)
        {
            ArgumentNullException.ThrowIfNull(response);

            HttpDigestAlgorithm algorithm = HttpDigestAlgorithm.Sha256;
            if (response.HttpContext.Request.TryGetWantContentDigest(out HttpWantDigestField want)
                && want.TrySelectPreferred(out HttpDigestAlgorithm preferred))
            {
                algorithm = preferred;
            }

            HttpDigestField field = HttpDigestField.ForContent(content, algorithm);
            response.Headers[HttpHeaderKey.ContentDigest] = field.Serialize();
            return field;
        }
    }
}
