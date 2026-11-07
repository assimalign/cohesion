using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;

DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);

// An opt-in test handshake retains the ordinary SDK entry point and declaration.
bool handshake = builder.Configuration["Sample:TestHandshake"] == "true";
using Socket? reservation = handshake ? new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) : null;
reservation?.Bind(new IPEndPoint(IPAddress.Loopback, 0));
int port = reservation is null ? 5740 : ((IPEndPoint)reservation.LocalEndPoint!).Port;

builder.AddSql("sample-sql", sql =>
{
    sql.Options.RootPath = builder.Configuration["Sample:DataPath"] ?? "data";
    sql.AddServer(server => server.Listen(Uri.CreateEndpoint("cohesion-db", "127.0.0.1", port)));

    // The engine's build provisions the database before application Build returns; the SDK reads
    // this declaration to emit obj/.../cohesion/database/sample.schema.json at build time.
    sql.AddDatabase("sample", database => database.Schema(schema =>
    {
        schema.Table<Order>("orders", table =>
        {
            table.Key(order => order.Id);
            table.Column(order => order.Item);
            table.Index(order => order.Item);
        });
    }));
});

await using DatabaseApplication application = builder.Build();
if (!handshake) { await application.RunAsync(); }
else
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    reservation!.Dispose();
    IDatabaseApplication host = application;
    await host.StartAsync(lifetime.Token);
    try
    {
        var engine = (SqlDatabaseEngine)application.Context.Engines.Single();
        var database = await engine.OpenDatabaseAsync("sample", lifetime.Token);
        Console.WriteLine(string.Join('|', "READY", port.ToString(CultureInfo.InvariantCulture),
            database.Provisioning?.FailureCode ?? string.Empty, database.Provisioning?.Detail ?? string.Empty));
        await Console.Out.FlushAsync(lifetime.Token);
        if (await Console.In.ReadLineAsync(lifetime.Token) != "STOP")
        { throw new InvalidOperationException("The SQL SDK fixture expected STOP."); }
    }
    finally
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StopAsync(shutdown.Token);
    }
    Console.WriteLine("STOPPED");
}

/// <summary>
/// Exposes the compiler-generated top-level entry point to in-process test factories.
/// </summary>
public partial class Program;
