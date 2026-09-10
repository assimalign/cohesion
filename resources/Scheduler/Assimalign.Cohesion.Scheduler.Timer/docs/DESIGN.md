# Scheduler Timer Design

TimerSchedule<TContext> waits for the configured due time, executes all enabled bound jobs sequentially, then waits the fixed interval after completion. This fixed-delay behavior prevents overlapping occurrences within one schedule.

Timer providers are internal and register through the root ISchedulerApplicationBuilder seam. A positive interval is required. Trigger cancellation stops waits and future occurrences but does not cancel work already running; outer host shutdown grace bounds the drain.
