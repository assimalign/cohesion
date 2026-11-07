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
using Socket? reservation = handshake ? ReserveTestPort() : null;
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

// Match Hosting.ApplicationModel/tests/TestObjects/TestPorts.cs: bind(0) and outgoing
// connections cannot claim ports below the platform ephemeral ranges. Retain the reservation
// through Build, and retry occupied or Windows-reserved ports before selecting a candidate.
static Socket ReserveTestPort()
{
    const int firstPort = 20000;
    const int portCount = 12000;
    for (int attempt = 0; ; attempt++)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        int candidate = firstPort + (Environment.ProcessId % portCount + attempt) % portCount;
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, candidate));
            return socket;
        }
        catch (SocketException exception) when (attempt < portCount - 1 &&
            exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        { socket.Dispose(); }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Exposes the compiler-generated top-level entry point to in-process test factories.
/// </summary>
public partial class Program;
