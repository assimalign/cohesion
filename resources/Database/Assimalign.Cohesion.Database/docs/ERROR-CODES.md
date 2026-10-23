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

## Sql migration

SQL syntax uses `CS00001`–`CS00099`; `CS00100` retains the recognized-unsupported profile condition.
Within category 03, condition bands 000–099 cover catalog/DDL, 100–199 provisioning,
200–299 declared scripts, 300–399 registered definitions, and 400–499 generic constraint failures.
These bands make allocation predictable while callers continue to match the complete identity.
The declared-script `CS03201`–`CS03205` refusals retain their existing message-only contract;
structured-carrier messages remain unprefixed.

| Previous code | Code | Category |
|---|---|---|
| SQL0001 | CS00001 | Language/syntax |
| SQL0002 | CS00002 | Language/syntax |
| SQL0003 | CS00003 | Language/syntax |
| SQL0004 | CS00004 | Language/syntax |
| SQL0005 | CS00005 | Language/syntax |
| SQL0006 | CS00006 | Language/syntax |
| SQL0007 | CS00007 | Language/syntax |
| SQL0008 | CS00008 | Language/syntax |
| SQL0100 | CS00100 | Language/syntax |
| COHSQLE005 | CS01005 | Binding/names |
| COHSQLE006 | CS01006 | Binding/names |
| COHSQLE008 | CS01008 | Binding/names |
| COHSQLE009 | CS01009 | Binding/names |
| COHSQLE014 | CS01014 | Binding/names |
| COHSQLE015 | CS01015 | Binding/names |
| COHSQLE016 | CS01016 | Binding/names |
| COHSQLE019 | CS01019 | Binding/names |
| COHSQLE020 | CS01020 | Binding/names |
| COHSQLE023 | CS01023 | Binding/names |
| COHSQLE026 | CS01026 | Binding/names |
| COHSQLE028 | CS01028 | Binding/names |
| COHSQLE051 | CS01051 | Binding/names |
| COHSQLE052 | CS01052 | Binding/names |
| COHSQLE001 | CS02001 | Values/types |
| COHSQLE002 | CS02002 | Values/types |
| COHSQLE003 | CS02003 | Values/types |
| COHSQLE010 | CS02010 | Values/types |
| COHSQLE011 | CS02011 | Values/types |
| COHSQLE012 | CS02012 | Values/types |
| COHSQLE018 | CS02018 | Values/types |
| COHSQLE022 | CS02022 | Values/types |
| COHSQLE034 | CS02034 | Values/types |
| COHSQLE046 | CS02046 | Values/types |
| COHSQLE047 | CS02047 | Values/types |
| COHSQLE048 | CS02048 | Values/types |
| COHSQLE055 | CS02055 | Values/types |
| COHSQLE062 | CS02062 | Values/types |
| COHSQLE063 | CS02063 | Values/types |
| COHSQLE024 | CS03024 | Schema/constraints |
| COHSQLE025 | CS03025 | Schema/constraints |
| COHSQLE027 | CS03027 | Schema/constraints |
| COHSQLE029 | CS03029 | Schema/constraints |
| COHSQLE030 | CS03030 | Schema/constraints |
| COHSQLE031 | CS03031 | Schema/constraints |
| COHSQLE033 | CS03033 | Schema/constraints |
| COHSQLE035 | CS03035 | Schema/constraints |
| COHSQLE037 | CS03037 | Schema/constraints |
| COHSQLE038 | CS03038 | Schema/constraints |
| COHSQLE039 | CS03039 | Schema/constraints |
| COHSQLE049 | CS03049 | Schema/constraints |
| COHSQLE050 | CS03050 | Schema/constraints |
| COHSQLE053 | CS03053 | Schema/constraints |
| COHSQLE054 | CS03054 | Schema/constraints |
| COHSQLE056 | CS03056 | Schema/constraints |
| COHSQLE059 | CS03059 | Schema/constraints |
| COHSQLE061 | CS03061 | Schema/constraints |
| COHSQLE065 | CS03065 | Schema/constraints |
| COHSQLE066 | CS03066 | Schema/constraints |
| COHSQLP001 | CS03101 | Schema/constraints |
| COHSQLP002 | CS03102 | Schema/constraints |
| COHSQLP003 | CS03103 | Schema/constraints |
| COHSQLP004 | CS03104 | Schema/constraints |
| COHSQLP005 | CS03105 | Schema/constraints |
| COHSQLS001 | CS03201 | Schema/constraints |
| COHSQLS002 | CS03202 | Schema/constraints |
| COHSQLS003 | CS03203 | Schema/constraints |
| COHSQLS004 | CS03204 | Schema/constraints |
| COHSQLS005 | CS03205 | Schema/constraints |
| COHSQLD001 | CS03301 | Schema/constraints |
| COHSQLD002 | CS03302 | Schema/constraints |
| COHSQLD003 | CS03303 | Schema/constraints |
| COHSQLC001 | CS03401 | Schema/constraints |
| COHSQLT001 | CS04001 | Transactions/locking |
| COHSQLT002 | CS04002 | Transactions/locking |
| COHSQLT003 | CS04003 | Transactions/locking |
| COHSQLT004 | CS04004 | Transactions/locking |
| COHSQLT005 | CS04005 | Transactions/locking |
| COHSQLT006 | CS04006 | Transactions/locking |
| COHSQLE021 | CS04021 | Transactions/locking |
| COHSQLE044 | CS04044 | Transactions/locking |
| COHSQLE064 | CS04064 | Transactions/locking |
| COHSQLE004 | CS05004 | Execution/budgets |
| COHSQLE007 | CS05007 | Execution/budgets |
| COHSQLE013 | CS05013 | Execution/budgets |
| COHSQLE017 | CS05017 | Execution/budgets |
| COHSQLE032 | CS05032 | Execution/budgets |
| COHSQLE036 | CS05036 | Execution/budgets |
| COHSQLE040 | CS05040 | Execution/budgets |
| COHSQLE042 | CS05042 | Execution/budgets |
| COHSQLE043 | CS05043 | Execution/budgets |
| COHSQLE045 | CS05045 | Execution/budgets |
| COHSQLE057 | CS05057 | Execution/budgets |
| COHSQLE060 | CS05060 | Execution/budgets |
| COHSQLE067 | CS05067 | Execution/budgets |
| COHSQLE041 | CS06041 | Storage/durability |
| COHSQLE058 | CS06058 | Storage/durability |

