using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Security.Tests;

/// <summary>
/// Tests for the authenticator base (#1258): what <see cref="DatabaseAuthenticator.AuthenticateAsync"/>
/// checks before a derived authenticator's core runs, and the built-in
/// <see cref="DatabaseAuthenticator.AllowAll"/>.
/// </summary>
public class DatabaseAuthenticatorTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Security] - Authenticator: AllowAll accepts any principal and evidence")]
    public async Task AllowAll_AnyPrincipal_ShouldAuthenticate()
    {
        // Arrange
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        bool authenticated = await DatabaseAuthenticator.AllowAll.AuthenticateAsync("appdb", "svc-user", new byte[] { 1, 2, 3 }, source.Token);

        // Assert
        authenticated.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Security] - Authenticator: the core receives the caller's arguments and returns the result")]
    public async Task AuthenticateAsync_ValidArguments_ShouldForwardToTheCore()
    {
        // Arrange
        var authenticator = new RecordingAuthenticator(result: false);
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        bool authenticated = await authenticator.AuthenticateAsync("appdb", "svc-user", new byte[] { 7, 8 }, source.Token);

        // Assert
        authenticated.ShouldBeFalse();
        authenticator.Calls.ShouldBe(1);
        authenticator.Database.ShouldBe("appdb");
        authenticator.Principal.ShouldBe("svc-user");
        authenticator.Evidence.ShouldBe(new byte[] { 7, 8 });
        authenticator.Token.ShouldBe(source.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Security] - Authenticator: a canceled token throws before the core runs")]
    public async Task AuthenticateAsync_CanceledToken_ShouldThrowWithoutCallingTheCore()
    {
        // Arrange
        var authenticator = new RecordingAuthenticator(result: true);
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await authenticator.AuthenticateAsync("appdb", "svc-user", ReadOnlyMemory<byte>.Empty, source.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await DatabaseAuthenticator.AllowAll.AuthenticateAsync("appdb", "svc-user", ReadOnlyMemory<byte>.Empty, source.Token));
        authenticator.Calls.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Security] - Authenticator: a null database or principal is rejected before the core runs")]
    [InlineData(null, "svc-user", "database")]
    [InlineData("appdb", null, "principal")]
    public async Task AuthenticateAsync_NullDatabaseOrPrincipal_ShouldThrowWithoutCallingTheCore(string? database, string? principal, string parameter)
    {
        // Arrange
        var authenticator = new RecordingAuthenticator(result: true);

        // Act
        var exception = await Should.ThrowAsync<ArgumentNullException>(async () =>
            await authenticator.AuthenticateAsync(database!, principal!, ReadOnlyMemory<byte>.Empty, CancellationToken.None));

        // Assert
        exception.ParamName.ShouldBe(parameter);
        authenticator.Calls.ShouldBe(0);
    }
}
