using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

internal readonly record struct ControlPlaneCredentialVerification(
    ResourceCredentialVerification Verification,
    CredentialRejectionReason Reason);

internal enum CredentialRejectionReason
{
    None,
    MissingCredential,
    UnsupportedScheme,
    InvalidToken,
    AudienceMismatch,
    CallerMismatch,
    VerifierRejected,
    NonLoopbackPeer,
}
