using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerFactoryForwarderTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: created once each, in registration order, with the built factory")]
    public void Build_RegisteredForwarders_CreatesEachOnceInOrderWithTheFactory()
    {
        // Arrange
        var created = new List<RecordingForwarder>();
        var builder = new LoggerFactoryBuilder()
            .AddForwarder(factory => Track(created, new RecordingForwarder("first", factory)))
            .AddForwarder(factory => Track(created, new RecordingForwarder("second", factory)));

        // Act
        using var factory = builder.Build();

        // Assert
        created.Select(forwarder => forwarder.Name).ShouldBe(new[] { "first", "second" });
        created.ShouldAllBe(forwarder => ReferenceEquals(forwarder.Factory, factory));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: an entry written while the forwarder is created reaches the providers")]
    public void Build_ForwarderWritesDuringCreation_EntryReachesProviders()
    {
        // Arrange
        var provider = new RecordingProvider();
        var builder = new LoggerFactoryBuilder()
            .AddProvider(provider)
            .AddForwarder(factory =>
            {
                factory.Create("Forwarded").LogInformation("Forwarded", "raised during startup");
                return new RecordingForwarder("startup", factory);
            });

        // Act
        using var factory = builder.Build();

        // Assert
        provider.Entries.ShouldHaveSingleItem().Category.ShouldBe("Forwarded");
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: options populated directly are honored by the constructor")]
    public void Constructor_OptionsWithForwarders_CreatesForwarders()
    {
        // Arrange
        RecordingForwarder? created = null;
        var options = new LoggerFactoryOptions();
        options.Forwarders.Add(factory => created = new RecordingForwarder("direct", factory));

        // Act
        using var factory = new LoggerFactory(options);

        // Assert
        created.ShouldNotBeNull().Factory.ShouldBeSameAs(factory);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: disposed newest first, before the providers")]
    public void Dispose_Factory_DisposesForwardersNewestFirstBeforeProviders()
    {
        // Arrange
        var teardown = new List<string>();
        var factory = new LoggerFactoryBuilder()
            .AddProvider(new TeardownProvider("a", teardown))
            .AddProvider(new TeardownProvider("b", teardown))
            .AddForwarder(factory => new RecordingForwarder("first", factory, teardown))
            .AddForwarder(factory => new RecordingForwarder("second", factory, teardown))
            .Build();

        // Act
        factory.Dispose();

        // Assert
        teardown.ShouldBe(new[] { "forwarder:second", "forwarder:first", "provider:a", "provider:b" });
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: a forwarder that throws on dispose does not stop the rest of teardown")]
    public void Dispose_ForwarderThrows_DisposesRemainingForwardersAndProviders()
    {
        // Arrange
        var teardown = new List<string>();
        var factory = new LoggerFactoryBuilder()
            .AddProvider(new TeardownProvider("a", teardown))
            .AddForwarder(factory => new RecordingForwarder("first", factory, teardown))
            .AddForwarder(factory => new RecordingForwarder("faulty", factory, teardown, throwOnDispose: true))
            .Build();

        // Act
        Should.NotThrow(() => factory.Dispose());

        // Assert
        teardown.ShouldBe(new[] { "forwarder:faulty", "forwarder:first", "provider:a" });
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: disposing the factory twice disposes each forwarder once")]
    public void Dispose_CalledTwice_DisposesEachForwarderOnce()
    {
        // Arrange
        RecordingForwarder? forwarder = null;
        var factory = new LoggerFactoryBuilder()
            .AddForwarder(built => forwarder = new RecordingForwarder("only", built))
            .Build();

        // Act
        factory.Dispose();
        factory.Dispose();

        // Assert
        forwarder.ShouldNotBeNull().DisposeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: a throwing registration releases what was created and propagates")]
    public void Build_RegistrationThrows_DisposesCreatedForwardersAndProvidersAndRethrows()
    {
        // Arrange
        var teardown = new List<string>();
        var builder = new LoggerFactoryBuilder()
            .AddProvider(new TeardownProvider("a", teardown))
            .AddForwarder(factory => new RecordingForwarder("first", factory, teardown))
            .AddForwarder(factory => new RecordingForwarder("second", factory, teardown))
            .AddForwarder(_ => throw new InvalidOperationException("registration failed"));

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        exception.Message.ShouldBe("registration failed");
        teardown.ShouldBe(new[] { "forwarder:second", "forwarder:first", "provider:a" });
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: a factory whose construction failed reports itself disposed")]
    public void Build_RegistrationThrows_CapturedFactoryIsDisposed()
    {
        // Arrange
        ILoggerFactory? captured = null;
        var builder = new LoggerFactoryBuilder()
            .AddForwarder(factory => new RecordingForwarder("first", captured = factory))
            .AddForwarder(_ => throw new InvalidOperationException("registration failed"));

        // Act
        Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        Should.Throw<ObjectDisposedException>(() => captured.ShouldNotBeNull().Create("Late"));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: a registration that returns null fails the build")]
    public void Build_RegistrationReturnsNull_ThrowsInvalidOperationException()
    {
        // Arrange
        var teardown = new List<string>();
        var builder = new LoggerFactoryBuilder()
            .AddProvider(new TeardownProvider("a", teardown))
            .AddForwarder(_ => null!);

        // Act
        Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        teardown.ShouldBe(new[] { "provider:a" });
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: a null registration in the options fails construction")]
    public void Constructor_NullRegistration_ThrowsInvalidOperationException()
    {
        // Arrange
        var options = new LoggerFactoryOptions();
        options.Forwarders.Add(null!);

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => new LoggerFactory(options));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: AddForwarder rejects a null registration")]
    public void AddForwarder_Null_ThrowsArgumentNullException()
    {
        // Arrange
        var builder = new LoggerFactoryBuilder();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => builder.AddForwarder(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: AddForwarder after Build throws")]
    public void AddForwarder_AfterBuild_ThrowsInvalidOperationException()
    {
        // Arrange
        var builder = new LoggerFactoryBuilder();
        using var factory = builder.Build();

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => builder.AddForwarder(built => new RecordingForwarder("late", built)));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - Forwarders: a factory without forwarders still disposes its providers")]
    public void Dispose_NoForwarders_DisposesProviders()
    {
        // Arrange
        var teardown = new List<string>();
        var factory = new LoggerFactoryBuilder()
            .AddProvider(new TeardownProvider("a", teardown))
            .Build();

        // Act
        factory.Dispose();

        // Assert
        teardown.ShouldBe(new[] { "provider:a" });
    }

    private static RecordingForwarder Track(List<RecordingForwarder> created, RecordingForwarder forwarder)
    {
        created.Add(forwarder);
        return forwarder;
    }
}
