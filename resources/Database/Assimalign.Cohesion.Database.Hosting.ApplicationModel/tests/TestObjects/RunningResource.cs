using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.ApplicationModel.Tests;

internal sealed class RunningResource : IAsyncDisposable
{
    private readonly TemporaryDirectory _directory;

    private RunningResource(TemporaryDirectory directory, DatabaseApplication application, int port)
    {
        _directory = directory;
        Application = application;
        Port = port;
    }

    public DatabaseApplication Application { get; }
    public int Port { get; }

    public static RunningResource Build(TestGatewayClient? gateway, Action<DatabaseResourceOptions>? configure = null,
        Action<DatabaseApplicationBuilder>? configureApplication = null)
    {
        var directory = new TemporaryDirectory();
        try
        {
            int port = TestPorts.GetFree();
            DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
            ConfigureSql(builder, directory.Path, port);
            configureApplication?.Invoke(builder);
            builder.Services.AddResource("orders-db", resource =>
            {
                resource.Platform = gateway?.Platform;
                if (gateway is not null)
                {
                    resource.UseGatewayClient(gateway);
                }
                configure?.Invoke(resource);
            });
            return new RunningResource(directory, builder.Build(), port);
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    internal static void ConfigureSql(DatabaseApplicationBuilder builder, string path, int port)
    {
        builder.AddSql("orders-sql", sql =>
        {
            sql.Options.RootPath = path;
            sql.AddDatabase("orders", database => database.Schema(schema =>
                schema.Table<Order>("orders", table => table.Key(order => order.Id))));
            sql.AddServer(server => server.Listen(new Uri($"cohesion-db://127.0.0.1:{port}")));
        });
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => ((IHost)Application).StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => ((IHost)Application).StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await ((IAsyncDisposable)Application).DisposeAsync();
        }
        finally
        {
            _directory.Dispose();
        }
    }

    private sealed record Order(int Id);
}
