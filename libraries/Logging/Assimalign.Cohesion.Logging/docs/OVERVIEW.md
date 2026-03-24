# Assimalign.Cohesion.Logging

## Summary

Provides the core logging abstractions, entries, factories, providers, and scope contracts for Cohesion.

## Current Evaluation

- Status: Implemented
- Production source files: 16; key type candidates discovered: 8; test files discovered: 1.
- Project references: Assimalign.Cohesion.Core
- Package references: None
- NotImplementedException markers: 4

## Primary Responsibilities

- ILogger, ILoggerProvider, ILoggerFactory, and the scope interfaces form the public contract surface.
- LoggerEntry carries the structured data for a log event.
- LoggerFactory composes providers into cached named loggers instead of forcing every caller to manage providers directly.

## Key Types

- CompositeLogger
- ILogger
- ILoggerEntry
- ILoggerFactory
- ILoggerFactoryBuilder
- ILoggerProvider
- Logger
- LoggerEntry

## Source Layout

- src/Abstractions
- src/Extensions
- src/Internal
- src/ValueObjects
