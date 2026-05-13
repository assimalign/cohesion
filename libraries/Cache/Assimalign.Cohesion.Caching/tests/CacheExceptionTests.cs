using System;

namespace Assimalign.Cohesion.Caching.Tests;

public class CacheExceptionTests
{
    [Fact(DisplayName = "Cohesion Test [Caching] - CacheException: ctor stores error code and message")]
    public void Ctor_StoresErrorCodeAndMessage()
    {
        var exception = new CacheException(CacheErrorCode.Disposed, "cache disposed");

        Assert.Equal(CacheErrorCode.Disposed, exception.ErrorCode);
        Assert.Equal("cache disposed", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact(DisplayName = "Cohesion Test [Caching] - CacheException: ctor preserves inner exception")]
    public void Ctor_PreservesInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var exception = new CacheException(CacheErrorCode.InvalidEntry, "outer", inner);

        Assert.Same(inner, exception.InnerException);
    }
}
