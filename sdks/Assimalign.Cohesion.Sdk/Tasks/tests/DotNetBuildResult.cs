namespace Assimalign.Cohesion.Sdk.Tests;

internal sealed record DotNetBuildResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public string Output => $"{StandardOutput}{System.Environment.NewLine}{StandardError}";
}
