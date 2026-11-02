# ADR-0003: SQLite by default, SQL Server ready

**Status:** accepted

## Context
Reviewers should be able to run everything with only the .NET SDK. The target estate uses SQL Server and Azure SQL.

## Decision
- **One provider-neutral EF Core model.** `Store:Provider` selects SQLite (default) or SQL Server.
- **The model avoids provider quirks:** UTC `DateTime` conversion, enums stored as text, and an application-managed `Revision` concurrency token instead of `rowversion`.
- **The single-runner lease is a conditional `UPDATE`,** which behaves the same on both providers.
- **The schema is created with `EnsureCreated`.**

## Consequences
- Zero setup locally, and the SQL Server path is one configuration change. SQL Server was not exercised here because Docker wasn't available; it should get a CI job against a SQL Server container.
- `EnsureCreated` can't evolve an existing schema. Production would ship EF migration bundles per provider and apply them in the deployment pipeline.

## Alternatives considered
- **SQL Server in Docker by default:** adds a dependency for every reviewer.
- **In-memory state:** loses the crash-recovery guarantees the design relies on.
