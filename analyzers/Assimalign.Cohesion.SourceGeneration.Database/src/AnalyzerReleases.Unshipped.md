; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
COHMAP001 | Database.Mapping | Error | Schema callback cannot be analyzed without executing application code
COHMAP002 | Database.Mapping | Error | Entity members cannot be materialized and snapshotted statically
COHMAP003 | Database.Mapping | Error | Entity requires a non-null immutable scalar primary key
COHMAP004 | Database.Mapping | Error | Conflicting declarations would generate divergent mappings
