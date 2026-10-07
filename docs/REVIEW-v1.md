# Specification Review v1

Review of `CLAUDE.md` and `docs/*` before any implementation. No code has been written.

Items marked **(verify)** are Revit/.NET facts that must be checked against the Autodesk SDK docs for the chosen version before they are relied on.

---

## 1. Critical Issues

These block implementation or would force a rewrite if left undecided.

### C1. No decision on how the AI loop reaches Revit (threading)

The spec says "respect Revit's threading model" but does not define the mechanism, and it is the core of the whole add-in.

- Every Revit API call — **including reads** — must run in a valid API context. A WPF button handler in a modeless dockable pane is not one; an `await`ed OpenAI response continuation is not one.
- The standard mechanism is `ExternalEvent` + `IExternalEventHandler`, raised from the UI and executed by Revit when it is idle.
- The AI loop is async and calls tools many times per request. So the orchestrator needs an awaitable dispatcher: `Task<T> RevitDispatcher.InvokeAsync(Func<UIApplication, T>)` built on a queue + `TaskCompletionSource`, served by a single `ExternalEvent`.
- Consequences to document: Revit does not run external events while a modal dialog is open or a command is active, so tool calls can stall; the dispatcher needs timeouts and cancellation.

**Recommendation:** add this as an ADR and put it in Phase 0. Nothing else in the design works without it.

### C2. Two incompatible execution models for modifications

- `AI_COMMANDS.md §2` describes a tool-calling loop: AI calls a tool → it executes → result goes back to the AI.
- `AI_COMMANDS.md §5–6, §13` and `UX_SPEC.md` describe a plan: AI emits a command object with several operations → preview → user clicks Apply → execution.

These conflict for write tools. If `create_wall` executes inside the loop, there is nothing left to preview or confirm. If it does not execute, later operations cannot use its result (e.g. `create_door` needs the `hostWallId` of a wall that does not exist yet).

**Recommendation:**
- Read tools execute immediately inside the loop.
- Write tools are **not executed** by the loop; each call is validated and appended to a pending plan, and the AI receives "queued as op N" with a symbolic reference (e.g. `"$op1.elementId"`).
- On Apply, the plan runs in one `TransactionGroup`, resolving symbolic references in order, then `Assimilate()` → one undo entry.

### C3. Confirmation is decided by the AI

`AI_COMMANDS.md §5, §12` have the AI set `requiresConfirmation`. That contradicts the core principle that safety is deterministic and independent of the LLM (`VALIDATION.md §15`, ADR-006).

**Recommendation:** risk and confirmation are computed by the tool registry: the plan's risk = the highest risk of its operations, escalated by thresholds (e.g. more than N elements created → `LARGE_MODIFICATION`). Ignore any risk the AI suggests.

### C4. Units are inconsistent across tool contracts

Revit's internal length unit is **decimal feet**. The docs mix:

- `create_wall.height: 2800` (mm),
- `create_room.location {x: 5000}` (mm),
- `calculate_distance` with `x: 5` → `distance: 5` (metres?),
- `create_level.elevation: 3000` (mm).

The LLM will get this wrong unless there is one rule.

**Recommendation:** all tool inputs and outputs use **millimetres** for length, **m²** for area and **degrees** for angles, independent of project display units, and the units are stated in every schema description. Convert at the Revit boundary with `UnitUtils`. Results shown to the user are formatted in project display units.

### C5. Preview has no feasible definition yet

Revit has no general "show this change without making it" API. Options:

| Option | Reality |
|---|---|
| Data preview from a rolled-back transaction | Run the plan in a transaction, read the results and Revit's warnings, then `RollBack()`. Cheap, and it gives **real** Revit validation. Nothing visual remains after rollback. |
| `DirectContext3D` transient graphics | Real visual preview, but a substantial amount of work for each element type. |
| Temporary elements + highlight | Modifies the model, pollutes undo, and conflicts with "preview must not change the model". |

**Recommendation (MVP):** data preview via dry-run + rollback (counts, lengths, areas, warnings), plus selecting/zooming to affected existing elements. Defer visual preview.

### C6. Revit failures and warnings are not handled

Revit reports problems ("walls overlap", "room not enclosed", "highlighted elements are joined but do not intersect") through its failure-processing system. Without an `IFailuresPreprocessor` on every transaction, those appear as **modal dialogs in the middle of an AI action** and block the event loop (see C1).

**Recommendation:** every AI transaction gets a failures preprocessor that collects warnings into the validation result and, for errors, rolls back. This is the real source of "post-execution validation".

### C7. Development machine

