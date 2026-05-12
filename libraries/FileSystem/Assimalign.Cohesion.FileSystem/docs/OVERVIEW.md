# Assimalign.Cohesion.FileSystem

## Summary

Defines the common file-system abstraction for Cohesion, including factories, paths, file and directory contracts, events, watchers, and file-system extensions.

## Current Evaluation

- Status: Implemented
- Production source files: 17; key type candidates discovered: 8; test files discovered: 2.
- Project references: Assimalign.Cohesion.Core
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- IFileSystem, IFileSystemFile, IFileSystemDirectory, and related interfaces define the common contract.
- FileSystemFactoryBuilder handles named and typed backend registration.
- Events, glob-aware watching, and helper extensions are part of the base abstraction rather than optional add-ons.

## Key Types

- ErrorMessages
- FileSystemEnumerationOptions
- FileSystemErrorCode
- FileSystemEvent
- FileSystemEventType
- FileSystemException
- FileSystemExtensions
- FileSystemFactoryBuilder

## Source Layout

- src/Abstractions
- src/Exceptions
- src/Extensions
- src/Internal
- src/Properties
