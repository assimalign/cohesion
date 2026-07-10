using System;
using System.IO;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// The factory for the built-in <see cref="IResult"/> implementations. Every method returns the
/// abstraction; use <see cref="TypedResults"/> when the concrete carrier type is wanted for
/// return-type inference or endpoint metadata.
/// </summary>
/// <remarks>
/// The concrete result types have internal constructors: this factory (and its typed twin) is the
/// only way to create them, which keeps the public surface a single seam that the negotiated-result
/// (#149), file-precondition (#777), and exception-boundary (#881) slices can extend without
/// breaking consumers. Factories are plain statics — results capture everything they need as
/// constructor state, so there is no builder, no options object, and no request-time service
/// resolution.
/// </remarks>
public static class Results
{
    /// <summary>
    /// Creates a bodyless result that sets only <paramref name="statusCode"/>.
    /// </summary>
    /// <param name="statusCode">The status code to set on the response.</param>
    /// <returns>The status-code result.</returns>
    public static IResult StatusCode(HttpStatusCode statusCode) => new StatusCodeHttpResult(statusCode);

    /// <summary>
    /// Creates a result that sets <c>204 No Content</c>.
    /// </summary>
    /// <returns>The no-content result.</returns>
    public static IResult NoContent() => NoContentHttpResult.Instance;

    /// <summary>
    /// Creates a result that writes nothing, leaving the response exactly as the pipeline shaped
    /// it. Use when the handler already wrote the response imperatively.
    /// </summary>
    /// <returns>The empty result.</returns>
    public static IResult Empty() => EmptyHttpResult.Instance;

