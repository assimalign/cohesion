# Assimalign.Cohesion.ObjectPool

## Summary

Provides generic object-pool abstractions, default pool implementations, and helper extensions for common pooling scenarios.

## Current Evaluation

- Status: Implemented
- Production source files: 16; key type candidates discovered: 8; test files discovered: 3.
- Project references: Assimalign.Cohesion.Core
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- ObjectPool<T> and ObjectPool<T, TArgs> define the rent and return contract.
- Factories create new instances while policies decide whether returned instances should be kept.
- DefaultObjectPool provides the standard retention behavior without forcing callers to build their own pool types.

## Key Types

- CancellationTokenSourcePool
- DefaultObjectPool
- DefaultObjectPoolFactory
- DefaultObjectPoolPolicy
- ObjectPool
- ObjectPoolDisposable
- ObjectPoolExtensions
- ObjectPoolFactory

## Source Layout

- src/Extensions
- src/Internal
- src/Properties
