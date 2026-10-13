# Build-generated exception messages

Decision 59 in the [HTTP/Web program plan](programs/HTTP_WEB_PROGRAM_PLAN.md), implemented by
#1467 and #1468, puts exception text in build-generated resources and reusable throw members on
the exception type. The standing rule is in
[general-rules.md](../.claude/rules/general-rules.md).

## Generation contract

A project adds `src/Properties/ErrorMessages.resx`. The shared
[Build.ErrorMessages.targets](../build/Targets/Build.ErrorMessages.targets), imported by
[Build.targets](../build/Build.targets), updates the SDK's existing `EmbeddedResource` item:

| Metadata | Value |
| --- | --- |
| `StronglyTypedLanguage` | `CSharp` |
| `StronglyTypedClassName` | `ErrorMessages` |
| `StronglyTypedNamespace` | `$(RootNamespace)` |
| `StronglyTypedFileName` | `$(IntermediateOutputPath)ErrorMessages.g.cs` |
| `PublicClass` | `false` |
| `ManifestResourceName` | `$(RootNamespace).ErrorMessages` |

MSBuild's `GenerateResource` task emits and compiles the internal accessor during ordinary
`dotnet build`. There is no checked-in `Designer.cs`, Visual Studio custom tool, or per-project
generation configuration. Explicit manifest naming keeps the accessor's `ResourceManager` base
name equal to the embedded resource name. The existing `Resources.resx` convention receives the
same treatment with class `Resources`, output `Resources.g.cs`, and manifest name
`$(RootNamespace).Resources`, for strings that are not exception messages.

Each distinct exception message has a PascalCase resource name describing the failure. Call
sites use `ErrorMessages.Name`; formatted messages use
`string.Format(CultureInfo.CurrentCulture, ErrorMessages.Name, args...)`. Numbered placeholders
preserve the original interpolation's ordering, format specifiers, punctuation, and whitespace.
The resource files retain the repository's UTF-8 BOM and CRLF convention.

The exhaustive exception-text migration covers `libraries/Connections/**`, `libraries/Http/**`,
and `resources/Web/**`. EventSource templates, log text, and HTTP response bodies retain their
existing ownership. Messages that deliberately exclude untrusted request input continue to do
so; resource extraction does not widen EventSource's type-only security carve-outs. Other areas
adopt the rule when next touched; #1470 covers Database.

## Existing resource projects

The seven existing projects now use the shared build mechanism. Their prior resource names and
values are preserved; call sites are updated when an accessor name changes.

| Project | Resource treatment |
| --- | --- |
| Core | Keeps `ErrorMessages.resx`; removes its checked-in designer. |
| DependencyInjection | Renames exception-only `Resources.resx` to `ErrorMessages.resx` and updates accessor references. `ErrorMessageFormatter` retains the `System.Resources.UseSystemResourceKeys` switch and uses explicit current-culture formatting otherwise. |
| FileSystem | Keeps `ErrorMessages.resx`; #1468 adds the path-outside-root message and moves `ThrowPathOutsideRoot` onto `FileSystemException`. |
| ObjectMapping | Renames exception-only `Resources.resx` to `ErrorMessages.resx` and updates accessor references. |
| ObjectValidation | Keeps `Resources.resx` and `Resources` for validation-result codes and text, which are not exception messages. |
| Resilience | Keeps `ErrorMessages.resx`; removes its checked-in designer. |
| IdentityHub | Keeps `Resources.resx` and `Resources` for the `claims.roles` XML payload, which is not an exception message. |

All seven remove their design-time `Generator`, `LastGenOutput`, and `DependentUpon` items.
#1468 preserves the caller spelling `FileSystemException.ThrowPathOutsideRoot(...)`. Reusable
throw logic belongs on an owned exception type; extension members are reserved for exception
types the assembly does not own.

## Mechanism proof and CI coverage

