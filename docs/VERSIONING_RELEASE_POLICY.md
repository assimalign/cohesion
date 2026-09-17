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
<CohesionPatchVersion>1-preview.3</CohesionPatchVersion>
```

That resolves to `10.0.1-preview.3`. Verify it without restoring or building:

```powershell
pwsh installer/scripts/Get-CohesionVersion.ps1
```

Do not add per-project version overrides. Release packaging may pass the resolved version as global
MSBuild properties, but it must preserve this single synchronized identity.

## Channels

| Channel | Version example | Publication policy |
| --- | --- | --- |
| Local development | `10.0.1-preview.3.local` | `Install-Local.ps1` only; written to `_out/packages/`; never published |
| Staging | `10.0.1-preview.3` | Every published GitHub Release tagged `v$(CohesionVersion)` is validated, packed, and staged in GitHub Packages |
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
`10.0.1-preview.3` becomes `10.0.1-preview.3.local`. In SemVer, that additional identifier sorts
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

## Consumer SDK pin agreement

A repository `global.json` pins the .NET SDK and its Cohesion MSBuild SDKs. Templates write all 20
Cohesion identities: `Assimalign.Cohesion.Sdk`, the 18 resource-area SDKs (including `Sdk.Web`),
and `Sdk.Gateway`. Every Cohesion pin present in the block must use exactly the same version string,
and the pinned .NET SDK version must be `10.0.300` or newer. The base SDK enforces those rules during
restore and build with COHSDK002 after finding the nearest `global.json` above the consumer project.
A consumer without `global.json` is unaffected.

Pin agreement uses ordinal string equality among the Cohesion entries; it does not require equality
with the canonical version in this repository. Consequently, an inner-loop consumer legitimately
uses `10.0.1-preview.3.local` for every entry after `Install-Local.ps1` packs that local identity.
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
6. After `10.0.1-preview.3` exists in staging, repin `cohesion-platforms` and
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
