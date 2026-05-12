# Assimalign.Cohesion.Configuration.Xml

## Summary

Adds XML parsing and registration helpers to the configuration stack.

## Current Evaluation

- Status: Implemented
- Production source files: 9; key type candidates discovered: 9; test files discovered: 1.
- Project references: Assimalign.Cohesion.Configuration.FileSystem
- Package references: System.Security.Cryptography.Xml
- NotImplementedException markers: 0

## Primary Responsibilities

- Builder extensions provide the registration surface for files and streams.
- ConfigurationXmlProvider builds on the shared file-backed provider layer from Configuration.FileSystem.
- XmlConfigurationParser turns XML document structure into flattened configuration keys while preserving the composite-model constraints.
- XmlDocumentDecryptor keeps encrypted XML scenarios inside the format package instead of leaking them into callers.

## Key Types

- ConfigurationBuilderExtensions
- ConfigurationXmlOptions
- ConfigurationXmlProvider
- ConfigurationXmlStreamProvider
- XmlConfigurationElement
- XmlConfigurationElementAttributeValue
- XmlConfigurationElementTextContent
- XmlDocumentDecryptor
- XmlConfigurationParser

## Source Layout

- src/Extensions
- src/Internal