[ResourceGenerationCheck](../build/ResourceGenerationCheck/Assimalign.Cohesion.ResourceGenerationCheck.csproj)
is an executable probe of the shared mechanism. It sets both current cultures to `fr-FR`,
retrieves the neutral resource through the generated accessor, and constructs an exception with
the resource format and `12.5`. It requires the exact message
`Operation probe failed at 12,5.` and returns a failure exit code on a mismatch. This checks
resource fallback and culture-sensitive formatting together.

The probe passed a managed Release run with `ContinuousIntegrationBuild=true` and a
`win-arm64` NativeAOT publish and native execution. Removing the shared target import and
performing a clean rebuild failed with the missing-accessor compiler error; restoring it passed.
Visual Studio MSBuild restore also passed. The SDK mechanism therefore met the requirement;
an incremental source generator was unnecessary.

[resource-web.yml](../.github/workflows/resource-web.yml) runs the probe as a managed CI build
and as a `linux-x64` NativeAOT executable. Those workflow checks are registered; this cleanup
session did not trigger a hosted CI run. The workflow also retains the Web NativeAOT guard and
its `--smoke` execution. No command-line argument printing is added to that guard.

## Messages cached on normal lifecycle paths

The following static readonly fields avoid resource lookups when an exception represents
routine cancellation, connection closure, shutdown, or an expected authentication rejection.
Only the resource text or format is cached; each exception retains its own instance and any
runtime arguments. Formatting still uses `CultureInfo.CurrentCulture` when the exception is
created.

Project names below omit the common `Assimalign.Cohesion.` prefix. Field names identify the
complete cache inventory for these paths.

| Project / type and path | Cached field(s) and resource(s) |
| --- | --- |
| Connections / `ConnectionAbortedException` parameterless construction | `_defaultMessage`: `ConnectionAborted` |
| Connections / `ConnectionResetException` default construction | `_defaultMessage`: `ConnectionResetByPeer` |
| Connections.InMemory / `InMemoryConnectionListener.AcceptCoreAsync` during disposal | `_listenerDisposedMessage`: `ListenerDisposed` |
| Connections.InMemory / `InMemoryMultiplexedConnectionListener` accept during disposal | `_listenerDisposedMessage`: `MultiplexedListenerDisposed` |
| Connections.InMemory / `InMemoryMultiplexedConnection.AcceptStreamAsync` and `Close`, including disposal | `_connectionClosedMessage`: `MultiplexedConnectionClosed`; `_connectionClosedWithErrorCodeMessage`: `MultiplexedConnectionClosedWithErrorCode` |
| Connections.InMemory / `InMemoryConnection.AbortInput`, `AbortOutput`, and `ReadAborted` | `_peerReadingStoppedMessage`: `PeerReadingStopped`; `_streamResetByPeerMessage`: `StreamResetByPeer`; `_streamReadingAbortedMessage`: `StreamReadingAborted` |
| Connections.NamedPipes / `NamedPipeConnectionListener` accept during disposal | `_listenerDisposedMessage`: `ListenerDisposed` |
| Connections.Quic / `QuicStreamConnection.ReadAborted` | `_streamReadingAbortedMessage`: `StreamReadingAborted` |
| Http.Connections / `Http2Stream.ReceiveReset`, `SendReset`, `AbortOnShutdown`, and `AbortOnCancellation` | `_exchangeAbandonedBeforeBodyEndedFormat`: `Http2ExchangeOnStreamWasAbandonedBeforeItsRequestBodyEnded`; `_connectionClosedBeforeBodyEndedFormat`: `Http2ConnectionClosedBeforeTheRequestBodyOnStreamEnded`; `_requestBodyResetFormat`: `Http2StreamWasResetBeforeItsRequestBodyEnded` |
| Http.Connections / `Http3ConnectionContext.CancelRequestStream`, `RejectUndispatchedAsync`, and cancelled head parsing | `_requestDispatchStoppedMessage`: `ConnectionStoppedBeforeTheRequestWasDispatched`; `_exchangeCancelledMessage`: `ExchangeWasCancelled` |
| Http.Connections / `Http3RequestBodyStream.MarkReset` and `CompleteInput` after response completion | `_requestBodyResetMessage`: `Http3RequestStreamWasResetBeforeItsBodyWasRead`; `_requestBodyStoppedMessage`: `RequestBodyStoppedAfterCompleteResponse` |
| Http.Connections / `Http3RequestBodyStream.ReadTrailerSectionAsync` caller-only cancellation | `_trailerReadCancelledMessage`: `ReadWasCancelledInsideTheRequestSTrailerSectionTheRequestBody` |
| Web.Authentication.Bearer / `JwtBearerHandler` authentication rejection | `_bearerCredentialHasNoTokenMessage`: `BearerCredentialHasNoToken`; `_bearerTokenAudienceRejectedMessage`: `BearerTokenAudienceRejected`; `_bearerTokenIssuerRejectedMessage`: `BearerTokenIssuerRejected`; `_bearerTokenMalformedMessage`: `BearerTokenMalformed`; `_bearerTokenNotCompactJwsMessage`: `BearerTokenNotCompactJws`; `_bearerTokenSignatureEncodingInvalidMessage`: `BearerTokenSignatureEncodingInvalid`; `_bearerTokenSignatureInvalidMessage`: `BearerTokenSignatureInvalid`; `_bearerTokenUsesUnsecuredAlgorithmMessage`: `BearerTokenUsesUnsecuredAlgorithm`; `_bearerTokenValidationFailedMessage`: `BearerTokenValidationFailed` |
| Web.Authentication.Cookie / `CookieAuthenticationHandler` authentication rejection | `_authenticationCookieEncodingInvalidMessage`: `AuthenticationCookieEncodingInvalid`; `_authenticationCookiePayloadMalformedMessage`: `AuthenticationCookiePayloadMalformed`; `_authenticationCookieUnprotectFailedMessage`: `AuthenticationCookieUnprotectFailed`; `_authenticationTicketExpiredMessage`: `AuthenticationTicketExpired` |
| Web.Authentication.Certificate / `CertificateAuthenticationHandler` authentication rejection | `_certificatePrincipalNotAuthenticatedMessage`: `CertificatePrincipalNotAuthenticated` |
| Web.ErrorHandling / `ExceptionBoundaryMiddleware` peer-abandoned response cancellation | `_clientAbandonedResponseMessage`: `ClientAbandonedResponse` |
| Web.Server / `WebApplicationServerDrain` drain cancellation | `_serverDrainBudgetExceededMessage`: `ServerDrainBudgetExceeded` |

