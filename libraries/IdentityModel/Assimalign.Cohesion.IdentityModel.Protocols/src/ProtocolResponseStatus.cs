using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the normalized outcome carried by a protocol response, covering both the
/// OAuth error triplet (<c>error</c>/<c>error_description</c>/<c>error_uri</c>) and the
/// SAML status structure (nested status codes, <c>StatusMessage</c>, <c>StatusDetail</c>).
/// </summary>
/// <remarks>
/// The outcome is stored, not inferred from code presence: SAML successes always carry the
/// <c>Success</c> status code on the wire, and a single-logout response can legally succeed
/// while carrying a second-level <c>PartialLogout</c> code. <see cref="SubCodes" /> is the
/// ordered chain of nested status codes (SAML status nesting is unbounded; real products
/// emit three levels). Wire-code provenance is preserved verbatim; canonical mapping is a
/// consumer concern.
/// </remarks>
public sealed class ProtocolResponseStatus
{
    private ProtocolResponseStatus(
        bool succeeded,
        string? code,
        IReadOnlyList<string> subCodes,
        string? message,
        string? detailUri,
        IReadOnlyDictionary<string, IdentityClaimValue> properties)
    {
        Succeeded = succeeded;
        Code = code;
        SubCodes = subCodes;
        Message = message;
        DetailUri = detailUri;
        Properties = properties;
    }

    /// <summary>
    /// Gets the plain success status with no wire detail — the common OpenID Connect case,
    /// where a success response carries no status element at all.
    /// </summary>
    public static ProtocolResponseStatus Success { get; } =
        new(true, null, Array.Empty<string>(), null, null, ModelSnapshot.EmptyProperties);

    /// <summary>
    /// Creates a success status carrying wire detail (for example a SAML top-level
    /// <c>Success</c> status code with a second-level <c>PartialLogout</c> code).
    /// </summary>
    /// <param name="code">The top-level wire status code.</param>
    /// <param name="subCodes">The ordered chain of nested status codes.</param>
    /// <param name="message">The wire status message.</param>
    /// <param name="properties">Additional status detail (for example SAML <c>StatusDetail</c> content).</param>
    /// <returns>A succeeded status.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code" /> is present but whitespace, when a sub-code
    /// entry is null or whitespace, or when a property name is blank or a property value
    /// is undefined.
    /// </exception>
    public static ProtocolResponseStatus SuccessWith(
        string? code = null,
        IReadOnlyList<string>? subCodes = null,
        string? message = null,
        IReadOnlyDictionary<string, IdentityClaimValue>? properties = null)
    {
        return Create(true, code, subCodes, message, null, properties);
    }

    /// <summary>
    /// Creates a failed status.
    /// </summary>
    /// <param name="code">The top-level wire error or status code.</param>
    /// <param name="message">The wire error description or status message.</param>
    /// <param name="subCodes">The ordered chain of nested status codes.</param>
    /// <param name="detailUri">A URI with further error information (OAuth <c>error_uri</c>).</param>
    /// <param name="properties">Additional status detail.</param>
    /// <returns>A failed status.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="code" /> is null or whitespace, when a sub-code entry
    /// is null or whitespace, or when a property name is blank or a property value is
    /// undefined.
    /// </exception>
    public static ProtocolResponseStatus Failed(
        string code,
        string? message = null,
        IReadOnlyList<string>? subCodes = null,
        string? detailUri = null,
        IReadOnlyDictionary<string, IdentityClaimValue>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return Create(false, code, subCodes, message, detailUri, properties);
    }

    /// <summary>
    /// Gets a value indicating whether the response reported success. Stored, never
    /// inferred from code presence. When <see langword="false" />, <see cref="Code" /> is
    /// non-null: every failed status carries its wire code.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Code))]
    public bool Succeeded { get; }

    /// <summary>
    /// Gets the top-level wire status or error code, verbatim.
    /// </summary>
    public string? Code { get; }

    /// <summary>
    /// Gets the ordered chain of nested status codes (SAML second-level and deeper
    /// <c>StatusCode</c> values). Empty for protocols without status nesting.
    /// </summary>
    public IReadOnlyList<string> SubCodes { get; }

    /// <summary>
    /// Gets the wire status message or error description.
    /// </summary>
    public string? Message { get; }

    /// <summary>
    /// Gets the URI with further error information (OAuth <c>error_uri</c>).
    /// </summary>
    public string? DetailUri { get; }

    /// <summary>
    /// Gets additional status detail (for example SAML <c>StatusDetail</c> content).
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <inheritdoc />
    public override string ToString()
        => Succeeded ? $"Succeeded{(Code is null ? "" : $" ({Code})")}" : $"Failed ({Code})";

    private static ProtocolResponseStatus Create(
        bool succeeded,
        string? code,
        IReadOnlyList<string>? subCodes,
        string? message,
        string? detailUri,
        IReadOnlyDictionary<string, IdentityClaimValue>? properties)
    {
        // The top-level code is optional, but when present it follows the same rule as
        // every sub-code entry: a blank wire code is a malformation at any nesting depth.
        if (code is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
        }

        IReadOnlyList<string> subCodeSnapshot;
        if (subCodes is null || subCodes.Count == 0)
        {
            subCodeSnapshot = Array.Empty<string>();
        }
        else
        {
            var copy = new string[subCodes.Count];
            for (var index = 0; index < subCodes.Count; index++)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(subCodes[index], nameof(subCodes));
                copy[index] = subCodes[index];
            }

            subCodeSnapshot = new ReadOnlyCollection<string>(copy);
        }

        return new ProtocolResponseStatus(
            succeeded,
            code,
            subCodeSnapshot,
            message,
            detailUri,
            ModelSnapshot.Properties(properties, nameof(properties)));
    }
}
