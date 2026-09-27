#Requires -Version 5.1
<#
.SYNOPSIS
    The authoritative inventory of everything a Cohesion release ships.

.DESCRIPTION
    One module, one answer to "what is in a Cohesion release". Both the release packer
    (installer/scripts/Pack-Release.ps1) and the release pipeline's validation matrix
    (installer/scripts/Get-ReleaseMatrix.ps1, consumed by .github/workflows/release.yml) read
    the lists below, so the set that CI builds and tests and the set that reaches nuget.org
    cannot disagree.

    Four distribution shapes make up a release:

        libraries + resources   one NuGet package per shipping project, consumed directly via
                                <PackageReference>
        SDK packs               Assimalign.Cohesion.Sdk[.<Domain>], consumed via
                                <Project Sdk="...">
        framework packs         Assimalign.Cohesion.App[.<Domain>].Ref (targeting pack) and
                                Assimalign.Cohesion.App[.<Domain>].Runtime.<rid> (one per RID),
                                pulled in by the SDK's KnownFrameworkReference machinery

    The library/resource inventory is CURATED, not globbed. Well over a third of the src csprojs
    under libraries/ and resources/ are scaffolded placeholders for work that has not landed;
    globbing would put them on nuget.org. The contract is deliberately stricter - a package ships
    only if:

        1. a per-area CI workflow under .github/workflows builds it,
        2. it is not <IsPackable>false</IsPackable>, and
        3. it has at least one source file.

    Assert-CohesionReleaseInventory enforces all three in both directions, so the inventory and
    the repository cannot drift apart silently. It also scans every project under the product,
    SDK, analyzer, tooling, and extension roots so a packable project with source cannot remain
    invisible to every workflow matrix. A pre-existing project may be held outside CI only by an
    exact path entry in the known-exclusion ledger below, with a non-empty reason.

    Note what (1) does NOT claim. The per-area workflows run `dotnet test` when a tests csproj
    exists beside the project, and 12 of the shipping entries have none - mostly resource area
    roots. "CI builds it" is the guarantee; "CI tests it" is true of most entries, not all.

.NOTES
    Adding a shipping project is a two-line edit: add it to its area workflow's matrix under
    .github/workflows, then add it here. Doing only one of the two fails the release pack with a
    message naming the offender.
#>

Set-StrictMode -Version 3.0

# ---------------------------------------------------------------------------------------------
# Inventory
# ---------------------------------------------------------------------------------------------

