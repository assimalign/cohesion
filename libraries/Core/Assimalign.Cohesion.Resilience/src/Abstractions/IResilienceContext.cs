using System;
using System.Threading;

namespace Assimalign.Cohesion.Resilience;

public interface IResilienceContext
{
    /// <summary>
    /// Gets a key unique to the call site of the current execution.
    /// </summary>
    /// <remarks>
    /// Resilience context instances are commonly reused across multiple call sites.
    /// Set an <see cref="OperationKey"/> so that logging and metrics can distinguish usages of policy instances at different call sites.
    /// The operation key value should have a low cardinality (i.e. do not assign values such as <see cref="Guid"/> to this property).
    /// </remarks>
    /// <value>The default value is <see langword="null"/>.</value>
    OperationKey? OperationKey { get; }

    /// <summary>
    /// Gets the <see cref="CancellationToken"/> associated with the execution.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets a value indicating whether the execution should continue on the captured context.
    /// </summary>
    bool ContinueOnCapturedContext { get; }

    /// <summary>
    /// 
    /// </summary>
    IServiceProvider? ServiceProvider { get; }
}
