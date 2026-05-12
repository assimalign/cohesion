using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Resilience;

using Internal;
using Internal.Pipeline;

/// <summary>
/// Extensions for <see cref="ResiliencePipelineBuilderBase"/>.
/// </summary>
public static class ResiliencePipelineBuilderExtensions
{
    /// <summary>
    /// Adds an already created pipeline instance to the builder.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The builder instance.</param>
    /// <param name="pipeline">The pipeline instance.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipeline"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this builder was already used to create a pipeline. The builder cannot be modified after it has been used.</exception>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "The EmptyOptions have nothing to validate.")]
    public static TBuilder AddPipeline<TBuilder>(this TBuilder builder, ResiliencePipelineO pipeline)
        where TBuilder : ResiliencePipelineBuilderBase
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pipeline);

        builder.AddPipelineComponent(_ => PipelineComponentFactory.FromPipeline(pipeline), EmptyOptions.Instance);
        return builder;
    }

    /// <summary>
    /// Adds an already created pipeline instance to the builder.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="builder">The builder instance.</param>
    /// <param name="pipeline">The pipeline instance.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipeline"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this builder was already used to create a strategy. The builder cannot be modified after it has been used.</exception>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "The EmptyOptions have nothing to validate.")]
    public static ResiliencePipelineBuilderO<TResult> AddPipeline<TResult>(this ResiliencePipelineBuilderO<TResult> builder, ResiliencePipelineO<TResult> pipeline)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(pipeline);

        builder.AddPipelineComponent(_ => PipelineComponentFactory.FromPipeline(pipeline), EmptyOptions.Instance);
        return builder;
    }

    /// <summary>
    /// Adds a proactive resilience strategy to the builder.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The builder instance.</param>
    /// <param name="factory">The factory that creates a resilience strategy.</param>
    /// <param name="options">The options associated with the strategy.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/>, <paramref name="factory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this builder was already used to create a pipeline. The builder cannot be modified after it has been used.</exception>
    /// <exception cref="ValidationException">Thrown when <paramref name="options"/> is invalid.</exception>
    [RequiresUnreferencedCode(Constants.OptionsValidation)]
    public static TBuilder AddStrategy<TBuilder>(
        this TBuilder builder,
        Func<StrategyBuilderContext, ResilienceStrategy> factory,
        ResilienceStrategyOptions options)
        where TBuilder : ResiliencePipelineBuilderBase
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        builder.AddPipelineComponent(context => PipelineComponentFactory.FromStrategy(factory(context)), options);
        return builder;
    }

    /// <summary>
    /// Adds a reactive strategy to the builder.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="factory">The factory that creates a resilience strategy.</param>
    /// <param name="options">The options associated with the strategy.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/>, <paramref name="factory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this builder was already used to create a pipeline. The builder cannot be modified after it has been used.</exception>
    /// <exception cref="ValidationException">Thrown when <paramref name="options"/> is invalid.</exception>
    [RequiresUnreferencedCode(Constants.OptionsValidation)]
    public static ResiliencePipelineBuilderO AddStrategy(
        this ResiliencePipelineBuilderO builder,
        Func<StrategyBuilderContext, ResilienceStrategy<object>> factory,
        ResilienceStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        builder.AddPipelineComponent(context => PipelineComponentFactory.FromStrategy(factory(context)), options);
        return builder;
    }

    /// <summary>
    /// Adds a reactive strategy to the builder.
    /// </summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    /// <param name="builder">The builder instance.</param>
    /// <param name="factory">The factory that creates a resilience strategy.</param>
    /// <param name="options">The options associated with the strategy.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/>, <paramref name="factory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this builder was already used to create a pipeline. The builder cannot be modified after it has been used.</exception>
    /// <exception cref="ValidationException">Thrown when <paramref name="options"/> is invalid.</exception>
    [RequiresUnreferencedCode(Constants.OptionsValidation)]
    public static ResiliencePipelineBuilderO<TResult> AddStrategy<TResult>(
        this ResiliencePipelineBuilderO<TResult> builder,
        Func<StrategyBuilderContext, ResilienceStrategy<TResult>> factory,
        ResilienceStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        builder.AddPipelineComponent(context => PipelineComponentFactory.FromStrategy(factory(context)), options);
        return builder;
    }

    /// <summary>
    /// Adds a reactive resilience strategy to the builder.
    /// </summary>
    /// <typeparam name="TBuilder">The builder type.</typeparam>
    /// <param name="builder">The builder instance.</param>
    /// <param name="factory">The strategy factory.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this builder was already used to create a pipeline. The builder cannot be modified after it has been used.</exception>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(EmptyOptions))]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "All options members preserved for the empty options.")]
    public static TBuilder AddStrategy<TBuilder>(
        this TBuilder builder,
        Func<StrategyBuilderContext, ResilienceStrategy> factory)
        where TBuilder : ResiliencePipelineBuilderBase
        => builder.AddStrategy(factory, EmptyOptions.Instance);

    /// <summary>
    /// Adds a proactive resilience strategy to the builder.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="factory">The strategy instance.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this builder was already used to create a pipeline. The builder cannot be modified after it has been used.</exception>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(EmptyOptions))]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "All options members preserved for the empty options.")]
    public static ResiliencePipelineBuilderO AddStrategy(
        this ResiliencePipelineBuilderO builder,
        Func<StrategyBuilderContext, ResilienceStrategy<object>> factory)
        => builder.AddStrategy(factory, EmptyOptions.Instance);

    /// <summary>
    /// Adds a reactive resilience strategy to the builder.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="builder">The builder instance.</param>
    /// <param name="factory">The strategy instance.</param>
    /// <returns>The same builder instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when this builder was already used to create a pipeline. The builder cannot be modified after it has been used.</exception>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(EmptyOptions))]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code",
        Justification = "All options members preserved for the empty options.")]
    public static ResiliencePipelineBuilderO<TResult> AddStrategy<TResult>(
        this ResiliencePipelineBuilderO<TResult> builder,
        Func<StrategyBuilderContext, ResilienceStrategy<TResult>> factory)
        => builder.AddStrategy(factory, EmptyOptions.Instance);

    internal sealed class EmptyOptions : ResilienceStrategyOptions
    {
        public static readonly EmptyOptions Instance = new();
    }
}
