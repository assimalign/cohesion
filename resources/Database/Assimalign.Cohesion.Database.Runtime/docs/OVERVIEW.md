# Database runtime pack

The runtime producer packages the Database shared framework used by Sdk.Database consumers.
It adds the model engines and common kernel assemblies to the App framework. Model client
packages remain explicit NuGet dependencies where the framework membership list excludes them.

Framework membership is declared in this project's `Directory.Build.props` and imported by
both the Runtime and Refs producers. Public members appear in both packs; private members
ship only in the runtime pack. Each item kind has its own unconditioned item group, so
splitting the declarations preserves evaluation order and visibility. Compiler and AOT
settings come from the shared build.

See [DESIGN](DESIGN.md) for packaging and membership details. The convention audit changes
build declarations without changing membership, protocol versions, or disk formats.