# Shipping libraries and resources, as '<area>/<category>/<project>'. The triple is what
# .github/actions/build takes as inputs, so this list doubles as the release validation matrix.
#
# Deliberately absent: resources/Database/Assimalign.Cohesion.Database.Testing/fixtures/Assimalign.Cohesion.Database.SampleHost. It is
# built through Database.Testing's E2E project reference but carries <IsPackable>false</IsPackable>;
# the real resource apphost is a test fixture, never a shipped package. Database.Testing itself is
# shipped so customer test projects can invoke their own resource Program under an ambient scope.
$script:CohesionReleaseLibrary = @(
    # libraries/Amqp
    'libraries/Amqp/Assimalign.Cohesion.Amqp.Connections'

    # libraries/ApplicationModel
    'libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel'
    'libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway'
    'libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane'
    'libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway.InProcess'

    # libraries/Cache
    'libraries/Cache/Assimalign.Cohesion.Caching'
    'libraries/Cache/Assimalign.Cohesion.Caching.InMemory'

    # libraries/Configuration
    'libraries/Configuration/Assimalign.Cohesion.Configuration'
    'libraries/Configuration/Assimalign.Cohesion.Configuration.CommandLine'
    'libraries/Configuration/Assimalign.Cohesion.Configuration.EnvironmentVariables'
    'libraries/Configuration/Assimalign.Cohesion.Configuration.FileSystem'
    'libraries/Configuration/Assimalign.Cohesion.Configuration.Ini'
    'libraries/Configuration/Assimalign.Cohesion.Configuration.Json'
    'libraries/Configuration/Assimalign.Cohesion.Configuration.Xml'

    # libraries/Connections
    'libraries/Connections/Assimalign.Cohesion.Connections'
    'libraries/Connections/Assimalign.Cohesion.Connections.Tcp'
    'libraries/Connections/Assimalign.Cohesion.Connections.Udp'
    'libraries/Connections/Assimalign.Cohesion.Connections.Quic'
    'libraries/Connections/Assimalign.Cohesion.Connections.Security'
    'libraries/Connections/Assimalign.Cohesion.Connections.InMemory'

    # libraries/Content
    'libraries/Content/Assimalign.Cohesion.Content'
    'libraries/Content/Assimalign.Cohesion.Content.Text'
    'libraries/Content/Assimalign.Cohesion.Content.Ebml'
    'libraries/Content/Assimalign.Cohesion.Content.Markdown'
    'libraries/Content/Assimalign.Cohesion.Content.Yaml'

    # libraries/Core
    'libraries/Core/Assimalign.Cohesion.Core'

    # libraries/DependencyInjection
    'libraries/DependencyInjection/Assimalign.Cohesion.DependencyInjection'

    # libraries/Dns
    'libraries/Dns/Assimalign.Cohesion.Dns'
    'libraries/Dns/Assimalign.Cohesion.Dns.Client'

    # libraries/FileSystem
    'libraries/FileSystem/Assimalign.Cohesion.FileSystem'
    'libraries/FileSystem/Assimalign.Cohesion.FileSystem.Globbing'
    'libraries/FileSystem/Assimalign.Cohesion.FileSystem.InMemory'
    'libraries/FileSystem/Assimalign.Cohesion.FileSystem.Physical'
    'libraries/FileSystem/Assimalign.Cohesion.FileSystem.IsolatedStorage'
    'libraries/FileSystem/Assimalign.Cohesion.FileSystem.Aggregate'

    # libraries/Hosting
    'libraries/Hosting/Assimalign.Cohesion.Hosting'
    'libraries/Hosting/Assimalign.Cohesion.Hosting.Health'
    'libraries/Hosting/Assimalign.Cohesion.Hosting.Resources'
    'libraries/Hosting/Assimalign.Cohesion.Hosting.Telemetry'

    # libraries/Http
    'libraries/Http/Assimalign.Cohesion.Http'
    'libraries/Http/Assimalign.Cohesion.Http.Sessions'
    'libraries/Http/Assimalign.Cohesion.Http.Forms'
    'libraries/Http/Assimalign.Cohesion.Http.ClientFactory'
    'libraries/Http/Assimalign.Cohesion.Http.Connections'
    'libraries/Http/Assimalign.Cohesion.Http.ExtendedConnect'
    'libraries/Http/Assimalign.Cohesion.Http.ProtocolUpgrade'
    'libraries/Http/Assimalign.Cohesion.Http.RequestLimits'
    'libraries/Http/Assimalign.Cohesion.Http.Cookies'
    'libraries/Http/Assimalign.Cohesion.Http.Antiforgery'
    'libraries/Http/Assimalign.Cohesion.Http.Forwarded'
    'libraries/Http/Assimalign.Cohesion.Http.Streaming'

    # libraries/IdentityModel
    'libraries/IdentityModel/Assimalign.Cohesion.IdentityModel'
    'libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Protocols'
    'libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect'
    'libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Protocols.Saml'
    'libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Token'
    'libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Token.JsonWebToken'
    'libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Token.Saml'

    # libraries/Logging
    'libraries/Logging/Assimalign.Cohesion.Logging'
    'libraries/Logging/Assimalign.Cohesion.Logging.Console'
    'libraries/Logging/Assimalign.Cohesion.Logging.Debug'
    'libraries/Logging/Assimalign.Cohesion.Logging.EventSource'

    # libraries/ObjectMapping
    'libraries/ObjectMapping/Assimalign.Cohesion.ObjectMapping'

    # libraries/ObjectPool
    'libraries/ObjectPool/Assimalign.Cohesion.ObjectPool'

    # libraries/ObjectValidation
    'libraries/ObjectValidation/Assimalign.Cohesion.ObjectValidation'

    # libraries/OpenApi
    'libraries/OpenApi/Assimalign.Cohesion.OpenApi'
    'libraries/OpenApi/Assimalign.Cohesion.OpenApi.Serialization'
    'libraries/OpenApi/Assimalign.Cohesion.OpenApi.Validation'

    # libraries/OpenTelemetry
    'libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry'

    # libraries/Resilience
    'libraries/Resilience/Assimalign.Cohesion.Resilience'
    'libraries/Resilience/Assimalign.Cohesion.Resilience.CircuitBreaker'
    'libraries/Resilience/Assimalign.Cohesion.Resilience.Fallback'
    'libraries/Resilience/Assimalign.Cohesion.Resilience.Hedging'
    'libraries/Resilience/Assimalign.Cohesion.Resilience.RateLimiting'
    'libraries/Resilience/Assimalign.Cohesion.Resilience.Retry'
    'libraries/Resilience/Assimalign.Cohesion.Resilience.Timeout'

    # libraries/Security
    'libraries/Security/Assimalign.Cohesion.Security'
    'libraries/Security/Assimalign.Cohesion.Security.DataProtection'

    # resources/ApiManager
    'resources/ApiManager/Assimalign.Cohesion.ApiManager'
    'resources/ApiManager/Assimalign.Cohesion.ApiManager.ApplicationModel'
    'resources/ApiManager/Assimalign.Cohesion.ApiManager.Hosting'

    # resources/ConfigurationStore
    'resources/ConfigurationStore/Assimalign.Cohesion.ConfigurationStore'
    'resources/ConfigurationStore/Assimalign.Cohesion.ConfigurationStore.ApplicationModel'
    'resources/ConfigurationStore/Assimalign.Cohesion.ConfigurationStore.Client'
    'resources/ConfigurationStore/Assimalign.Cohesion.ConfigurationStore.Hosting'

    # resources/Database
    'resources/Database/Assimalign.Cohesion.Database'
    'resources/Database/Assimalign.Cohesion.Database.ApplicationModel'
    'resources/Database/Assimalign.Cohesion.Database.Blob'
    'resources/Database/Assimalign.Cohesion.Database.Blob.Catalog'
    'resources/Database/Assimalign.Cohesion.Database.Blob.Client'
    'resources/Database/Assimalign.Cohesion.Database.Blob.Storage'
    'resources/Database/Assimalign.Cohesion.Database.Client'
    'resources/Database/Assimalign.Cohesion.Database.Documents'
    'resources/Database/Assimalign.Cohesion.Database.Documents.Catalog'
    'resources/Database/Assimalign.Cohesion.Database.Documents.Language'
    'resources/Database/Assimalign.Cohesion.Database.Documents.Storage'
    'resources/Database/Assimalign.Cohesion.Database.Embedded'
    'resources/Database/Assimalign.Cohesion.Database.Execution'
    'resources/Database/Assimalign.Cohesion.Database.Governance'
    'resources/Database/Assimalign.Cohesion.Database.Graph'
    'resources/Database/Assimalign.Cohesion.Database.Graph.Catalog'
    'resources/Database/Assimalign.Cohesion.Database.Graph.Client'
    'resources/Database/Assimalign.Cohesion.Database.Graph.Language'
    'resources/Database/Assimalign.Cohesion.Database.Graph.Storage'
    'resources/Database/Assimalign.Cohesion.Database.Hosting'
    'resources/Database/Assimalign.Cohesion.Database.Indexing'
    'resources/Database/Assimalign.Cohesion.Database.KeyValuePair'
    'resources/Database/Assimalign.Cohesion.Database.KeyValuePair.Catalog'
    'resources/Database/Assimalign.Cohesion.Database.KeyValuePair.Client'
    'resources/Database/Assimalign.Cohesion.Database.KeyValuePair.Storage'
    'resources/Database/Assimalign.Cohesion.Database.Language'
    'resources/Database/Assimalign.Cohesion.Database.Protocol'
    'resources/Database/Assimalign.Cohesion.Database.Security'
    'resources/Database/Assimalign.Cohesion.Database.Sql'
    'resources/Database/Assimalign.Cohesion.Database.Sql.Catalog'
    'resources/Database/Assimalign.Cohesion.Database.Sql.Client'
    'resources/Database/Assimalign.Cohesion.Database.Sql.Language'
    'resources/Database/Assimalign.Cohesion.Database.Sql.Schema'
    'resources/Database/Assimalign.Cohesion.Database.Sql.Storage'
    'resources/Database/Assimalign.Cohesion.Database.Sql.Tcp'
    'resources/Database/Assimalign.Cohesion.Database.Storage'
    'resources/Database/Assimalign.Cohesion.Database.Testing'
    'resources/Database/Assimalign.Cohesion.Database.Transactions'
    'resources/Database/Assimalign.Cohesion.Database.Types'

    # resources/EmailHub
    'resources/EmailHub/Assimalign.Cohesion.EmailHub'
    'resources/EmailHub/Assimalign.Cohesion.EmailHub.ApplicationModel'
    'resources/EmailHub/Assimalign.Cohesion.EmailHub.Hosting'

    # resources/EventHub
    'resources/EventHub/Assimalign.Cohesion.EventHub'
    'resources/EventHub/Assimalign.Cohesion.EventHub.ApplicationModel'
    'resources/EventHub/Assimalign.Cohesion.EventHub.Hosting'

    # resources/IdentityHub
    'resources/IdentityHub/Assimalign.Cohesion.IdentityHub'
    'resources/IdentityHub/Assimalign.Cohesion.IdentityHub.ApplicationModel'
    'resources/IdentityHub/Assimalign.Cohesion.IdentityHub.Client'
    'resources/IdentityHub/Assimalign.Cohesion.IdentityHub.Hosting'
    'resources/IdentityHub/Assimalign.Cohesion.IdentityHub.Models'

    # resources/IoTHub
    'resources/IoTHub/Assimalign.Cohesion.IoTHub'
    'resources/IoTHub/Assimalign.Cohesion.IoTHub.ApplicationModel'
    'resources/IoTHub/Assimalign.Cohesion.IoTHub.Hosting'

    # resources/LoadBalancer
    'resources/LoadBalancer/Assimalign.Cohesion.LoadBalancer'
    'resources/LoadBalancer/Assimalign.Cohesion.LoadBalancer.ApplicationModel'
    'resources/LoadBalancer/Assimalign.Cohesion.LoadBalancer.Hosting'

    # resources/LogSpace
    'resources/LogSpace/Assimalign.Cohesion.LogSpace'
    'resources/LogSpace/Assimalign.Cohesion.LogSpace.ApplicationModel'
    'resources/LogSpace/Assimalign.Cohesion.LogSpace.Hosting'

    # resources/MediaHub
    'resources/MediaHub/Assimalign.Cohesion.MediaHub'
    'resources/MediaHub/Assimalign.Cohesion.MediaHub.ApplicationModel'
    'resources/MediaHub/Assimalign.Cohesion.MediaHub.Hosting'

    # resources/MessageHub
    'resources/MessageHub/Assimalign.Cohesion.MessageHub'
    'resources/MessageHub/Assimalign.Cohesion.MessageHub.ApplicationModel'
    'resources/MessageHub/Assimalign.Cohesion.MessageHub.Hosting'

    # resources/NatGateway
    'resources/NatGateway/Assimalign.Cohesion.NatGateway'
    'resources/NatGateway/Assimalign.Cohesion.NatGateway.ApplicationModel'
    'resources/NatGateway/Assimalign.Cohesion.NatGateway.Hosting'

    # resources/NotificationHub
    'resources/NotificationHub/Assimalign.Cohesion.NotificationHub'
    'resources/NotificationHub/Assimalign.Cohesion.NotificationHub.ApplicationModel'
    'resources/NotificationHub/Assimalign.Cohesion.NotificationHub.Hosting'

    # resources/Rezolvr
    'resources/Rezolvr/Assimalign.Cohesion.Rezolvr'
    'resources/Rezolvr/Assimalign.Cohesion.Rezolvr.ApplicationModel'
    'resources/Rezolvr/Assimalign.Cohesion.Rezolvr.Client'
    'resources/Rezolvr/Assimalign.Cohesion.Rezolvr.Hosting'

    # resources/Scheduler
    'resources/Scheduler/Assimalign.Cohesion.Scheduler'
    'resources/Scheduler/Assimalign.Cohesion.Scheduler.ApplicationModel'
    'resources/Scheduler/Assimalign.Cohesion.Scheduler.Cron'
    'resources/Scheduler/Assimalign.Cohesion.Scheduler.Hosting'
    'resources/Scheduler/Assimalign.Cohesion.Scheduler.Timer'

    # resources/SecretStore
    'resources/SecretStore/Assimalign.Cohesion.SecretStore'
    'resources/SecretStore/Assimalign.Cohesion.SecretStore.ApplicationModel'
    'resources/SecretStore/Assimalign.Cohesion.SecretStore.Client'
    'resources/SecretStore/Assimalign.Cohesion.SecretStore.Hosting'

    # resources/VpnGateway
    'resources/VpnGateway/Assimalign.Cohesion.VpnGateway'
    'resources/VpnGateway/Assimalign.Cohesion.VpnGateway.ApplicationModel'
    'resources/VpnGateway/Assimalign.Cohesion.VpnGateway.Hosting'

    # resources/Web
    'resources/Web/Assimalign.Cohesion.Web'
    'resources/Web/Assimalign.Cohesion.Web.ApplicationModel'
    'resources/Web/Assimalign.Cohesion.Web.Api'
    'resources/Web/Assimalign.Cohesion.Web.Authentication'
    'resources/Web/Assimalign.Cohesion.Web.Authentication.Bearer'
    'resources/Web/Assimalign.Cohesion.Web.Authentication.Cookie'
    'resources/Web/Assimalign.Cohesion.Web.Caching'
    'resources/Web/Assimalign.Cohesion.Web.Compression'
    'resources/Web/Assimalign.Cohesion.Web.CookiePolicy'
    'resources/Web/Assimalign.Cohesion.Web.Diagnostics'
    'resources/Web/Assimalign.Cohesion.Web.ErrorHandling'
    'resources/Web/Assimalign.Cohesion.Web.Forms'
    'resources/Web/Assimalign.Cohesion.Web.ForwardedHeaders'
    'resources/Web/Assimalign.Cohesion.Web.Health'
    'resources/Web/Assimalign.Cohesion.Web.HostFiltering'
    'resources/Web/Assimalign.Cohesion.Web.Hosting'
    'resources/Web/Assimalign.Cohesion.Web.Hosting.Health'
    'resources/Web/Assimalign.Cohesion.Web.Hosting.Resources'
    'resources/Web/Assimalign.Cohesion.Web.HttpsPolicy'
    'resources/Web/Assimalign.Cohesion.Web.ProblemDetails'
    'resources/Web/Assimalign.Cohesion.Web.Query'
    'resources/Web/Assimalign.Cohesion.Web.RateLimiting'
    'resources/Web/Assimalign.Cohesion.Web.RequestTimeouts'
    'resources/Web/Assimalign.Cohesion.Web.Routing'
    'resources/Web/Assimalign.Cohesion.Web.Serialization'
    'resources/Web/Assimalign.Cohesion.Web.Sessions'
    'resources/Web/Assimalign.Cohesion.Web.StaticFiles'
    'resources/Web/Assimalign.Cohesion.Web.Testing'

    # tooling/Cli
    'tooling/Cli/Assimalign.Cohesion.Cli'

    # tooling/templates
    'tooling/templates/Assimalign.Cohesion.Templates'
)

# SDK families. The base Sdk comes first because the domain SDKs chain to it. Each entry maps to
# sdks/<name>/Tasks/src/<name>.Tasks.csproj and packs as the NuGet id <name>.
$script:CohesionReleaseSdk = @(
    'Assimalign.Cohesion.Sdk'
    'Assimalign.Cohesion.Sdk.ApplicationModel'
    'Assimalign.Cohesion.Sdk.Web'
    'Assimalign.Cohesion.Sdk.Database'
    'Assimalign.Cohesion.Sdk.Gateway'
    'Assimalign.Cohesion.Sdk.ApiManager'
    'Assimalign.Cohesion.Sdk.ConfigurationStore'
    'Assimalign.Cohesion.Sdk.EmailHub'
    'Assimalign.Cohesion.Sdk.EventHub'
    'Assimalign.Cohesion.Sdk.IdentityHub'
    'Assimalign.Cohesion.Sdk.IoTHub'
    'Assimalign.Cohesion.Sdk.LoadBalancer'
    'Assimalign.Cohesion.Sdk.LogSpace'
    'Assimalign.Cohesion.Sdk.MediaHub'
    'Assimalign.Cohesion.Sdk.MessageHub'
    'Assimalign.Cohesion.Sdk.NatGateway'
    'Assimalign.Cohesion.Sdk.NotificationHub'
    'Assimalign.Cohesion.Sdk.Rezolvr'
    'Assimalign.Cohesion.Sdk.Scheduler'
    'Assimalign.Cohesion.Sdk.SecretStore'
    'Assimalign.Cohesion.Sdk.VpnGateway'
)

