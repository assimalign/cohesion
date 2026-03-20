# Assimalign.Cohesion.Resilience.Timeout

## Summary

Provides the timeout strategy options, timeout exception, and builder extensions for the Cohesion resilience pipeline.

## Current Evaluation

- Status: Implemented
- Production source files: 11; key type candidates discovered: 8; test files discovered: 1.
- Project references: Assimalign.Cohesion.Resilience
- Package references: None
- NotImplementedException markers: 1

## Primary Responsibilities

- TimeoutStrategyOptions owns fixed and dynamic timeout selection.
- TimeoutResilienceExtensions attaches the strategy to pipeline builders.
- TimeoutRejectedException keeps timeout failures explicit at the strategy boundary.

## Key Types

- OnTimeoutArguments
- TimeoutGeneratorArguments
- TimeoutRejectedException
- TimeoutResilienceContext
- TimeoutResilienceEventSource
- TimeoutResilienceExtensions
- TimeoutResilienceStrategy
- TimeoutResilienceStrategyBase

## Source Layout

- src/Exception
- src/Extensions
- src/Internal