## Other cached diagnostic text

Two successful paths select diagnostic fragments before a failure is known. They cache those
fragments as well. The HTTP/3 reader also caches its truncated-frame subjects, and the existing
internal extended-CONNECT refusal fields remain shared readonly fields backed by resources.

| Http.Connections type | Cached field(s) and resource(s) |
| --- | --- |
| `Http1MessageReader` request-line and header reads | `_requestHeaderSectionSubject`: `RequestHeaderSection`; `_requestLineSubject`: `RequestLine` |
| `HttpListenerRegistration.ValidateStreamCapabilities` | `_http2ProtocolDescription`: `Http2ProtocolDescription`; `_combinedHttpProtocolDescription`: `CombinedHttpProtocolDescription`; `_http1ProtocolDescription`: `Http1ProtocolDescription` |
| `Http3RequestStreamReader` truncated-frame diagnostics | `_headersFrameSubject`: `HeadersFrame`; `_dataFrameSubject`: `DataFrame`; `_framePayloadSubject`: `FramePayload`; `_frameHeaderSubject`: `FrameHeader` |
| `HttpExtendedConnectRules` shared refusal messages | `NotExtendedConnectMessage`: `ExchangeIsNotExtendedConnect`; `AlreadyAcceptedMessage`: `ExtendedConnectAcceptHasAlreadyBeenAttemptedForThisExchange`; `CancelledMessage`: `ExtendedConnectTunnelCannotBeAcceptedTheExchangeWasCancelled`; `ResponseStartedMessage`: `ExtendedConnectTunnelCannotBeAcceptedAfterTheResponseHasStarted` |

The `HttpMethod` and `HttpStatusCode` guards now evaluate their resource formatting only after
the existing failure condition is true, preserving successful-path allocation behavior.
