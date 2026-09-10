# Assimalign.Cohesion.Scheduler

The root package defines Scheduler application, context, job, schedule, and provider contracts. SchedulerApplication.CreateBuilder lives in the Hosting package.

Declare delegate-backed work with AddJob. The returned IScheduleJob remains dormant until AddCronSchedule or AddTimerSchedule binds it. Build validates that every scheduled job is the same instance present in the declaration registry.

ISchedulerApplicationContext exposes read-only job and provider snapshots for observation and hosting. ISchedule exposes next and last scheduled timestamps, status, priority, retry metadata, bound jobs, and its asynchronous evaluation loop.
