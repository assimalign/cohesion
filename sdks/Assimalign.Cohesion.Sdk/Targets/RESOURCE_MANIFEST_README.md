# Cohesion resource manifest

This package carries a generated Cohesion resource manifest for cross-repository gateway consumption.
It intentionally contains no runtime assemblies or package dependencies.

Reference it from a Cohesion project with `CohesionResourceReference`. The package's
`buildTransitive` props surface `cohesion/resource.json` as a `CohesionResourceManifest` item.
