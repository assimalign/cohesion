# Assimalign.Cohesion.Database.Cache — Design

## Model component boundaries

The empty Cache Catalog and Storage projects are retired. Cache has no Security satellite and remains deferred. Future public Catalog and Storage types belong under src/Catalog and src/Storage; internal component types belong under src/Internal/Catalog and src/Internal/Storage and declare Assimalign.Cohesion.Database.CacheDb.Internal.

The dependency direction remains engine → Catalog → Storage → shared kernel. Storage-folder types must reference neither Catalog-folder nor engine types; Catalog-folder types must reference no engine types. The build exports the actual compiler inputs, and this model's architecture test recompiles them with Roslyn and checks resolved type/member symbols by their declaring source folders, including partial and generated types. Build-generated resource accessors and assembly metadata are shared infrastructure, outside the three layers. Cache currently has no component types, so its guard protects future implementation. No new shipped-assembly visibility grant, wire or disk format, diagnostic code, or behavior is introduced.


Cache is deferred behind the Key-Value engine and currently has no implemented
engine, persistence, or lifecycle. Its empty source and test stubs do not define
the future Cache model architecture.

The only build correction in Phase 2 is the test project's item classification:
`xunit.runner.json` is content rather than a C# compile item. The stub test remains
unchanged. Future runtime implementation must compose the shared Database kernel
and follow the resource-area reference rules.

The test project references runner packages; runner configuration is a content
file owned by that project. This diagram shows those existing build dependencies.

```mermaid
flowchart LR
    Tests["Cache test project"] --> Runner["xUnit and test runner packages"]
    Tests --> Config["xunit.runner.json content"]
```


## Public assembly identity changes

The following public types move from their Catalog or Storage assembly into Assimalign.Cohesion.Database.Cache. Their namespaces, members, diagnostic codes and serialized representations are unchanged. Public members whose signatures expose these types also reference their new assembly identity. Consumers must replace satellite project/package/assembly references with the model reference and recompile; assembly-qualified type names change. No type forwarding or compatibility assembly is supplied. Empty Security (and Cache Catalog/Storage) projects had no public types.

This deferred model has no public Catalog or Storage types to move.
