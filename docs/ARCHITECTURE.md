# Revit AI Assistant — Architecture

## 1. High-Level Architecture

The system consists of six major layers:

1. Revit UI
2. AI orchestration
3. Context engine
4. Tool/command layer
5. Validation
6. Revit execution

Architecture:

```
Revit
  │
  ├── WPF Dockable Panel
  │
  ├── Context Engine
  │
  ├── AI Orchestrator
  │       │
  │       └── OpenAI API
  │
  ├── Tool Registry
  │
  ├── Command Planner
  │
  ├── Validator
  │
  ├── Transaction Manager
  │
  └── Revit API
```

---

## 1a. Revit Dispatcher

All Revit API access — reads included — goes through `RevitDispatcher` (ADR-023): a single `ExternalEvent` that drains a queue of requests inside Revit's API context. Callers on any thread `await` the result. A request either never runs (timed out or cancelled while waiting) or runs to completion.

Revit events (`ViewActivated`, `SelectionChanged`, …) already run in API context and may read the model directly.

---

## 2. WPF UI

Responsibilities:

- chat
- quick actions
- preview
- confirmation
- errors
- history
- action details

The UI must not contain business logic.

---

## 3. Context Engine

The context engine collects relevant Revit information.

Potential context:

```json
{
  "project": {
    "name": "Example Project",
    "units": "metric",
    "revitVersion": "..."
  },
  "view": {
    "id": 100,
    "name": "Level 1 Floor Plan",
    "type": "FloorPlan"
  },
  "level": {
    "id": 200,
    "name": "Level 1",
    "elevation": 0
  },
  "selection": [],
  "recentActions": []
}
```

Context must be task-scoped.

Do not serialize the complete Revit model.

---

## 4. AI Orchestrator

Responsibilities:

- send user request
- build AI context
- provide relevant tools
- receive structured tool calls
- execute planning loop
- interpret tool results
- generate user-facing response

The orchestrator should not directly manipulate Revit.

---

## 5. Tool Registry

The registry contains approved operations.

Each tool contains:

- name
- description
- schema
- handler
- validation rules
- risk level

Example:

```
create_wall
    ↓
schema validation
    ↓
business validation
    ↓
Revit handler
```

---

## 6. Command Layer

The command layer converts high-level tool requests into deterministic Revit API operations.

Example AI call:

```
create_wall({
  start: ...,
  end: ...,
  typeId: 123,
  levelId: 456
})
```

Command layer:

1. validate type
2. validate level
3. validate geometry
4. create wall
5. return element ID

---

## 7. Domain Layer

Contains Revit-independent concepts where practical.

Examples:

- geometry calculations
- layout planning
- room area constraints
- command models
- validation models
- risk classification

---

## 8. Revit Integration Layer

All Revit-specific API access belongs here.

Examples:

- element lookup
- wall creation
- room creation
- family lookup
- parameter access
- view creation
- transactions

---

## 9. Validation Architecture

Validation occurs before execution whenever possible.

Pipeline:

```
AI Proposal
  ↓
Schema Validation
  ↓
Command Validation
  ↓
Geometry Validation
  ↓
BIM Validation
  ↓
Project Standard Validation
  ↓
Preview
  ↓
Transaction
  ↓
Post-execution Validation
```

---

## 10. Transaction Architecture

Small operation:

```
Transaction
  → execute
  → commit
```

Multi-step operation:

```
TransactionGroup
  → operation 1
  → operation 2
  → operation 3
  → validation
  → assimilate/commit
```

On critical failure: rollback.

The system must maintain an AI Action ID for every modifying request.

---

## 11. Preview Architecture

Preview should not require permanent model changes.

Possible implementation:

- temporary graphics
- transient geometry
- temporary elements where appropriate
- calculated preview data
- temporary transaction rollback

The implementation should be selected based on Revit API capabilities for the specific operation.

MVP decision (ADR-024): data preview only. The plan runs in a transaction that is rolled back, and the preview reports counts, measurements and Revit warnings. Visual preview is deferred.

---

## 12. Risk Engine

Every tool has a risk classification:

