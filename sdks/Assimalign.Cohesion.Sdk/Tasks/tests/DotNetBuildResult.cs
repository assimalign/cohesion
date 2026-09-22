#if COHESION_APPLICATION_MODEL_TESTS
namespace Assimalign.Cohesion.Sdk.ApplicationModel.Tests;
#else
namespace Assimalign.Cohesion.Sdk.Tests;
#endif

internal sealed record DotNetBuildResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public string Output => $"{StandardOutput}{System.Environment.NewLine}{StandardError}";
}
