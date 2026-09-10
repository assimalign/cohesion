# Scheduler Hosting Design

SchedulerApplication discovers an enabled resource registration from the entry assembly and builds an internal SchedulerApplicationHost. The builder materializes user host services, validates every provider binding against the declared job registry, adds the resource control-plane listener when an ambient http endpoint exists, and finally adds the schedule execution service.

The execution service snapshots schedules from every registered IScheduleProvider and runs their evaluation loops concurrently. It does not execute unbound jobs. On stop it cancels trigger waits, prevents new occurrences, and joins any occurrence already underway within the shared host shutdown budget.

The private http listener serves public /healthz, /readyz, and /livez probes and their authenticated /cohesion/v1 equivalents. It also serves authenticated endpoint and command discovery and accepts /cohesion/v1/stop. Readiness stays unavailable until the outer Scheduler host reaches Started.

Builder, context, host, execution service, and listener are internal. The only public creation surface is SchedulerApplication; contracts live in the root package.
