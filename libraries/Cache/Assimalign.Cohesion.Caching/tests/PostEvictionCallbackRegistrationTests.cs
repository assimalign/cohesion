using System;

namespace Assimalign.Cohesion.Caching.Tests;

public class PostEvictionCallbackRegistrationTests
{
    [Fact(DisplayName = "Cohesion Test [Caching] - PostEvictionCallbackRegistration: ctor stores callback and state")]
    public void Ctor_StoresCallbackAndState()
    {
        PostEvictionDelegate callback = static (_, _, _, _) => { };
        object state = "tag";

        var registration = new PostEvictionCallbackRegistration(callback, state);

        Assert.Same(callback, registration.EvictionCallback);
        Assert.Same(state, registration.State);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - PostEvictionCallbackRegistration: state is optional")]
    public void Ctor_StateIsOptional()
    {
        PostEvictionDelegate callback = static (_, _, _, _) => { };

        var registration = new PostEvictionCallbackRegistration(callback);

        Assert.Same(callback, registration.EvictionCallback);
        Assert.Null(registration.State);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - PostEvictionCallbackRegistration: throws when callback is null")]
    public void Ctor_NullCallback_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PostEvictionCallbackRegistration(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - PostEvictionCallbackRegistration: callback receives provided arguments")]
    public void Callback_ReceivesProvidedArguments()
    {
        object? capturedKey = null;
        object? capturedValue = null;
        CacheEvictionReason capturedReason = CacheEvictionReason.None;
        object? capturedState = null;

        PostEvictionDelegate callback = (key, value, reason, state) =>
        {
            capturedKey = key;
            capturedValue = value;
            capturedReason = reason;
            capturedState = state;
        };

        var registration = new PostEvictionCallbackRegistration(callback, "ctx");
        registration.EvictionCallback("key", 42, CacheEvictionReason.Expired, registration.State);

        Assert.Equal("key", capturedKey);
        Assert.Equal(42, capturedValue);
        Assert.Equal(CacheEvictionReason.Expired, capturedReason);
        Assert.Equal("ctx", capturedState);
    }
}
