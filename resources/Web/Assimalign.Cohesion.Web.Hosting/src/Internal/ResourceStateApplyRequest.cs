using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Web.Hosting.Internal;

internal sealed record ResourceStateApplyRequest(ResourceStateApplyContext Context, ResourceStateValue Desired);
