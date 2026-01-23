using System;
using System.Threading;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public abstract class ResilienceContext : IResilienceContext
{
    private OperationKey? _operationKey;
    private CancellationToken _cancellationToken;
    private bool _continueOnCapturedContext;

    protected ResilienceContext()
    {
        
    }

    /// <inheritdoc />
    public virtual OperationKey? OperationKey => _operationKey;

    /// <inheritdoc />
    public virtual CancellationToken CancellationToken => _cancellationToken;

    /// <inheritdoc />
    public virtual bool ContinueOnCapturedContext => _continueOnCapturedContext;

    public IServiceProvider? ServiceProvider => throw new NotImplementedException();

    internal bool TryReset()
    {
        _operationKey = null;
        _cancellationToken = default(CancellationToken);
        _continueOnCapturedContext = false;

        return true;
    }
    internal bool TryInitialize(ResilienceContextCreationArguments args)
    {
        _operationKey = args.OperationKey;
        _cancellationToken = args.CancellationToken;
        _continueOnCapturedContext = args.ContinueOnCapturedContext ?? false;

        return true;
    }
}
