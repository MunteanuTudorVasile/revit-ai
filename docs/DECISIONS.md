# Revit AI Assistant — Architecture Decisions

This document records important decisions.

Claude Code should treat these decisions as the current source of truth unless explicitly changed.

---

## ADR-001 — Revit Integration

Status: Accepted

Decision: use a native Revit add-in using C#/.NET and the Autodesk Revit API.

Reason: the product needs reliable access to Revit's BIM model and transaction system.

---

## ADR-002 — User Interface

Status: Accepted

Decision: use a WPF dockable panel initially.

Reason: it provides a native-feeling experience inside Revit and is appropriate for the initial product.

---

## ADR-003 — AI Provider

Status: Accepted

Decision: use OpenAI as the initial AI provider.

Reason: the product requires strong natural-language reasoning and structured tool/function calling.

The AI provider must remain isolated behind an abstraction.

---

## ADR-004 — AI Execution

Status: Accepted

Decision: the AI cannot generate or execute arbitrary C#.

Reason: security, reliability, reproducibility and BIM correctness.

---

## ADR-005 — Tool Layer

Status: Accepted

Decision: all AI interaction with Revit occurs through a controlled tool registry.

Reason — provides:

- security
- validation
- observability
- testability
- predictable behavior

---

## ADR-006 — Validation

Status: Accepted

Decision: validation is deterministic and independent of the AI.

Reason: an LLM is not a reliable authority for BIM validity.

---

## ADR-007 — Transactions

Status: Accepted

Decision: all model modifications use Revit transactions. Complex operations should use transaction groups where appropriate.

Reason: provides rollback and model integrity.

---

## ADR-008 — Preview

Status: Accepted

Decision: large or risky operations require Preview → Apply.

Reason: users need confidence before significant model changes.

---

## ADR-009 — Context

Status: Accepted

Decision: do not send the entire Revit model to the AI for every request. Instead construct task-specific context.

Reason:

- lower cost
- lower latency
- better relevance
- improved privacy
- reduced token usage

---

## ADR-010 — Project Standards

Status: Accepted

Decision: the assistant should prefer existing project/company standards.

Reason: AI should adapt to the project instead of imposing arbitrary conventions.

---

## ADR-011 — Family Selection

Status: Accepted

Decision: the assistant must search for existing families/types before attempting placement.

Reason: family/type IDs cannot be hallucinated.

---

## ADR-012 — Backend

Status: Accepted

Decision: no external backend is required for the initial MVP.

Reason: the initial product can operate as a Revit add-in with direct AI API access. A backend can be introduced later.

---

## ADR-013 — Cloud Architecture

Status: Deferred

Potential future services:

- authentication
- subscriptions
- organizations
- usage
- project memory
- company standards

Do not implement until required.

---

## ADR-014 — High-Level Workflows

Status: Accepted

Decision: support high-level workflows in addition to primitive tools.

Example — Create Apartment may internally use:

- inspect boundary
- find wall types
- generate layout
- create walls
- create rooms
- place doors
- place windows
- validate

Reason: users think in outcomes, not individual API operations.

---

## ADR-015 — Revit API Threading

Status: Accepted

Decision: all Revit API operations must respect Revit's supported execution/threading model.

Reason: Revit API access cannot be treated like a normal thread-safe application API.

---

## ADR-016 — MVP Scope

Status: Amended by ADR-031

MVP focuses on:

- context
- chat
- tool calling
- walls
- rooms
- doors
- windows
- floors
- basic documentation
- validation
- preview
- transactions
- history

Advanced BIM disciplines and image/PDF understanding are deferred.

---

## ADR-017 — Testing

Status: Accepted

Decision — separate:

- domain tests
- validation tests
- AI contract tests
- Revit integration tests

AI tests should not depend entirely on live LLM calls.

---

## ADR-018 — No Premature Abstraction

Status: Accepted

Decision: do not create abstractions merely because a future feature may need them.

Implement the simplest architecture that preserves the documented boundaries.

---

## ADR-019 — Security

Status: Accepted

Decision: the AI can only access explicitly registered tools.

No arbitrary:

- shell
- PowerShell
- file system
- C#
- assembly loading

Reason: the assistant is effectively an automation agent inside a user's professional BIM environment.

---

## ADR-020 — Product Philosophy

Status: Accepted

The product is not "ChatGPT inside Revit."

The product is "an intelligent, safe, context-aware Revit assistant that converts natural-language intent into validated BIM operations."

