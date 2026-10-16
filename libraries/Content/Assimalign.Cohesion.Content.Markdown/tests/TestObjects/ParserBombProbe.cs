using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

internal static class ParserBombProbe
{
    // Authored synthetic fixtures: no downloaded input. Compact block keys/sequences make
    // 100,000 levels linear in source length; indented variants exercise multi-line paths.
    internal static string CreateInput(string shape, int depth)
    {
        var builder = new StringBuilder();
        return shape switch
        {
            "quotes" => Repeat("> ", depth) + "end",
            "lists" => Repeat("- ", depth) + "end",
            "emphasis" => new string('*', depth) + "end" + new string('*', depth),
            "links" => new string('[', depth) + "end" + Repeat("](u)", depth),
            "images" => Repeat("![", depth) + "end" + Repeat("](u)", depth),
            "destinations" => "[end](" + new string('(', depth) + "u" + new string(')', depth) + ")",
            _ => throw new ArgumentException(nameof(shape)),
        };
    }

    private static string Repeat(string value, int count)
    {
        var builder = new StringBuilder(value.Length * count);
        for (var index = 0; index < count; index++) { builder.Append(value); }
        return builder.ToString();
    }

    // The existing test executable doubles as the child helper; no new project or shipped API.
    private static int Main(string[] args)
    {
        var source = CreateInput(args[0], int.Parse(args[1], CultureInfo.InvariantCulture));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        try
        {
            var document = MarkdownText.Parse(source);
            GC.KeepAlive(document);
            Console.Write("Accepted");
        }
        catch (ContentFormatException exception)
        {
            Console.Write(exception.ErrorCode);
        }
        Console.WriteLine($" | {timer.ElapsedMilliseconds} ms | {GC.GetAllocatedBytesForCurrentThread() - allocated} bytes");
        return 0;
    }

    internal static async Task<(int ExitCode, string Output)> RunAsync(string shape, int depth, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Content.Markdown.Tests.dll"));
        start.ArgumentList.Add(shape);
        start.ArgumentList.Add(depth.ToString(CultureInfo.InvariantCulture));
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await error;
            return (process.ExitCode, await output + stderr[..Math.Min(stderr.Length, 500)]);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}
