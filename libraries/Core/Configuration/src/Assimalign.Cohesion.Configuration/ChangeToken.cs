using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

public abstract class ChangeToken : IChangeToken
{
    public bool HasChanged => throw new NotImplementedException();

    public bool ActiveChangeCallbacks => throw new NotImplementedException();

    /// <summary>
    /// Registers the <paramref name="changeTokenConsumer"/> action to be called whenever the token produced changes.
    /// </summary>
    /// <param name="changeTokenProducer">Produces the change token.</param>
    /// <param name="changeTokenConsumer">Action called when the token changes.</param>
    /// <returns></returns>
    public static IDisposable OnChange(Func<IChangeToken> changeTokenProducer, Action changeTokenConsumer)
    {

        if (changeTokenProducer == null)
        {
            throw new ArgumentNullException(nameof(changeTokenProducer));
        }
        if (changeTokenConsumer == null)
        {
            throw new ArgumentNullException(nameof(changeTokenConsumer));
        }

        return new ChangeTokenRegistration<Action>(changeTokenProducer, callback => callback(), changeTokenConsumer);
    }

    /// <summary>
    /// Registers the <paramref name="changeTokenConsumer"/> action to be called whenever the token produced changes.
    /// </summary>
    /// <param name="changeTokenProducer">Produces the change token.</param>
    /// <param name="changeTokenConsumer">Action called when the token changes.</param>
    /// <param name="state">state for the consumer.</param>
    /// <returns></returns>
    public static IDisposable OnChange<TState>(Func<IChangeToken> changeTokenProducer, Action<TState> changeTokenConsumer, TState state)
    {
        if (changeTokenProducer == null)
        {
            throw new ArgumentNullException(nameof(changeTokenProducer));
        }
        if (changeTokenConsumer == null)
        {
            throw new ArgumentNullException(nameof(changeTokenConsumer));
        }

        return new ChangeTokenRegistration<TState>(changeTokenProducer, changeTokenConsumer, state);
    }

    public IDisposable OnChange(Action<object> callback, object state)
    {
        throw new NotImplementedException();
    }

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

            IDisposable registraton = token.RegisterChangeCallback(s => ((ChangeTokenRegistration<TState>)s).OnChangeTokenFired(), this);

            SetDisposable(registraton);
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
