# Cohesion developer tooling

This L1 area contains the application CLI, installable .NET templates and repository
maintenance scripts. These tools reuse the SDK and gateway contracts in
[developer-experience design §4.4 and §7](../docs/DEVELOPER_EXPERIENCE_DESIGN.md).

| Project | Purpose |
| --- | --- |
| [CLI](Cli/Assimalign.Cohesion.Cli/docs/OVERVIEW.md) | `Assimalign.Cohesion.Cli`: the `cohesion` .NET tool and local developer state readers/writers. |
| [Templates](templates/README.md) | `Assimalign.Cohesion.Templates`: `dotnet new` applications, landing zones, gateways, composites and standalone resources. |
| [Dev scripts](scripts/README.md) | PowerShell `New-CohesionDotnetSolution` for repository solution generation. |

## The cohesion tool

| Verb | Behavior |
| --- | --- |
| `new <template> [args...]` | Validates a shipped template name and invokes `dotnet new <template> <args...>`. `new --list` invokes `dotnet new list cohesion`. |
| `run [--gateway <name>] [-- args...]` | Invokes `dotnet run --project <gateway> -- [--gateway <name>] <args...>`. |
| `deploy [--gateway <name>] [-- args...]` | As `run`, with `--mode apply` before passthrough. For example, `deploy -- --mode teardown` delegates that later mode to the gateway. |
| `publish [<project>] [args...]` | Invokes `dotnet publish <project-or-gateway> <args...>`. Also accepts `--project`. |
| `trust issue --developer <name>` | Runs the gateway's `--mode trust-issue --developer <name>`. Child stdout, containing the token, passes through unmodified and is never logged or stored by the CLI. |
| `trust add <peer> --from <export-url\|file>` | Runs `--mode trust-add --peer <peer> --from <source>` against the verifying application. |
| `parameter set <name> <value\|--stdin>` | Atomically updates the application's local secret parameter file. |
| `parameter list` / `parameter remove <name>` | Lists names only, or removes one parameter while preserving the others. |
| `status [--live [--token <jwt>]]` | Reports declared/allocated local values and process liveness; optionally reads observed state from the gateway control plane. |
| `login --issuer <url> --client-id <id>` | Discovers IdentityHub's device flow, displays approval instructions and polls for an access token. Optional `--client-secret`, `--audience`, `--scope` (default `openid`), and `--print`. |
| `--help` / `--version` | Prints syntax or the canonical Cohesion version, without SDK commit metadata. |

Child process exit codes pass through. CLI usage/deferred-feature errors return 2;
local I/O, malformed data or transport failures return 1; cancelled local commands return 130.
Unknown child arguments retain their spelling and order. The first `--` separates CLI options
from passthrough and is consumed by the wrapper. Local commands (`parameter`, `status`,
`login`) reject surplus arguments because they have no child process to receive them.
Gateway names are never validated here. Omitting `--gateway` preserves the gateway's
`COHESION_GATEWAY` fallback and its `local` default in Development.

The shipped `new` names are:

`cohesion-app`, `cohesion-landing-zone`, `cohesion-gateway`, `cohesion-composite`,
`cohesion-web`, `cohesion-spa`, `cohesion-database`, `cohesion-secretstore`,
`cohesion-configurationstore`, `cohesion-identityhub`, `cohesion-rezolvr`.

Names are accepted case-insensitively and forwarded verbatim. `-n` and `--applicationName`
pass through. Only the landing-zone template accepts `--topology single|federated`.
A failed valid-template invocation prints one install hint using
`Assimalign.Cohesion.Templates::<CLI version>`; the version is required for the prerelease
package and is read from the CLI assembly, not hard-coded.

## Gateway, application and state discovery

Use `--project <csproj|dir>` for gateway commands and state readers. An explicit csproj
selects that file. Otherwise the CLI recursively finds exactly one csproj beneath the
selected directory (current directory by default), skipping `bin/`, `obj/` and directory
links. The marker is the root `Sdk="Assimalign.Cohesion.Sdk.Gateway"` attribute, including
`Name/Version` forms, or a child `<Sdk Name="Assimalign.Cohesion.Sdk.Gateway" ... />`.
Zero/multiple matches list the candidates and require explicit selection.

