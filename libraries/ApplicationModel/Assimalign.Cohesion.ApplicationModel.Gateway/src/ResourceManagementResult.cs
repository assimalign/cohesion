using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// The outcome of a management API operation: its status, the JSON document a control plane returns, and the record
/// version the response names as its entity tag.
/// </summary>
/// <remarks>
/// A refusal's document is <c>{"error": "&lt;code&gt;", "detail": "..."}</c>, with the code that
/// <see cref="ResourceManagementStatus"/> gives for the status. Some refusals, such as an action no instance could
/// run, return a fuller document instead.
/// </remarks>
[DebuggerDisplay("{Status} ({Document.Length} bytes)")]
public sealed class ResourceManagementResult
{
    private ResourceManagementResult(ResourceManagementStatus status, ReadOnlyMemory<byte> document, string? version)
    {
        Status = status;
        Document = document;
        Version = version;
    }

    /// <summary>Gets the operation's status.</summary>
    public ResourceManagementStatus Status { get; }

    /// <summary>Gets the UTF-8 JSON response document.</summary>
    public ReadOnlyMemory<byte> Document { get; }

    /// <summary>Gets the record version the response names as its entity tag, if any.</summary>
    public string? Version { get; }

    /// <summary>Creates a result with a response document.</summary>
    /// <param name="status">The operation's status.</param>
    /// <param name="document">The UTF-8 JSON response document.</param>
    /// <param name="version">The record version, if the response names one.</param>
    /// <returns>The result; the document is copied.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is undefined.</exception>
    public static ResourceManagementResult Create(ResourceManagementStatus status, ReadOnlyMemory<byte> document, string? version = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new ResourceManagementResult(status, document.ToArray(), version);
    }

    /// <summary>Creates a refusal whose document names the status's error code and a detail.</summary>
    /// <param name="status">The refusal; not <see cref="ResourceManagementStatus.Ok"/>.</param>
    /// <param name="detail">The human-readable reason, which must not contain secrets.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentException"><paramref name="detail"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="detail"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is <see cref="ResourceManagementStatus.Ok"/> or undefined.</exception>
    public static ResourceManagementResult Refuse(ResourceManagementStatus status, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (status == ResourceManagementStatus.Ok || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("error", GetErrorCode(status));
            writer.WriteString("detail", detail);
            writer.WriteEndObject();
        }

        return new ResourceManagementResult(status, stream.ToArray(), null);
    }

    /// <summary>Gets the wire error code of a status, such as <c>unknown-section</c>.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The code; <c>ok</c> for <see cref="ResourceManagementStatus.Ok"/>.</returns>
    public static string GetErrorCode(ResourceManagementStatus status) => status switch
    {
        ResourceManagementStatus.Ok => "ok",
        ResourceManagementStatus.InvalidConfiguration => "invalid-configuration",
        ResourceManagementStatus.InvalidInput => "invalid-input",
        ResourceManagementStatus.UnknownResource => "unknown-resource",
        ResourceManagementStatus.UnknownSection => "unknown-section",
        ResourceManagementStatus.UnknownAction => "unknown-action",
        ResourceManagementStatus.UnknownPlatformSurface => "unknown-platform-surface",
        ResourceManagementStatus.DeploymentOnly => "deployment-only",
        ResourceManagementStatus.NotOverridable => "not-overridable",
        ResourceManagementStatus.NotClaimable => "not-claimable",
        ResourceManagementStatus.ClaimConflict => "claim-conflict",
        ResourceManagementStatus.VersionMoved => "version-moved",
        ResourceManagementStatus.RecordsUnsupported => "records-unsupported",
        ResourceManagementStatus.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
