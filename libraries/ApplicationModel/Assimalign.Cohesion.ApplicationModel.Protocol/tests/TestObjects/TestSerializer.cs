namespace Assimalign.Cohesion.ApplicationModel.Protocol.Tests;

/// <summary>
/// The content serializer the tests use, over the test types,
/// which keep <see cref="TestJsonContext"/>'s Pascal-case names.
/// </summary>
internal static class TestSerializer
{
    public static JsonApplicationContentSerializer Json { get; } = JsonApplicationContentSerializer.Create(TestJsonContext.Default);
}