The CLI explicitly launches gateway commands with the selected project directory as their
working directory. A probe on SDK 10.0.401 showed that `dotnet run --project` alone retains
the caller's directory, contrary to the work-item brief's assumption. Setting the child
directory makes the default state root `.cohesion/` beside the gateway csproj, even when
invoking the CLI from a repository root. `parameter` and `status` accept `--state-root <dir>`, relative to
the CLI's current directory; use it for a gateway with custom `LocalGatewayOptions.StateDirectory`,
`ExportDirectory` or run working directory. This option selects the readers/writer's location;
it does not configure the gateway.

Application selection is `--app <name>`, then the gateway's `CohesionApplicationName`,
then its `CohesionApplication`, then `CohesionApplication` from the nearest
`Directory.Build.props` walking upward, then the single directory under the state root.
Project XML is read without evaluating MSBuild expressions, conditions or imports; use
`--app` for calculated identity. No state yields “no local gateway state; run
`cohesion run` first”. An explicit application identity lets `parameter set` create its
file before the gateway's first run.

The gateway interprets relative passthrough paths, including a relative `trust add --from`
file, from its own run directory. An absolute export path avoids ambiguity.

## Local files and trust

Paths below are relative to `.cohesion/<app>/` (or the selected state root/application).
The CLI opens only the files needed by the selected command.

| Path | CLI use |
| --- | --- |
| `export.json` | `status` reads application/environment/version, resource name/kind/manifestHash, and endpoint name/internal/public. The embedded `model`, command outcomes and public trust key are ignored. |
| `control-plane.json` | `status --live` reads `url`; the public `trustKey` is ignored. |
| `.state/ports.json` | Reads allocated `controlPlane` and per-resource/per-endpoint port numbers. |
| `.state/owner` | Reads the gateway owner identity. |
| `.state/<resource>/pid` | Checks a running process against both `processId` and `startTimeUtcTicks`; a reused PID does not prove liveness. |
| `parameters.json` | `parameter` reads/writes a JSON object containing string values only; `list` prints names, never values. |
| `trust/trusted-issuers.json` | Owned by the gateway: `trust add` may write this **Development-only fallback** through the child command. The CLI does not open it. |
| `.state/gateway.lock` | Owned by the gateway; the CLI does not open it. |
| `trust/<gateway>/**` | The gateway's private key and key ring are **never read, copied or printed by the CLI**. |

`parameters.json` uses DPAPI CurrentUser with null entropy on Windows, matching the
gateway reader, and UTF-8 JSON with mode 0600 on POSIX. Updates use a temporary file in
the same directory followed by an overwrite rename; POSIX permissions are set before
plaintext is written. `--stdin` preserves all input, including trailing newlines.
Writers should be serialized by the caller; simultaneous read-modify-write operations
do not implement a transaction or cross-process merge.

For `trust add`, the gateway stores the grant in the application's **own SecretStore first**.
The local trusted-issuers file is used only in Development when no own store endpoint is
reachable, or when the store call fails in Development. Outside Development the gateway
rejects that fallback. The operation registers the peer's public gateway key on the
application that must verify it.

File-only status labels endpoint and port values as declared/allocated, not observed resource
state. `--live` performs one authenticated `GET /cohesion/v1/resources/{name}` per exported
resource and prints observed `state` and endpoint addresses. Supply this application's
`trust issue --developer` token using `--token` or `COHESION_TOKEN`. Missing bearer headers
receive HTTP 401; failed verification receives HTTP 403. Developer tokens have audience
`cohesion-export` and the gateway's default eight-hour lifetime. The CLI does not print
live-status tokens.

## IdentityHub login and deferred work

