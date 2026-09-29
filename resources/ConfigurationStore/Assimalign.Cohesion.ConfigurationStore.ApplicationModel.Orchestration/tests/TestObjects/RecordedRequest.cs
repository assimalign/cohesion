using System;
using System.Net.Http;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// The parts of a sent request a test asserts on, captured before the client disposes it.
/// </summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri RequestUri,
    string? Authorization,
    string Accept,
    bool HasContent);
