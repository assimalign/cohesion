# Assimalign.Cohesion.Caching

## Summary

Defines the small synchronous cache contracts for Cohesion and includes a current in-memory implementation scaffold.

## Current Evaluation

- Status: Partial
- Production source files: 6; key type candidates discovered: 4; test files discovered: 1.
- Project references: None
- Package references: None
- NotImplementedException markers: 6

## Primary Responsibilities

- ICache is the minimal core contract and keeps the public surface easy to embed.
- IMemoryCache, IDistributedCache, and ICacheEntry give the package room to grow into richer caching scenarios.
- The current MemoryCache implementation is still incomplete, so the package is stronger as an API contract than as a finished runtime component today.

## Key Types

- CacheExtensions
- ICache
- ICacheEntry
- MemoryCache

## Source Layout

- src/Abstractions
- src/Extensions
