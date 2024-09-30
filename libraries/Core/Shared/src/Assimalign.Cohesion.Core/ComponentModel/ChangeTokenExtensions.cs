using System;

namespace Assimalign.Cohesion;

public static class ChangeTokenExtensions
{
    public static IDisposable OnChange<TState>(this IChangeToken token, Action<TState> callback, TState state)
    {
        return token.OnChange(callback, state);
    }
}
