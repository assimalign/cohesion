using System.Collections.Generic;

namespace Assimalign.Cohesion.Logging.Tests;

/// <summary>
/// Provider that records entries through an inner <see cref="RecordingProvider"/> and appends
/// <c>provider:{name}</c> to a shared teardown log when disposed.
/// </summary>
internal sealed class TeardownProvider : ILoggerProvider
{
    private readonly List<string> _teardown;

    public TeardownProvider(string name, List<string> teardown)
    {
        Inner = new RecordingProvider(name);
        _teardown = teardown;
    }

    public RecordingProvider Inner { get; }

    public string Name => Inner.Name;

    public ILogger Create(string category) => Inner.Create(category);

    public void Dispose()
    {
        _teardown.Add("provider:" + Name);
        Inner.Dispose();
    }
}
