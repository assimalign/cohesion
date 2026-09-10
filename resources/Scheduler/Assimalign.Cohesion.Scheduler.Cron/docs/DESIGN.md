# Scheduler Cron Design

Crontab accepts exactly five fields: minute, hour, day of month, month, and day of week. Each field supports wildcard, list, inclusive range, and positive step forms. Values are normalized and validated during parsing; malformed tokens, descending ranges, zero steps, and out-of-range values fail deterministically.

GetDateTime uses an exclusive lower bound and returns a minute-precision occurrence. Sunday is normalized from either 0 or 7. When both day fields are restricted they use OR semantics; when either is wildcard the restricted field governs. Evaluation crosses month, year, and leap-year boundaries and is bounded to one Gregorian cycle.

CronSchedule<TContext> evaluates against a TimeProvider, publishes the next occurrence, waits cancellably, and executes bound jobs through its internal provider. Cancellation stops future occurrences while an active occurrence drains.
