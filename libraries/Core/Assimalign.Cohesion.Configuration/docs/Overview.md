# Assimalign.Cohesion.Configuration

## Summary

Implements the core configuration object model, builder pipeline, provider abstractions, sections, values, and supporting key and path primitives.

## Current Evaluation

- Status: Partial
- Production source files: 31; key type candidates discovered: 8; test files discovered: 16.
- Project references: Assimalign.Cohesion.Core
- Package references: None
- NotImplementedException markers: 6

## Primary Responsibilities

- ConfigurationBuilder gathers provider factories and builds a configuration snapshot.
- ConfigurationManager extends that flow for longer-lived provider orchestration.
- Keys, paths, sections, values, and provider abstractions keep the rest of the configuration family consistent.

## Key Types

- Configuration
- ConfigurationBindingAttribute
- ConfigurationBuilder
- ConfigurationBuilderContext
- ConfigurationChangeToken
- ConfigurationEntry
- ConfigurationErrorCode
- ConfigurationException

## Source Layout

- src/Abstractions
- src/Decorators
- src/Exceptions
- src/Extensions
- src/Internal
- src/Properties
- src/ValueObjects
