# Versioning and Release Channels

Cohesion uses synchronized mono-repository versioning. Every standalone library or resource,
MSBuild SDK, framework targeting pack, and framework runtime pack produced from one commit carries
the same package version.

The direction of record is:

> cohesion's version never sorts below any version present on a feed

This comparison uses NuGet's SemVer precedence. Before changing the version line, inspect both the
GitHub Packages staging feed and nuget.org. Never move backward to an older channel, patch, or
prerelease number, and never reuse a package id/version that has already been published.

## Source of truth

[`build/Targets/Build.Version.props`](../build/Targets/Build.Version.props) is the only source of
the Cohesion version. The .NET target framework supplies the major version; the file supplies the
minor version and the patch plus optional prerelease suffix. The current line is:

```xml
<CohesionPatchVersion>0-preview.1</CohesionPatchVersion>
```

That resolves to `10.0.0-preview.1`. Verify it without restoring or building:

```powershell
pwsh scripts/Get-CohesionVersion.ps1
```

Do not add per-project version overrides. Release packaging may pass the resolved version as global
MSBuild properties, but it must preserve this single synchronized identity.

## Channels

| Channel | Version example | Publication policy |
| --- | --- | --- |
| Local development | `10.0.0-preview.1.local` | `Install-Local.ps1` only; written to `_out/packages/`; never published |
| CI | `10.0.0-preview.0.ci.57`, `10.0.0-preview.0.pr.1520.58` | `ci-packages.yml` publishes every push to `main` and every same-repository pull request commit to GitHub Packages; never promoted (see [CI packages](#ci-packages)) |
| Staging | `10.0.0-preview.1` | Every published GitHub Release tagged `v$(CohesionVersion)` is validated, packed, and staged in GitHub Packages |
| Public preview or release candidate | `10.0.1-preview.N`, `10.0.1-rc.N` | Promoted from an already-published release only through an explicit manual dispatch and reviewer approval |
| Alpha or beta | `10.0.1-alpha.N`, `10.0.1-beta.N` | Staging only |
| Stable | `10.0.1` | Not opened by the current workflow; enabling stable promotion is a separate release decision |

GitHub Packages is the staging and release-validation feed. A matching published release tag
always stages after validation; a prerelease suffix alone never causes public promotion. nuget.org
promotion begins only at the signed-off `preview` gate after developer-experience design items
2–26 are complete, using the then-current `10.0.1-preview.N` rather than assuming it will still be
`preview.3`.

The `publish-nuget` job is a separate manual run from `main` that names the already-published tag:

```powershell
gh workflow run release.yml --ref main -f release_tag=v10.0.1-preview.N -f promote=true
```

The promotion input is a Boolean and defaults to `false`. The workflow requires the dispatch itself
to use the default branch, proves that `release_tag` names a published GitHub Release, pins and
revalidates that tag commit, rebuilds and checksums the release set, attempts staging with
`--skip-duplicate`, and only then enters the protected `nuget-org` environment. The job cannot start
until a configured required reviewer approves it. See GitHub's
[deployment environment documentation](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments).

## Local package identities and pruning

`Install-Local.ps1` appends `.local` to the canonical prerelease, so
`10.0.0-preview.1` becomes `10.0.0-preview.1.local`. In SemVer, that additional identifier sorts
after the canonical prerelease and cannot collide with its published package identity.

A stable canonical line cannot safely acquire a local suffix: `10.0.1-local` would sort below
`10.0.1`. The local installer therefore fails on a stable canonical version until the required
post-tag bump lands. It never invents a patch number. `Pack-Release.ps1` rejects any version whose
prerelease identifiers contain the reserved `local` identifier.

Before rebuilding libraries and resources, the local installer removes their prior `.nupkg`,
`.snupkg`, and legacy `.symbols.nupkg` artifacts from `_out/packages/`. Selection is exact by
package id plus SemVer; prefix-sharing ids, SDK/framework packages, and unrelated files remain.
It also removes only the current local-version extract for each package being rebuilt from NuGet's
global packages cache, preserving cached published versions. `-SkipSdks`, `-SkipFramework`, and
`-SkipLibraries` also skip the corresponding cache and feed pruning.

## CI packages

[`.github/workflows/ci-packages.yml`](../.github/workflows/ci-packages.yml) publishes the complete
release set to GitHub Packages for every push to `main` (each merged pull request) and every commit
to an open pull request from a branch of this repository. Downstream work can then pin an exact
build of `main`, or of a pull request still in review, without waiting for a release: benchmarking,
`cohesion-platforms` integration, and `cohesion-examples` build-outs. The workflow never writes to
nuget.org, never publishes a canonical version, and never replaces a published version.

### Version shape

`Get-CohesionCiPackageVersion` in
[`CohesionPackaging.psm1`](../scripts/modules/CohesionPackaging.psm1) derives the version
from the canonical one. Appending to the canonical prerelease, as the local channel does, would be
wrong here: `10.0.0-preview.1.ci.57` sorts **above** `10.0.0-preview.1`, so every CI build on the
feed would outrank the release it precedes. The CI version instead extends the canonical version's
predecessor, made by decrementing its final prerelease number:

| Canonical | Build | CI version |
| --- | --- | --- |
| `10.0.0-preview.1` | push to `main`, run 57 | `10.0.0-preview.0.ci.57` |
| `10.0.0-preview.1` | pull request 1520, run 58 | `10.0.0-preview.0.pr.1520.58` |
| `10.0.0-preview.2` (after the post-tag bump) | push to `main`, run 90 | `10.0.0-preview.1.ci.90` |

Every CI build therefore sorts above the previous line and below the canonical version. After
`10.0.0-preview.1` is released and `main` is bumped to `preview.2`, CI builds sort above the
released `preview.1`, and the rule of record holds: the canonical version never sorts below a CI
build on the feed.

- **The run number makes each version unique.** `github.run_number` is shared by both channels, so
  a `main` build and a pull request build never collide, and later builds sort higher within a
  channel. A re-run attempt keeps its run number and version. Only a re-run may skip packages
  already on the feed; a first attempt that meets an existing version fails instead, because
  that means the run numbers restarted (a renamed or recreated workflow) and
  `--skip-duplicate` would silently keep older packages.
- **Numeric identifiers stay within Int32.** NuGet parses numeric prerelease identifiers with
  `Int32.TryParse` and compares a larger value as text, which would order `9` above `10`. The
  derivation rejects a run or pull request number outside `1..2147483647`.
- **The canonical line must end in a positive number.** A stable canonical version
  (`10.0.1`), or a prerelease such as `preview` or `preview.0`, has no derivable predecessor, so
  the workflow fails rather than inventing one. On `main` that state exists only between a stable
  release commit and its post-tag bump. The identifiers `local`, `ci`, and `pr` are reserved and
  may not appear in the canonical version.
- **No commit SHA in the version.** An all-digit short SHA with a leading zero is invalid SemVer.
  Each package's repository metadata records the commit instead, and the publish job's summary
  lists version, channel, commit, and package count.

### What is validated before publishing

The set is the release's: `Pack-Release.ps1` strictly packs the full inventory, every SDK and
framework family, and all release RIDs, then asserts the package set and metadata. The packed SDK
consumers then build, publish self-contained, and run against that exact artifact on Linux before
any package is pushed. There is no per-package validation matrix and no Windows or macOS consumer
leg here. The per-area workflows already build and test the same commits, and `release.yml` still
runs its full gate before a release version is staged.

A pull request build packs the pull request's **head** commit, not GitHub's synthetic merge commit,
so the commit recorded in each package stays resolvable. Pull requests from forks, and runs that
Dependabot triggers, publish nothing: their tokens cannot write packages, and the workflow is never
run on `pull_request_target`, which would grant that permission to the code under review. A newer
commit on the same pull request cancels a pack still in progress, but never a publish under way, so
a superseded commit cannot leave a partial version on the feed. Packages are pushed in
`package-order.txt` order, SDKs last, so a consumer pinning the SDK version does not resolve it
before its framework and library packages have arrived. Pull requests that change only `docs/` or
`.claude/` publish nothing new.

### Consuming CI packages

- **Pin an exact version.** Set every Cohesion entry in the consumer's `global.json`
  `msbuild-sdks` block to the same CI version (pin agreement is ordinal, see below). The publish
  job's name and summary show the version for each run.
- **A pin is temporary.** A pull request's builds are deleted when it closes, and a `main` build
  once 20 newer `main` builds of its base exist (see "Feed growth"). Pin a release version for
  anything that must keep restoring.
- **Float only within a channel.** `10.0.0-preview.0.ci.*` tracks the latest `main` build and
  `10.0.0-preview.0.pr.1520.*` the latest build of one pull request. `pr` sorts above `ci`, so an
  unbounded float such as `10.0.0-*`, or an update bot reading the feed, picks the newest pull
  request build over `main`.
- **Authenticate without committing a credential.** GitHub Packages requires a token with
  `read:packages` even for reads, and `credential-guard.yml` rejects credentials in tracked
  `NuGet.Config` files. Declare the source by key only and supply the credential from the
  environment, for example `NuGetPackageSourceCredentials_assimalign=Username=<user>;Password=<token>`
  for a source keyed `assimalign`, from a CI secret.

### Feed growth

Each run publishes the full set: 396 package versions today (223 libraries and resources, 21 SDKs,
and 19 frameworks with one targeting pack and seven runtime packs each).
[`.github/workflows/ci-packages-prune.yml`](../.github/workflows/ci-packages-prune.yml) deletes
two kinds of CI version, and nothing else ever deletes from the feed:

- **Closed pull requests.** Every `<base>.pr.<N>.<build>` version of a pull request the GitHub API
  reports closed, merged or not. `N` is compared as a number, so closing #15 never touches #152.
  The run starts when the pull request closes; a pull request reopened before it runs keeps its
  builds.
- **Old `main` builds.** For each base, every `main` build outside the newest **20**.

**A release version is never deleted.** Selection is `Select-CohesionCiPackageVersionToPrune` in
[`CohesionPackaging.psm1`](../scripts/modules/CohesionPackaging.psm1), which considers
only versions `ConvertFrom-CohesionCiPackageVersion` recognizes: the exact shapes
`Get-CohesionCiPackageVersion` produces. A release (`10.0.0-preview.1`), stable, or local version,
or a near miss such as `10.0.0-ci.5`, is ignored whatever else the run is asked to do, and the
deleting loop checks the shape again before each request. The release-inventory Pester suite pins
both, including a test that feeds release versions to the most aggressive selection possible.

**Why 20 `main` builds per base.** The count bounds what stays on the feed; the deletion rate does
not depend on it, because every `main` build is deleted eventually whatever the count is.

- Twenty builds keep 7,920 versions per base, 20 per package, so a package's version list stays
  on one 100-entry API page and listing it stays one request.
- A pinned build lives for the next 20 merges: two and a half days at the eight merges of
  2026-10-15, about a week at two or three a day. A downstream repository that pins a `main` build
  should move its pin at least that often.
- A count rather than an age. An age limit is unbounded during a burst of merges and, if combined
  with a count, empties the channel after a quiet spell.
- Builds are ranked as whole builds by their newest publish time, not package by package and not
  by run number, so every kept build stays complete and a restart of the run counter does not
  delete the fresh builds as old.
- Per base, so the post-tag bump does not count the previous line's builds against the new line.
  A superseded line keeps its last 20 builds (7,920 versions) until someone deletes them; that is
  the one growth the pruner leaves, at one set per release line.

**Which packages it examines.** `GITHUB_TOKEN` cannot use the organization's list-packages
endpoint, so the run examines the ids the release inventory publishes
(`Get-CohesionReleasePackageId`, which is exactly what `ci-packages.yml` pushes). If the listing
does answer, it adds packages named `Assimalign.Cohesion.*` and linked to this repository.
`cohesion-platforms` publishes `Assimalign.Cohesion.*` packages too; they are in neither set. The
cost: a package dropped from the inventory, and a package only a never-merged pull request added,
keep whatever builds they had, a bounded handful each.

**Permissions.** The NuGet registry uses granular permissions, so deleting through `GITHUB_TOKEN`
needs the Admin role on each package, not only `packages: write`. GitHub grants that role
automatically to a repository whose workflow publishes the package, which `ci-packages.yml` and
`release.yml` do. Deleting through `GITHUB_TOKEN` is a GitHub public preview. If deletions are
refused (HTTP 403, and the run stops after five in a row), open the package's settings, choose
**Manage Actions access**, and give `assimalign/cohesion` the **Admin** role. No personal access
token is needed or used. The alternative, a classic token with `read:packages` and
`delete:packages` from an organization owner, can delete any package in the organization, release
versions included, so it would be a deliberate owner decision, not a fallback.

**Rate limits, not CI time, bound the pruner.** `GITHUB_TOKEN` gets 1,000 REST requests per hour
per repository. A run lists every package (397 requests, one each plus the refused organization
listing), asks the state of each pull request that has builds, then deletes one request per
version, a second apart as GitHub asks for mutating requests. It stops, successfully, when 100
requests remain, when a secondary rate limit asks for a wait over two minutes, or after 45 minutes,
and reports the rest as deferred. Every run recomputes the whole selection, so the next run picks
up the remainder with nothing to carry over.

- That makes one run worth about 500 deletes, and the hourly schedule about 12,000 a day.
- Demand is 396 deletes per `main` merge plus 396 per published pull request commit. At the eight
  merges of 2026-10-15 with two published commits each, that is about 9,500 deletes, inside the
  ceiling. Heavier days leave a backlog that quiet hours drain.
- GitHub limits content-creating requests to 500 per hour and does not say whether a delete counts.
  If it does, the run meets the secondary limit first and stops early on its own.
- If the backlog grows, publish fewer pull request builds before reaching for a broader token: the
  pull request channel is most of the volume.

**Triggers.** `pull_request_target` (closed) starts a run when a pull request closes. It runs this
workflow from the default branch, so the code that decides what to delete is never the closing pull
request's own, and it fires for a pull request closed with a merge conflict, which `pull_request`
does not. No step checks out or runs pull request code. Fork pull requests publish nothing and are
skipped, and so is a pull request that changes only `docs/` or `.claude/` (the paths
`ci-packages.yml` ignores), since its close would cost a full scan for nothing; a
Dependabot-triggered close gets a read-only token. The hourly run covers all three.
`ci-packages.yml` itself stays off `pull_request_target`, because it builds the code under review.
`workflow_dispatch` defaults to a dry run that lists, selects, and writes the summary without
deleting anything. One run at a time; a queued run replaced by a newer one loses nothing.

## Consumer SDK pin agreement

A repository `global.json` pins the .NET SDK and its Cohesion MSBuild SDKs. Templates write all 20
Cohesion identities: `Assimalign.Cohesion.Sdk`, the 18 resource-area SDKs (including `Sdk.Web`),
and `Sdk.Gateway`. Every Cohesion pin present in the block must use exactly the same version string,
and the pinned .NET SDK version must be `10.0.300` or newer. The base SDK enforces those rules during
restore and build with COHSDK002 after finding the nearest `global.json` above the consumer project.
A consumer without `global.json` is unaffected.

Pin agreement uses ordinal string equality among the Cohesion entries; it does not require equality
with the canonical version in this repository. Consequently, an inner-loop consumer legitimately
uses `10.0.0-preview.1.local` for every entry after `Install-Local.ps1` packs that local identity.
`CohesionSkipSdkPinCheck=true` exists only for tooling that must load an intentionally inconsistent
tree and should not be set in normal builds.

## Release sequence and post-tag bump

1. Enumerate versions on GitHub Packages and nuget.org. Choose a canonical version that does not
   sort below either feed and update only `Build.Version.props`.
2. Run the repository build, tests, release inventory validation, strict pack, and consumer smoke
   checks required by the release gate.
3. Publish a GitHub Release whose tag is exactly `v$(CohesionVersion)`. The release workflow
   validates and stages the immutable package set in GitHub Packages.
4. Before the public preview gate, stop here. At the gate, manually dispatch `release.yml` from
   `main`, set `release_tag` to the published tag, and set `promote=true`; approve the `nuget-org`
   environment only after reviewing the validated version, package count, and checksums.
5. Immediately after the tag, bump `Build.Version.props` on `main` to the next higher development
   line before development resumes. After `preview.3`, use at least `preview.4`; after a stable
   `10.0.1`, move to a higher core such as `10.0.2-preview.1`. Local packs then add `.local` to that
   new line.
6. After `10.0.0-preview.1` exists in staging, repin `cohesion-platforms` and
   `cohesion-examples` with their release floor plus sibling override. That cross-repository work
   belongs to design items 33 and 38 and is not part of this repository change.

The orphaned `10.0.1-preview.2` packages were not built from the release inventory and were never
release-validated. They must be deleted from GitHub Packages; they must not be promoted or used as
the basis for a lower version line.

## nuget.org push ceiling

nuget.org limits package upload requests to **350 pushes per hour** for an API key. The signed-off
item-5 capacity baseline described the release as **303 packages today**. Each new framework family
adds **9** package pushes before any standalone libraries or resources are counted: one SDK, one
targeting pack, and seven runtime packs. From that baseline, five new families reach 348 pushes and
a sixth reaches 357, beyond the ceiling.

The baseline is planning evidence, not a hard-coded inventory assertion. The release inventory can
grow between design sign-off and promotion. Treat `_out/release/packages/package-order.txt`, emitted
by `Pack-Release.ps1`, as the count for the actual release and do not approve promotion when it
exceeds the remaining hourly budget. Avoid immediately restarting a partially successful push;
duplicate attempts can still consume requests. See the official [NuGet API rate limits](https://learn.microsoft.com/en-us/nuget/api/rate-limits).

## Owner actions before the preview tag

These actions are repository-administration and feed operations; the release scripts do not create
or perform them:

- Delete every orphaned `10.0.1-preview.2` Cohesion package version from GitHub Packages after
  auditing the exact package ids. Do not delete any other package version.
- In `assimalign/cohesion`, create the `nuget-org` environment and configure at least one required
  reviewer. Enable prevention of self-review when the owner requires separation of duties. Merely
  referencing an environment in YAML does not configure its protection rules.
- Create the `NUGET_USER` GitHub Actions organization variable and grant it visibility to
  `assimalign/cohesion`. Its value is the public nuget.org account/profile name, not a credential.
- In the nuget.org account's Trusted Publishing page, add the GitHub Actions policy for repository
  owner `assimalign`, repository `cohesion`, workflow file `release.yml`, and environment
  `nuget-org`. Enter the workflow file name, not `.github/workflows/release.yml`. See the official
  [NuGet trusted publishing setup](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
- Verify that the environment shows its required-reviewer protection and that the trusted-publishing
  policy exactly matches the repository, workflow, and environment before the first public
  promotion. Do not approve promotion until items 2–26 and the package-count review are complete.
