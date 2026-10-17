# Cohesion GQL dialect

The target is ISO/IEC 39075:2024 with the subset and extensions recorded in [DESIGN.md](DESIGN.md).
The supported-clause matrix there is authoritative; RETURN still projects bound variables
and properties only.

## WHERE expressions

WHERE accepts AND, OR, XOR, NOT, comparisons, bare Boolean properties, IS [NOT]
NULL/TRUE/FALSE/UNKNOWN, numeric + - * /, % (Cypher MOD spelling), `||`, string +,
labeled predicates and parentheses. IN [items], STARTS WITH, ENDS WITH and CONTAINS
are Cohesion extensions. String predicates are ordinal, case-sensitive and UNKNOWN unless
both operands are strings. Lists are restricted to IN's right operand. LIKE, BETWEEN, =~,
EXISTS, quantified predicates and nested property access remain COHDBL001; IN (1, 2)
is GQL0002 naming the bracketed form.

WHERE keeps TRUE only. Logical operators use three-valued logic. Incomparable non-null values
are unequal; UNKNOWN is a Boolean literal represented by NULL. Incomparable ordering is UNKNOWN.
IN over an empty list is FALSE even for NULL;
otherwise a match wins, then a NULL operand or item yields UNKNOWN, otherwise FALSE.
IS truth tests accept Boolean/NULL and return Boolean. Evaluated non-Boolean logical values
raise COHDBG013.

Numeric promotion selects Double for any floating operand, otherwise Decimal for decimal/ulong,
otherwise checked Int64. Integer division truncates toward zero. Zero divisors, overflow,
non-finite results and invalid arithmetic types raise COHDBG013; concatenation requires two
strings. Structured engine diagnostics carry code-free messages; protocol 1.0 sends
ExecutionFailure `COHDBG013: message` before ResultHeader.

Expressions have no count limit. Recursive depth follows the thread's stack: GQL0009 during
parsing, COHDBG008 in the engine. See [DESIGN.md](DESIGN.md#scalar-where-expressions-1140)
for the grammar, precedence, promotion decisions, comments and reference evidence.
