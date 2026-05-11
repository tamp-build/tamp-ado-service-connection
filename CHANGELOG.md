# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.1] - 2026-05-11

### Notes
- Object-init overloads on every ADO Service Connection wrapper (TAM-161 satellite fanout).
  - N/A for this satellite: `Tamp.AdoServiceConnection.V1` is an end-to-end orchestrator
    (`CreateWifAzureRmAsync(Tool az, WifAzureRmRequest request, ...)`) — it exposes no
    `CommandPlan`-returning verb wrappers with an `Action<TSettings>` configure delegate,
    and its public entry point already takes a record-shaped DTO that supports object-init
    construction natively. No overloads to add.

## [0.1.0]

- Initial release.
