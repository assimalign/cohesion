using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Execution;

/// <summary>
/// Composes an <see cref="IQueryPipeline"/> from ordered stages and a terminal
/// executor delegate.
/// </summary>
/// <remarks>
/// Model engines build one pipeline per database (or per session) and reuse it for
/// every execution: <c>new QueryPipelineBuilder().Use(stage).Build(terminal)</c>.
/// The built pipeline enforces the transaction boundary semantics documented on
/// <see cref="IQueryPipeline"/> around the whole stage chain, so stages observe a
/// still-active scope.
/// </remarks>
public sealed class QueryPipelineBuilder
{
    private readonly List<IQueryPipelineStage> _stages = new();

    /// <summary>
    /// Appends a stage to the pipeline. Stages execute in registration order.
    /// </summary>
    /// <param name="stage">The stage to append.</param>
    /// <returns>This builder.</returns>
    public QueryPipelineBuilder Use(IQueryPipelineStage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        _stages.Add(stage);
        return this;
    }

    /// <summary>
    /// Builds the pipeline around the terminal executor.
    /// </summary>
    /// <param name="terminal">The delegate that performs the actual execution after every stage.</param>
    /// <returns>The composed pipeline.</returns>
    public IQueryPipeline Build(QueryPipelineDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        var chain = terminal;

        for (int i = _stages.Count - 1; i >= 0; i--)
        {
            var stage = _stages[i];
            var next = chain;
            chain = (context, cancellationToken) => stage.ExecuteAsync(context, next, cancellationToken);
        }

        return new BuiltQueryPipeline(chain);
    }
}
