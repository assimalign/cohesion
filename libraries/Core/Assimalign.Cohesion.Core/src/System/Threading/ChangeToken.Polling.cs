using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.Threading;

using Assimalign.Cohesion.Internal;

public abstract class PollingChangeToken<T> : IChangeToken<T>, IDisposable
{
    private readonly Timer timer;
    private readonly List<IDisposable> subscribers;

    protected PollingChangeToken(TimeSpan startAfter, TimeSpan interval)
        : this(startAfter, interval, null)
    {
    }

    protected PollingChangeToken(TimeSpan startAfter, TimeSpan interval, object? state)
    {
        subscribers = new List<IDisposable>();
        timer = new Timer((state =>
        {
            if (HasChanged(state, out var data))
            {

            }
        }),
        state,
        startAfter,
        interval);
    }

    public abstract bool HasChanged(out T? state);
    public abstract IDisposable OnChange(Action<T> callback);

    public virtual bool HasChanged(object? data, out T? state)
    {
        return HasChanged(out state);
    }


    public virtual IDisposable OnChange(Action<object> callback)
    {
        if (callback is not Action<T>)
        {
            ThrowHelper.ThrowInvalidOperationException("");
        }
        return OnChange((callback as Action<T>)!);
    }


    public void Dispose()
    {
        timer.Dispose();
    }
}



public class TestPollingChangeToken : PollingChangeToken<string>
{
    public TestPollingChangeToken(TimeSpan startAfter, TimeSpan interval) : base(startAfter, interval)
    {
    }

    public override bool HasChanged(out string? state)
    {
        state = null;

        return true;

    }

    public override IDisposable OnChange(Action<string> callback)
    {
        throw new NotImplementedException();
    }
}