IdentityHub currently advertises device authorization only for a Development hub bound to
loopback. If discovery omits it, `login` fails with that explanation. Approval instructions
(the complete verification URL and user code) go to stderr. Polling honors `interval`,
`authorization_pending`, `slow_down` (+5 seconds), expiration and access denial. Credentials
require HTTPS except for a loopback Development hub; redirects are not followed.

**Proposed credential contract, for item 27 (`L01.01.02.13`, #972) to pin:** login stores
`{ access_token, token_type, expires_at, issuer }` in
`~/.cohesion/credentials/<issuer-host>.json`, using the same DPAPI/0600 protection and atomic
replacement as parameters. No client secret or device code is persisted. This location
currently holds one credential per host (different ports, paths and clients on that host
replace it); IPv6 host colons are represented by underscores. `--print` emits only the
access token on stdout and creates no credential file.

Nothing at HEAD consumes an IdentityHub login token. Gateway control planes verify gateway
trust keys only. Login stores the token for the later IdentityHub bridge in design §7(6),
“after #64”; use a gateway developer token for `status --live`.

| Deferred surface | Current behavior / owner |
| --- | --- |
| `publish --in-container` | Forwards to `CohesionPublishImage` through private SDK state. The SDK probes route availability; the in-container build image, mount layout, and command contract remain pending under item 15 (#955). |
| `trust add --against/--allow` | Parsed and rejected until item 31's substantive half (`L03.04.01.06`, #982), O25 command-kind scoping. |
| Additional single-resource templates | ApiManager, EmailHub, EventHub, IoTHub, LoadBalancer, LogSpace, MediaHub, MessageHub, NatGateway, NotificationHub, Scheduler and VpnGateway have no shipped template yet; `new` rejects them by name. Item 39 follow-up owns them. |
| IdentityHub-token acceptance | Deferred to the §7(6) bridge; login does not substitute gateway trust credentials. |
| Resource command dispatch | No CLI dispatch surface: the command routes require `cohesion_token_use=gateway`, which developer tokens do not carry. |

## Build, package and install locally

From the repository root, use only an in-repository tool path:

```powershell
dotnet build build/Tasks
dotnet build tooling/Cli/Assimalign.Cohesion.Cli/src/Assimalign.Cohesion.Cli.csproj
dotnet build tooling/Cli/Assimalign.Cohesion.Cli/tests/Assimalign.Cohesion.Cli.Tests.csproj
dotnet test tooling/Cli/Assimalign.Cohesion.Cli/tests/
dotnet pack tooling/Cli/Assimalign.Cohesion.Cli/src/Assimalign.Cohesion.Cli.csproj -o _out/verify-40
$cliVersion = & ./installer/scripts/Get-CohesionVersion.ps1
dotnet tool install --add-source _out/verify-40 --tool-path _out/verify-40/tool --version $cliVersion Assimalign.Cohesion.Cli
./_out/verify-40/tool/cohesion.exe --help
```

On POSIX invoke `./_out/verify-40/tool/cohesion`. The explicit canonical `--version` pins the
prerelease package. The work-item brief requested both `--prerelease` and `--version`, but
SDK 10.0.401 rejects that combination: “The --prerelease and --version options are not supported
in the same command”. The verified command above uses the exact prerelease version alone;
it never relies on the floating default version range. The tool package contains
`tools/net10.0/any/DotnetToolSettings.xml` and the inherited Cohesion NuGet icon.
The tool depends on the BCL and the centrally pinned ProtectedData facade, with no Cohesion
runtime, hosting, DI, configuration-binding or prompt-library dependency.

[tooling-cli.yml](../.github/workflows/tooling-cli.yml) builds/tests this project on Ubuntu,
Windows and macOS through the shared build action using `area: tooling` and `category: Cli`.
The release inventory entry is `tooling/Cli/Assimalign.Cohesion.Cli`; the old
`tooling/Cli/src/...` exclusion must be removed as part of shared-file integration.
The CLI is not a framework assembly. Package metadata, Pester inventory fixtures and a
scratch rehearsal of release inventory integration are separate local verification steps.
