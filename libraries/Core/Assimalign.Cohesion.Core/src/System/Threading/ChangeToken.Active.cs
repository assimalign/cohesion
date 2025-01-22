using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace System.Threading;

public abstract class ActiveChangeToken 
{

    protected ActiveChangeToken()
    {
        var token = default(ChangeToken<string>);

        using var tracker = token.OnChange(state =>
        {

        });
    }
}



public abstract class ChangeToken<T> : IChangeToken<T>
{
    private readonly List<IDisposable> subscribers;

    protected ChangeToken()
    {
        subscribers = new List<IDisposable>();
    }


    public abstract IDisposable OnChange(Action<T> callback);
    
    
    IDisposable IChangeToken.OnChange(Action<object> callback)
    {
        return OnChange((callback as Action<T>)!);
    }
}