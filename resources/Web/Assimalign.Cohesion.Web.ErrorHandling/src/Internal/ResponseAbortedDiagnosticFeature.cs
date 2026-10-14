using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling.Internal;

/// <summary>Immutable marker installed only on an exchange whose peer-abort diagnostic was emitted.</summary>
internal sealed class ResponseAbortedDiagnosticFeature : IHttpFeature
{
    internal static readonly ResponseAbortedDiagnosticFeature Instance = new();

    private ResponseAbortedDiagnosticFeature()
    {
    }

    public string Name => "Assimalign.Cohesion.Web.ErrorHandling.ResponseAbortedDiagnostic";
}
