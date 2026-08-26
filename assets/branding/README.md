# Cohesion branding artifacts

These files are imported from the read-only `assimalign/branding` repository
(`C:\Source\repos\assimalign\branding` when checked out beside this repo). Regenerate branding
there and copy only the artifacts Cohesion actually references; do not edit the imported artwork
in this repository.

## `nuget/`

`cohesion-nuget-mono-light-128.png` is the `<PackageIcon>` embedded in every shipped Cohesion
package. It is wired centrally — `build/Targets/Build.Branding.props` names it and
`build/Targets/Build.Packaging.targets` packs it — so libraries, resources, SDK packs, framework
targeting/runtime packs, and the template pack all carry the same mark without per-project edits.

Three choices are deliberate:

- **128 px.** NuGet's documented `<PackageIcon>` size. The branding repo also ships 32/64/256; they
  are not copied here because nothing in this repository references them.
- **mono.** The company moss accent, the variant the branding system reserves for contexts where a
  single color must carry the whole family. Cohesion's per-product blue is used on theme-aware
  surfaces, not on this static single-asset one.
- **light.** NuGet gallery and IDE package browsers expose exactly one icon and cannot swap it with
  the active theme. The light tile self-grounds the dark mono glyph, so the mark stays legible on
  both light and dark UI. This mirrors the same decision in the Viu repository.
