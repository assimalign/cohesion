using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

internal sealed class InSetExternalResourceResolver : IExternalResourceResolver
{
    private readonly IApplicationSetExternalResourceResolver _direct;
    private readonly IReadOnlyList<IApplicationModel> _models;
    private readonly IExternalResourceResolver _fallback;

    public InSetExternalResourceResolver(
        IApplicationSetExternalResourceResolver direct,
        IReadOnlyList<IApplicationModel> models,
        IExternalResourceResolver fallback)
    {
        _direct = direct ?? throw new ArgumentNullException(nameof(direct));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public async ValueTask<ExternalResourceResolution> ResolveAsync(
        ExternalResourceResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        ExternalResourceResolution direct = await _direct
            .ResolveInSetAsync(_models, context, cancellationToken)
            .ConfigureAwait(false);
        if (direct.Resolved)
        {
            return direct;
        }

        return await _fallback.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