# Shared-framework families. Each produces one targeting pack (<family>.Ref) from its Refs
# producer and one runtime pack per RID (<family>.Runtime.<rid>) from its Runtime producer;
# Get-CohesionFrameworkProjectPath maps a family to those projects. Keep aligned with the
# KnownFrameworkReferences in
# sdks/Assimalign.Cohesion.Sdk/Targets/Assimalign.Cohesion.Sdk.FrameworkReference.props.
$script:CohesionReleaseFramework = @(
    'Assimalign.Cohesion.App'
    'Assimalign.Cohesion.App.Web'
    'Assimalign.Cohesion.App.Database'
    'Assimalign.Cohesion.App.ApiManager'
    'Assimalign.Cohesion.App.ConfigurationStore'
    'Assimalign.Cohesion.App.EmailHub'
    'Assimalign.Cohesion.App.EventHub'
    'Assimalign.Cohesion.App.IdentityHub'
    'Assimalign.Cohesion.App.IoTHub'
    'Assimalign.Cohesion.App.LoadBalancer'
    'Assimalign.Cohesion.App.LogSpace'
    'Assimalign.Cohesion.App.MediaHub'
    'Assimalign.Cohesion.App.MessageHub'
    'Assimalign.Cohesion.App.NatGateway'
    'Assimalign.Cohesion.App.NotificationHub'
    'Assimalign.Cohesion.App.Rezolvr'
    'Assimalign.Cohesion.App.Scheduler'
    'Assimalign.Cohesion.App.SecretStore'
    'Assimalign.Cohesion.App.VpnGateway'
)

# Runtime identifiers the release builds runtime packs for. Every entry here must also appear on
# KnownFrameworkReference's RuntimePackRuntimeIdentifiers, or the SDK cannot resolve the pack it
# asks NuGet for. All packs are managed-only, so one Linux runner cross-builds the whole set.
$script:CohesionReleaseRuntimeIdentifier = @(
    'win-x64'
    'win-arm64'
    'linux-x64'
    'linux-arm64'
    'linux-musl-x64'
    'osx-x64'
    'osx-arm64'
)

# Source-less projects to ship ANYWAY, to reserve the package id on nuget.org before the
# implementation lands. Empty by default, and the default is the safe one.
#
# Six projects under libraries/ and resources/ currently compile to an empty assembly - Amqp,
# the three Dns.Client transports, Web.Authorization, Web.Cors. They are real CI citizens, and the
# two Web ones already reach consumers inside the App.Web packs (they are listed in
# resources/Web/Assimalign.Cohesion.Web.Runtime/Directory.Build.props), but a STANDALONE
# `Assimalign.Cohesion.Amqp` package on nuget.org is a different artifact: a permanent,
# unlistable-only promise of functionality the download does not contain.
#
# So the release omits them, and the omission is reversible; publishing is not. Adding a name here
# is the deliberate opposite decision, and the guard then requires it to appear in
# $script:CohesionReleaseLibrary too, so "reserve the id" cannot half-happen.
$script:CohesionReleaseSourcelessPackage = @(
)

# Cohesion package ids a shipping project may name in a public CohesionProjectReference without the
# release producing them. Empty, and meant to stay that way: a public project reference becomes a
# <dependency> in the .nuspec, so a shipping package that names an unpublished one restores to a
# NU1101 for every consumer. Assert-CohesionReleaseInventory enforces the closure.
$script:CohesionReleaseUnpublishedDependency = @(
)

# Packable, source-bearing projects that predate the repository-wide workflow-matrix guard and are
# deliberately not treated as new blind spots. Keys are repository-relative csproj paths rather
# than project names so two same-named projects cannot hide behind one matrix entry. Every entry
# needs a reason; Assert-CohesionReleaseInventory rejects blank and stale entries.
$script:CohesionCiMatrixExclusion = [ordered]@{
    'libraries/Connections/Assimalign.Cohesion.Connections.NamedPipes/src/Assimalign.Cohesion.Connections.NamedPipes.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/Content/Assimalign.Cohesion.Content.Binary/src/Assimalign.Cohesion.Content.Binary.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/Content/Assimalign.Cohesion.Content.Bmff/src/Assimalign.Cohesion.Content.Bmff.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/Content/Assimalign.Cohesion.Content.Exe/src/Assimalign.Cohesion.Content.Exe.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/Content/Assimalign.Cohesion.Content.Media/src/Assimalign.Cohesion.Content.Media.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/Content/Assimalign.Cohesion.Content.Mkv/src/Assimalign.Cohesion.Content.Mkv.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/Content/Assimalign.Cohesion.Content.Mpeg/src/Assimalign.Cohesion.Content.Mpeg.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/Http/Assimalign.Cohesion.Http.Connections/examples/Assimalign.Cohesion.Http.Connections.Examples.Http1/Assimalign.Cohesion.Http.Connections.Examples.Http1.csproj' = 'Example project is not an independently shipped CI matrix entry.'
    'libraries/Http/Assimalign.Cohesion.Http.Connections/examples/Assimalign.Cohesion.Http.Connections.Examples.Http2/Assimalign.Cohesion.Http.Connections.Examples.Http2.csproj' = 'Example project is not an independently shipped CI matrix entry.'
    'libraries/Http/Assimalign.Cohesion.Http.Connections/examples/Assimalign.Cohesion.Http.Connections.Examples.Http3/Assimalign.Cohesion.Http.Connections.Examples.Http3.csproj' = 'Example project is not an independently shipped CI matrix entry.'
    'libraries/Http/Assimalign.Cohesion.Http.DigestFields/src/Assimalign.Cohesion.Http.DigestFields.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/Http/Assimalign.Cohesion.Http.InterimResponses/src/Assimalign.Cohesion.Http.InterimResponses.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/Http/Assimalign.Cohesion.Http.ServerSentEvents/examples/Assimalign.Cohesion.Http.ServerSentEvents.Examples.Sse/Assimalign.Cohesion.Http.ServerSentEvents.Examples.Sse.csproj' = 'Example project is not an independently shipped CI matrix entry.'
    'libraries/Http/Assimalign.Cohesion.Http.ServerSentEvents/src/Assimalign.Cohesion.Http.ServerSentEvents.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/OpenApi/Assimalign.Cohesion.OpenApi.Attributes/src/Assimalign.Cohesion.OpenApi.Attributes.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/OpenApi/Assimalign.Cohesion.OpenApi.Fluent/src/Assimalign.Cohesion.OpenApi.Fluent.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/OpenApi/Assimalign.Cohesion.OpenApi.Generation/src/Assimalign.Cohesion.OpenApi.Generation.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/OpenApi/Assimalign.Cohesion.OpenApi.Integration/src/Assimalign.Cohesion.OpenApi.Integration.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'libraries/OpenApi/Assimalign.Cohesion.OpenApi.Versioning/src/Assimalign.Cohesion.OpenApi.Versioning.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'resources/Database/Assimalign.Cohesion.Database.Cache/src/Assimalign.Cohesion.Database.Cache.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'resources/Database/Assimalign.Cohesion.Database.Cache/src/Assimalign.Cohesion.Database.Cache.Tests.csproj' = 'Stray duplicate csproj shares the Cache source directory; cleanup or an explicit packability fix is outside #944.'
    'resources/Database/Assimalign.Cohesion.Database.Replication/src/Assimalign.Cohesion.Database.Replication.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'resources/Database/Assimalign.Cohesion.Database.Sql.Replication/src/Assimalign.Cohesion.Database.Sql.Replication.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'resources/MessageHub/Assimalign.Cohesion.MessageHub.Client/src/Assimalign.Cohesion.MessageHub.Client.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'resources/NotificationHub/Assimalign.Cohesion.NotificationHub.Client/src/Assimalign.Cohesion.NotificationHub.Client.csproj' = 'Pre-existing project predates the matrix guard; CI and release onboarding require a separate work item.'
    'resources/Web/Assimalign.Cohesion.Web.Hosting/examples/Hosting1/Hosting1.csproj' = 'Example project is not an independently shipped CI matrix entry.'
    'tooling/scripts/src/Assimalign.Cohesion.DevScripts/Assimalign.Cohesion.DevScripts.csproj' = 'Legacy tooling has no dedicated CI workflow; onboarding requires a separate release decision.'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-app/Acme.Api/Acme.Api.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-app/Acme.Database/Acme.Database.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-app/Acme.Gateway/Acme.Gateway.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-composite/CohesionProject.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-configurationstore/CohesionProject.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-database/CohesionProject.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-gateway/CohesionProject.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-identityhub/CohesionProject.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Gateway/Example.Gateway/Example.Gateway.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Identity/Example.Identity.Gateway/Example.Identity.Gateway.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Identity/Example.Identity.IdentityHub/Example.Identity.IdentityHub.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Networking/Example.Networking.Gateway/Example.Networking.Gateway.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Networking/Example.Networking.Rezolvr/Example.Networking.Rezolvr.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Networking/Example.Networking.VpnGateway/Example.Networking.VpnGateway.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Platform/Example.Platform.ConfigurationStore/Example.Platform.ConfigurationStore.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Platform/Example.Platform.Gateway/Example.Platform.Gateway.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Platform/Example.Platform.LogSpace/Example.Platform.LogSpace.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Platform/Example.Platform.SecretStore/Example.Platform.SecretStore.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppA/Example.AppA.Api/Example.AppA.Api.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppA/Example.AppA.Database/Example.AppA.Database.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppA/Example.AppA.Gateway/Example.AppA.Gateway.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppA/Example.AppA.SecretStore/Example.AppA.SecretStore.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppA/Example.AppA.Spa/Example.AppA.Spa.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppB/Example.AppB.Api/Example.AppB.Api.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppB/Example.AppB.Database/Example.AppB.Database.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppB/Example.AppB.Gateway/Example.AppB.Gateway.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppB/Example.AppB.SecretStore/Example.AppB.SecretStore.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppB/Example.AppB.Spa/Example.AppB.Spa.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppC/Example.AppC.Api/Example.AppC.Api.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppC/Example.AppC.Database/Example.AppC.Database.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppC/Example.AppC.Gateway/Example.AppC.Gateway.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppC/Example.AppC.SecretStore/Example.AppC.SecretStore.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-landing-zone/Zones/AppC/Example.AppC.Spa/Example.AppC.Spa.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-rezolvr/CohesionProject.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-secretstore/CohesionProject.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-spa/CohesionProject.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
    'tooling/templates/Assimalign.Cohesion.Templates/src/content/cohesion-web/CohesionProject.csproj' = 'dotnet new template content; instantiated and built by tooling-templates.yml'
}

