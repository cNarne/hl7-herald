# Jacobian HL7 Integration Service

A .NET 8 Windows Service that watches a configurable directory for inbound HL7 v2.4 ORM^O01 (Order Message) files, parses them into typed order records, and persists them via a repository interface.

---

## Project Structure

```
src/
  Jacobian.Hl7Integration.Domain/        # Typed domain models: OrderRecord, OrderGroup, Hl7Identifier
  Jacobian.Hl7Integration.Parser/        # Hl7OrmParser — pure, thread-safe parsing logic
  Jacobian.Hl7Integration.Worker/        # IHostedService file watcher, DI wiring, configuration
tests/
  Jacobian.Hl7Integration.Parser.Tests/  # 25 unit tests covering all parser branches
docs/
  MIGRATION-STRATEGY.md                  # Migration rationale, risk assessment, phasing plan
samples/                                 # Sample .hl7 files for manual testing
hl7-migration-evaluation.md             # Full evaluation: bugs found, option comparison, recommendation
prompts.md                              # Prompt log documenting the AI-assisted development process
```

---

## How to Run

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) — version 8.0.400 or later

### Run the tests

```bash
dotnet test
```

All 25 tests pass with 0 warnings. The test suite covers: happy-path parsing, two-order-group messages (multi-order regression), PID-3 type-code selection, OBR-6 exclusion, escape sequence decoding, line ending normalisation, DTM format variants, and edge cases.

### Run the worker (development)

1. Set `WatchDirectory` in `src/Jacobian.Hl7Integration.Worker/appsettings.Development.json`:

```json
{
  "Ingestion": {
    "WatchDirectory": "C:\\Temp\\hl7-watch",
    "ConcurrencyLimit": 2
  }
}
```

2. Create the directory, then run:

```bash
mkdir C:\Temp\hl7-watch
dotnet run --project src/Jacobian.Hl7Integration.Worker
```

Drop any `.hl7` file from the `samples/` directory into the watch path — the service picks it up automatically.

> **Note:** `IOrderRepository` is defined as an interface but has no concrete implementation (see Trade-offs). The ingestion pipeline runs fully end-to-end up to the repository call.

### Install as a Windows Service

```bash
dotnet publish src/Jacobian.Hl7Integration.Worker -r win-x64 --self-contained -o publish/
sc create "Jacobian HL7 Ingestion" binPath="C:\path\to\publish\Jacobian.Hl7Integration.Worker.exe"
sc start "Jacobian HL7 Ingestion"
```

The service name and configuration path are set in `Program.cs` and `appsettings.json`.

---

## Stack Choice and Rationale

**.NET 8, C# 12, NHapi 3.x.** The legacy parser was a .NET Framework 4.8.1 Windows Service with six silent data-loss bugs — including wrong patient IDs from incorrect PID-3 splitting, silently dropped order groups on multi-order messages, and datetime values read from a deprecated field that most hospital systems leave empty. After a structured eight-category evaluation comparing .NET 8 against Node.js/TypeScript (documented in `hl7-migration-evaluation.md`), .NET 8 with NHapi was chosen for two decisive reasons: NHapi provides a fully typed ORM^O01 message model that makes the hardest bugs structurally impossible — order group repetition is handled correctly by `message.GetORDER()`, PID-3 repetitions are split by `pid.GetPatientIdentifierList()`, and HL7 escape sequences are decoded automatically on field access — and the migration is additive rather than a full rewrite, preserving the existing Windows Service host model, CI/CD pipeline, and deployment tooling.

---

## Trade-offs and Known Limitations

- **`IOrderRepository` is not implemented.** The interface is defined and fully wired into DI (`src/Jacobian.Hl7Integration.Worker/Services/IOrderRepository.cs`). A concrete EF Core or ADO.NET implementation is required before records are actually persisted.

- **Parser is synchronous.** NHapi's `PipeParser` is a synchronous library. `Parse()` is called synchronously inside the async `ProcessFileAsync` pipeline — wrapping it in `Task.Run` would misrepresent its CPU cost and was explicitly avoided per the project constraints.

- **Single message type.** Only ORM^O01 is handled. ORU^R01, ADT^A01, and other common event types would require additional parser implementations and a message-type router in the ingestion layer.

---

## What I Would Do Differently with More Time

- **Implement `IOrderRepository` with EF Core 8.** The schema would store `RawHl7Message` as `nvarchar(max)`, all date fields as `datetimeoffset`, and use a composite unique index on `(MessageControlId, SendingApplication)` for idempotency. An `ISaveChangesInterceptor` would write an audit log row on every insert.

- **Add integration tests with Testcontainers.** The current suite is pure unit tests against the parser. A SQL Server Testcontainer would enable end-to-end testing of the full ingestion pipeline including persistence, idempotency enforcement, and transaction rollback on parse failure.

- **Corpus regression tests.** Obtain or generate A set of anonymised real-world HL7 messages from each sending system, run against the parser as a regression suite on every build. This catches NHapi version upgrade behavioral changes and source system quirks — such as non-standard field delimiters or missing required segments — before they reach production.