- `READ_ONLY`
- `SAFE_MODIFICATION`
- `LARGE_MODIFICATION`
- `DESTRUCTIVE`

Risk influences UI confirmation.

Risk is owned by the tool registry, never by the AI (ADR-025). A plan's risk is the highest risk among its operations, escalated by size thresholds.

---

## 13. History

Each action should record:

```json
{
  "actionId": "...",
  "timestamp": "...",
  "userRequest": "...",
  "operations": [],
  "affectedElementIds": [],
  "validation": {},
  "execution": {},
  "status": "completed"
}
```

---

## 14. Undo

Initial implementation may rely on Revit's transaction undo mechanism.

AI-specific history should still map actions to transaction boundaries.

The Revit API cannot undo a specific earlier entry; undo is a stack. Each AI action is therefore applied as one `TransactionGroup` named after the action, so it appears as a single, recognisable entry in Revit's undo list (ADR-024). Targeted undo of older actions is not planned.

---

## 15. AI Provider

The initial provider is OpenAI.

The AI layer should be abstract enough that provider-specific logic does not leak into the domain or Revit layers.

Example conceptual abstraction: `IAiClient`

Possible implementations:

- `OpenAiClient`
- Future: `OtherAiProviderClient`

---

## 16. Cloud Backend

Not required for MVP.

Future backend responsibilities:

- authentication
- organization management
- subscriptions
- usage limits
- centralized project standards
- project memory
- analytics

The Revit core should remain functional without requiring a complex backend architecture.

---

## 17. Security Boundaries

The security boundary is:

```
AI
  ↓
Approved tools
  ↓
Validation
  ↓
Revit API
```

Never:

```
AI
  ↓
Arbitrary C#
  ↓
Operating system
```

---

## 18. Performance

Avoid unnecessary calls to the AI.

Use deterministic local operations for:

- measurements
- distance calculations
- area calculations
- element lookup
- validation

Use AI for:

- language
- intent
- planning
- ambiguity resolution
- complex reasoning

---

## 19. Agent Loop and BIM QA (ADR-040, ADR-041)

The "AI BIM engineer" direction adds **no new layers or infrastructure**. The agent is an orchestration capability of the
existing components:

| Agent step | Existing component |
|---|---|
| Understand | Context engine, read tools |
| Inspect | Check tools (issue detectors) |
| Reason, plan | AI orchestrator |
| Validate plan | Schema validation, each write tool's `Validate`, plan references, risk engine |
| Preview | `PlanExecutor` run + rollback |
| Confirm when required | Risk engine (registry-owned) + UI (ADR-025, ADR-037) |
| Execute | `PlanExecutor` in a `TransactionGroup` |
| Validate result | Per-step post-execution checks, Revit failure handling, **re-running the relevant detectors** |
| Report | UI messages, action history |

### Issue model (Domain layer, Revit-free)

Check results converge on one **Issue** shape (conceptual; ADR-041):

| Field | Meaning |
|---|---|
| issue ID | Stable within a check run (e.g. detector + element IDs) |
| category | e.g. unhosted door, room without tag, invalid room boundary, non-standard type, missing parameter, duplicate, naming violation, documentation inconsistency, Revit warning |
| severity | error / warning / info, set by the detector |
| description | Plain-language explanation |
| affected element IDs | Elements involved |
| location / view | Level and/or view where relevant |
| detected by | Detector (check tool) name |
| suggested fix | The fix tool and arguments, when one exists |
| auto-fixable | Set by a **deterministic fix rule**, never by the AI |
| validation status | open → fix planned → fixed (verified by re-check) / still open |

Detectors are the existing check tools plus Revit's own warnings (`Document.GetWarnings`, e.g. rooms not enclosed or
overlapping walls); they live in the Revit integration layer and map their findings to Issues. Fix rules (issue category →
fix tool) live in the Domain layer and only emit operations for the existing plan. A fix is "verified" only when the
detector no longer reports the issue after Apply.

---

## 20. Scalability

The initial architecture should support adding new tools without rewriting the AI layer.

Adding `create_beam` should not require changes to:

- chat UI
- AI provider
- context engine
- transaction infrastructure

Only the tool registration, handler and validation should be required.
