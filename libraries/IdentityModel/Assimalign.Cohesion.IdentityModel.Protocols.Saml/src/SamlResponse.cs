using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>Response</c> (SAML Core §3.3.3). The base envelope carries the
/// message identifier, the issuer (the identity provider entity id), the destination, the
/// <c>InResponseTo</c> correlation, and the status.
/// </summary>
public sealed class SamlResponse : ProtocolResponse
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlResponse" /> class by snapshotting
    /// the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The response contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no status, or an assertion list contains a null entry.
    /// </exception>
    public SamlResponse(SamlResponseDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.Saml2)
    {
        Assertions = SnapshotList(descriptor.Assertions);
        EncryptedAssertions = SnapshotList(descriptor.EncryptedAssertions);
        RawXml = descriptor.RawXml;
    }

    /// <summary>
    /// Gets the assertions carried by the response.
    /// </summary>
    public IReadOnlyList<SamlAssertion> Assertions { get; }

    /// <summary>
    /// Gets the encrypted assertion markers the descriptive layer cannot open.
    /// </summary>
    public IReadOnlyList<SamlEncryptedElement> EncryptedAssertions { get; }

    /// <summary>
    /// Gets the verbatim, as-received response element octets, when retained. This is the
    /// exact <c>&lt;samlp:Response&gt;</c> element — never a re-serialization — so the token
    /// package can re-verify a response-level signature independently of any assertion-level
    /// signature.
    /// </summary>
    public string? RawXml { get; }

    /// <summary>
    /// Validates the response envelope against the receiving context: a success status,
    /// the <c>InResponseTo</c> and <c>Destination</c> matches, and — on success — the
    /// presence of at least one assertion (cleartext or encrypted). Each assertion's own
    /// data rules are validated through <see cref="SamlAssertion.Validate" />.
    /// </summary>
    /// <param name="options">The receiving context.</param>
    /// <returns>The validation findings.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options" /> is null.</exception>
    public ProtocolValidationResult Validate(SamlResponseValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<ProtocolValidationDiagnostic>();

        if (!Status.Succeeded)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.StatusNotSuccess,
                $"The response reports a non-success status ({Status.Code}).",
                member: "Status"));
        }

        if (options.ExpectedInResponseTo is not null &&
            !string.Equals(InResponseTo, options.ExpectedInResponseTo, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.InResponseToMismatch,
                "The response InResponseTo does not match the sent request.",
                member: "InResponseTo"));
        }

        if (options.ExpectedDestination is not null &&
            !string.Equals(Destination, options.ExpectedDestination, StringComparison.Ordinal))
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.DestinationMismatch,
                "The response Destination does not match the receiving endpoint.",
                member: "Destination"));
        }

        if (Status.Succeeded && Assertions.Count == 0 && EncryptedAssertions.Count == 0)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.AssertionMissing,
                "A success response carries no assertion.",
                member: "Assertion"));
        }

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }

    private static IReadOnlyList<TItem> SnapshotList<TItem>(IList<TItem> source)
        where TItem : class
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Count == 0)
        {
            return Array.Empty<TItem>();
        }

        var snapshot = new TItem[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            snapshot[index] = source[index]
                ?? throw new IdentityModelException("A SAML response list must not contain null entries.");
        }

        return new ReadOnlyCollection<TItem>(snapshot);
    }
}
