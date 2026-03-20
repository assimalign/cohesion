# Assimalign.Cohesion.Configuration.Xml

## Summary

Adds XML parsing and registration helpers to the configuration stack.

## Current Evaluation

- Status: Implemented
- Production source files: 9; key type candidates discovered: 8; test files discovered: 1.
- Project references: Assimalign.Cohesion.Configuration.FileSystem
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- Builder extensions provide the registration surface for files and streams.
- The XML stream provider turns document structure into flattened configuration keys.
- An XmlDocumentDecryptor hook keeps encrypted XML scenarios inside the format package instead of leaking them into callers.

## Key Types

- XmlConfigurationElement
- XmlConfigurationElementAttributeValue
- XmlConfigurationElementTextContent
- XmlConfigurationExtensions
- XmlConfigurationProvider
- XmlConfigurationSource
- XmlDocumentDecryptor
- XmlStreamConfigurationProvider

## Source Layout

- No source subfolders were discovered.
