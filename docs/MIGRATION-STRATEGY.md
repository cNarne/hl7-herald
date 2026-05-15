# HL7 v2 ORM^O01 Parser — Migration Strategy

**Source:** `LegacyHl7Parser.cs` (.NET Framework 4.8.1)  
**Target:** .NET 8 + NHapi  
**Date:** 2026-05-15  
**Companion document:** `hl7-migration-evaluation.md`

---

## 1. Behavioral and Infrastructure Risks

### Behavioral Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| **Multi-order message drops (Bug 4)** — legacy parser silently overwrites on every ORC/OBR pass | High | Critical | New `List<OrderGroup>` model; regression suite with 2+ order messages |
| **Wrong patient matched (Bug 2)** — PID-3 repetition split by `^` instead of `~` first | Medium | Critical | NHapi `GetPatientIdentifierList()` returns correct `CX[]`; explicit type-code selection |
| **Null `OrderedDateTime` (Bug 3)** — OBR-6 is deprecated and almost always empty | High | High | Read ORC-9 → OBR-7 only; OBR-6 is never accessed |
| **Garbled names (Bug 5)** — HL7 escape sequences stored literally | Low–Medium | Medium | NHapi decodes `\F\`, `\S\`, `\E\`, `\R\`, `\T\` automatically on field access |
| **Segment parse failure after CRLF (Bug 6)** — `\r\n` leaves `\n` prefix on field[0] | High | High | `NormalizeLineEndings()` applied before NHapi parse AND before raw storage |
| **MSH-9 missing component 3** — `ORM^O01` vs `ORM^O01^ORM_O01` | High | High | `NoValidation` context on `PipeParser`; tighten incrementally via corpus tests |
| **NHapi 1-based field numbers vs 0-based C# collections** | Medium | Medium | Never access by index; use named properties (`orc.PlacerOrderNumber`) only |

### Infrastructure Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| `ServiceBase` → `IHostedService` host model change | Low | Medium | `UseWindowsService()` drop-in; SCM integration preserved |
| `app.config` → `appsettings.json` | Low | Low | Direct key mapping; no behavioral change |
| `DateTime` stored as string → `DateTimeOffset` column | Medium | Medium | Schema migration adds `datetimeoffset` column; old `string` column kept for parallel period |
| NHapi version upgrades changing lenient-parse behavior | Low | Medium | Pin exact version in `.csproj`; treat upgrades as requiring full corpus regression |
| EF6 → EF Core 8 query translation differences | Medium | Low | Query audit; existing LINQ expressions are simple enough that translation is equivalent |

---

## 2. Incremental Phasing Approach

### Phase 0 — Corpus Capture (Week 1)
Instrument the production `.NET Framework 4.8.1` service to log every raw inbound message to a capture file.
Target: 72-hour rolling window of real hospital messages.
This corpus drives regression tests in Phase 2 and validates NHapi lenient parsing behavior.

### Phase 1 — New Domain + Parser (Weeks 1–2)
Deploy `Jacobian.Hl7Integration.Domain` and `Jacobian.Hl7Integration.Parser` as class libraries.
Run the new `Hl7OrmParser` **alongside** the legacy parser on every production message.
Log discrepancies: fields where old and new disagree, order count mismatches, date parse failures.
**No writes to the order database yet.** This phase is pure observability.

### Phase 2 — Regression Gate (Week 3)
Feed the Phase 0 corpus through the new parser.
Gate: zero unhandled exceptions, zero `OrderGroups.Count == 0` for messages with known orders,
`PatientId` matches the MR number from the source system for every message.
Only proceed to Phase 3 when the gate passes.

### Phase 3 — Worker Service + Side-by-Side Writes (Weeks 3–4)
Deploy `Jacobian.Hl7Integration.Worker` targeting the new parser.
Write to a **shadow schema** (`orders_v2` tables) in parallel with the legacy schema.
Reconciliation job compares shadow to production counts per 15-minute window.

### Phase 4 — Cutover + Legacy Decommission (Week 5)
Swap the worker as the authoritative writer.
Keep the `.NET Framework 4.8.1` service running in read-only (ACK generation only) mode for 5 business days.
Decommission legacy service after 5 days of zero reconciliation discrepancies.

---

## 3. Rationale for .NET 8 over Node.js/TypeScript

The full comparison is in `hl7-migration-evaluation.md` §Option B. The decisive factors:

1. **NHapi eliminates the order-group problem.** `message.GetORDER()` in a loop gives each ORC/OBR pair correctly scoped by the message model. In Node.js there is no equivalent library; building it is weeks of engineering debt owned forever.

2. **`DateTimeOffset` over JavaScript `Date`.** HL7 DTM timezone offsets (`-0500`) are not ISO 8601. Node.js `new Date()` returns `Invalid Date`. A custom parser is required, and `Date` loses the offset internally unless wrapped with a library. .NET's `DateTimeOffset.TryParseExact` with a normalized format handles this natively.

3. **Stack coherence.** The rest of the integration service (MLLP listener, ACK generator, ADT consumer) is .NET. A Node.js island creates a bifurcated CI/CD pipeline, two deployment models, and a split hiring profile.

4. **Native Windows Service integration.** `UseWindowsService()` preserves SCM event log, service recovery policies, and service account security. Node.js requires NSSM or a VBScript wrapper.

### Scenario Where Node.js Would Have Been Correct

If this service were being built **from scratch** as a cloud-native HTTP microservice, with no existing .NET infrastructure, deployed to Linux containers, and consumed exclusively by REST clients (no MLLP, no Windows Service, no ADO.NET), the argument for Node.js would be viable — specifically if the team's primary expertise were TypeScript and the workload were purely event-driven I/O with no CPU-bound parsing. Even then, the absence of a mature typed HL7 model library would be the deciding factor against Node.js for ORM^O01 parsing specifically.

---

## 4. Proposed Solution Layout

```
Jacobian.Hl7Integration.sln
│
├── src/
│   ├── Jacobian.Hl7Integration.Domain/         # No dependencies outside BCL
│   │   ├── Models/
│   │   │   └── OrderModels.cs                  # OrderRecord, OrderGroup, Hl7Identifier
│   │   └── Exceptions/
│   │       └── Hl7ParseException.cs
│   │
│   ├── Jacobian.Hl7Integration.Parser/         # Depends on Domain + NHapi
│   │   ├── Hl7OrmParser.cs
│   │   └── Extensions/
│   │       └── ServiceCollectionExtensions.cs  # DI registration
│   │
│   └── Jacobian.Hl7Integration.Worker/         # Depends on Domain + Parser
│       ├── Program.cs                          # IHostedService host
│       ├── Configuration/
│       │   └── IngestionOptions.cs             # IOptions<T> config
│       └── Services/
│           ├── IOrderRepository.cs             # Persistence contract
│           └── Hl7FileIngestionService.cs      # Channel<T> file watcher
│
└── tests/
    └── Jacobian.Hl7Integration.Parser.Tests/   # xUnit + Verify + Testcontainers
        ├── Fixtures/
        │   └── TestFixtures.cs                 # Shared HL7 message strings
        └── Hl7OrmParserTests.cs
```

**Dependency graph (no cycles):**
```
Worker → Parser → Domain
  Tests → Parser → Domain
```

**Why three projects, not one?**
- `Domain` has zero runtime dependencies (no NHapi, no Microsoft.Extensions.*). It can be referenced by persistence layers, ACK generators, and API surfaces without pulling the parser or worker into the dependency graph.
- `Parser` depends on NHapi but not on any hosting infrastructure. It can be unit-tested without a running host.
- `Worker` owns the `IHostedService` host, file system, and `IOptions<T>` — concerns that should not leak into the parser.
