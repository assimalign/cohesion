using System;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Transports.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Transports.Tests;

public class HttpConnectionListenerOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: Should default backlog capacity to 512")]
    public void BacklogCapacity_OnCreate_ShouldDefaultTo512()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Assert
        options.BacklogCapacity.ShouldBe(512);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: Should reject backlog capacity less than one")]
    [InlineData(0)]
    [InlineData(-1)]
    public void BacklogCapacity_OnNonPositiveValue_ShouldThrowArgumentOutOfRangeException(int value)
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act + Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.BacklogCapacity = value);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: Should accept a positive backlog capacity")]
    public void BacklogCapacity_OnPositiveValue_ShouldAcceptValue()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act
        options.BacklogCapacity = 1;

        // Assert
        options.BacklogCapacity.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: UseHttp1 should reject a null listener")]
    public void UseHttp1_OnNullListener_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act + Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp1((IConnectionListener)null!));
        Should.Throw<ArgumentNullException>(() => options.UseHttp1((Func<IConnectionListener>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: UseHttp2 should reject a null listener")]
    public void UseHttp2_OnNullListener_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act + Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp2((IConnectionListener)null!));
        Should.Throw<ArgumentNullException>(() => options.UseHttp2((Func<IConnectionListener>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: UseHttp3 should reject a null listener")]
    public void UseHttp3_OnNullListener_ShouldThrowArgumentNullException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();

        // Act + Assert
        Should.Throw<ArgumentNullException>(() => options.UseHttp3((IMultiplexedConnectionListener)null!));
        Should.Throw<ArgumentNullException>(() => options.UseHttp3((Func<IMultiplexedConnectionListener>)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: UseHttp1 should reject a datagram-delivery listener")]
    public void UseHttp1_OnDatagramDeliveryListener_ShouldThrowArgumentException()
    {
        // Arrange — HTTP/1.1 requires a byte stream; a datagram transport
        // (e.g. raw UDP) cannot carry it.
        HttpConnectionListenerOptions options = new();
        TestConnectionListener listener = new(TestConnection.DefaultCapabilities with
        {
            Delivery = ConnectionDelivery.Datagram
        });

        // Act + Assert
        Should.Throw<ArgumentException>(() => options.UseHttp1(listener));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: UseHttp1 should reject an unreliable listener")]
    public void UseHttp1_OnUnreliableListener_ShouldThrowArgumentException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        TestConnectionListener listener = new(TestConnection.DefaultCapabilities with
        {
            IsReliable = false
        });

        // Act + Assert
        Should.Throw<ArgumentException>(() => options.UseHttp1(listener));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: UseHttp1 should reject an unordered listener")]
    public void UseHttp1_OnUnorderedListener_ShouldThrowArgumentException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        TestConnectionListener listener = new(TestConnection.DefaultCapabilities with
        {
            IsOrdered = false
        });

        // Act + Assert
        Should.Throw<ArgumentException>(() => options.UseHttp1(listener));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: UseHttp2 should reject a listener that is not a reliable ordered byte stream")]
    public void UseHttp2_OnNonStreamCapableListener_ShouldThrowArgumentException()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        TestConnectionListener listener = new(TestConnection.DefaultCapabilities with
        {
            Delivery = ConnectionDelivery.Datagram,
            IsReliable = false,
            IsOrdered = false
        });

        // Act + Assert
        ArgumentException exception = Should.Throw<ArgumentException>(() => options.UseHttp2(listener));
        exception.Message.ShouldContain("HTTP/2");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: UseHttp1 and UseHttp2 should accept a reliable ordered stream listener")]
    public void UseHttp_OnStreamCapableListener_ShouldRegisterWithoutThrowing()
    {
        // Arrange — the gate is capability-based, never protocol-identity
        // based, so a TLS-layered listener (Security = Tls) passes too.
        HttpConnectionListenerOptions options = new();
        TestConnectionListener plain = new();
        TestConnectionListener secured = new(TestConnection.DefaultCapabilities with
        {
            Security = ConnectionSecurity.Tls
        });

        // Act
        HttpConnectionListenerOptions result = options
            .UseHttp1(plain)
            .UseHttp2(secured);

        // Assert — fluent chaining returns the same options instance.
        result.ShouldBeSameAs(options);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: Factory registrations should be capability-validated when the listener is constructed")]
    public void UseHttp1_OnFactoryReturningNonStreamListener_ShouldThrowWhenListenerIsConstructed()
    {
        // Arrange — a factory cannot be inspected at registration time; the
        // capability gate runs when HttpConnectionListener materializes it.
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(() => new TestConnectionListener(TestConnection.DefaultCapabilities with
        {
            Delivery = ConnectionDelivery.Datagram
        }));

        // Act + Assert
        Should.Throw<ArgumentException>(() => new HttpConnectionListener(options));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Transports] - HttpConnectionListenerOptions: A factory returning null should fail listener construction")]
    public void UseHttp1_OnFactoryReturningNull_ShouldThrowWhenListenerIsConstructed()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.UseHttp1(() => null!);

        // Act + Assert
        Should.Throw<InvalidOperationException>(() => new HttpConnectionListener(options));
    }
}
