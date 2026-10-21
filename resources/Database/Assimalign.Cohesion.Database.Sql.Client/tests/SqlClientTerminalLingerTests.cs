using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection(nameof(SqlClientEventSourceCollection))]
public sealed class SqlClientTerminalLingerTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - An unread pooled client releases stop and idle capacity after linger")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_UnreadPooledClient_ShouldReleaseSessionAfterLinger(bool shutdown)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var harness = await SqlClientTestHarness.StartAsync(configureServer: options =>
        {
            options.MaxSessions = 1;
            options.ShutdownDrainTimeout = TimeSpan.FromDays(1);
            options.TerminalDrainLinger = TimeSpan.FromMilliseconds(100);
            options.IdleTimeout = shutdown ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(100);
        });
        await using (var lease = await harness.Client.ConnectAsync(timeout.Token))
        {
            // Returning a healthy rental leaves the authenticated transport open in the pool.
        }
        harness.Server.Sessions.ShouldHaveSingleItem();

        // Act
        if (shutdown) { await harness.Server.StopAsync(timeout.Token).WaitAsync(timeout.Token); }
        else
        {
            while (harness.Server.Sessions.Count != 0) { await Task.Delay(1, timeout.Token); }
        }

        // Assert: the client and its idle transport remain undisposed throughout stop/eviction.
        timeout.IsCancellationRequested.ShouldBeFalse();
        harness.Server.Sessions.ShouldBeEmpty();
        if (!shutdown)
        {
            await using var replacement = SqlClient.Create(new()
            {
                Settings = new DatabaseConnectionSettings { Database = "app", EndPoint = harness.Listener.EndPoint },
                ConnectionFactory = ((Assimalign.Cohesion.Connections.InMemory.InMemoryConnectionListener)harness.Listener).CreateFactory(),
            });
            await using var lease = await replacement.ConnectAsync(timeout.Token);
            (await lease.QueryAsync("SELECT 1", cancellationToken: timeout.Token)).Count.ShouldBe(1);
        }
    }

}