Revit is Windows-only. This repo is on macOS. Running Revit in a VM on Apple Silicon is not an officially supported configuration **(verify)**.

You can compile (`net8.0-windows` with `EnableWindowsTargeting`) and unit-test the Revit-free code on macOS, but loading, debugging and integration testing need a Windows machine with licensed Revit.

**Decision needed** before Phase 0.

---

## 2. Contradictions and Ambiguities

| # | Where | Issue | Suggested resolution |
|---|---|---|---|
| A1 | `CLAUDE.md §6` vs `REVIT_TOOLS.md` | `get_family_types` vs `find_family_types` | Use `find_family_types` everywhere. |
| A2 | `ARCHITECTURE.md §6` vs `REVIT_TOOLS.md` | `type_id`/`level_id` vs `typeId`/`levelId` | camelCase everywhere. |
| A3 | `PRODUCT_SPEC.md §7` vs `ROADMAP.md` | MVP lists levels, grids, dimensions, tags, text, views and sheets; the roadmap puts documentation in Phase 4 | Define "MVP = Phases 0–3" or move items. |
| A4 | `PRODUCT_SPEC.md §14` vs `ROADMAP.md` | "check model" is an MVP success criterion but QA is Phase 5 | Same as A3. |
| A5 | `ROADMAP.md` Phase 3 vs Phase 6 | `get_project_standard_types` is needed in Phase 3, but standards are defined in Phase 6, and nothing says where they are stored | See decision D6. |
| A6 | ADR-014 vs `AI_COMMANDS.md §7` | "Workflows are deterministic" vs "the LLM may plan the workflow" | State it plainly: the LLM chooses the workflow and its parameters; the workflow's steps are fixed C#. |
| A7 | `REVIT_TOOLS.md` | Risk is given for only some tools (`create_level` is LARGE, `create_wall` SAFE, most have none) | Each tool gets an explicit risk in the registry, written down in one table. |
| A8 | `PRODUCT_SPEC.md §13` | "The assistant should learn project conventions" vs "deterministic and inspectable" | Conventions are stored, user-editable configuration; the AI may *suggest* them, never silently learn them. |
| A9 | `CLAUDE.md §12`, `ARCHITECTURE.md §14` | "Targeted undo" of an earlier action | Not possible through the API: Revit undo is a stack, and the API cannot undo a specific earlier entry. MVP: name each `TransactionGroup` after the action so it reads clearly in Revit's undo list, and rely on Ctrl+Z. |
| A10 | `PRODUCT_SPEC.md` flows | "Create a wall **here**" — how is "here" captured? `PickPoint` is modal and must run in API context | Decide: the user picks before asking, or a `request_pick_point` tool that the UI triggers. |
| A11 | `REVIT_TOOLS.md §28` | `create_dimension.references` — the LLM cannot produce Revit `Reference` objects | Replace with intent-level tools (`dimension_wall`, `dimension_room`) that compute references in C#. |
| A12 | IDs in schemas | Since Revit 2024, `ElementId` is 64-bit (`ElementId.Value`) | All IDs are `long`/JSON integers; never use `IntegerValue`. |

---

## 3. Revit API Risks to Verify

1. **Target runtime per version (verify):** Revit 2025 and 2026 run on .NET 8; 2024 and earlier on .NET Framework 4.8. Check the runtime of Revit 2027 if you intend to support it. Mixing net48 and net8 doubles build and test effort.
2. **Dockable pane registration (verify):** `RegisterDockablePane` must be called during `OnStartup`, before any document opens.
3. **Selection tracking (verify):** whether a selection-changed event is available in the chosen versions, or whether polling on `Idling` is needed for the context indicator.
4. **Assembly conflicts (verify):** all add-ins share one process. Dependencies such as JSON libraries, the OpenAI SDK and `System.ClientModel` can clash with other add-ins or with Revit's own copies. Check whether the chosen Revit version offers add-in dependency isolation. Keep dependencies minimal: `HttpClient` + `System.Text.Json` may be safer than the OpenAI SDK.
5. **Room creation:** needs a level, a phase and a plan circuit. An unenclosed room is created with area 0 and a warning, not an exception, so post-execution validation must check the area.
6. **Hosted families:** door and window placement uses `NewFamilyInstance` with a host. Whether placement succeeds depends on the family and wall type, and some failures surface only as warnings (see C6).
7. **Wall extension:** changing `LocationCurve` affects wall joins. "No connected elements move" (Flow A) has to be computed, not assumed.
8. **Integration testing:** Revit cannot run headless as a normal test host. Choose between a manual smoke-test checklist and an in-Revit test runner (community options exist; **verify** they are maintained for the chosen version).
9. **API references for building:** reference `RevitAPI.dll`/`RevitAPIUI.dll` from a local install with `Private=false`, or use the community NuGet reference packages. **Verify** the licensing terms of the packages.

