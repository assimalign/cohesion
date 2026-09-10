# ISchedulerApplicationBuilder

Namespace: Assimalign.Cohesion.Scheduler

Assembly: Assimalign.Cohesion.Scheduler

The builder separates job declaration from trigger binding:

- AddJob registers an IScheduleJob without executing it.
- AddScheduleProvider contributes schedules for Hosting to run.
- AddService contributes ordinary host services.
- Build returns the typed ISchedulerApplication.

Feature packages expose convenient AddCronSchedule and AddTimerSchedule verbs over AddScheduleProvider. Hosting rejects providers whose schedules bind a job that was not declared through AddJob.
