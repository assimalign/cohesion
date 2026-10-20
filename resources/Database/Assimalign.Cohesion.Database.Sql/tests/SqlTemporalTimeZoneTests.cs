using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>Exercises plain-string storage under two actual TimeZoneInfo.Local values (#1648).</summary>
[Collection(SqlTimingCollection.Name)]
public sealed class SqlTemporalTimeZoneTests
{
    private const string zoneVariable = "COHESION_TEMPORAL_TEST_ZONE";
    private readonly ITestOutputHelper _output;

    public SqlTemporalTimeZoneTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [UnixTimeZoneFact(DisplayName = "Cohesion Test [SqlEngine] - Temporal storage: plain strings are independent of the server zone in two child processes (#1648)")]
    public async Task Insert_PlainTemporalStrings_ShouldBeIndependentOfServerTimeZone()
    {
        // Arrange: child processes isolate the runtime's global zone cache from the test host.
        string? childZone = Environment.GetEnvironmentVariable(zoneVariable);
        if (childZone is not null)
        {
            await VerifyStorageAsync(int.Parse(childZone, CultureInfo.InvariantCulture));
            return;
        }

        // Act / Assert: one negative and one positive non-UTC server offset.
        foreach (int hours in new[] { -7, 9 })
        {
            await VerifyChildAsync(hours);
        }
    }

    private async Task VerifyChildAsync(int hours)
    {
        string results = Path.Combine(Path.GetTempPath(), "cohesion-temporal-zone", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(results);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Database.Sql.Tests.dll"));
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=" + typeof(SqlTemporalTimeZoneTests).FullName
            + "." + nameof(Insert_PlainTemporalStrings_ShouldBeIndependentOfServerTimeZone));
        start.ArgumentList.Add("--Logger:trx;LogFileName=zone.trx");
        start.ArgumentList.Add("--ResultsDirectory:" + results);
        LocalTimeZone.Set(start, hours);
        start.Environment[zoneVariable] = hours.ToString(CultureInfo.InvariantCulture);
        start.Environment["DOTNET_TieredCompilation"] = "0";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = new Process { StartInfo = start };
        bool started = false;
        try
        {
            started = process.Start();
            started.ShouldBeTrue();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string output = await stdout + await stderr;
            _output.WriteLine($"UTC{hours:+0;-0}: {output}");
            process.ExitCode.ShouldBe(0, output);
            var counters = XDocument.Load(Path.Combine(results, "zone.trx")).Descendants()
                .Single(element => element.Name.LocalName == "Counters");
            counters.Attribute("executed")!.Value.ShouldBe("1");
            counters.Attribute("passed")!.Value.ShouldBe("1");
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            Directory.Delete(results, recursive: true);
        }
    }

    private static async Task VerifyStorageAsync(int hours)
    {
        var offset = TimeSpan.FromHours(hours);
        string zoneId = LocalTimeZone.GetId(hours);

        // Sentinel: prove ordinary .NET parsing really sees this zone in the child.
        TimeZoneInfo.Local.Id.ShouldBe(zoneId);
        TimeZoneInfo.Local.BaseUtcOffset.ShouldBe(offset);
        DateTimeOffset.Parse("2026-01-02 03:04:05", CultureInfo.InvariantCulture).Offset.ShouldBe(offset);
        await using var engine = SqlDatabaseEngine.Create("temporal-zone", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE times (id INT, ts TIMESTAMP, tz TIMESTAMPTZ)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO times VALUES "
            + "(1, '2026-01-02T03:04:05+05:00', '2026-01-02 03:04:05'), "
            + "(2, '2026-07-02T03:04:05-04:00', '2026-07-02 03:04:05')", cancellationToken: CancellationToken.None);

        // Read the stored values through SQL; DateTime equality alone does not check Kind.
        await using var rows = (await session.ExecuteAsync("SELECT id, ts, tz FROM times ORDER BY id", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        int count = 0;
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
        {
            int month = ++count == 1 ? 1 : 7;
            var clock = new DateTime(2026, month, 2, 3, 4, 5, DateTimeKind.Unspecified);
            row.GetValue(1).ShouldBeOfType<DateTime>().ShouldBe(clock);
            row.GetValue(1).ShouldBeOfType<DateTime>().Kind.ShouldBe(DateTimeKind.Unspecified);
            var instant = row.GetValue(2).ShouldBeOfType<DateTimeOffset>();
            instant.Offset.ShouldBe(TimeSpan.Zero);
            instant.ShouldBe(new DateTimeOffset(clock, TimeSpan.Zero));
        }
        count.ShouldBe(2);
    }
}
