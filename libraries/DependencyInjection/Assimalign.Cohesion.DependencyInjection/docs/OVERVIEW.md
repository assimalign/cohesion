# Assimalign.Cohesion.DependencyInjection

## Summary

Implements a homegrown dependency injection container with service descriptors, collections, builders, scopes, call-site resolution, and activator utilities.

## Current Evaluation

- Status: Active
- Production source files: 60; key type candidates discovered: 8; test files discovered: 59.
- Project references: Assimalign.Cohesion.Core
- Package references: None
- NotImplementedException markers: 1

## Primary Responsibilities

- ServiceDescriptor and IServiceCollection capture registration intent independently of runtime resolution.
- ServiceProviderBuilder composes a provider from descriptors and exposes extension points for registration helpers.
- Resolution internals, call sites, scopes, and activator utilities sit behind the public contracts so the outer API remains compact.

## Key Types

- ActivatorUtilities
- ActivatorUtilitiesConstructorAttribute
- AsyncServiceScope
- CallSiteChain
- CallSiteFactory
- CallSiteKind
- CallSiteResultCache
- CallSiteResultCacheLocation

## Source Layout

- src/Abstractions
- src/Extensions
- src/Internal
- src/Properties
- src/Scopes
- src/Utilities
