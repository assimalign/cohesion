# CLI design

## Intent and authority

Developer-experience design §4.4 and §7 define thin wrappers with no new gateway semantics.
The CLI delegates execution and errors to the landed gateway parser and SDK targets. Its
own behavior is argument mapping, project discovery, local file I/O and OIDC device login.
The command reference and the single documented template roster live in the
[tooling README](../../../README.md).

## Execution and lifecycle

`CliApplication` routes commands; `IProcessRunner` is the single process-launch seam.
`ProcessRunner` uses `ProcessStartInfo.ArgumentList` without shell interpolation and inherits
stdin/stdout/stderr. Child exit codes are preserved. It never captures developer-token
stdout. Ctrl+C reaches the child console; cancellation gives it 35 seconds to complete the
gateway's normal 30-second stop grace before terminating a remaining process tree.

`Arguments` consumes known options before the first `--`. Unrecognized child arguments
and the passthrough tail retain their order and spelling. Wrapper-generated mode arguments
come first, letting later gateway mode options preserve the gateway's existing precedence.
No gateway-name allowlist is added. `Templates` owns the accepted short-name list and
checks the landing-zone-only topology option without rewriting it.

`GatewayDiscovery` reads project XML without MSBuild execution. SDK markers accept
attribute name/version and child Sdk forms. Explicit project paths avoid ambiguous recursive
selection. State defaults beside the gateway csproj: the CLI explicitly starts gateway
commands in that directory, and `ApplicationGateway.GetStateRoot()` uses the current directory.
This corrects the work-item brief's assumption that `dotnet run --project` itself changes
directory; an SDK 10.0.401 probe retained the caller's directory. User-customized gateway
locations require the reader/writer's `--state-root` override. Literal application identity
uses the precedence documented in the tooling guide. There is no configuration binder.

## Local contracts

`LocalStateCommands` projects only fields needed for display. `StateJsonContext` skips unknown
fields, including the exported declarative model and command graph. Using
`ApplicationExportDocument.Load` would validate an unrelated entire model and create a
Cohesion runtime dependency in this installable tool.

The landed local state uses `.state/ports.json`, `.state/owner` and
`.state/<resource>/pid`, although §4.4's earlier prose omits that intermediate directory.
The implementation follows these runtime paths. A PID is live only if its start-time ticks
also match. File status never promotes declarations or allocations into observed resource
state. Live status uses only the per-resource GET route with a bearer developer token;
command routes require a gateway-use claim and are outside this CLI.

`ProtectedFile` owns DPAPI CurrentUser/null-entropy protection on Windows and 0600 UTF-8
files on POSIX. Writes create a unique temporary file beside the destination, flush it and
rename over the destination. The mode is restricted before plaintext is written. Parameter
objects contain only strings, as required by `ApplicationGateway.ReadParametersAsync`;
tests exercise that reader's DPAPI/JsonDocument contract on CLI-produced bytes. Concurrent
writers are not transactionally merged. Callers must serialize mutations.

No local command opens the gateway's private trust key/key ring. Federation grants are
written by the gateway into the application's own SecretStore, with a Development-only
local fallback; the CLI does not implement a second grant store.

## Login protocol and proposed credential store

`DeviceLogin` discovers the issuer's device endpoint, POSTs form data and polls the token
endpoint with the device grant. Approval is manual. `authorization_pending` continues,
`slow_down` adds five seconds to subsequent polls, and expiration/access denial stop the
flow. Cancellation covers requests and delays. HTTP redirects are disabled; credential
endpoints require HTTPS except for loopback development endpoints. Token/error response
bodies are never included in diagnostics. `--print` reserves stdout for the access token;
approval instructions go to stderr.

The design specifies device login but no credential storage contract. Per this work item's
explicit proposal, credentials are persisted under
`~/.cohesion/credentials/<issuer-host>.json` with fields
`access_token`, `token_type`, `expires_at` and `issuer`, protected by `ProtectedFile`.
The host filename intentionally has one slot per host (ports, issuer paths and clients
collide; IPv6 colons are replaced by underscores). **Item 27 (#972) must pin or revise this
proposal.** Client secrets and device codes are not saved.

IdentityHub's landed flow is Development/loopback-only. It does not issue a gateway-trust-key
token accepted by current control planes. The §7(6) IdentityHub-token bridge remains later work.

## Error model and extension points

CLI usage/deferred-feature errors use internal `CliException` and exit 2. I/O, malformed
documents and transport/startup failures exit 1 without echoing potentially secret values.
Local cancellation exits 130. Child processes return their own exit status.

`publish --in-container` forwards to `dotnet publish <project> -t:CohesionPublishImage`
with private `-p:_CohesionImageInContainer=true`, preserving the remaining argument vector.
The SDK owns daemon probing and capability errors; the in-container build image/mount/command
contract still needs specification under item 15 (#955). The public AOT value set stays
`auto|true|false`. Item 31's substantive half (#982, O25) owns scoped trust options. Item 39's
follow-up owns unshipped templates. Extensions should replace these gates only when the
underlying contract exists, retaining process mapping tests.

## Packaging, tests and AOT

The project follows `tooling/Cli/<project>/{src,tests,docs}` for the release resolver and
shared build action. It sets `PackAsTool` and `ToolCommandName=cohesion`; assembly/package
identity stays `Assimalign.Cohesion.Cli`. Central version/branding imports remain authoritative.
The informational-version reader strips SDK commit metadata after `+`.

There is no public managed API, runtime type discovery or reflection JSON serializer.
Source-generated JSON metadata and fixed assembly informational-version metadata are
compatible with trimming/NativeAOT. The tool package itself is framework-dependent.

The test project has the explicitly sanctioned raw relative `ProjectReference`, documented
at its point of use: the Cohesion name resolver indexes only `libraries/` and `resources/`.
Production code has no project references. Tests use a fake process runner, fake HTTP,
deterministic polling time and in-repository filesystem fixtures. DPAPI tests execute on
Windows and POSIX mode checks execute on the other CI hosts.

The new tooling CLI workflow supplies the standard three-OS build/test matrix. Release
inventory and root solution edits are orchestrator-owned shared entries; the CLI requires
no framework or Dependabot change. All package verification installs use an in-repository
`_out/` tool path.