# Workflows under .github/workflows that are not per-area release-library matrices, and so are not
# part of the inventory/CI equality check. Names may be reserved before their files land; only
# workflows present on disk are parsed. The repository-wide blind-spot check still reads static
# project matrices from every workflow, including entries in this list.
$script:CohesionNonMatrixWorkflow = @(
    'release.yml'
    'release-inventory.yml'
    'analyzers.yml'
    'sdk-smoke.yml'
    'credential-guard.yml'
)

# ---------------------------------------------------------------------------------------------
# Inventory accessors
# ---------------------------------------------------------------------------------------------

function Join-CohesionPath {
    <#
    .SYNOPSIS
        Joins a forward-slash, repository-relative path onto a root using the platform separator.

    .DESCRIPTION
        Every constructed path in this module is written with forward slashes and converted here,
        so the results are real platform paths rather than paths that only work by accident.

        Measured on PowerShell 7.6 / linux-arm64: Join-Path normalizes '\' to '/', and the
        provider cmdlets (Test-Path -LiteralPath, Get-Content -LiteralPath) accept a raw
        backslash-separated path too. But raw .NET does not - [System.IO.File]::Exists() on that
        same string returns $false - and neither does a native process. These paths are handed to
        `dotnet pack` by Pack-Release.ps1, which puts them squarely outside the range where the
        cmdlet-level leniency applies.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Root,

        [Parameter(Mandatory)]
        [string] $Relative
    )

    return Join-Path $Root ($Relative -replace '/', [System.IO.Path]::DirectorySeparatorChar)
}

function Resolve-CohesionRepositoryDirectory {
    [CmdletBinding()]
    param(
        [string] $RepositoryDirectory
    )

    if (-not $RepositoryDirectory) {
        # modules/ sits two levels under installer/, which is one level under the repo root.
        $RepositoryDirectory = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
    }

    # Not [System.IO.Path]::GetFullPath: that anchors a relative path to the PROCESS working
    # directory, which PowerShell does not keep in step with its own location. Set-Location in a
    # session leaves the two pointing at different folders, and a relative -RepositoryDirectory
    # would then silently resolve somewhere the caller never named.
    $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RepositoryDirectory)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "Repository directory '$resolved' does not exist."
    }

    return $resolved
}

function Get-CohesionReleaseRuntimeIdentifier {
    <#
    .SYNOPSIS
        The runtime identifiers a release builds framework runtime packs for.
    #>
    [CmdletBinding()]
    param()

    return , @($script:CohesionReleaseRuntimeIdentifier)
}

function Get-CohesionReleaseSdk {
    <#
    .SYNOPSIS
        The SDK family names, base first. Consumed by the release packer and by
        installer/scripts/Install-Local.ps1, so the local feed and the release cannot disagree.
    #>
    [CmdletBinding()]
    param()

    return , @($script:CohesionReleaseSdk)
}

function Get-CohesionReleaseFramework {
    <#
    .SYNOPSIS
        The shared-framework family names. Each yields one <family>.Ref pack and one
        <family>.Runtime.<rid> pack per RID.
    #>
    [CmdletBinding()]
    param()

    return , @($script:CohesionReleaseFramework)
}

function Get-CohesionFrameworkProjectPath {
    <#
    .SYNOPSIS
        The repository-relative csproj path of a shared-framework producer.

    .DESCRIPTION
        The single encoding of the framework producer naming convention
        (.claude/rules/build-system.md, "Framework producer projects"). A producer project is
        named for the area that owns it; its assembly, package, and framework names keep the App
        segment and never change with the project:

            Assimalign.Cohesion.App         -> libraries/App/Assimalign.Cohesion.App.<Kind>/src/Assimalign.Cohesion.App.<Kind>.csproj
            Assimalign.Cohesion.App.<Area>  -> resources/<Area>/Assimalign.Cohesion.<Area>.<Kind>/src/Assimalign.Cohesion.<Area>.<Kind>.csproj

        Install-Local.ps1, the release pack plan, and Assert-CohesionReleaseInventory all resolve
        producers through this function, so none of them can disagree about where one lives.

    .PARAMETER Framework
        The framework family name, for example Assimalign.Cohesion.App.Web.

    .PARAMETER Kind
        Refs for the targeting-pack producer, Runtime for the runtime-pack producer.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Framework,

        [Parameter(Mandatory)]
        [ValidateSet('Refs', 'Runtime')]
        [string] $Kind
    )

    $appFramework = 'Assimalign.Cohesion.App'
    if ($Framework -ceq $appFramework) {
        return "libraries/App/$appFramework.$Kind/src/$appFramework.$Kind.csproj"
    }

    $areaPrefix = "$appFramework."
    $area = if ($Framework.StartsWith($areaPrefix, [System.StringComparison]::Ordinal)) {
        $Framework.Substring($areaPrefix.Length)
    }
    if ([string]::IsNullOrEmpty($area) -or $area.Contains('.')) {
        throw "'$Framework' is not a shared-framework family name. Expected '$appFramework' or '$appFramework.<Area>'."
    }

    return "resources/$area/Assimalign.Cohesion.$area.$Kind/src/Assimalign.Cohesion.$area.$Kind.csproj"
}

function Get-CohesionReleaseLibrary {
    <#
    .SYNOPSIS
        The shipping libraries and resources, as objects carrying Area, Category, Project, the
        repository-relative csproj path, and the NuGet package id.

    .DESCRIPTION
        Project folder name and package id are the same for every library and resource in this
        repository; the pack assertions still compare the produced files against these ids rather
        than assuming that stays true.
    #>
    [CmdletBinding()]
    param(
        [string] $RepositoryDirectory
    )

    $repositoryDirectory = Resolve-CohesionRepositoryDirectory -RepositoryDirectory $RepositoryDirectory

    return @(
        foreach ($entry in $script:CohesionReleaseLibrary) {
            $segment = $entry.Split('/')
            if ($segment.Count -ne 3) {
                throw "Inventory entry '$entry' must be '<area>/<category>/<project>'."
            }

            $area = $segment[0]
            $category = $segment[1]
            $project = $segment[2]
            $relativePath = "$area/$category/$project/src/$project.csproj"

            [pscustomobject]@{
                Kind        = 'Library'
                Area        = $area
                Category    = $category
                Project     = $project
                PackageId   = $project
                ProjectPath = Join-CohesionPath -Root $repositoryDirectory -Relative $relativePath
                RelativePath = $relativePath
            }
        }
    )
}

function Get-CohesionReleaseProject {
    <#
    .SYNOPSIS
        The complete, ordered pack plan for a release.

    .DESCRIPTION
        Order is deterministic and mirrors the dependency direction of the distribution shapes:
        libraries and resources first (nothing in the release depends on the packs), then the
        framework targeting and runtime packs that bundle those assemblies, then the SDK packs
        that resolve the frameworks. It is NOT a topological sort of the library graph - NuGet
        does not resolve dependencies at push time, so the order matters only for reproducibility
        and for reading the log.

    .PARAMETER RuntimeIdentifier
        RIDs to emit framework runtime pack entries for. Defaults to the full release set.
    #>
    [CmdletBinding()]
    param(
        [string] $RepositoryDirectory,

        [string[]] $RuntimeIdentifier
    )

    $repositoryDirectory = Resolve-CohesionRepositoryDirectory -RepositoryDirectory $RepositoryDirectory
    if (-not $RuntimeIdentifier -or $RuntimeIdentifier.Count -eq 0) {
        $RuntimeIdentifier = $script:CohesionReleaseRuntimeIdentifier
    }

    $toFullPath = {
        param([string] $Relative)
        Join-CohesionPath -Root $repositoryDirectory -Relative $Relative
    }

    return @(
        Get-CohesionReleaseLibrary -RepositoryDirectory $repositoryDirectory

        foreach ($framework in $script:CohesionReleaseFramework) {
            $relativePath = Get-CohesionFrameworkProjectPath -Framework $framework -Kind Refs
            [pscustomobject]@{
                Kind              = 'FrameworkRef'
                Project           = "$framework.Refs"
                PackageId         = "$framework.Ref"
                ProjectPath       = & $toFullPath $relativePath
                RelativePath      = $relativePath
                RuntimeIdentifier = $null
            }
        }

        foreach ($framework in $script:CohesionReleaseFramework) {
            $relativePath = Get-CohesionFrameworkProjectPath -Framework $framework -Kind Runtime
            foreach ($rid in $RuntimeIdentifier) {
                [pscustomobject]@{
                    Kind              = 'FrameworkRuntime'
                    Project           = "$framework.Runtime"
                    PackageId         = "$framework.Runtime.$rid"
                    ProjectPath       = & $toFullPath $relativePath
                    RelativePath      = $relativePath
                    RuntimeIdentifier = $rid
                }
            }
        }

        foreach ($sdk in $script:CohesionReleaseSdk) {
            $relativePath = "sdks/$sdk/Tasks/src/$sdk.Tasks.csproj"
            [pscustomobject]@{
                Kind              = 'Sdk'
                Project           = "$sdk.Tasks"
                PackageId         = $sdk
                ProjectPath       = & $toFullPath $relativePath
                RelativePath      = $relativePath
                RuntimeIdentifier = $null
            }
        }
    )
}

function Get-CohesionReleasePackageId {
    <#
    .SYNOPSIS
        Every NuGet package id a release produces, in publication order.
    #>
    [CmdletBinding()]
    param(
        [string] $RepositoryDirectory,

        [string[]] $RuntimeIdentifier
    )

    return @(
        Get-CohesionReleaseProject `
            -RepositoryDirectory $RepositoryDirectory `
            -RuntimeIdentifier $RuntimeIdentifier |
            ForEach-Object PackageId
    )
}

# ---------------------------------------------------------------------------------------------
# Drift guards
# ---------------------------------------------------------------------------------------------