---

## ADR-021 — Supported Revit Versions

Status: Accepted (2026-10-07)

Decision: support Revit 2025 and 2026. Both run on .NET 8 (verify against the Autodesk SDK of each version), so the add-in has a single target framework (`net8.0-windows`), built once per Revit year with a `RevitVersion` build property.

Revit 2024 and earlier (.NET Framework 4.8) are not supported. Revit 2027 is added only after its runtime is verified.

---

## ADR-022 — Development Environment

Status: Accepted (2026-10-07)

Decision: code is written on macOS and unit-tested there (Revit-free projects only). Building, loading, debugging and integration testing happen on a Windows PC with licensed Revit.

Consequence: `RevitAi.Core` and its tests must build and run on macOS. `RevitAi.Addin` must compile on macOS (`EnableWindowsTargeting`) but is only run on Windows.

---

## ADR-023 — Revit Dispatcher

Status: Accepted (2026-10-07)

Decision: all Revit API access — reads included — goes through one `RevitDispatcher` built on a single `ExternalEvent`, a request queue and `TaskCompletionSource`, with timeouts and cancellation. Nothing else calls the Revit API from the UI or from async continuations.

Reason: Revit API calls are only valid inside Revit's API context; the AI loop is async (see ADR-015).

---

## ADR-024 — Write Execution Model (Plan → Preview → Apply)

Status: Accepted (2026-10-07)

Decision:

- Read tools execute immediately inside the AI loop.
- Write tools are not executed by the AI loop. Each call is validated and appended to a pending plan; the AI receives a symbolic reference (e.g. `$op1.elementId`) for later operations.
- Preview runs the plan in a transaction and rolls it back, reporting counts, measurements and Revit warnings (data preview; no visual preview in the MVP).
- Apply runs the plan in one `TransactionGroup`, resolving symbolic references in order, and assimilates it into a single undo entry named after the action.

---

## ADR-025 — Risk and Confirmation Are Deterministic

Status: Accepted (2026-10-07)

Decision: every tool has a fixed risk level in the registry. A plan's risk is the highest risk among its operations, escalated by size thresholds. Whether confirmation is required is computed from the risk; any risk or confirmation flag suggested by the AI is ignored.

Reason: safety must not depend on the LLM (ADR-006).

---

## ADR-026 — Units in Tool Contracts

Status: Accepted (2026-10-07)

Decision: all tool inputs and outputs use millimetres for length, m² for area and degrees for angles, regardless of project display units. Units are stated in every schema description. Conversion to Revit internal units (feet, radians) happens only at the Revit boundary via `UnitUtils`. Values shown to the user are formatted in project display units.

---

## ADR-027 — Revit Failure Handling

Status: Accepted (2026-10-07)

Decision: every AI transaction uses an `IFailuresPreprocessor` that collects warnings into the validation result and rolls back on errors. Revit warning dialogs must never appear in the middle of an AI action.

---

## ADR-028 — Target Discipline

Status: Accepted (2026-10-07)

Decision: the first version targets architecture (walls, rooms, doors, windows, floors), as specified. MEP remains deferred.

---

## ADR-029 — OpenAI API Key

Status: Accepted (2026-10-07)

Decision: each user provides their own OpenAI API key, entered once in the add-in's settings and stored encrypted with Windows DPAPI (current user scope). The key is never written to source code, logs, or plain-text settings files, and is never included in AI context.

Reason: no backend is needed for the MVP (ADR-012).

Consequence: each user needs an OpenAI account and pays for their own usage. If the product later needs centralized billing or usage limits, a proxy backend (ADR-013) replaces this; only the AI client's endpoint and authentication change.

---

## ADR-030 — Data Sent to the AI

Status: Accepted (2026-10-07)

Decision: on first run, the user is told that model data (element names, types, parameters, levels, views) is sent to OpenAI and must accept before the assistant is enabled. Text from the model (names, parameter values, linked models) is treated as untrusted data, never as instructions.

---

## ADR-031 — MVP = Phases 0–3

Status: Accepted (2026-10-07)

Decision: the MVP is roadmap Phases 0–3: foundation, read-only AI, basic modeling (walls, rooms, doors, windows, floors) and context intelligence. Documentation (Phase 4) and model QA (Phase 5) are not MVP success criteria.

Reason: resolves the conflict between `PRODUCT_SPEC.md §7/§14`, ADR-016 and `ROADMAP.md` (review items A3, A4).
