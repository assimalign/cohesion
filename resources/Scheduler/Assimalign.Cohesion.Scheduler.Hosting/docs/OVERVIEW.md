# Scheduler Hosting Overview

Assimalign.Cohesion.Scheduler.Hosting supplies SchedulerApplication.CreateBuilder. The internal host executes all schedules supplied through registered providers while preserving caller-added host services.

Enabled resources inherit their environment, content root, http endpoint, bootstrap credential, and shutdown grace from ResourceRuntime. The runtime observes the endpoint, attaches the default control plane, exposes health/readiness/liveness and management routes, and drains active occurrences during stop.
