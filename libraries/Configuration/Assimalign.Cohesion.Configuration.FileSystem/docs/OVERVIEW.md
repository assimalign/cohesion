# Assimalign.Cohesion.Configuration.FileSystem

## Summary

Defines the bridge layer for configuration providers that read from the abstract Cohesion file-system model instead of directly from physical files.

## Current Evaluation

- Status: Implemented
- Production source files: 4; key type candidates discovered: 4; test files discovered: 1.
- Project references: Assimalign.Cohesion.Configuration, Assimalign.Cohesion.FileSystem
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- FileSystemConfigurationOptions captures the file-system, target path, and reload behavior.
- FileSystemConfigurationProvider centralizes watch and reload behavior over IFileSystemFile.
- Format-specific providers can inherit from this layer instead of rewriting file access concerns.

## Key Types

- ConfigurationBuilderExtensions
- ConfigurationFileLoadExceptionContext
- FileSystemConfigurationOptions
- FileSystemConfigurationProvider

## Source Layout

- src/Extensions
