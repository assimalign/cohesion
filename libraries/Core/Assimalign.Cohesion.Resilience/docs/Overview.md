# Assimalign.Cohesion.Resilience

## Summary

Implements the core resilience pipeline builder, execution context, outcomes, and strategy composition model used by the resilience strategy packages.

## Current Evaluation

- Status: Transitional
- Production source files: 87; key type candidates discovered: 8; test files discovered: 2.
- Project references: Assimalign.Cohesion.Core, Assimalign.Cohesion.ObjectPool
- Package references: None
- NotImplementedException markers: 1

## Primary Responsibilities

- ResiliencePipelineBuilder composes strategies from the outside in and produces executable pipelines.
- Outcome and IResilienceContext give every strategy a shared execution contract.
- Concrete policies such as retry and timeout are intentionally pushed into sibling packages that extend the base builder.

## Key Types

- BridgeComponent
- BridgeComponentBase
- BridgeStrategy
- CancellationTokenSourcePool
- ComponentDisposeHelper
- ComponentWithDisposeCallbacks
- CompositeComponent
- CompositeComponentDebuggerProxy

## Source Layout

- src/_old
- src/Abstractions
- src/Delegates
- src/Exceptions
- src/Extensions
- src/Internal
- src/Properties
- src/ValueTypes
