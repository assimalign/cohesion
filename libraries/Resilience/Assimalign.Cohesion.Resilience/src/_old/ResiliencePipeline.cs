namespace Assimalign.Cohesion.Resilience;

using Assimalign.Cohesion.Resilience.Internal;
using Assimalign.Cohesion.Resilience.Internal.Pipeline;

/// <summary>
/// Resilience pipeline is used to execute the user-provided callbacks.
/// </summary>
/// <remarks>
/// Resilience pipeline supports various types of callbacks and provides a unified way to execute them.
/// This includes overloads for synchronous and asynchronous callbacks, generic and non-generic callbacks.
/// </remarks>
public sealed partial class ResiliencePipelineO
{
    /// <summary>
    /// Resilience pipeline that executes the user-provided callback without any additional logic.
    /// </summary>
    public static readonly ResiliencePipelineO Empty = new(PipelineComponent.Empty, DisposeBehavior.Ignore, null);

    internal ResiliencePipelineO(PipelineComponent component, DisposeBehavior disposeBehavior, ResilienceContextPool? pool)
    {
        Component = component;
        DisposeHelper = new ComponentDisposeHelper(component, disposeBehavior);
        Pool = pool ?? ResilienceContextPool.Shared;
    }

    internal ResilienceContextPool Pool { get; }

    internal PipelineComponent Component { get; }

    internal ComponentDisposeHelper DisposeHelper { get; }
}
