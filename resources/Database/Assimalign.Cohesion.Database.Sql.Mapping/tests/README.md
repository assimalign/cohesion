# SQL mapper acceptance tests

Run from the repository root:

```powershell
dotnet test resources/Database/Assimalign.Cohesion.Database.Sql.Mapping/tests/Assimalign.Cohesion.Database.Sql.Mapping.Tests.csproj
```

There are no external service or local package feed prerequisites. Each integration case starts
the real SQL engine and server with an in-memory connection transport, deploys the retained schema,
and executes the generated mapping through the SQL client. Query cases run the actual parser,
planner, evaluator and protocol value codec.

Ownership tests protect the retained table and its retained index. The engine deliberately
permits separately owned ad-hoc indexes on schema-owned tables; those indexes retain their own
ownership and are outside the mapper's API.

The commit-outcome cases inject the same missing acknowledgement at the mapper/client boundary
before and after forwarding COMMIT to the real server. This is deterministic fault injection,
not a claim that the current wire protocol provides a definite transaction outcome. Both paths
must discard the rental and prohibit replay; an independent scope observes their different
database states for application reconciliation.