SQLSTATE meanings remain those in [the SQL dialect](../../Assimalign.Cohesion.Database.Sql.Language/docs/DIALECT.md); the Cohesion identities changed, not ISO/PostgreSQL SQLSTATE values.

## Documents migration

| Previous code | Code | Category |
|---|---|---|
| OQL0001 | CD00001 | Language/syntax |
| OQL0002 | CD00002 | Language/syntax |
| OQL0003 | CD00003 | Language/syntax |
| OQL0004 | CD00004 | Language/syntax |
| OQL0005 | CD00005 | Language/syntax |
| OQL0006 | CD00006 | Language/syntax |
| OQL0007 | CD00007 | Language/syntax |
| OQL0008 | CD00008 | Language/syntax |
| OQL0009 | CD00009 | Language/syntax |
| OQL0010 | CD00010 | Language/syntax |
| COHDOC002 | CD01002 | Binding/names |
| COHDOC003 | CD01003 | Binding/names |
| COHDOC005 | CD01005 | Binding/names |
| COHDOC009 | CD01009 | Binding/names |
| COHDOC014 | CD01014 | Binding/names |
| COHDOC017 | CD01017 | Binding/names |
| COHDOC023 | CD01023 | Binding/names |
| COHDOC025 | CD01025 | Binding/names |
| COHDOC006 | CD02006 | Values/types |
| COHDOC007 | CD02007 | Values/types |
| COHDOC008 | CD02008 | Values/types |
| COHDOC013 | CD02013 | Values/types |
| COHDOC016 | CD02016 | Values/types |
| COHDOC018 | CD02018 | Values/types |
| COHDOC019 | CD02019 | Values/types |
| COHDOC021 | CD02021 | Values/types |
| COHDOC026 | CD02026 | Values/types |
| COHDOC010 | CD03010 | Schema/constraints |
| COHDOC011 | CD03011 | Schema/constraints |
| COHDOC012 | CD03012 | Schema/constraints |
| COHDOC020 | CD03020 | Schema/constraints |
| COHDOC022 | CD03022 | Schema/constraints |
| COHDOC024 | CD03024 | Schema/constraints |
| COHDOC004 | CD04004 | Transactions/locking |
| COHDBD001 | CD04101 | Transactions/locking |
| COHDOC001 | CD05001 | Execution/budgets |
| COHDOC015 | CD05015 | Execution/budgets |
| COHDBD002 | CD06102 | Storage/durability |
| COHDBD003 | CD06103 | Storage/durability |
| COHDBD004 | CD06104 | Storage/durability |
| COHDBD005 | CD06105 | Storage/durability |

## Graph migration

| Previous code | Code | Category |
|---|---|---|
| GQL0001 | CG00001 | Language/syntax |
| GQL0002 | CG00002 | Language/syntax |
| GQL0003 | CG00003 | Language/syntax |
| GQL0004 | CG00004 | Language/syntax |
| GQL0005 | CG00005 | Language/syntax |
| GQL0006 | CG00006 | Language/syntax |
| GQL0007 | CG00007 | Language/syntax |
| GQL0008 | CG00008 | Language/syntax |
| GQL0009 | CG00009 | Language/syntax |
| GQL0010 | CG00010 | Language/syntax |
| COHDBG002 | CG01002 | Binding/names |
| COHDBG010 | CG01010 | Binding/names |
| COHDBG011 | CG01011 | Binding/names |
| COHDBG014 | CG01014 | Binding/names |
| COHDBG015 | CG01015 | Binding/names |
| COHDBG009 | CG02009 | Values/types |
| COHDBG013 | CG02013 | Values/types |
| COHDBG003 | CG03003 | Schema/constraints |
| COHDBG007 | CG04007 | Transactions/locking |
| COHDBG022 | CG04022 | Transactions/locking |
| COHDBG023 | CG04023 | Transactions/locking |
| COHDBG001 | CG05001 | Execution/budgets |
| COHDBG004 | CG05004 | Execution/budgets |
| COHDBG008 | CG05008 | Execution/budgets |
| COHDBG020 | CG05020 | Execution/budgets |
| COHDBG006 | CG06006 | Storage/durability |
| COHDBG012 | CG06012 | Storage/durability |
| COHDBG017 | CG06017 | Storage/durability |
| COHDBG018 | CG06018 | Storage/durability |
| COHDBG005 | CG07005 | Protocol/transport |
| COHDBG016 | CG07016 | Protocol/transport |
| COHDBG021 | CG08021 | Configuration |
| COHDBG019 | CG09019 | Lifecycle |
