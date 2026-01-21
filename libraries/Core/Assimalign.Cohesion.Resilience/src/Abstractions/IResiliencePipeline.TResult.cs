using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Defines a contract for executing a resilience pipeline that applies fault-handling strategies to asynchronous
/// operations and returns a result of the specified type.
/// </summary>
/// <remarks>
/// Implementations of this interface allow callers to execute operations with built-in resilience
/// mechanisms, such as retries, timeouts, or circuit breakers. The pipeline can be customized to handle transient
/// faults and improve reliability in distributed systems.
/// </remarks>
/// <typeparam name="TResult">The type of result produced by the pipeline after executing the operation.</typeparam>
public interface IResiliencePipeline<TResult>
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    /// <param name="callback"></param>
    /// <param name="context"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    ValueTask<TResult> ExecuteAsync<TState>(
        ResiliencePipelineCallback<TResult, TState> callback,
        IResilienceContext context,
        TState state);
}
