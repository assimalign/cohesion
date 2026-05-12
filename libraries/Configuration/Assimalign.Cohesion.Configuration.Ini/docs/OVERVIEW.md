# Assimalign.Cohesion.Configuration.Ini

## Summary

Adds INI parsing and registration helpers to the configuration stack.

## Current Evaluation

- Status: Implemented
- Production source files: 5; key type candidates discovered: 5; test files discovered: 1.
- Project references: Assimalign.Cohesion.Configuration
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- Builder extensions provide the familiar AddIniFile and AddIniStream entry points.
- The stream provider owns the parsing behavior for section and key handling.
- The package composes with the base configuration model rather than inventing a new data shape.

## Key Types

- IniConfigurationExtensions
- IniConfigurationProvider
- IniConfigurationSource
- IniStreamConfigurationProvider
- IniStreamConfigurationSource

## Source Layout

- No source subfolders were discovered.
