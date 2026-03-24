using System.ComponentModel.DataAnnotations;

namespace Assimalign.Cohesion.Resilience;

using Assimalign.Cohesion.Resilience.Internal;

/// <summary>
/// A builder that is used to create an instance of <see cref="ResiliencePipelineO"/>.
/// </summary>
/// <remarks>
/// The builder supports combining multiple strategies into a pipeline of resilience strategies.
/// The resulting instance of <see cref="ResiliencePipelineO"/> created by the <see cref="Build"/> call executes the strategies in the same order they were added to the builder.
/// The order of the strategies is important.
/// </remarks>
public sealed class ResiliencePipelineBuilderO : ResiliencePipelineBuilderBase
{
    /// <summary>
    /// Builds the resilience pipeline.
    /// </summary>
    /// <returns>An instance of <see cref="ResiliencePipelineO"/>.</returns>
    /// <exception cref="ValidationException">Thrown when this builder has invalid configuration.</exception>
    public ResiliencePipelineO Build() => new ResiliencePipelineO(BuildPipelineComponent(), DisposeBehavior.Allow, ContextPool);
}