function Test-CohesionProjectHasSource {
    <#
    .SYNOPSIS
        True when a project folder contains at least one C# source file outside bin/ and obj/.

    .DESCRIPTION
        The test for "is this a product or a scaffold". A scaffolded project compiles and passes
        CI - there is simply nothing in it - so no build signal distinguishes the two, and the
        release has to look at the source tree to tell them apart.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ProjectPath
    )

    $sourceDirectory = Split-Path -Parent $ProjectPath
    if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
        return $false
    }

    $sourceFile = @(
        Get-ChildItem -LiteralPath $sourceDirectory -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    )

    return $sourceFile.Count -gt 0
}

function Test-CohesionProjectIsPackable {
    <#
    .SYNOPSIS
        True when repository-owned MSBuild declarations make a project packable.

    .DESCRIPTION
        Keeps the inventory guard build-free while honoring the declarations that matter in this
        repository: SDK-style projects default to packable, the nearest Directory.Build.props and
        its explicit parent chain may override that default, test projects are non-packable, and
        the project body wins last. Conditional declarations are interpreted conservatively: if a
        project can be packable in any configuration, the matrix guard includes it. Non-SDK
        projects need an explicit IsPackable=true declaration.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryDirectory,

        [Parameter(Mandatory)]
        [string] $ProjectPath
    )

    $readXml = {
        param([string] $Path)

        try {
            return [xml] (Get-Content -LiteralPath $Path -Raw)
        }
        catch {
            # A malformed project will fail its build too. Treating unknown metadata as potentially
            # packable is the safe direction for a blind-spot guard: it cannot hide the project.
            return $null
        }
    }

    $projectXml = & $readXml $ProjectPath
    if ($null -eq $projectXml) {
        return $true
    }

    $projectRoot = $projectXml.DocumentElement
    $isPackable = -not [string]::IsNullOrWhiteSpace([string] $projectRoot.GetAttribute('Sdk')) -or
        @($projectXml.SelectNodes('//*[local-name()="Sdk" and string-length(@Name) > 0]')).Count -gt 0 -or
        @($projectXml.SelectNodes('//*[local-name()="Import" and string-length(@Sdk) > 0]')).Count -gt 0

    $hasConditionalContext = {
        param([System.Xml.XmlNode] $Node)

        $context = $Node
        while ($null -ne $context -and $context.NodeType -ne [System.Xml.XmlNodeType]::Document) {
            if ($context.NodeType -eq [System.Xml.XmlNodeType]::Element) {
                if (-not [string]::IsNullOrWhiteSpace([string] $context.GetAttribute('Condition')) -or
                    $context.LocalName -in @('When', 'Otherwise', 'Target')) {
                    return $true
                }
            }

            $context = $context.ParentNode
        }

        return $false
    }

    $applyPackability = {
        param(
            [bool] $Current,
            [string] $Path
        )

        $xml = & $readXml $Path
        if ($null -eq $xml) {
            return $true
        }

        foreach ($node in @($xml.SelectNodes(
                    '//*[local-name()="PropertyGroup"]/*[local-name()="IsPackable"]'))) {
            $value = $node.InnerText.Trim()
            if (& $hasConditionalContext $node) {
                # A conditional false leaves the other branch unchanged. A conditional true or an
                # expression can make the project packable, so retain that possibility.
                if ($value -ine 'false') {
                    $Current = $true
                }
                continue
            }

            if ($value -ieq 'true') {
                $Current = $true
            }
            elseif ($value -ieq 'false') {
                $Current = $false
            }
            else {
                $Current = $true
            }
        }

        return $Current
    }

    # MSBuild auto-imports only the nearest Directory.Build.props. Repository props explicitly
    # import their parent, so follow that declared chain instead of assuming every ancestor loads.
    $repositoryDirectory = [System.IO.Path]::GetFullPath($RepositoryDirectory).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $pathComparison = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
        [System.StringComparison]::OrdinalIgnoreCase
    }
    else {
        [System.StringComparison]::Ordinal
    }
    $pathComparer = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') {
        [System.StringComparer]::OrdinalIgnoreCase
    }
    else {
        [System.StringComparer]::Ordinal
    }
    $repositoryPrefix = $repositoryDirectory + [System.IO.Path]::DirectorySeparatorChar
    $isRepositoryPath = {
        param([string] $Path)

        $fullPath = [System.IO.Path]::GetFullPath($Path)
        return $fullPath.Equals($repositoryDirectory, $pathComparison) -or
            $fullPath.StartsWith($repositoryPrefix, $pathComparison)
    }

    $propertyFile = [System.Collections.Generic.List[string]]::new()
    $directory = Get-Item -LiteralPath (Split-Path -Parent $ProjectPath)
    $nearestPropertyFile = $null
    while ($null -ne $directory -and (& $isRepositoryPath $directory.FullName)) {
        $candidate = Join-Path $directory.FullName 'Directory.Build.props'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $nearestPropertyFile = $candidate
            break
        }

        if ($directory.FullName.Equals($repositoryDirectory, $pathComparison)) {
            break
        }

        $directory = $directory.Parent
    }

    $visitedPropertyFile = [System.Collections.Generic.HashSet[string]]::new($pathComparer)
    while ($nearestPropertyFile -and $visitedPropertyFile.Add($nearestPropertyFile)) {
        $propertyFile.Insert(0, $nearestPropertyFile)
        $propertyXml = & $readXml $nearestPropertyFile
        $parentPropertyFile = $null
        if ($null -ne $propertyXml) {
            foreach ($import in @($propertyXml.DocumentElement.ChildNodes | Where-Object LocalName -eq 'Import')) {
                $importPath = [string] $import.GetAttribute('Project')
                if (-not [string]::IsNullOrWhiteSpace([string] $import.GetAttribute('Condition')) -or
                    [string]::IsNullOrWhiteSpace($importPath) -or
                    $importPath.Contains('$(')) {
                    continue
                }

                $portableImportPath = $importPath -replace '[\\/]', [System.IO.Path]::DirectorySeparatorChar
                $resolvedImport = [System.IO.Path]::GetFullPath(
                    (Join-Path (Split-Path -Parent $nearestPropertyFile) $portableImportPath))
                if ((Split-Path -Leaf $resolvedImport).Equals('Directory.Build.props', $pathComparison) -and
                    (& $isRepositoryPath $resolvedImport) -and
                    (Test-Path -LiteralPath $resolvedImport -PathType Leaf)) {
                    $parentPropertyFile = $resolvedImport
                    break
                }
            }
        }

        $nearestPropertyFile = $parentPropertyFile
    }

    foreach ($file in $propertyFile) {
        $isPackable = & $applyPackability $isPackable $file
    }

    $isTestProject = @(
        $projectXml.SelectNodes(
            '//*[local-name()="PropertyGroup"]/*[local-name()="IsTestProject"]') |
            Where-Object {
                $_.InnerText.Trim() -ieq 'true' -and
                -not (& $hasConditionalContext $_)
            }
    ).Count -gt 0
    $hasTestSdk = @(
        $projectXml.SelectNodes(
            '//*[local-name()="ItemGroup"]/*[local-name()="PackageReference" or local-name()="CohesionPackageReference"]') |
            Where-Object {
                $_.GetAttribute('Include') -ieq 'Microsoft.NET.Test.Sdk' -and
                -not (& $hasConditionalContext $_)
            }
    ).Count -gt 0
    if ($isTestProject -or $hasTestSdk) {
        $isPackable = $false
    }

    return & $applyPackability $isPackable $ProjectPath
}

function Get-CohesionWorkflowMatrixProject {
    <#
    .SYNOPSIS
        Every project entry in a static projects matrix in any repository workflow.

    .DESCRIPTION
        This is intentionally independent of the release-library matrix parser. A workflow such
        as analyzers.yml may not participate in release inventory equality but can still prove
        that a project is visible to CI. Dynamic matrices are represented by their source
        inventory instead and therefore do not contribute names here.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryDirectory
    )

    $removeYamlComment = {
        param([string] $Line)

        $singleQuoted = $false
        $doubleQuoted = $false
        $escaped = $false
        for ($index = 0; $index -lt $Line.Length; $index++) {
            $character = $Line[$index]
            if ($doubleQuoted -and $escaped) {
                $escaped = $false
                continue
            }

            if ($doubleQuoted -and $character -eq '\') {
                $escaped = $true
                continue
            }

            if (-not $doubleQuoted -and $character -eq "'") {
                if ($singleQuoted -and $index + 1 -lt $Line.Length -and $Line[$index + 1] -eq "'") {
                    $index++
                    continue
                }

                $singleQuoted = -not $singleQuoted
                continue
            }

            if (-not $singleQuoted -and $character -eq '"') {
                $doubleQuoted = -not $doubleQuoted
                continue
            }

            if (-not $singleQuoted -and -not $doubleQuoted -and $character -eq '#') {
                return $Line.Substring(0, $index)
            }
        }

        return $Line
    }

    $workflowDirectory = Join-Path $RepositoryDirectory '.github/workflows'
    $project = [System.Collections.Generic.List[object]]::new()
    foreach ($workflow in @(Get-ChildItem -LiteralPath $workflowDirectory -Filter '*.yml' -File)) {
        $context = [System.Collections.Generic.List[object]]::new()
        $matrixValue = $null
        $blockScalarIndent = $null
        foreach ($rawLine in @(Get-Content -LiteralPath $workflow.FullName)) {
            $line = (& $removeYamlComment $rawLine).TrimEnd()
            if ([string]::IsNullOrWhiteSpace($line)) {
                continue
            }

            $lineIndent = ([regex]::Match($line, '^[ ]*')).Value.Length
            if ($null -ne $blockScalarIndent) {
                if ($lineIndent -gt $blockScalarIndent) {
                    continue
                }

                $blockScalarIndent = $null
            }

            $blockScalar = [regex]::Match(
                $line,
                @'
(?x)^
(?<indent>[ ]*)
(?:
    (?:-\s+)?(?:[A-Za-z0-9_-]+|'(?:[^']|'')+'|"(?:[^"\\]|\\.)+"):
    \s*(?:[!&]\S+\s+)*[|>][0-9+-]*
  |
    -\s+(?:[!&]\S+\s+)*[|>][0-9+-]*
)
\s*$
'@)
            if ($blockScalar.Success) {
                $blockScalarIndent = $blockScalar.Groups['indent'].Value.Length
                continue
            }

            if ($null -ne $matrixValue) {
                $matrixValue += "`n$line"
                if ($line -notmatch ']') {
                    continue
                }

                foreach ($entry in [regex]::Matches($matrixValue, '[''"]([^''"]+)[''"]')) {
                    $project.Add([pscustomobject]@{
                        Project  = $entry.Groups[1].Value
                        Workflow = $workflow.Name
                    })
                }
                $matrixValue = $null
                continue
            }

            $mapping = [regex]::Match($line, '^(?<indent>[ ]*)(?<key>[A-Za-z0-9_-]+):(?<value>.*)$')
            if (-not $mapping.Success) {
                continue
            }

            $indent = $mapping.Groups['indent'].Value.Length
            while ($context.Count -gt 0 -and $context[$context.Count - 1].Indent -ge $indent) {
                $context.RemoveAt($context.Count - 1)
            }

            $key = $mapping.Groups['key'].Value
            $value = $mapping.Groups['value'].Value.Trim()
            $isProjectMatrix = $key -eq 'projects' -and
                $context.Count -ge 2 -and
                $context[$context.Count - 1].Key -eq 'matrix' -and
                $context[$context.Count - 2].Key -eq 'strategy'

            if ($isProjectMatrix -and $value.StartsWith('[')) {
                if ($value -match ']') {
                    foreach ($entry in [regex]::Matches($value, '[''"]([^''"]+)[''"]')) {
                        $project.Add([pscustomobject]@{
                            Project  = $entry.Groups[1].Value
                            Workflow = $workflow.Name
                        })
                    }
                }
                else {
                    $matrixValue = $value
                }
            }

            if ([string]::IsNullOrWhiteSpace($value)) {
                $context.Add([pscustomobject]@{
                    Indent = $indent
                    Key    = $key
                })
            }
        }
    }

    return @($project)
}

