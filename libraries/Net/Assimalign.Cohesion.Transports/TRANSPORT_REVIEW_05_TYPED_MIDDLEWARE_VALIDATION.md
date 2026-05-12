# Typed Middleware Validation

## Problem

`TransportPipelineBuilder<TConnection, TContext>` previously treated runtime type mismatches as a no-op. If a pipeline configured for one connection/context pair was executed with another pair, middleware was skipped and the pipeline appeared to complete successfully.

## Chosen Direction

Typed middleware registration remains strongly typed, but runtime mismatches now fail explicitly with `TransportPipelineConfigurationException`.

## Why

- Configuration mistakes are surfaced immediately instead of being hidden.
- Failure messages now show the expected and actual connection/context types.
- The normal transport path is unchanged because UDP, TCP, and QUIC options already build matching typed pipelines.

## Tradeoff

This is a behavioral breaking change for anyone relying on mismatched typed pipelines silently completing. The new behavior is intentionally strict because it is safer and easier to diagnose.
