# Assimalign.Cohesion.Resilience.Retry

## Summary

Provides the retry strategy options, callback arguments, and builder extensions for the Cohesion resilience pipeline.

## Current Evaluation

- Status: Implemented
- Production source files: 17; key type candidates discovered: 8; test files discovered: 2.
- Project references: Assimalign.Cohesion.Resilience
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- RetryStrategyOptions carries retry count, delay, jitter, predicate, and callback behavior.
- RetryResilienceExtensions adds the strategy to both generic and non-generic pipeline builders.
- Internal strategy types keep execution details out of the public options surface.

## Key Types

- DelayBackoffType
- OnRetryArguments
- RetryConstants
- RetryDelayGeneratorArguments
- RetryHelper
- RetryPredicateArguments
- RetryResilienceEventSource
- RetryResilienceExtensions

## Source Layout

- src/Extensions
- src/Internal
