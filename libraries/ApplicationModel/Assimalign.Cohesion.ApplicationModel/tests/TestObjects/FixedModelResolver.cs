using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

/// <summary>
/// Resolves a member declaration to a fixed model and records how often, and in which order
/// relative to other callbacks, it was asked.
/// </summary>
internal sealed class FixedModelResolver : IApplicationModelResolver
{
    private readonly IApplicationModel _model;
    private readonly ICollection<string>? _events;

    public FixedModelResolver(IApplicationModel model, ICollection<string>? events = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _events = events;
    }

    public int CallCount { get; private set; }

    public ValueTask<IApplicationModel> ResolveAsync(
        ApplicationModelResolutionContext context,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        _events?.Add($"resolve:{_model.Name}");
        return ValueTask.FromResult(_model);
    }
}
