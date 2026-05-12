# Assimalign.Cohesion.ObjectValidation

## Summary

Implements a fluent validation engine built from validators, profiles, rules, and validation contexts.

## Current Evaluation

- Status: Active
- Production source files: 76; key type candidates discovered: 8; test files discovered: 28.
- Project references: None
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- Validator coordinates profile execution and produces ValidationResult objects.
- ValidationProfile and descriptor types capture the fluent rule configuration model.
- ValidationOptions control execution behavior such as stop-on-first-failure and throw-on-failure.

## Key Types

- IValidationCondition
- IValidationContext
- IValidationError
- IValidationItem
- IValidationItemQueue
- IValidationProfile
- IValidationProfileBuilder
- IValidationRule

## Source Layout

- src/Abstractions
- src/Exceptions
- src/Extensions
- src/Internal
- src/Properties
