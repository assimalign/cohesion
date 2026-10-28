namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

/// <summary>
/// The content serializer the tests' control planes and invocations use: the resource contracts'
/// own types plus the test types.
/// </summary>
internal static class TestSerializer
{
    public static Assimalign.Cohesion.Hosting.Resources.JsonResourceContentSerializer Json { get; } =
        Assimalign.Cohesion.Hosting.Resources.JsonResourceContentSerializer.Create(TestJsonContext.Default);
}