function Get-CohesionPackableSourceProject {
    <#
    .SYNOPSIS
        Every packable, source-bearing project in a repository-owned product or tooling root.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryDirectory
    )

    $project = [System.Collections.Generic.List[object]]::new()
    foreach ($rootName in @(
            'libraries',
            'resources',
            'sdks',
            'analyzers',
            'tooling',
            'extensions')) {
        $rootDirectory = Join-Path $RepositoryDirectory $rootName
        if (-not (Test-Path -LiteralPath $rootDirectory -PathType Container)) {
            continue
        }

        foreach ($projectFile in @(Get-ChildItem -LiteralPath $rootDirectory -Recurse -Filter '*.csproj' -File)) {
            if ($projectFile.FullName -match '[\\/](bin|obj)[\\/]') {
                continue
            }

            if (-not (Test-CohesionProjectIsPackable `
                    -RepositoryDirectory $RepositoryDirectory `
                    -ProjectPath $projectFile.FullName) -or
                -not (Test-CohesionProjectHasSource -ProjectPath $projectFile.FullName)) {
                continue
            }

            $relativePath = $projectFile.FullName.Substring($RepositoryDirectory.Length + 1) -replace '\\', '/'
            $segment = $relativePath.Split('/')
            $ciKey = $null
            if ($segment.Count -eq 5 -and
                $segment[0] -in @('libraries', 'resources') -and
                $segment[3] -eq 'src' -and
                $segment[4] -eq "$($segment[2]).csproj") {
                $ciKey = "$($segment[0])/$($segment[1])/$($segment[2])"
            }

            $project.Add([pscustomobject]@{
                Project      = $projectFile.BaseName
                ProjectPath  = $projectFile.FullName
                RelativePath = $relativePath
                CiKey        = $ciKey
            })
        }
    }

    return @($project)
}

function Get-CohesionSourceProjectPath {
    <#
    .SYNOPSIS
        Resolves a Cohesion project name to its shipping csproj under libraries/ or resources/,
        or $null when no such project exists.

    .DESCRIPTION
        Mirrors how build/Targets/Build.References.Projects.targets resolves a
        CohesionProjectReference by name: one src csproj per project folder, name-matched. The
        dependency-closure check needs to tell "references something real that the release omits"
        (a blocker) from "references a name that no longer exists" (dead weight the build drops).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryDirectory,

        [Parameter(Mandatory)]
        [string] $ProjectName
    )

    foreach ($area in @('libraries', 'resources')) {
        $areaDirectory = Join-Path $RepositoryDirectory $area
        if (-not (Test-Path -LiteralPath $areaDirectory -PathType Container)) {
            continue
        }

        $match = @(
            Get-ChildItem -LiteralPath $areaDirectory -Recurse -Filter "$ProjectName.csproj" -File -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Directory.Name -eq 'src' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
                }
        )
        if ($match.Count -gt 0) {
            return $match[0].FullName
        }
    }

    return $null
}

function Get-CohesionCiMatrixEntry {
    <#
    .SYNOPSIS
        Every '<area>/<category>/<project>' triple the per-area CI workflows build.

    .DESCRIPTION
        The per-area workflows are machine-uniform: one 'projects: [ ... ]' matrix leg plus a
        single area/category pair handed to .github/actions/build. Parsing that shape is enough
        to cross-check the release inventory, and a workflow that stops matching it is reported
        rather than silently ignored - see Assert-CohesionReleaseInventory.
    #>
    [CmdletBinding()]
    param(
        [string] $RepositoryDirectory
    )

    $repositoryDirectory = Resolve-CohesionRepositoryDirectory -RepositoryDirectory $RepositoryDirectory
    $workflowDirectory = Join-Path $repositoryDirectory '.github/workflows'
    if (-not (Test-Path -LiteralPath $workflowDirectory -PathType Container)) {
        throw "No workflow directory at $workflowDirectory."
    }

    $entry = [System.Collections.Generic.List[object]]::new()
    $unparsed = [System.Collections.Generic.List[string]]::new()

    foreach ($workflow in @(Get-ChildItem -LiteralPath $workflowDirectory -Filter '*.yml' -File | Sort-Object Name)) {
        if ($script:CohesionNonMatrixWorkflow -contains $workflow.Name) {
            continue
        }

        $content = Get-Content -LiteralPath $workflow.FullName -Raw
        $area = [regex]::Match($content, 'area:\s*[''"]([^''"]+)[''"]')
        $category = [regex]::Match($content, 'category:\s*[''"]([^''"]+)[''"]')
        $projects = [regex]::Match($content, 'projects:\s*\[(.*?)\]', 'Singleline')
        if (-not ($area.Success -and $category.Success -and $projects.Success)) {
            $unparsed.Add($workflow.Name)
            continue
        }

        foreach ($project in [regex]::Matches($projects.Groups[1].Value, '[''"]([^''"]+)[''"]')) {
            $entry.Add([pscustomobject]@{
                Area     = $area.Groups[1].Value
                Category = $category.Groups[1].Value
                Project  = $project.Groups[1].Value
                Workflow = $workflow.Name
            })
        }
    }

    return [pscustomobject]@{
        Entry    = @($entry)
        Unparsed = @($unparsed)
    }
}

