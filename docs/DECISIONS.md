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

Status: Accepted; refined by ADR-040

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

Status: Superseded by ADR-042

Decision: the MVP is roadmap Phases 0–3: foundation, read-only AI, basic modeling (walls, rooms, doors, windows, floors) and context intelligence. Documentation (Phase 4) and model QA (Phase 5) are not MVP success criteria.

Reason: resolves the conflict between `PRODUCT_SPEC.md §7/§14`, ADR-016 and `ROADMAP.md` (review items A3, A4).

---

## ADR-032 — OpenAI Client

Status: Accepted (2026-10-07)

Decision: call the OpenAI Chat Completions API with strict function calling directly over `HttpClient` and `System.Text.Json`, behind `IAiClient`. No OpenAI SDK.

Reason: every add-in shares Revit's process; fewer third-party assemblies means fewer version conflicts with other add-ins. The request/response surface we need is small and covered by unit tests.

Details:

- The model name is a setting (`OpenAiModel`, default `gpt-5`). Verify it against the current OpenAI model list.
- Tool schemas follow strict-mode rules: every property listed in `required`, `additionalProperties: false`, optional values expressed as `["type", "null"]`.
- The API key is read from the encrypted store on every request (ADR-029).
- Moving to the newer Responses API, or to another provider, only changes the `IAiClient` implementation.

---

## ADR-033 — Project Standards File

Status: Accepted (2026-10-07)

Decision: preferred types live in `%APPDATA%\RevitAi\standards.json`, edited by the user:

```json
{
  "default":  { "Walls": ["Basic Wall: Interior - 100mm"], "Doors": ["Single-Flush: 0915 x 2134mm"] },
  "projects": { "House": { "Walls": ["Basic Wall: Interior - 125mm"] } }
}
```

- Entries are `"Family: Type"` or `"Type"`, most preferred first; category names as shown in Revit.
- A project entry (document title without `.rvt`) replaces the default list for the categories it names.
- The AI reads standards through `get_project_standard_types` and `find_family_types` and never writes them (resolves review item A8 and decision D6).
- The file is created empty on first start and re-read on every tool call, so edits apply without restarting Revit.

Reason: deterministic, inspectable and editable without code (PRODUCT_SPEC §13).

---

## ADR-034 — Panel Language

Status: Accepted (2026-10-07)

Decision: panel texts exist in English and Romanian (`UiText`); English is the default, set with `Language` in `settings.json` or the panel's language button.

