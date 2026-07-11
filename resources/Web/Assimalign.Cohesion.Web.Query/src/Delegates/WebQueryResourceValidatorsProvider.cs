using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Query;

using Assimalign.Cohesion.Http;

/// <summary>
/// Resolves the target resource's current validators for a conditional QUERY evaluation —
/// the ETag / Last-Modified of the representation the equivalent GET request would select
/// (RFC 10008 &#167; 2.6).
/// </summary>
/// <param name="context">The exchange whose target resource's validators are requested.</param>
/// <returns>
/// The resource's current validators, or <see langword="null"/> when they are unknown for this
/// request — in which case the conditional middleware performs no precondition evaluation and
/// passes the request through.
/// </returns>
public delegate ValueTask<WebQueryResourceValidators?> WebQueryResourceValidatorsProvider(IHttpContext context);
