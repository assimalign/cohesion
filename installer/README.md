# Assimalign Cohesion Installer

Builds a Windows MSI that installs the `_out\dotnet\…` build output to
**`C:\Program Files\cohesion`** and produces matching
[winget](https://learn.microsoft.com/windows/package-manager/) manifests.

> **The MSI is for offline / corporate / CI scenarios only.** The primary
> distribution path for Cohesion is NuGet — consumers add an `msbuild-sdks`
> entry to their `global.json` and the standard NuGet MSBuild SDK resolver
> (shipped with every MSBuild client) handles SDK + framework resolution.
> No installer is required for normal consumption.

## What gets installed

| Source (`_out\dotnet\…`)            | Destination (`C:\Program Files\cohesion\…`) |
| ----------------------------------- | -------------------------------------------- |
| `sdk\<version>\sdks\…`              | `sdk\<version>\sdks\…`                       |

To install other parts of `_out/` (NuGet packages, dev scripts, code-generation
templates), add additional `<Files Include="…" />` elements to
[`Product.wxs`](./Product.wxs) inside the `CohesionPayload` feature.

The installer also registers an Add/Remove Programs entry under "Assimalign
Cohesion SDK".

`root-restore-diag.log` is excluded — it can be hundreds of MB and is not part of the SDK.

## Prerequisites

- .NET SDK **10.0.101** or later (the WiX SDK is restored as a NuGet package — no separate install).
- The repo's `_out` folder must already be populated. Build the repo first.
- Admin rights are required to install the MSI (per-machine, env vars, PATH).

## Build the MSI + manifests

```powershell
.\installer\build.ps1
```

Optional parameters:

```powershell
.\installer\build.ps1 -Version 9.0.1 -Configuration Release `
    -InstallerUrl 'https://github.com/assimalign/cohesion/releases/download/v9.0.1/Assimalign.Cohesion.msi'
```

Outputs:

- `installer\bin\Release\Assimalign.Cohesion.msi`
- `installer\manifests\<version>\Assimalign.Cohesion.yaml`
- `installer\manifests\<version>\Assimalign.Cohesion.installer.yaml`
- `installer\manifests\<version>\Assimalign.Cohesion.locale.en-US.yaml`

The build script extracts the freshly-built MSI's `ProductCode` and SHA256 and stamps
them into the manifests automatically, so they always match the binary you just built.

## Install

### Plain MSI (admin shell)

```powershell
msiexec /i .\installer\bin\Release\Assimalign.Cohesion.msi /qn
```

### Via winget (local manifest, no submission needed)

```powershell
winget install --manifest .\installer\manifests\9.0.0
```

### Via winget once published to the public repo

```powershell
winget install Assimalign.Cohesion
```

## Uninstall

```powershell
msiexec /x .\installer\bin\Release\Assimalign.Cohesion.msi /qn
# or
winget uninstall Assimalign.Cohesion
```

## Publishing to the winget public repo

1. Tag a release in the GitHub repo and upload the MSI as a release asset.
2. Re-run `build.ps1 -Version <tag> -InstallerUrl <release-asset-url>` so the manifest
   points at the public download URL with the correct SHA256.
3. Validate locally:

   ```powershell
   winget validate --manifest .\installer\manifests\<version>
   ```

4. Open a PR against [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)
   placing the three YAML files under
   `manifests/a/Assimalign/Cohesion/<version>/`.

## Layout

```
installer\
├── Assimalign.Cohesion.Installer.wixproj   # WiX SDK project (Sdk="WixToolset.Sdk/6.0.0")
├── Product.wxs                              # WiX source (Package, Files, CustomActions)
├── build.ps1                                # MSI + manifest build script
├── scripts\
│   └── Register-SdkResolver.ps1             # Custom action: register the MSBuild SDK resolver
├── manifests\
│   ├── templates\                           # YAML templates with {{Token}} placeholders
│   │   ├── Assimalign.Cohesion.yaml.template
│   │   ├── Assimalign.Cohesion.installer.yaml.template
│   │   └── Assimalign.Cohesion.locale.en-US.yaml.template
│   └── <version>\                           # Rendered output (gitignored)
└── bin\, obj\                               # Build output (gitignored)
```

## Notes on the install path

Windows Installer's auto-GUID heuristic refuses to generate stable component GUIDs for
install paths that aren't rooted under a standard directory. To satisfy it, the WiX
source roots `INSTALLFOLDER` under `ProgramFiles64Folder` at compile time and uses
`<SetDirectory Id="INSTALLFOLDER" Value="C:\Program Files\cohesion" />` to redirect the
actual on-disk path at install time. This produces a valid MSI with deterministic GUIDs
*and* the desired install location.
