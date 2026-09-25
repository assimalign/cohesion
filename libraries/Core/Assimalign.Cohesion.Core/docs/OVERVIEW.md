# Assimalign.Cohesion.Core

## Summary

Provides the foundational primitives, frozen resource environment contract, typed `System.Uri`
endpoints, environment helpers, glob support, and low-level extensions shared across Cohesion.

## Current Evaluation

- Status: Partial
- Production source files: 59; key type candidates discovered: 10; test files discovered: 13.
- Project references: None
- Package references: None
- NotImplementedException markers: 8

## Primary Responsibilities

- Core value types such as Size and Glob capture reusable concepts that show up across multiple libraries.
- Exception and system extension helpers centralize small cross-cutting behaviors.
- The library is dependency-light by design so other Core packages can reference it safely.

## Key Types

- AdaptiveMemoryPool
- AdaptiveMemoryPoolOptions
- AdaptiveMemoryPoolPressurePolicy
- AdaptiveMemoryPoolSnapshot
- AIMetadataAttribute
- AppEnvironment
- ResourceEnvironment
- UriExtensions
- AsyncExtensions
- CertificateManager

## Source Layout

- src/Exceptions
- src/Internal
- src/Properties
- src/Shared
- src/System
- src/Utilities
