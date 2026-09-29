using System.Text;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// A credential presented to a gateway control plane, handed to each
/// <see cref="IApplicationCallerAuthenticator"/> in turn.
/// </summary>
/// <param name="Application">The application whose gateway control plane received the call.</param>
/// <param name="Scheme">The HTTP authentication scheme of the presented credential, such as <c>Bearer</c>.</param>
/// <param name="Credential">The presented credential value.</param>
/// <remarks>
/// <see cref="object.ToString"/> redacts <see cref="Credential"/>; never log the credential itself.
/// </remarks>
public sealed record ApplicationCallerRequest(ApplicationName Application, string Scheme, string Credential)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Application = ").Append(Application.ToString())
            .Append(", Scheme = ").Append(Scheme)
            .Append(", Credential = <redacted>");
        return true;
    }
}
