using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion;

public abstract class ChangeToken : IChangeToken
{
    public abstract bool HasChanged { get; }
    public abstract bool ActiveChangeCallbacks { get; }
    public abstract IDisposable OnChange(Action<object> callback, object state);

    private sealed class ChangeTokenRegistration<TState> : IDisposable
    {
        private readonly Func<IChangeToken> changeTokenProducer;
        private readonly Action<TState> changeTokenConsumer;
        private readonly TState state;
        private IDisposable disposable;

        private static readonly NoopDisposable disposedSentinel = new NoopDisposable();

        public ChangeTokenRegistration(Func<IChangeToken> changeTokenProducer, Action<TState> changeTokenConsumer, TState state)
        {
            this.changeTokenProducer = changeTokenProducer;
            this.changeTokenConsumer = changeTokenConsumer;
            this.state = state;

            var token = changeTokenProducer();

            RegisterChangeTokenCallback(token);
        }

        private void OnChangeTokenFired()
        {
            // The order here is important. We need to take the token and then apply our changes BEFORE
            // registering. This prevents us from possible having two change updates to process concurrently.
            //
            // If the token changes after we take the token, then we'll process the update immediately upon
            // registering the callback.
            IChangeToken token = changeTokenProducer();

            try
            {
                changeTokenConsumer(state);
            }
            finally
            {
                // We always want to ensure the callback is registered
                RegisterChangeTokenCallback(token);
            }
        }

        private void RegisterChangeTokenCallback(IChangeToken token)
        {
            if (token is null)
            {
                return;
            }

            IDisposable registration = token.OnChange(s => ((ChangeTokenRegistration<TState>)s).OnChangeTokenFired(), this);

            SetDisposable(registration);
        }

        private void SetDisposable(IDisposable disposable)
        {
            // We don't want to transition from _disposedSentinel => anything since it's terminal
            // but we want to allow going from previously assigned disposable, to another
            // disposable.
            var current = Volatile.Read(ref this.disposable);

            // If Dispose was called, then immediately dispose the disposable
            if (current == disposedSentinel)
            {
                disposable.Dispose();
                return;
            }

            // Otherwise, try to update the disposable
            var previous = Interlocked.CompareExchange(ref this.disposable, disposable, current);

            if (previous == disposedSentinel)
            {
                // The subscription was disposed so we dispose immediately and return
                disposable.Dispose();
            }
            else if (previous == current)
            {
                // We successfuly assigned the _disposable field to disposable
            }
            else
            {
                // Sets can never overlap with other SetDisposable calls so we should never get into this situation
                throw new InvalidOperationException("Somebody else set the _disposable field");
            }
        }

        public void Dispose()
        {
            // If the previous value is disposable then dispose it, otherwise,
            // now we've set the disposed sentinel
            Interlocked.Exchange(ref disposable, disposedSentinel).Dispose();
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
