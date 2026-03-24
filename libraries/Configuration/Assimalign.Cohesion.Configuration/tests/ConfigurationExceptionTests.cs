using System;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationExceptionTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Exception: NotFound has correct code")]
    public void Exception_NotFound_ShouldHaveCorrectCode()
    {
        var exception = ConfigurationException.NotFound;

        Assert.Equal(ConfigurationErrorCode.NotFound, exception.Code);
        Assert.NotEmpty(exception.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Exception: ThrowNotFound throws")]
    public void Exception_ThrowNotFound_ShouldThrow()
    {
        Assert.Throws<ConfigurationException>(() => ConfigurationException.ThrowNotFound());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Exception: Constructor with code and message")]
    public void Exception_Constructor_ShouldSetProperties()
    {
        var exception = new ConfigurationException(ConfigurationErrorCode.NotFound, "test message");

        Assert.Equal(ConfigurationErrorCode.NotFound, exception.Code);
        Assert.Equal("test message", exception.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Exception: Constructor with inner exception")]
    public void Exception_ConstructorWithInner_ShouldSetInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var exception = new ConfigurationException(ConfigurationErrorCode.NotFound, "outer", inner);

        Assert.Equal(ConfigurationErrorCode.NotFound, exception.Code);
        Assert.Equal("outer", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Exception: Inherits from CohesionException")]
    public void Exception_ShouldInheritFromCohesionException()
    {
        var exception = ConfigurationException.NotFound;

        Assert.IsAssignableFrom<Assimalign.Cohesion.CohesionException>(exception);
    }
}
