using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// One HTTP request as the SecretStore client put it on the wire, captured by
/// <see cref="RecordingHttpMessageHandler"/>.
/// </summary>
/// <param name="Method">The request method.</param>
/// <param name="Uri">The absolute request URI, query included.</param>
/// <param name="AuthorizationScheme">The <c>Authorization</c> scheme, or <see langword="null"/>.</param>
/// <param name="AuthorizationParameter">The <c>Authorization</c> credential, or <see langword="null"/>.</param>
/// <param name="Accept">The <c>Accept</c> media types in order.</param>
/// <param name="ContentType">The body's media type, or <see langword="null"/> without a body.</param>
/// <param name="Body">The body bytes; empty without a body.</param>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    IReadOnlyList<string> Accept,
    string? ContentType,
    byte[] Body);
