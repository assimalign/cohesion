# Resource generation check

This executable proves the shared `Build.ErrorMessages.targets` contract without Visual Studio
or a checked-in designer. Adding `Properties/ErrorMessages.resx` is the only resource wiring in
the project. MSBuild emits an internal `ErrorMessages` into the pinned root namespace under the
configuration/TFM/RID intermediate directory and embeds the matching manifest resource.

The check throws and catches an exception through that accessor, with French current and UI
cultures, and pins the exact formatted text. This exercises neutral-resource fallback and
`CurrentCulture` formatting in both a normal CLI build and NativeAOT. The Web CI AOT job runs
both forms before publishing its full application guard. No reflection or runtime code generation
is used. This is a build-mechanism regression check; the library migrations preserve message text.
