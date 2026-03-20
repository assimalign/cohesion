# Assimalign.Cohesion.Configuration.Binder

## Summary

Adds reflection-based binding helpers that project IConfiguration values into typed .NET objects.

## Current Evaluation

- Status: Implemented
- Production source files: 2; key type candidates discovered: 2; test files discovered: 1.
- Project references: Assimalign.Cohesion.Configuration
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- Extension methods like Get<T>, Bind, and GetValue<T> live directly on IConfiguration.
- Binding can either create new instances or populate existing ones.
- Because the binder is reflection-driven, trimming and AOT concerns are part of the design conversation for callers.

## Key Types

- ConfigurationBinder
- ConfigurationBinderOptions

## Source Layout

- No source subfolders were discovered.
