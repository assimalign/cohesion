# Assimalign.Cohesion.Logging.Console

## Summary

Adds the console-specific logger provider for the core Cohesion logging contracts.

## Current Evaluation

- Status: Partial
- Production source files: 2; key type candidates discovered: 2; test files discovered: 1.
- Project references: Assimalign.Cohesion.Logging
- Package references: None
- NotImplementedException markers: 5

## Primary Responsibilities

- ConsoleLoggerProvider is the package entry point for registration into a factory.
- ConsoleLogger is the provider-specific logger implementation.
- Formatting and console output behavior belong here rather than in the shared logging contracts.

## Key Types

- ConsoleLogger
- ConsoleLoggerProvider

## Source Layout

- src/Internal
