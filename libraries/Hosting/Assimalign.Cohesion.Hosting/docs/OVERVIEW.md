# Assimalign.Cohesion.Hosting

## Summary

Defines the hosting abstractions, lifecycle orchestration, environment and context types,
transport-neutral health contributions, and service base classes for Cohesion applications.

## Current Evaluation

- Status: Active
- Production source includes the host lifecycle, ambient resource context, and control-plane contracts.
- Project references: Assimalign.Cohesion.Core
- Package references: System.Security.Cryptography.ProtectedData (Windows BCL facade)
- NotImplementedException markers: 0

## Primary Responsibilities

- Host<TContext> is the lifecycle coordinator that starts, stops, and tracks hosted services.
- HostContext and IHostEnvironment isolate runtime state from the host implementation itself.
- BackgroundService is the pool-scheduled base class for long-running units of asynchronous work inside a host.
- DedicatedThreadService is the base class for synchronous, blocking units of work that own a dedicated background OS thread.

## Key Types

- BackgroundService
- DedicatedThreadService
- Host
- HostContext
- HostEnvironment
- HostException
- HostStartupException
- HostExtensions
- HostOptions
- HostState
- HealthContribution
- HealthStatus
- IHealthContributor
- IResourceEntryInvocation
- IResourceControlPlane
- ResourceCommand
- ResourceContext
- ResourceControlPlane
- ResourceHealthCheck
- ResourceHealthReport
- ResourceMount
- ResourceRuntime

## Source Layout

- src/Abstractions
- src/Exceptions
- src/Extensions
- src/Implementation
- src/Internal
- src/Properties
- src/ValueObjects
