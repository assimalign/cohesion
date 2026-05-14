using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerFactoryCovariantReturnTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerFactory: Create returns LoggerBase covariantly")]
    public void Create_ReturnsLoggerBase()
    {
        var concreteFactory = (LoggerFactory)new LoggerFactoryBuilder()
            .AddProvider(new RecordingProvider())
            .Build();

        // Strongly typed call returns LoggerBase.
        LoggerBase typed = concreteFactory.Create("Cat");

        Assert.NotNull(typed);
        Assert.Equal("Cat", typed.Category);

        // Calling through ILoggerFactory returns ILogger but is the same instance.
        ILoggerFactory asInterface = concreteFactory;
        ILogger interfaceLogger = asInterface.Create("Cat");
        Assert.Same(typed, interfaceLogger);

        concreteFactory.Dispose();
    }
}
