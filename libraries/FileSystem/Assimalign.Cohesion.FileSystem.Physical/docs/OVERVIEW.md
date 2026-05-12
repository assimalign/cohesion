# Assimalign.Cohesion.FileSystem.Physical

## Summary

Implements the physical disk-backed IFileSystem backend and the companion factory registration extensions.

## Current Evaluation

- Status: Implemented
- Production source files: 8; key type candidates discovered: 8; test files discovered: 1.
- Project references: Assimalign.Cohesion.FileSystem
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- PhysicalFileSystem is the public adapter over the operating system file system.
- PhysicalFileSystemOptions shape runtime behavior without polluting the shared file-system contracts.
- Factory registration extensions keep physical storage easy to compose with other backends.

## Key Types

- FileSystemInfoHelper
- PhsysicalFileSystemExtensions
- PhysicalFileSystem
- PhysicalFileSystemChangeToken
- PhysicalFileSystemDirectory
- PhysicalFileSystemFile
- PhysicalFileSystemInfo
- PhysicalFileSystemOptions

## Source Layout

- src/Extensions
- src/Internal
