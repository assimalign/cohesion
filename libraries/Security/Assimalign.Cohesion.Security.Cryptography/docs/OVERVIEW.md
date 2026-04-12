# Assimalign.Cohesion.Security.Cryptography

## Summary

Provides certificate store management abstractions and OS-specific certificate-provider selection for Cohesion.

## Current Evaluation

- Status: Partial
- Production source files: 17; key type candidates discovered: 8; test files discovered: 1.
- Project references: None
- Package references: None
- NotImplementedException markers: 10

## Primary Responsibilities

- CertificateManager is the public entry point that selects providers based on OS and store location.
- ICertificateProvider encapsulates store access, certificate lookup, import, export, and creation.
- ICertificateResult gives callers a normalized view of certificate validity and trust state.

## Key Types

- CertificateContext
- CertificateManager
- CertificateManagerException
- CertificateManagerExtensions
- CertificateManagerOptions
- CertificateNotFoundException
- CertificateProvider
- CertificateProviderBase

## Source Layout

- src/Abstractions
- src/Exceptions
- src/Extensions
- src/Internal
