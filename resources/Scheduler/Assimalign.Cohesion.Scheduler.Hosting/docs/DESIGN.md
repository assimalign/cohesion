# Scheduler Hosting Design

SchedulerApplication discovers an enabled resource registration from the entry assembly and builds an internal SchedulerApplicationHost. The builder materializes user host services, validates every provider binding against the declared job registry, adds the resource control-plane listener when an ambient http endpoint exists, and finally adds the schedule execution service.

The execution service snapshots schedules from every registered IScheduleProvider and runs their evaluation loops concurrently. It does not execute unbound jobs. On stop it cancels trigger waits, prevents new occurrences, and joins any occurrence already underway within the shared host shutdown budget.

The private http listener serves public /healthz, /readyz, and /livez probes and their authenticated /cohesion/v1 equivalents. It also serves authenticated endpoint and command discovery and accepts /cohesion/v1/stop. Readiness stays unavailable until the outer Scheduler host reaches Started.

Builder, context, host, execution service, and listener are internal. The only public creation surface is SchedulerApplication; contracts live in the root package.

## HTTPS endpoint certificate contract (31t)

The enabled resource's `http` listener consumes the shared Hosting.Resources endpoint certificate accessor. Endpoint metadata identifies an ordinary Secret mount (default `tls`), carrying one PEM leaf/private-key/chain document; existing hand-authored IdentityHub and LogSpace bundles retain the same format. Empty mounts are absent; malformed or multi-key bundles fail. TLS options are composed in Hosting from the returned leaf and chain, with no hosting-isolation exemptions or dependency changes. Plain application composition is unchanged.