function Assert-CohesionReleaseInventory {
    <#
    .SYNOPSIS
        Fails unless the release inventory, the repository, and CI all agree.

    .DESCRIPTION
        Eight checks, each reporting every offender rather than the first:

          1. Every inventory entry's csproj exists on disk.
          2. No inventory entry is marked <IsPackable>false</IsPackable>.
          3. Every inventory entry is built by a per-area CI workflow.
          4. Every packable project a CI workflow builds is in the inventory.
          5. Every SDK and framework family has its projects on disk, and no SDK folder or
             framework producer on disk is missing from the lists. A framework producer also has
             to sit at its family's conventional path and keep its family's assembly name.
          6. The inventory is CLOSED under public package dependencies.
          7. No inventory entry ships without source unless it is a declared reservation.
          8. Every packable, source-bearing project in a repository-owned product or tooling root
             appears in a workflow project matrix or has an exact, reason-bearing exclusion.

        Checks 3 and 4 are what make the "ships only if CI validates it" contract real in both
        directions. Check 4 is why adding a project to an area workflow without adding it here
        fails the release rather than quietly shipping a smaller set.

        Check 6 exists because 3 and 4 alone are not sufficient. "CI validates it" is a claim about
        the build; it says nothing about the package graph. A shipping project may name a public
        CohesionProjectReference on a project that is real, compiles fine, and is simply absent
        from every CI matrix - and that reference becomes a <dependency> in the .nuspec. The
        package then publishes successfully and restores to NU1101 for every consumer, which is the
        worst failure shape available: green pipeline, broken package, and nuget.org will not let
        you take it back.

        Check 8 closes the remaining blind spot: a project absent from BOTH the release inventory
        and every matrix was invisible to checks 3 and 4. Exact-path exclusions acknowledge the
        pre-existing gaps without allowing a new project to disappear behind a wildcard.
    #>
    [CmdletBinding()]
    param(
        [string] $RepositoryDirectory
    )

    $repositoryDirectory = Resolve-CohesionRepositoryDirectory -RepositoryDirectory $RepositoryDirectory
    $library = @(Get-CohesionReleaseLibrary -RepositoryDirectory $repositoryDirectory)
    $failure = [System.Collections.Generic.List[string]]::new()
    $sourceless = [System.Collections.Generic.List[string]]::new()

    # 1 + 2: the inventory describes real, packable projects.
    foreach ($item in $library) {
        if (-not (Test-Path -LiteralPath $item.ProjectPath -PathType Leaf)) {
            $failure.Add("Inventory entry has no project file: $($item.RelativePath)")
            continue
        }

        if ((Get-Content -LiteralPath $item.ProjectPath -Raw) -match '<IsPackable>\s*false\s*</IsPackable>') {
            $failure.Add("Inventory entry is marked IsPackable=false and cannot ship: $($item.RelativePath)")
        }
    }

    # 3 + 4: the inventory and the per-area CI matrices describe the same set.
    $ci = Get-CohesionCiMatrixEntry -RepositoryDirectory $repositoryDirectory
    foreach ($workflow in $ci.Unparsed) {
        $failure.Add("Workflow .github/workflows/$workflow no longer exposes a parseable area/category/projects matrix; the release inventory cross-check cannot see it. Restore the shape or add it to `$script:CohesionNonMatrixWorkflow.")
    }

    $ciKey = [System.Collections.Generic.HashSet[string]]::new(
        [string[]] @($ci.Entry | ForEach-Object { "$($_.Area)/$($_.Category)/$($_.Project)" }),
        [System.StringComparer]::Ordinal)
    $inventoryKey = [System.Collections.Generic.HashSet[string]]::new(
        [string[]] @($library | ForEach-Object { "$($_.Area)/$($_.Category)/$($_.Project)" }),
        [System.StringComparer]::Ordinal)

    foreach ($key in $inventoryKey) {
        if (-not $ciKey.Contains($key)) {
            $failure.Add("Release inventory ships '$key', but no per-area CI workflow builds or tests it. Add it to its area workflow matrix, or drop it from the inventory.")
        }
    }

    foreach ($key in $ciKey) {
        if ($inventoryKey.Contains($key)) {
            continue
        }

        # A CI project that is deliberately not a package (a host executable, a harness) is
        # identified by its own csproj, not by a list here that could go stale.
        $segment = $key.Split('/')
        $projectPath = Join-CohesionPath `
            -Root $repositoryDirectory `
            -Relative "$($segment[0])/$($segment[1])/$($segment[2])/src/$($segment[2]).csproj"
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            $failure.Add("CI workflow builds '$key', but there is no src csproj at $projectPath.")
            continue
        }

        if ((Get-Content -LiteralPath $projectPath -Raw) -match '<IsPackable>\s*false\s*</IsPackable>') {
            continue
        }

        # A scaffold that compiles to an empty assembly is a CI citizen but not a product. It is
        # excluded here rather than in the inventory list so the two stay mechanically derivable
        # from the same rule, and so the day someone adds the first source file the release picks
        # the package up by failing loudly here instead of silently omitting it forever.
        if (-not (Test-CohesionProjectHasSource -ProjectPath $projectPath)) {
            $sourceless.Add($key)
            continue
        }

        $failure.Add("CI builds packable project '$key', but the release inventory does not ship it. Add it to `$script:CohesionReleaseLibrary, or mark the project IsPackable=false.")
    }

    # 5: SDK and framework families resolve to real projects, and none on disk are missing.
    foreach ($sdk in $script:CohesionReleaseSdk) {
        $sdkProject = Join-CohesionPath -Root $repositoryDirectory -Relative "sdks/$sdk/Tasks/src/$sdk.Tasks.csproj"
        if (-not (Test-Path -LiteralPath $sdkProject -PathType Leaf)) {
            $failure.Add("SDK inventory entry has no project file: $sdkProject")
        }
    }

    foreach ($sdkFolder in @(Get-ChildItem -LiteralPath (Join-Path $repositoryDirectory 'sdks') -Directory)) {
        $sdkFolderProject = Join-CohesionPath -Root $sdkFolder.FullName -Relative "Tasks/src/$($sdkFolder.Name).Tasks.csproj"
        if (-not (Test-Path -LiteralPath $sdkFolderProject -PathType Leaf)) {
            continue
        }

        if ($script:CohesionReleaseSdk -notcontains $sdkFolder.Name) {
            $failure.Add("sdks/$($sdkFolder.Name) produces an SDK pack that the release inventory does not ship. Add it to `$script:CohesionReleaseSdk.")
        }
    }

    foreach ($framework in $script:CohesionReleaseFramework) {
        foreach ($kind in @('Refs', 'Runtime')) {
            $frameworkProject = Join-CohesionPath `
                -Root $repositoryDirectory `
                -Relative (Get-CohesionFrameworkProjectPath -Framework $framework -Kind $kind)
            if (-not (Test-Path -LiteralPath $frameworkProject -PathType Leaf)) {
                $failure.Add("Framework inventory entry has no project file: $frameworkProject")
            }
        }
    }

    # A framework producer is any csproj that declares a CohesionFrameworkName. Wherever one turns
    # up, it must sit at its family's conventional path, keep its family's assembly name, and belong
    # to a family the release ships. Otherwise a producer created under the wrong name - or in
    # frameworks/, the retired home of every producer - builds quietly outside the pack plan.
    $readProjectValue = {
        param(
            [xml] $ProjectXml,
            [string] $ElementName
        )

        $ProjectXml.SelectNodes("//*[local-name()='$ElementName']") |
            ForEach-Object { $_.InnerText.Trim() } |
            Where-Object { $_ }
    }

    foreach ($rootName in @('libraries', 'resources', 'sdks', 'analyzers', 'tooling', 'extensions', 'frameworks')) {
        $rootDirectory = Join-Path $repositoryDirectory $rootName
        if (-not (Test-Path -LiteralPath $rootDirectory -PathType Container)) {
            continue
        }

        foreach ($projectFile in @(
                Get-ChildItem -LiteralPath $rootDirectory -Recurse -Filter '*.csproj' -File |
                    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })) {
            try {
                $projectXml = [xml] (Get-Content -LiteralPath $projectFile.FullName -Raw)
            }
            catch {
                # A malformed project fails its own build; it cannot pass for a producer.
                continue
            }

            $family = @(& $readProjectValue $projectXml 'CohesionFrameworkName')
            if ($family.Count -eq 0) {
                continue
            }

            $relativePath = $projectFile.FullName.Substring($repositoryDirectory.Length + 1) -replace '\\', '/'
            $declaredKind = @(& $readProjectValue $projectXml 'CohesionFrameworkKind')
            if ($family.Count -ne 1 -or $declaredKind.Count -ne 1 -or $declaredKind[0] -cnotin @('Ref', 'Runtime')) {
                $failure.Add("Framework producer '$relativePath' must declare exactly one CohesionFrameworkName and one CohesionFrameworkKind of Ref or Runtime.")
                continue
            }

            $kind = if ($declaredKind[0] -ceq 'Ref') { 'Refs' } else { 'Runtime' }
            try {
                $expectedPath = Get-CohesionFrameworkProjectPath -Framework $family[0] -Kind $kind
            }
            catch {
                $failure.Add("Framework producer '$relativePath': $($_.Exception.Message)")
                continue
            }

            if ($relativePath -cne $expectedPath) {
                $failure.Add("Framework producer '$relativePath' builds $($family[0]) ($($declaredKind[0])) and must be '$expectedPath'. Producer projects are named for their owning area; see .claude/rules/build-system.md, 'Framework producer projects'.")
            }

            $expectedAssembly = if ($kind -eq 'Runtime') { $family[0] } else { "$($family[0]).Refs" }
            $assemblyName = @(& $readProjectValue $projectXml 'AssemblyName')
            if ($assemblyName.Count -ne 1 -or $assemblyName[0] -cne $expectedAssembly) {
                $failure.Add("Framework producer '$relativePath' must declare <AssemblyName>$expectedAssembly</AssemblyName>: the project name follows the owning area, the assembly name follows the framework.")
            }

            if ($script:CohesionReleaseFramework -cnotcontains $family[0]) {
                $failure.Add("Framework producer '$relativePath' belongs to family '$($family[0])', which the release inventory does not ship. Add it to `$script:CohesionReleaseFramework.")
            }
        }
    }

    # 6: the inventory is closed under public package dependencies.
    #
    # Only CohesionProjectReference is checked. CohesionPrivateProjectReference emits
    # PrivateAssets="all", so it deliberately does not reach the .nuspec - that is the whole point
    # of the private variant (see build/Targets/Build.References.Projects.targets), and treating it
    # as a dependency here would flag every sanctioned cross-resource implementation detail.
    $shippedPackageId = [System.Collections.Generic.HashSet[string]]::new(
        [string[]] @($library | ForEach-Object PackageId),
        [System.StringComparer]::Ordinal)

    foreach ($item in $library) {
        if (-not (Test-Path -LiteralPath $item.ProjectPath -PathType Leaf)) {
            continue
        }

        $projectXml = Get-Content -LiteralPath $item.ProjectPath -Raw
        foreach ($reference in [regex]::Matches($projectXml, '<CohesionProjectReference\s+Include="(?<id>[^"]+)"')) {
            $dependencyId = $reference.Groups['id'].Value
            if ($shippedPackageId.Contains($dependencyId) -or
                $script:CohesionReleaseUnpublishedDependency -contains $dependencyId) {
                continue
            }

            # A name that resolves to nothing is dropped silently by the reference resolver - the
            # transform produces an empty Include - so it never becomes a .nuspec dependency and
            # cannot break a consumer. It is still dead weight worth seeing, but it is not a
            # release blocker, so warn rather than fail.
            $dependencyProject = Get-CohesionSourceProjectPath `
                -RepositoryDirectory $repositoryDirectory `
                -ProjectName $dependencyId
            if (-not $dependencyProject) {
                Write-Warning "$($item.RelativePath) references '$dependencyId', which has no project in libraries/ or resources/. The reference resolves to nothing and is silently dropped by the build."
                continue
            }

            $failure.Add("$($item.PackageId) publicly references '$dependencyId', which the release does not publish - the .nuspec would carry a <dependency> on a package that does not exist, and every consumer restore fails with NU1101. Ship '$dependencyId' (add it to its area CI matrix and to `$script:CohesionReleaseLibrary), or make the reference a CohesionPrivateProjectReference.")
        }
    }

    # 7: nothing ships as an empty assembly by accident, and every declared reservation is real.
    foreach ($item in $library) {
        if (Test-CohesionProjectHasSource -ProjectPath $item.ProjectPath) {
            continue
        }

        if ($script:CohesionReleaseSourcelessPackage -notcontains $item.Project) {
            $failure.Add("$($item.PackageId) has no source files - the release would publish an empty assembly under a name that promises functionality, and nuget.org allows unlisting but never deletion. Drop it from `$script:CohesionReleaseLibrary until the implementation lands, or add it to `$script:CohesionReleaseSourcelessPackage to reserve the id deliberately.")
        }
    }

    foreach ($reservation in $script:CohesionReleaseSourcelessPackage) {
        if ($inventoryKey -notmatch "/$([regex]::Escape($reservation))`$") {
            $failure.Add("'$reservation' is declared in `$script:CohesionReleaseSourcelessPackage but is not in `$script:CohesionReleaseLibrary, so nothing reserves the id. Add it to the inventory, or drop the reservation.")
        }
    }

    # 8: no packable, source-bearing project is invisible to every workflow matrix.
    #
    # This is intentionally repository-wide rather than another release-library comparison. The
    # three-way checks above cannot see a project omitted from both their inventory and their CI
    # set, while this census also covers SDK, analyzer, tooling, and extension roots. Framework
    # producers live under libraries/ and resources/ and have no source, so they are never candidates.
    $workflowMatrixProject = @(Get-CohesionWorkflowMatrixProject `
            -RepositoryDirectory $repositoryDirectory)
    $matrixProject = [System.Collections.Generic.HashSet[string]]::new(
        [string[]] @($workflowMatrixProject | ForEach-Object Project),
        [System.StringComparer]::Ordinal)
    $nonMatrixProject = [System.Collections.Generic.HashSet[string]]::new(
        [string[]] @(
            $workflowMatrixProject |
                Where-Object { $script:CohesionNonMatrixWorkflow -contains $_.Workflow } |
                ForEach-Object Project),
        [System.StringComparer]::Ordinal)
    $candidateByPath = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::Ordinal)
    $projectNameCount = [System.Collections.Generic.Dictionary[string, int]]::new(
        [System.StringComparer]::Ordinal)

    # A name-only matrix entry is usable for a nonstandard project layout only when it identifies
    # one csproj across the guarded roots. Standard library/resource matrices remain path-safe via
    # their area/category/project key even when a tests project happens to share the basename.
    foreach ($rootName in @(
            'libraries',
            'resources',
            'sdks',
            'analyzers',
            'tooling',
            'extensions')) {
        $rootDirectory = Join-Path $repositoryDirectory $rootName
        if (-not (Test-Path -LiteralPath $rootDirectory -PathType Container)) {
            continue
        }

        foreach ($projectFile in @(
                Get-ChildItem -LiteralPath $rootDirectory -Recurse -Filter '*.csproj' -File |
                    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })) {
            if ($projectNameCount.ContainsKey($projectFile.BaseName)) {
                $projectNameCount[$projectFile.BaseName]++
            }
            else {
                $projectNameCount.Add($projectFile.BaseName, 1)
            }
        }
    }

    foreach ($candidate in @(Get-CohesionPackableSourceProject -RepositoryDirectory $repositoryDirectory)) {
        $candidateByPath.Add($candidate.RelativePath, $candidate)
    }

    foreach ($exclusion in $script:CohesionCiMatrixExclusion.GetEnumerator()) {
        $relativePath = [string] $exclusion.Key
        $reason = [string] $exclusion.Value
        if ([string]::IsNullOrWhiteSpace($reason)) {
            $failure.Add("CI matrix exclusion '$relativePath' has no reason. Add a non-empty reason or remove the exclusion.")
        }

        if (-not $candidateByPath.ContainsKey($relativePath)) {
            $failure.Add("CI matrix exclusion '$relativePath' is stale: it is not a packable, source-bearing project. Remove the exclusion.")
            continue
        }

        $excludedProject = $candidateByPath[$relativePath]
        $hasUniqueProjectName = $projectNameCount[$excludedProject.Project] -eq 1
        $isMatrixBuilt = if ($excludedProject.CiKey) {
            $ciKey.Contains($excludedProject.CiKey) -or
                ($hasUniqueProjectName -and $nonMatrixProject.Contains($excludedProject.Project))
        }
        else {
            $hasUniqueProjectName -and $matrixProject.Contains($excludedProject.Project)
        }
        if ($isMatrixBuilt) {
            $failure.Add("CI matrix exclusion '$relativePath' is stale: the project now appears in a workflow matrix. Remove the exclusion.")
        }
    }

    foreach ($candidate in $candidateByPath.Values) {
        $hasUniqueProjectName = $projectNameCount[$candidate.Project] -eq 1
        $isMatrixBuilt = if ($candidate.CiKey) {
            $ciKey.Contains($candidate.CiKey) -or
                ($hasUniqueProjectName -and $nonMatrixProject.Contains($candidate.Project))
        }
        else {
            $hasUniqueProjectName -and $matrixProject.Contains($candidate.Project)
        }
        if ($isMatrixBuilt -or
            $script:CohesionCiMatrixExclusion.Contains($candidate.RelativePath)) {
            continue
        }

        $failure.Add("Packable source-bearing project '$($candidate.RelativePath)' is absent from every workflow project matrix. Add '$($candidate.Project)' to a workflow's projects matrix, or add the exact project path to `$script:CohesionCiMatrixExclusion with a reason.")
    }

    if ($sourceless.Count -gt 0) {
        Write-Host ("Not shipped - {0} CI project(s) compile to an empty assembly: {1}" -f
            $sourceless.Count,
            (($sourceless | Sort-Object) -join ', '))
    }

    if ($failure.Count -gt 0) {
        throw "The release inventory is out of sync with the repository:`n  " + ($failure -join "`n  ")
    }

    Write-Host ("Release inventory validated: {0} libraries/resources, {1} SDK packs, {2} framework families x {3} RIDs." -f
        $library.Count,
        $script:CohesionReleaseSdk.Count,
        $script:CohesionReleaseFramework.Count,
        $script:CohesionReleaseRuntimeIdentifier.Count)
}

