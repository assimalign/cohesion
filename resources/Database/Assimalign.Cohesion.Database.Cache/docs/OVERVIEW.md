# Assimalign.Cohesion.Database.Cache — Overview

This package is a deferred Cache model scaffold. Cache behavior is outside the
database MVP; the source and test stubs remain placeholders.

The co-located test project treats `xunit.runner.json` as content, matching the
other Database test projects, so runner configuration is not compiled as C#.
There are no new package dependencies or runtime behavior in this correction.

See [DESIGN.md](DESIGN.md) for the current scope.


## Deferred conventions (#1470)

`CC` is reserved for Cache diagnostic identities under the [Database code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md).
Cache currently defines no runtime errors, factories, exception templates, wire protocol or disk
format. The existing placeholder `Class1` is sealed to follow the Database public-leaf rule;
its implicit public parameterless constructor remains. This introduces no Cache implementation.
Future errors use generated resources and explicit category/condition allocations rather than
adding a second code vocabulary. The Client and Language projects remain empty scaffolds.

## Placeholder documentation

The existing sealed `Class1` placeholder has an XML summary. It remains the same deferred public surface with implicit parameterless construction; this maintenance adds no Cache behavior.
