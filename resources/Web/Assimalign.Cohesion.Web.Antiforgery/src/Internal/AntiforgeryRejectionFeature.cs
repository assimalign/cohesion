using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Antiforgery.Internal;

// A shared marker stored only on exchanges whose antiforgery rejection has already been counted.
internal sealed class AntiforgeryRejectionFeature : IHttpFeature
{
    public static readonly AntiforgeryRejectionFeature Instance = new();

    private AntiforgeryRejectionFeature()
    {
    }

    public string Name => "Assimalign.Cohesion.Web.Antiforgery.Rejection";
}
