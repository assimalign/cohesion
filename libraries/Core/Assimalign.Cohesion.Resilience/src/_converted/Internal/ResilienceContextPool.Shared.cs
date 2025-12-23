using System;

namespace Assimalign.Cohesion.Resilience;

using Assimalign.Cohesion.ObjectPool;

public abstract partial class ResilienceContextPool
{
    private const bool ContinueOnCapturedContextDefault = false;

    private sealed class SharedPool : ResilienceContextPool
    {
        private readonly ObjectPool<ResilienceContext> _pool = ObjectPool<ResilienceContext>.Create(new ResilienceContextPoolPolicy());

        public override ResilienceContext Rent(ResilienceContextCreationArguments arguments)
        {
            var context = _pool.Rent();

            context.OperationKey = arguments.OperationKey;
            context.CancellationToken = arguments.CancellationToken;
            context.ContinueOnCapturedContext = arguments.ContinueOnCapturedContext ?? ContinueOnCapturedContextDefault;

            return context;
        }

        public override void Return(ResilienceContext context) =>
            _pool.Return(ArgumentNullException.ThrowIfNull<ResilienceContext>(context));
    }

    private class ResilienceContextPoolPolicy : ObjectPoolPolicy<ResilienceContext>
    {
        public override ResilienceContext Create() => new ResilienceContext();
        public override bool Return(ResilienceContext obj)
        {
            obj.Reset();
            return true;
        }
    }
}
