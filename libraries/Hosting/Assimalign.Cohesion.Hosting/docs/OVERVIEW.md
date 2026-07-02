# Assimalign.Cohesion.Hosting

## Summary

Defines the hosting abstractions, lifecycle orchestration, environment and context types, and background-service base class for Cohesion applications.

## Current Evaluation

- Status: Active
- Production source files: 17; key type candidates discovered: 9; test files discovered: 9.
- Project references: Assimalign.Cohesion.Core
- Package references: None
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
- HostExtensions
- HostOptions
- HostState

## Source Layout

- src/Abstractions
- src/Exceptions
- src/Extensions
- src/Implementation
- src/Internal
- src/Properties
- src/ValueObjects
