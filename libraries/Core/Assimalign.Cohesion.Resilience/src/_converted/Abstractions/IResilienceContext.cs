using System;
using System.Collections.Generic;
using System.Text;
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
    string? OperationKey { get; }

    /// <summary>
    /// Gets the <see cref="CancellationToken"/> associated with the execution.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Gets a value indicating whether the execution should continue on the captured context.
    /// </summary>
    bool ContinueOnCapturedContext { get; }

    /// <summary>
    /// Gets the custom properties attached to the context.
    /// </summary>
   // ResilienceProperties Properties { get; }

    /// <summary>
    /// Gets a value indicating whether the execution is synchronous.
    /// </summary>
    //internal bool IsSynchronous { get; }

    /// <summary>
    /// Gets the type of the result associated with the execution.
    /// </summary>
    //internal Type ResultType { get; } = typeof(UnknownResult);

    /// <summary>
    /// Gets a value indicating whether the execution represents a void result.
    /// </summary>
    //internal bool IsVoid => ResultType == typeof(VoidResult);

    /// <summary>
    /// Gets a value indicating whether the context is initialized.
    /// </summary>
    //internal bool IsInitialized => ResultType != typeof(UnknownResult);


}
