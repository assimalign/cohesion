using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.SecurityHeaders.Internal;

// Starting callbacks are single-consumer. Presence records that a nested registration already
// reported this exchange, so the process-wide total counts responses, not middleware instances.
internal sealed class SecurityHeadersFailureFeature : IHttpFeature
{
    public string Name => nameof(SecurityHeadersFailureFeature);
}
