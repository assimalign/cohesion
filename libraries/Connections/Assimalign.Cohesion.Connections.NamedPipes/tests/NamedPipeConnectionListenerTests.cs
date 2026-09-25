using System;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.NamedPipes.Tests;

public class NamedPipeConnectionListenerTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - BindAsync: Should reserve the pipe before accepting")]
    public async Task BindAsync_BeforeAccept_ShouldReservePipeAndAcceptConnection()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        NamedPipeEndPoint endPoint = new(NamedPipeTestName.Create());
        await using NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = endPoint);

        // Act
        await listener.BindAsync(cancellation.Token);
        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        NamedPipeConnectionFactory factory = new();
        await using Connection client = await factory.ConnectAsync(endPoint, cancellation.Token);
        await using Connection server = await acceptTask;

        // Assert
        server.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - BindAsync: Dispose should release the pipe for a new listener")]
    public async Task BindAsync_AfterDisposedListenerOnSameEndPoint_ShouldSucceed()
    {
        // Arrange
        NamedPipeEndPoint endPoint = new(NamedPipeTestName.Create());
        NamedPipeConnectionListener first = NamedPipeConnectionListener.Create(
            options => options.EndPoint = endPoint);
        await first.BindAsync();

        // Act
        await first.DisposeAsync();

        await using NamedPipeConnectionListener second = NamedPipeConnectionListener.Create(
            options => options.EndPoint = endPoint);
        await second.BindAsync();

        // Assert
        second.EndPoint.ShouldBe(endPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - BindAsync: Disposed listener should reject bind and accept")]
    public async Task BindAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = new NamedPipeEndPoint(NamedPipeTestName.Create()));
        await listener.DisposeAsync();

        // Act / Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.BindAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.AcceptAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Listener: Should reject options without an endpoint")]
    public void Constructor_WithoutEndPoint_ShouldThrowArgumentException()
    {
        // Act / Assert
        Should.Throw<ArgumentException>(() => new NamedPipeConnectionListener(new NamedPipeConnectionListenerOptions()));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Listener: Should reject a remote-host endpoint")]
    public void Constructor_WithRemoteEndPoint_ShouldThrowArgumentException()
    {
        // Arrange
        NamedPipeConnectionListenerOptions options = new()
        {
            EndPoint = new NamedPipeEndPoint("orders", "remote-host")
        };

        // Act / Assert
        Should.Throw<ArgumentException>(() => new NamedPipeConnectionListener(options));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Listener: Should reject a null configure delegate")]
    public void Create_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => NamedPipeConnectionListener.Create(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Listener: Should describe a reliable, ordered named-pipe stream")]
    public void Capabilities_OnListener_ShouldDescribeReliableOrderedNamedPipeStream()
    {
        // Arrange
        NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = new NamedPipeEndPoint(NamedPipeTestName.Create()));

        // Act
        ConnectionCapabilities capabilities = listener.Capabilities;

        // Assert
        capabilities.ShouldBe(new ConnectionCapabilities(
            ConnectionProtocol.NamedPipe,
            ConnectionDelivery.Stream,
            IsReliable: true,
            IsOrdered: true,
            IsMultiplexed: false,
            ConnectionSecurity.None));

        listener.EndPoint.ShouldBeOfType<NamedPipeEndPoint>();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Listener: Should throw OperationCanceledException when the accept is canceled")]
    public async Task AcceptAsync_WhenCanceled_ShouldThrowOperationCanceledException()
    {
        // Arrange
        using CancellationTokenSource acceptCancellation = new();

        await using NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = new NamedPipeEndPoint(NamedPipeTestName.Create()));

        ValueTask<Connection> acceptTask = listener.AcceptAsync(acceptCancellation.Token);

        // Act
        acceptCancellation.Cancel();

        Exception? exception = await Record.ExceptionAsync(async () => await acceptTask);

        // Assert
        exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Listener: Should unblock a pending accept when disposed")]
    public async Task DisposeAsync_WithPendingAccept_ShouldUnblockAccept()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);

        NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = new NamedPipeEndPoint(NamedPipeTestName.Create()));

        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        // Act
        await listener.DisposeAsync();

        Exception? exception = await Record.ExceptionAsync(async () => await acceptTask);

        // Assert
        exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Listener: Should be idempotent on repeated dispose")]
    public async Task DisposeAsync_CalledTwice_ShouldBeIdempotent()
    {
        // Arrange
        NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(
            options => options.EndPoint = new NamedPipeEndPoint(NamedPipeTestName.Create()));

        // Act
        await listener.DisposeAsync();

        Exception? exception = await Record.ExceptionAsync(async () => await listener.DisposeAsync());

        // Assert
        exception.ShouldBeNull();
    }

    [SupportedOSPlatform("windows")]
    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - Listener: Should honor a Windows ACL that grants the current user")]
    public async Task AcceptAsync_WithPipeSecurityGrantingCurrentUser_ShouldAcceptConnection()
    {
        // The ACL surface is Windows-only; skip on other platforms rather than fail.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        NamedPipeEndPoint endPoint = new(NamedPipeTestName.Create());

        PipeSecurity security = new();
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        await using NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(options =>
        {
            options.EndPoint = endPoint;
            options.PipeSecurity = security;
        });

        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        NamedPipeConnectionFactory factory = new();

        // Act
        await using Connection client = await factory.ConnectAsync(endPoint, cancellation.Token);
        await using Connection server = await acceptTask;

        // Assert
        server.ShouldNotBeNull();
        server.Capabilities.Protocol.ShouldBe(ConnectionProtocol.NamedPipe);
    }
}
