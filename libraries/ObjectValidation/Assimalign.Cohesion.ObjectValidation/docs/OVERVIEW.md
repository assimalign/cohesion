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
- ValidationOptions control which failures are reported and whether a failure throws. With the defaults,
  every failing member is reported, each with the errors of one of its rules: `ValidationMode.Stop` stops
  at the first failing member, and `ContinueThroughValidationChain` runs every rule of a member (see
  [DESIGN.md](DESIGN.md), "Which Failures Are Reported").

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