function Assert-CohesionReleasePackageSet {
    <#
    .SYNOPSIS
        Fails unless the package directory holds exactly the expected release set at exactly the
        expected version.

    .DESCRIPTION
        Catches three distinct failure modes a per-project pack exit code cannot: a project that
        packed at the wrong version (the version props are layered, and a stray default lands a
        package at 1.0.0), a package that never appeared despite a zero exit code, and a stale
        package left over from a previous run.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $PackageDirectory,

        [Parameter(Mandatory)]
        [string] $Version,

        [Parameter(Mandatory)]
        [string[]] $ExpectedPackageId
    )

    if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
        throw "Package directory '$PackageDirectory' does not exist."
    }

    $duplicate = @(
        $ExpectedPackageId |
            Group-Object |
            Where-Object { $_.Count -gt 1 } |
            ForEach-Object { $_.Name }
    )
    if ($duplicate.Count -gt 0) {
        throw "The release inventory produces duplicate package ids: $($duplicate -join ', ')"
    }

    $expectedFile = @($ExpectedPackageId | ForEach-Object { "$_.$Version.nupkg" })
    $actualFile = @(
        Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File |
            ForEach-Object Name
    )

    $difference = @(
        Compare-Object `
            -ReferenceObject @($expectedFile | Sort-Object) `
            -DifferenceObject @($actualFile | Sort-Object)
    )
    if ($difference.Count -ne 0) {
        $missing = @($difference | Where-Object { $_.SideIndicator -eq '<=' } | ForEach-Object InputObject)
        $unexpected = @($difference | Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object InputObject)

        $message = [System.Collections.Generic.List[string]]::new()
        $message.Add("The release package set in '$PackageDirectory' does not match the inventory.")
        if ($missing.Count -gt 0) {
            $message.Add("  Missing ($($missing.Count)):")
            $missing | ForEach-Object { $message.Add("    $_") }
        }
        if ($unexpected.Count -gt 0) {
            $message.Add("  Unexpected ($($unexpected.Count)):")
            $unexpected | ForEach-Object { $message.Add("    $_") }
        }

        throw ($message -join "`n")
    }

    Write-Host "Validated $($expectedFile.Count) release packages at version $Version."
}

function Assert-CohesionPackageMetadata {
    <#
    .SYNOPSIS
        Fails unless every produced package carries the branding and licensing metadata the
        repository promises.

    .DESCRIPTION
        Checks, per package, that the .nuspec declares an <icon> and that the named file is
        actually inside the archive. NuGet's own NU5046 covers the case where the icon is declared
        and absent, but nothing covers the case that matters more here: a project that silently
        stops receiving the central branding at all - by dropping out of the Build.Packaging.targets
        import chain, or by setting IsPackable in a place the condition reads too early - packs
        cleanly with no icon and no warning.

        <license> and <readme> are reported rather than enforced. They are wired per-area
        (libraries/ and resources/ Directory.Build.props, which also cover the framework
        producers) and the SDK packs legitimately carry neither, so a hard requirement would be
        wrong; a count in the log is enough to see a regression.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $PackageDirectory,

        [Parameter(Mandatory)]
        [string] $ExpectedIcon
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $failure = [System.Collections.Generic.List[string]]::new()
    $withLicense = 0
    $withReadme = 0
    $package = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File | Sort-Object Name)
    if ($package.Count -eq 0) {
        throw "No packages to inspect in '$PackageDirectory'."
    }

    foreach ($file in $package) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($file.FullName)
        try {
            $nuspecEntry = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' -and $_.FullName -notmatch '/' })
            if ($nuspecEntry.Count -ne 1) {
                $failure.Add("$($file.Name): expected exactly one root .nuspec, found $($nuspecEntry.Count).")
                continue
            }

            $reader = [System.IO.StreamReader]::new($nuspecEntry[0].Open())
            try {
                $nuspec = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }

            $iconMatch = [regex]::Match($nuspec, '<icon>(?<icon>[^<]+)</icon>')
            if (-not $iconMatch.Success) {
                $failure.Add("$($file.Name): the .nuspec declares no <icon>. The project is not receiving build/Targets/Build.Branding.props.")
            }
            elseif ($iconMatch.Groups['icon'].Value -ne $ExpectedIcon) {
                $failure.Add("$($file.Name): declares icon '$($iconMatch.Groups['icon'].Value)', expected '$ExpectedIcon'.")
            }
            elseif (-not ($archive.Entries | Where-Object { $_.FullName -eq $ExpectedIcon })) {
                $failure.Add("$($file.Name): declares icon '$ExpectedIcon' but the archive does not contain it.")
            }

            if ($nuspec -match '<license\s') { $withLicense++ }
            if ($nuspec -match '<readme>') { $withReadme++ }
        }
        finally {
            $archive.Dispose()
        }
    }

    if ($failure.Count -gt 0) {
        throw "Package metadata is missing or wrong:`n  " + ($failure -join "`n  ")
    }

    Write-Host ("Package metadata validated: {0}/{0} carry the NuGet icon, {1} a license file, {2} a readme." -f
        $package.Count,
        $withLicense,
        $withReadme)
}

Export-ModuleMember -Function @(
    'Get-CohesionReleaseRuntimeIdentifier'
    'Get-CohesionReleaseSdk'
    'Get-CohesionReleaseFramework'
    'Get-CohesionFrameworkProjectPath'
    'Get-CohesionReleaseLibrary'
    'Get-CohesionReleaseProject'
    'Get-CohesionReleasePackageId'
    'Get-CohesionCiMatrixEntry'
    'Assert-CohesionReleaseInventory'
    'Assert-CohesionReleasePackageSet'
    'Assert-CohesionPackageMetadata'
)