---

## 4. Unrealistic for an MVP

- **Flow C (90 m² apartment generation)** appears as a product example but is Phase 8 work. Keep it in the vision and out of MVP acceptance.
- **General collision validation** (`VALIDATION.md §9`): distinguishing valid intersections from invalid conflicts is research-grade. MVP: rely on Revit's own failure messages (C6).
- **Visual preview for every operation** (see C5).
- **Targeted undo** (see A9).
- **`calculate_intersection` with arbitrary geometry**: not definable as a JSON schema. Drop it, or limit it to bounding boxes.
- **"Learning" project conventions** (see A8).
- **Documentation tools in the MVP** (A3). Dimensions especially are hard to automate well.

---

## 5. Missing Decisions

| # | Decision | Recommendation |
|---|---|---|
| D1 | Revit versions | 2025 + 2026 (both .NET 8, single target framework, one build per Revit year). Add 2027 only after verifying its runtime. |
| D2 | Dev/test machine | Windows PC or Windows cloud desktop with licensed Revit. |
| D3 | Units in tool contracts | mm / m² / degrees (see C4). |
| D4 | Write execution model | Plan → preview (dry run) → apply in a `TransactionGroup`, with symbolic references (see C2). |
| D5 | Preview | Data preview through rollback; no graphics in the MVP (see C5). |
| D6 | Where project standards live | A JSON file per company/project that users can open and edit (e.g. `%APPDATA%\RevitAi\standards\*.json`), or Revit Extensible Storage inside the RVT. JSON is simpler and inspectable. |
| D7 | API key model | No backend (ADR-012) means each user brings their own OpenAI key, stored with Windows DPAPI/Credential Manager. A shared company key needs a proxy, which is a backend. |
| D8 | OpenAI client | Raw `HttpClient` + `System.Text.Json` behind `IAiClient` (dependency-conflict risk, §3.4), and use the current function-calling API with strict JSON schemas **(verify)** the endpoint. |
| D9 | Data privacy | Model data (names, parameters, possibly client info) goes to OpenAI. Decide on first-run consent, which data may leave the machine, and the firm's or clients' contractual limits. |
| D10 | Prompt injection | Element names and parameter values (including from linked consultant models) are untrusted text sent to the LLM. Rule: tool results are data; confirmation for risky plans is enforced by the registry no matter what the AI says (ties to C3). |
| D11 | Target discipline | The spec is architecture-first (walls, rooms, apartments) and defers MEP. The project in hand (SCAN Timișoara refrigeration installations) is MEP. Confirm that architecture is the intended first market. |
| D12 | Loop limits | Maximum tool calls per request, per-call timeout, maximum result size sent to the AI, and behaviour when limits are hit. |
| D13 | "Here" input | How locations and pick points are captured (A10). |

---

## 6. Proposed Project Structure

Kept to the minimum that enforces the documented boundary: the core cannot reference Revit, because it is a separate project.

```
revit-ai/
├── CLAUDE.md
├── RevitAi.sln
├── Directory.Build.props          # shared settings, RevitVersion property (2025/2026)
├── docs/
├── src/
│   ├── RevitAi.Core/              # net8.0 — NO Revit, NO WPF
│   │   ├── Tools/                 # ITool contract, ToolResult, RiskLevel, registry
│   │   ├── Ai/                    # IAiClient, OpenAiClient (HttpClient), Orchestrator, Conversation
│   │   ├── Planning/              # PendingPlan, symbolic references (Phase 2)
│   │   ├── Validation/            # ValidationResult, schema validation
│   │   ├── Geometry/              # Revit-free math (distance, area), units (mm/feet)
│   │   └── Context/               # context DTOs sent to the AI
│   └── RevitAi.Addin/             # net8.0-windows, UseWPF — everything that touches Revit
│       ├── App.cs                 # IExternalApplication: ribbon, dockable pane registration
│       ├── RevitAi.addin          # manifest (per Revit year)
│       ├── Dispatch/              # RevitDispatcher (ExternalEvent + TaskCompletionSource)
│       ├── Tools/                 # Revit implementations of ITool (read tools in Phase 1)
│       ├── Context/               # ContextBuilder (active view, level, selection)
│       ├── Transactions/          # transaction runner + failures preprocessor (Phase 2)
│       ├── Infrastructure/        # settings, API key storage, file logging
│       └── UI/                    # WPF pane: views + view models
└── tests/
    └── RevitAi.Core.Tests/        # xUnit; orchestrator with fake IAiClient, schemas, geometry
```