    /// <summary>
    /// Creates a result that writes <paramref name="content"/> as UTF-8 text with
    /// <c>text/plain; charset=utf-8</c> (or <paramref name="contentType"/> when supplied).
    /// </summary>
    /// <param name="content">The response body text.</param>
    /// <param name="contentType">The <c>Content-Type</c> to set, or <see langword="null"/> for the text default.</param>
    /// <returns>The text result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static IResult Text(string content, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ContentHttpResult(content, contentType, statusCode: null);
    }

    /// <summary>
    /// Creates a result that writes <paramref name="content"/> as UTF-8 text with an explicit
    /// content type and optional status code.
    /// </summary>
    /// <param name="content">The response body text.</param>
    /// <param name="contentType">The <c>Content-Type</c> to set, or <see langword="null"/> for <c>text/plain; charset=utf-8</c>.</param>
    /// <param name="statusCode">The status code to set, or <see langword="null"/> to leave the current one.</param>
    /// <returns>The content result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static IResult Content(string content, string? contentType = null, HttpStatusCode? statusCode = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new ContentHttpResult(content, contentType, statusCode);
    }

    /// <summary>
    /// Creates a result that serializes <paramref name="value"/> as JSON through the
    /// source-generated <paramref name="typeInfo"/> — never through reflection — keeping the write
    /// NativeAOT- and trimming-safe.
    /// </summary>
    /// <typeparam name="T">The DTO type being serialized.</typeparam>
    /// <param name="value">The value to serialize. A <see langword="null"/> reference serializes as JSON <c>null</c>.</param>
    /// <param name="typeInfo">The source-generated serialization metadata for <typeparamref name="T"/>.</param>
    /// <param name="contentType">The <c>Content-Type</c> to set, or <see langword="null"/> for <c>application/json; charset=utf-8</c>.</param>
    /// <param name="statusCode">The status code to set, or <see langword="null"/> to leave the current one.</param>
    /// <returns>The JSON result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    public static IResult Json<T>(T? value, JsonTypeInfo<T> typeInfo, string? contentType = null, HttpStatusCode? statusCode = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new JsonHttpResult<T>(value, typeInfo, contentType, statusCode);
    }

    /// <summary>
    /// Creates a <c>200 OK</c> result that serializes <paramref name="value"/> as JSON through the
    /// source-generated <paramref name="typeInfo"/>. JSON-only in this foundation: content
    /// negotiation over the <c>Accept</c> header is deferred to the negotiated-results slice
    /// (#149).
    /// </summary>
    /// <typeparam name="T">The DTO type being returned.</typeparam>
    /// <param name="value">The value to serialize. A <see langword="null"/> reference serializes as JSON <c>null</c>.</param>
    /// <param name="typeInfo">The source-generated serialization metadata for <typeparamref name="T"/>.</param>
    /// <returns>The OK result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    public static IResult Ok<T>(T? value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return new OkHttpResult<T>(value, typeInfo);
    }

    /// <summary>
    /// Creates a result that writes <paramref name="contents"/> as a file payload with
    /// <c>Content-Length</c>. Unconditional: range and precondition support is deferred to #777.
    /// </summary>
    /// <param name="contents">The file bytes to write.</param>
    /// <param name="contentType">The <c>Content-Type</c> to set, or <see langword="null"/> for <c>application/octet-stream</c>.</param>
    /// <returns>The file result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="contents"/> is <see langword="null"/>.</exception>
    public static IResult File(byte[] contents, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(contents);
        return new FileHttpResult(contents, contentType);
    }

    /// <summary>
    /// Creates a result that copies <paramref name="stream"/> to the response body, setting
    /// <c>Content-Length</c> when the stream is seekable. The result takes ownership of the stream
    /// and disposes it after the copy, so the result is single-use. Unconditional: range and
    /// precondition support is deferred to #777.
    /// </summary>
    /// <param name="stream">The stream whose remaining contents become the response body.</param>
    /// <param name="contentType">The <c>Content-Type</c> to set, or <see langword="null"/> for <c>application/octet-stream</c>.</param>
    /// <returns>The file-stream result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public static IResult FileStream(Stream stream, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new FileStreamHttpResult(stream, contentType);
    }

    /// <summary>
    /// Creates a result that sends the file at <paramref name="path"/>, inferring the
    /// <c>Content-Type</c> from the file extension via <see cref="HttpContentTypes.GetContentType"/>
    /// when none is supplied. Unconditional: range and precondition support is deferred to #777.
    /// </summary>
    /// <param name="path">The absolute or working-directory-relative path of the file to send.</param>
    /// <param name="contentType">The <c>Content-Type</c> to set, or <see langword="null"/> to infer it from the extension.</param>
    /// <returns>The physical-file result.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    public static IResult PhysicalFile(string path, string? contentType = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new PhysicalFileHttpResult(path, contentType);
    }

    /// <summary>
    /// Creates a redirection result targeting <paramref name="url"/>. The status code follows the
    /// <paramref name="permanent"/> × <paramref name="preserveMethod"/> combination: 302, 301
    /// (permanent), 307 (preserve method), or 308 (both).
    /// </summary>
    /// <param name="url">The redirect target — an absolute or relative URI reference (RFC 3986).</param>
    /// <param name="permanent">Whether the redirect is permanent (301/308).</param>
    /// <param name="preserveMethod">Whether the user agent must preserve the request method (307/308).</param>
    /// <returns>The redirect result.</returns>
    /// <exception cref="ArgumentException"><paramref name="url"/> is <see langword="null"/> or empty.</exception>
    public static IResult Redirect(string url, bool permanent = false, bool preserveMethod = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        return new RedirectHttpResult(url, permanent, preserveMethod);
    }

    /// <summary>
    /// Creates an RFC 9457 problem result from an existing <see cref="ProblemDetails"/>. The
    /// payload is normalized (status defaults to 500; a missing title is filled from the status
    /// phrase for the default problem type) and rendered as <c>application/problem+json</c> by the
    /// AOT-safe default writer.
    /// </summary>
    /// <param name="problemDetails">The problem payload to write.</param>
    /// <returns>The problem result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="problemDetails"/> is <see langword="null"/>.</exception>
    public static IResult Problem(ProblemDetails problemDetails)
    {
        ArgumentNullException.ThrowIfNull(problemDetails);
        return new ProblemHttpResult(problemDetails);
    }

    /// <summary>
    /// Creates an RFC 9457 problem result from individual members. Omitted members follow the
    /// defaults: status 500, the reserved <c>about:blank</c> type, and the status phrase as the
    /// title.
    /// </summary>
    /// <param name="detail">A human-readable explanation specific to this occurrence (§3.1.4).</param>
    /// <param name="instance">A URI reference identifying this occurrence (§3.1.5).</param>
    /// <param name="statusCode">The HTTP status code for the problem, or <see langword="null"/> for 500.</param>
    /// <param name="title">A short, human-readable summary of the problem type (§3.1.2).</param>
    /// <param name="type">A URI reference identifying the problem type (§3.1.1).</param>
    /// <returns>The problem result.</returns>
    public static IResult Problem(
        string? detail = null,
        string? instance = null,
        HttpStatusCode? statusCode = null,
        string? title = null,
        string? type = null)
    {
        return new ProblemHttpResult(new ProblemDetails
        {
            Detail = detail,
            Instance = instance,
            Status = statusCode?.Value,
            Title = title,
            Type = type,
        });
    }

    /// <summary>
    /// Creates a streaming result that hands the exchange's
    /// <see cref="IHttpResponseStreamingFeature"/> to <paramref name="callback"/> so the body can
    /// be produced incrementally, then completes the response. Fails loudly with
    /// <see cref="NotSupportedException"/> at execution when streaming is not enabled on the
    /// exchange; never sets <c>Content-Length</c>.
    /// </summary>
    /// <param name="callback">Produces the body against the streaming feature.</param>
    /// <param name="contentType">The <c>Content-Type</c> to set before the first write, or <see langword="null"/> to leave it to the callback.</param>
    /// <param name="statusCode">The status code to set before the first write, or <see langword="null"/> to leave the current one.</param>
    /// <returns>The push-stream result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="callback"/> is <see langword="null"/>.</exception>
    public static IResult PushStream(
        Func<IHttpResponseStreamingFeature, CancellationToken, Task> callback,
        string? contentType = null,
        HttpStatusCode? statusCode = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new PushStreamHttpResult(callback, contentType, statusCode);
    }
}
