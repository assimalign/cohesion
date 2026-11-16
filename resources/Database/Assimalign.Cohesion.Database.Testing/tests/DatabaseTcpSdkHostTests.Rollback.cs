using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Testing.Tests;

public sealed partial class DatabaseTcpSdkHostTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Testing] - SDK rollback: the older executable serves TCP at the stored schema and retains newer data")]
    public async Task Start_OlderSqlExecutable_ShouldHoldAndServeStoredSchema()
    {
        // Arrange: the actual SDK executable establishes its own exact declaration/hash.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string path = Path.Combine(Path.GetTempPath(), "cohesion-sdk-rollback", Guid.NewGuid().ToString("N"));
        try
        {
            await RunRollbackSampleAsync(path, held: false, timeout.Token);
            var original = ReadSampleSchema(path);
            var seed = SqlDatabaseEngine.CreateBuilder("seed");
            seed.Options.RootPath = path;
            await using (var engine = await seed.BuildAsync(timeout.Token))
            {
                var database = await engine.OpenDatabaseAsync("sample", timeout.Token);
                var tables = original.Tables.Select(table => new CompiledSchemaTable(table.Name, table.RowType,
                    [.. table.Columns, new CompiledSchemaColumn("newer", DatabaseType.Int32, false, DefaultLiteral: "7")],
                    table.PrimaryKey, table.Indexes, table.Constraints)).ToArray();
                var newer = new SqlCompiledSchema(original.Format, original.Name, true, original.Types, tables, original.Principals);
                await database.ApplySchemaAsync(newer, timeout.Token);
                await using var session = await database.CreateSessionAsync(timeout.Token);
                await session.ExecuteAsync("INSERT INTO orders (Id, Item, newer) VALUES (1, 'preserved', 42)", cancellationToken: timeout.Token);
            }

            // Act / Assert: the old executable completes Build/Start and accepts wire work.
            await RunRollbackSampleAsync(path, held: true, timeout.Token);
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); } }
    }

    private static SqlCompiledSchema ReadSampleSchema(string path)
    {
        // The fixture is stopped. Read its public catalog through read-only streams,
        // without reaching SqlDatabase's internal engine/catalog wiring.
        string directory = Path.Combine(path, "sample");
        using var data = new FileStream(Path.Combine(directory, "catalog.dat"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var journal = new FileStream(Path.Combine(directory, "catalog.log"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var backup = new FileStream(Path.Combine(directory, "catalog.bak"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var storage = SqlStorage.Open(data, journal, backup, checkpointOnOpen: false);
        return SqlCompiledSchemaSerializer.Deserialize(SqlCatalog.Open(storage).SchemaState!.CanonicalDocument);
    }

    private static async Task RunRollbackSampleAsync(string path, bool held, CancellationToken token)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "SampleHost");
        string executable = Path.Combine(directory, "Assimalign.Cohesion.Database.SampleHost" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        File.Exists(executable).ShouldBeTrue("The packed-SDK SampleHost must be built and copied before acceptance tests.");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("--Sample:DataPath=" + path);
        start.ArgumentList.Add("--Sample:TestHandshake=true");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the SQL SDK consumer.");
        var stderr = process.StandardError.ReadToEndAsync();
        Task<string>? output = null;
        try
        {
            string ready = await ReadReadyAsync(process.StandardOutput, stderr, token);
            string[] fields = ready.Split('|', 4);
            fields.Length.ShouldBe(4);
            fields[2].ShouldBe(held ? "CS03405" : string.Empty);
            output = process.StandardOutput.ReadToEndAsync();
            if (held)
            {
                fields[3].ShouldContain("DropColumn 'orders.newer'", Case.Sensitive);
                int port = int.Parse(fields[1], CultureInfo.InvariantCulture);
                await using var client = SqlClient.Create(new SqlClientOptions
                {
                    Settings = DatabaseConnectionSettings.For(Uri.CreateEndpoint("cohesion-db", "127.0.0.1", port), "sample", "rollback"),
                    ConnectionFactory = new TcpConnectionFactory(),
                });
                await using var connection = await client.ConnectAsync(token);
                (await connection.QueryAsync("SELECT newer FROM orders WHERE Id=1", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(42);
                await connection.ExecuteAsync("INSERT INTO orders (Id, Item) VALUES (2, 'old application')", cancellationToken: token);
                (await connection.QueryAsync("SELECT newer FROM orders WHERE Id=2", cancellationToken: token)).ShouldHaveSingleItem()[0].ShouldBe(7);
            }
            await process.StandardInput.WriteLineAsync("STOP".AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
            await process.WaitForExitAsync(token);
            string tail = await output.WaitAsync(token);
            process.ExitCode.ShouldBe(0, await stderr.WaitAsync(token));
            tail.ShouldContain("STOPPED", Case.Sensitive);
        }
        finally
        {
            await StopProcessAsync(process);
            _ = await stderr;
            if (output is not null) { _ = await output; }
        }
    }
}
