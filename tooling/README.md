# Cohesion developer tooling

This L1 area contains the application CLI and installable .NET templates.
Repository maintenance scripts live in [root scripts](../scripts/README.md). The tools wrap
the SDKs and the gateway program a `Program.cs` declares; they add no application-model
semantics of their own (see [docs/libraries/ApplicationModel/DESIGN.md](../docs/libraries/ApplicationModel/DESIGN.md)).

| Project | Purpose |
| --- | --- |
| [CLI](Cli/Assimalign.Cohesion.Cli/docs/OVERVIEW.md) | `Assimalign.Cohesion.Cli`: the `cohesion` .NET tool. |
| [Templates](templates/README.md) | `Assimalign.Cohesion.Templates`: `dotnet new` applications, gateways and standalone resources. |
| [Database Studio](Assimalign.Cohesion.Database.Studio/README.md) | Throw-away, Windows-only MAUI editor for exercising the Database engines as a consumer. Not shipped: `IsPackable=false`, in no solution or CI matrix. |

## The cohesion tool

| Verb | Behavior |
| --- | --- |
| `new <template> [args...]` | Validates a shipped template name and invokes `dotnet new <template> <args...>`. `new --list` invokes `dotnet new list cohesion`. |
| `run [--project <csproj\|dir>] [-- args...]` | Invokes `dotnet run --project <gateway> -- <args...>` in the gateway's directory. The gateway's `Program.cs` declares the model and the platform it runs on. |
| `publish [<project>] [args...]` | Invokes `dotnet publish <project-or-gateway> <args...>`. Also accepts `--project`. |
| `login --issuer <url> --client-id <id>` | Discovers IdentityHub's device flow, displays approval instructions and polls for an access token. Optional `--client-secret`, `--audience`, `--scope` (default `openid`), and `--print`. |
| `--help` / `--version` | Prints syntax or the canonical Cohesion version, without SDK commit metadata. |

Child process exit codes pass through. CLI usage errors return 2; local I/O, malformed data or
transport failures return 1; cancelled local commands return 130. Unknown child arguments retain
their spelling and order. The first `--` separates CLI options from passthrough and is consumed
by the wrapper. `login` rejects surplus arguments because it has no child process to receive them.

`run` adds `dotnet run --no-launch-profile` when `COHESION_ENVIRONMENT` or `DOTNET_ENVIRONMENT`
is nonblank, because a launch profile would otherwise override the inherited value. Local is the
developer-machine environment; Development is an ordinary deployed environment with strict
security requirements. The framework's unset default remains Production.

The gateway's own command-line modes (`--gateway`, `--mode apply|describe|trust-issue|trust-add`),
its local state files under `.cohesion/`, and the `deploy`, `trust`, `parameter` and `status`
verbs that wrapped them were retired with the generated application model on 2026-10-31.

The shipped `new` names are:

`cohesion-app`, `cohesion-gateway`, `cohesion-web`, `cohesion-spa`, `cohesion-database`,
`cohesion-secretstore`, `cohesion-configurationstore`, `cohesion-identityhub`, `cohesion-rezolvr`.

Names are accepted case-insensitively and forwarded verbatim. `-n` and `--applicationName`
pass through. A failed valid-template invocation prints one install hint using
`Assimalign.Cohesion.Templates::<CLI version>`; the version is required for the prerelease
package and is read from the CLI assembly, not hard-coded.

## Gateway discovery

Use `--project <csproj|dir>` for `run` and `publish`. An explicit csproj selects that file.
Otherwise the CLI recursively finds exactly one csproj beneath the selected directory (current
directory by default), skipping `bin/`, `obj/` and directory links. The marker is the root
`Sdk="Assimalign.Cohesion.Sdk.Gateway"` attribute, including `Name/Version` forms, or a child
`<Sdk Name="Assimalign.Cohesion.Sdk.Gateway" ... />`. Zero/multiple matches list the candidates
and require explicit selection.

The CLI explicitly launches `run` with the selected project directory as its working directory.
A probe on SDK 10.0.401 showed that `dotnet run --project` alone retains the caller's directory,
so relative passthrough paths resolve against the gateway project either way.

## IdentityHub login and deferred work

IdentityHub currently advertises device authorization only for a Local hub bound to
loopback. If discovery omits it, `login` fails with that explanation. Approval instructions
(the complete verification URL and user code) go to stderr. Polling honors `interval`,
`authorization_pending`, `slow_down` (+5 seconds), expiration and access denial. Credentials
require HTTPS except for a loopback Local hub; redirects are not followed.

**Proposed credential contract, for item 27 (`L01.01.02.13`, #972) to pin:** login stores
`{ access_token, token_type, expires_at, issuer }` in
`~/.cohesion/credentials/<issuer-host>.json`, protected with DPAPI CurrentUser on Windows and
mode 0600 on POSIX, written by atomic replacement. No client secret or device code is persisted.
This location currently holds one credential per host (different ports, paths and clients on
that host replace it); IPv6 host colons are represented by underscores. `--print` emits only
the access token on stdout and creates no credential file.

Nothing at HEAD consumes an IdentityHub login token; login stores it for a later IdentityHub
bridge.

| Deferred surface | Current behavior / owner |
| --- | --- |
| Additional single-resource templates | ApiManager, EmailHub, EventHub, IoTHub, LoadBalancer, LogSpace, MediaHub, MessageHub, NatGateway, NotificationHub, Scheduler and VpnGateway have no shipped template yet; `new` rejects them by name. Item 39 follow-up owns them. |
| IdentityHub-token acceptance | Deferred to the IdentityHub bridge. |

## Build, package and install locally

From the repository root, use only an in-repository tool path:

```powershell
dotnet build build/Tasks
dotnet build tooling/Cli/Assimalign.Cohesion.Cli/src/Assimalign.Cohesion.Cli.csproj
dotnet test tooling/Cli/Assimalign.Cohesion.Cli/tests/Assimalign.Cohesion.Cli.Tests.csproj
dotnet pack tooling/Cli/Assimalign.Cohesion.Cli/src/Assimalign.Cohesion.Cli.csproj -o _out/verify-40
$cliVersion = & ./scripts/Get-CohesionVersion.ps1
dotnet tool install --add-source _out/verify-40 --tool-path _out/verify-40/tool --version $cliVersion Assimalign.Cohesion.Cli
./_out/verify-40/tool/cohesion.exe --help
```

On POSIX invoke `./_out/verify-40/tool/cohesion`. The explicit canonical `--version` pins the
prerelease package; SDK 10.0.401 rejects `--prerelease` together with `--version`. The tool
package contains `tools/net10.0/any/DotnetToolSettings.xml` and the inherited Cohesion NuGet
icon. The tool depends on the BCL and the centrally pinned ProtectedData facade, with no
Cohesion runtime, hosting, DI, configuration-binding or prompt-library dependency.

[tooling-cli.yml](../.github/workflows/tooling-cli.yml) builds/tests this project on Ubuntu,
Windows and macOS through the shared build action using `area: tooling` and `category: Cli`.
The release inventory entry is `tooling/Cli/Assimalign.Cohesion.Cli`. The CLI is not a
framework assembly.