Revit integration testing starts as a written smoke-test checklist (`docs/SMOKE_TESTS.md`) until an in-Revit runner is chosen.

---

## 7. Phase 0 Plan — Foundation (no AI)

1. Pin D1 (versions) and D2 (machine). Record them as an ADR.
2. Create the solution, the two projects and the test project. Set up `Directory.Build.props` with a `RevitVersion` property that picks the API references and output folder per Revit year.
3. Add `IExternalApplication`: a ribbon button that shows and hides the pane, with the dockable pane registered in `OnStartup`.
4. Build a WPF pane with a chat box that echoes input (no AI), and a context indicator for active view, level and selection count.
5. Build `RevitDispatcher`: a single `ExternalEvent`, a request queue, `TaskCompletionSource`, a timeout and cancellation. Prove it with a read-only round trip (pane → dispatcher → active view name → pane).
6. Infrastructure: a settings file in `%APPDATA%\RevitAi\`, API key storage (DPAPI), and a small file logger in `%LOCALAPPDATA%\RevitAi\logs`. No logging framework unless it is needed.
7. Post-build step that copies the add-in and manifest to `%APPDATA%\Autodesk\Revit\Addins\<year>\` for debugging.
8. xUnit project wired up with one trivial test; write `SMOKE_TESTS.md`.

**Exit criteria:** the pane loads in every supported Revit version, shows live context, and the dispatcher round trip works without blocking the Revit UI. No AI code yet.

---

## 8. Phase 1 Plan — AI Foundation (read-only)

1. **Tool contract** in Core: `ITool { Name, Description, InputSchema, Risk, ExecuteAsync(args) }`. In Phase 1 the registry **refuses to register any tool that isn't `READ_ONLY`**.
2. **Units helper** (D3): mm ↔ feet conversion at the Revit boundary, with unit tests.
3. **Seven read tools** in Addin, each running through `RevitDispatcher`: `get_project_info`, `get_active_view`, `get_active_level`, `get_selected_elements`, `get_element`, `find_elements`, `get_element_parameters`. Output DTOs live in Core. Every list has a limit.
4. **`IAiClient` + `OpenAiClient`** over `HttpClient`: function calling with strict schemas; the model name comes from settings.
5. **Orchestrator** in Core:
   - validates tool arguments against the schema before dispatch
   - handles unknown tools
   - enforces a maximum number of iterations, timeouts and cancellation
   - truncates large tool results
   - gives the AI structured errors
6. **Context builder**: a small snapshot sent with each turn (project, view, level, selection summary capped at N elements).
7. **Conversation state**: kept in memory per document and reset when the document changes.
8. **UI**: progress labels driven by tool calls ("Reading selection…"), and a clear "read only — nothing was changed" indicator.
9. **Privacy notice** on first run (D9).
10. **Tests:** the orchestrator runs against a scripted fake `IAiClient` (tool call → result → final answer). Add tests for schema validation, unknown tools and the iteration limit. No live AI calls in CI.

**Exit criteria:** on a sample project, "What did I select?", "What level am I on?" and "What type of wall is this?" are answered correctly, and no code path can modify the model.

---

## 9. Recommended Changes to the Docs

1. Add ADRs for C1 (dispatcher), C2/D4 (plan-then-apply), C3 (registry-owned risk), C4/D3 (units), C5/D5 (preview), C6 (failure handling).
2. Fix the naming inconsistencies A1 and A2.
3. Give every tool in `REVIT_TOOLS.md` an explicit risk and units in its descriptions.
4. Replace `create_dimension.references` and `calculate_intersection` with intent-level tools (A11, §4).
5. Redefine "MVP" as Phases 0–3 and move documentation and QA out of the MVP success criteria (A3, A4).
6. Reword "targeted undo" and "learn conventions" (A8, A9).
7. Add a security note about untrusted model text (D10).

Applied on 2026-10-07 (items 1–7). Decisions are recorded as ADR-021 to ADR-031.

---

## 10. Decisions Required Before Coding

In priority order:

1. **D1** Revit versions
2. **D2** Windows dev/test machine
3. **D4** plan-then-apply execution model (shapes the Phase 1 tool contract even though writes come in Phase 2)
4. **D3** units convention
5. **D7** API key model
6. **D11** target discipline (architecture vs. MEP)
7. **D9** data privacy stance

The rest (D5, D6, D8, D10, D12, D13) have recommended defaults above and can be accepted as written.
