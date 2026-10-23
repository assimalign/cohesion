# Database error codes

A Database diagnostic identity is two uppercase letters followed by five decimal digits.
The prefix identifies the component that owns the failure. The suffix is allocated within
one of the categories below; it is an identity, not an ordinal that callers should interpret.
The suffix has the form `CCNNN`: two category digits and three condition digits.
Use the complete code for branching. Allocation is append-only within a category; never
reuse a published identity for another condition. Allocate after the highest assigned number
in the chosen category or documented subcategory; leave retired identities reserved. `00000` is unassigned.

| Prefix | Owner |
|---|---|
| CK | Shared Database kernel and infrastructure |
| CS | SQL |
| CD | Documents |
| CB | Blob |
| CG | Graph |
| CV | KeyValuePair |
| CC | Cache |

| Suffix range | Category |
|---|---|
| 00001–00100 | Language and syntax |
| 00101–00999 | Reserved |
| 01000–01999 | Binding and name resolution |
| 02000–02999 | Values, types and encoding |
| 03000–03999 | Schema, catalog and constraints |
| 04000–04999 | Transactions, locking and transaction state |
| 05000–05999 | Execution, capabilities and budgets |
| 06000–06999 | Storage, durability and format |
| 07000–07999 | Protocol, transport, authentication and sessions |
| 08000–08999 | Configuration and argument validation |
| 09000–09999 | Hosting, pooling and lifecycle |
| 10000–99999 | Reserved |

The code migration changes diagnostic strings and corresponding legacy message prefixes.
It does not change SQLSTATE values, numeric wire error kinds, frame layouts, protocol versions,
or disk formats. A diagnostic carries its code separately from its unprefixed message; legacy
message-only failures carry one prefix. Framework argument guards keep .NET exception contracts
and do not gain synthetic database identities. Compiler/analyzer diagnostic identifiers such as
`COHDBSDK` and `COHRES` are independent and remain unchanged.

This registry covers the Database edit boundary. CK does not reserve codes for unrelated
Cohesion libraries or resource areas. Cache has its prefix reserved even when an operation
reports through the existing cache result vocabulary rather than an exception diagnostic.

## Kernel migration

| Previous code | Code | Condition family |
|---|---|---|
| COHDBL001 | CK00001 | Unsupported language clause |
| COHDBS001 | CK06001 | Storage format refusal |
| COHDBS002 | CK06002 | Storage offline |
| COHDBS003 | CK06003 | Record or page corruption |
| COHDBI001 | CK06101 | Index format refusal |
| COHDBI002 | CK06102 | Index corruption |
| COHDBP001 | CK07001 | Diagnostic truncation notice |
| COHDBP002 | CK07002 | Transaction status refusal |
| COHDBTCP001 | CK07101 | TCP listener host/address refusal |
| COHDBO001 | CK08001 | Listener option refusal |
| COHDBO002 | CK08002 | Session count refusal |
| COHDBO003 | CK08003 | Server timeout refusal |