- The AI's instructions stay in English for reliability. They state the interface language, and the AI answers in the language the user writes in.
- Plan summaries, step outcomes and failure reasons from the write tools and the plan executor follow the panel language (`TextSource`, shared and switchable at runtime).
- Not localized: Revit's own warnings (they come in Revit's language) and errors from read tools (seen only by the AI, which answers in the user's language).
- A unit test enforces that every English text has a Romanian translation with the same placeholders.

---

## ADR-035 — Selection Is Not a Model Change

Status: Accepted (2026-10-07)

Decision: `select_elements` (select and zoom to elements) is a read tool. It runs immediately, without a plan or confirmation, because it changes only Revit's UI selection and view zoom, never the model or its undo history.

---

## ADR-036 — Model Checks Are Read-Only; No Deletes Yet

Status: Superseded by ADR-037 for deleting (checks remain read-only)

Decision: Phase 5 checks only report problems. Fixes use existing write tools through plan → preview → apply. Removing duplicates or orphaned elements needs `delete_element`, which is `DESTRUCTIVE`. It stays unavailable until the explicit-confirmation flow (UX risk level 4) is built, and the tool registry rejects destructive tools until then.

---

## ADR-037 — Destructive Actions: Preview, Then Explicit Confirmation

Status: Accepted (2026-10-07)

Decision: `delete_elements` is the first `DESTRUCTIVE` tool (UX risk level 4).

- A plan containing it requires a successful **Preview** first. The preview performs the deletion and rolls it back, so it reports the full impact including dependent elements (e.g. doors in a deleted wall).
- After the preview, the user must tick an explicit confirmation ("I have checked the preview and want to delete these elements"). The tick resets after every preview and on every new plan. Apply stays disabled until then.
- Only model elements and annotations can be deleted, at most 200 per plan. Element types, views, sheets, levels, grids and pinned elements are refused.
- The AI may plan a deletion only when the user explicitly asks to delete; never as a side effect.
- The deletion is still one undo entry (Ctrl+Z), and the history records every deleted ID.

---

## ADR-038 — Company Rules in standards.json

Status: Accepted (2026-10-07)

Decision: `standards.json` (ADR-033) also holds rules, with optional per-project overrides:

```json
{
  "rules": {
    "roomNames": ["Living", "Kitchen", "Bedroom", "Bathroom"],
    "sheetNumberPattern": "^A\\d{3}$",
    "viewNamePatterns": { "FloorPlan": "^Level \\d+ - " },
    "requiredParameters": { "Doors": ["Mark", "Fire Rating"] },
    "viewTemplates": { "FloorPlan": "Architectural Plan" }
  },
  "projectRules": { "House": { "sheetNumberPattern": "^H-\\d{2}$" } }
}
```

- `check_standards` evaluates every configured rule plus the standard types and reports violations per rule. `get_project_standards` gives the AI the rules so it follows them when naming and creating.
- Invalid regular expressions, unknown view types or categories, and patterns that take too long to evaluate are reported as problems, never silently treated as passing.
- Fixes go through plan → preview → apply: `apply_view_template`, and `set_parameters` for values and renames. `set_parameters` is `LARGE_MODIFICATION` (bulk), instance parameters only, and accepts only values it can convert from mm/m²/degrees/m³ exactly.
- Renames that require a judgement (which allowed name, which number) are proposed to the user, never guessed.

---

## ADR-039 — Workflows as Fixed Recipes

Status: Accepted (2026-10-07)

Decision: high-level workflows (ADR-014) are fixed, versioned recipes in `RevitAi.Core/Workflows` (`prepare_floor_for_documentation`, `qa_floor`, `create_room_with_walls`, `create_sheet_set`, `fix_standards`). The AI fetches one with `get_workflow` and follows its steps using existing tools. All changes still form one plan the user previews and applies.

Reason: deterministic, reviewable steps without a second execution engine. A unit test ensures recipes only reference tools that exist.

Also: `MaxAiSteps` default raised from 8 to 12 for workflows. `settings.json` now stores only values that differ from the defaults, so changed defaults reach existing installs.

---

## ADR-040 — Product Direction: AI BIM Engineer

Status: Accepted (2026-10-07)

Decision: the product is positioned and specified as an AI BIM engineer inside Revit with four capabilities: **Understand,
Check, Act, Automate** (PRODUCT_SPEC §1). Conceptual modes Ask / Analyze / Fix / Create / Automate / Standards are
capabilities of one assistant, not separate screens.

- The architecture does not change (ARCHITECTURE §19): the agent is an orchestration capability of the existing components.
  Agent loop: understand → inspect → reason → plan → validate plan → preview → confirm when required → execute → validate result → report.
- No microservices, event infrastructure, agent frameworks, vector databases or other infrastructure are added because of the "agent" positioning.
- All existing safety decisions remain: registered tools only (ADR-005), deterministic validation (ADR-006), plan → preview → apply (ADR-024),
  registry-owned risk (ADR-025), destructive confirmation (ADR-037).

Reason: the existing architecture already implements the loop; the new direction mainly adds a first-class issue model (ADR-041).

---

## ADR-041 — Issues Are First-Class; Fixability Is Deterministic

Status: Accepted (2026-10-07), implementation planned in roadmap Phase 8–9

Decision:

- Check results converge on one Issue model (ARCHITECTURE §19): ID, category, severity, description, affected elements,
  location/view, detected by, suggested fix, auto-fixable, validation status.
- Detectors are deterministic: the existing check tools, new detectors (room boundaries, documentation) and Revit's own warnings.
- Whether an issue is auto-fixable, and which tool fixes it, is decided by deterministic fix rules in code, never by the AI.
- A fix is reported as fixed only when re-running the detector no longer finds the issue (VALIDATION §11).
- Supersedes ADR-036 ("checks are read-only") for fixing: checks stay read-only; fixes go through plans.

---

## ADR-042 — MVP Boundary

Status: Proposed (2026-10-07)

Proposal: the MVP is

- Phases 0–3 (understand, ask, basic modeling, context): Select / Ask → Understand → Plan → Preview → Apply → Validate, and
- the core of Phase 8: Issue model, model check with an explained issue list, and fixing single issues with re-check,

**validated in Revit** through the smoke tests. Phases 4–7 already exist and remain available, but are not MVP acceptance criteria.
"Fix everything that is safe" (Phase 9), agent workflows (Phase 10) and layout generation (Phase 11) are post-MVP.

Supersedes ADR-031.

---

## ADR-043 — Log Unmet Requests

Status: Accepted (2026-10-07)

Decision: when the AI cannot do what the user asks because no tool supports it, it calls `report_unavailable_request`, which
appends the request and the missing capability to `%LOCALAPPDATA%\RevitAi\unmet-requests.jsonl`. The file is local only;
nothing is sent anywhere. It drives the backlog from real demand.

---

## ADR-044 — General Building Blocks Plus Revit-Free Analysis

Status: Accepted (2026-10-07)

Decision: new capabilities are preferably built as a Revit-free analysis function in Core (unit-tested) plus general write
tools, rather than one special-purpose tool per request. First instances: `GridDetection` + `find_grid_lines` (read),
`create_grids` and `place_family_instances` (write, LARGE because they create many elements).

---

## ADR-045 — Automatic In-Revit Self-Test

Status: Accepted (2026-10-08)

Decision: a **Self-test** ribbon command runs about 50 scripted checks inside Revit against the real tools:

- Tools are called through the same `Validate` / `Execute` code as the AI path, with arguments checked against each tool's schema; plans run through the real `PlanExecutor` (transactions, failure handling, references).
- Test content is built about 300 m from the internal origin, and the whole run happens in one transaction group that is **always rolled back**. The project is unchanged.
- Category and parameter names come from Revit, so the checks work in non-English Revit. Checks that need content the project lacks (door, room tag, title block, level-based family, template) are reported as skipped, not failed.
- A Markdown report (`%LOCALAPPDATA%\RevitAi\self-test-<date>.md`) lists failures first.
- Not covered, so tested manually: the panel UI, OpenAI, the dispatcher's threading, and Ctrl+Z after a real Apply.

Reason: manual smoke testing does not scale with ~50 tools. One click plus one file makes every rebuild verifiable.
