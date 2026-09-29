# Assimalign.Cohesion.Scheduler.ApplicationModel

The assembly provides the public SchedulerResource and SchedulerResourceOptions types, the AddScheduler application-builder verb, and SchedulerResourceControlPlane.Create.

SchedulerResource delegates platform-neutral planning to an internal singleton, stateless planner. AddScheduler returns an IApplicationResourceDescriptor for graph composition. SchedulerResourceControlPlane.Create returns a fresh default control plane with no area command kinds.
