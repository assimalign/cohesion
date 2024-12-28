using System;

namespace Assimalign.Cohesion;

using Internal;

public static class ChangeTokenExtensions
{
    public static IDisposable OnChange<TState>(this IChangeToken token, Action<TState?> callback, TState? state)
    {
        if (token is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(token));
        }
        if (callback is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(callback));
        }
        return token.OnChange(callback, state);
    }
}
