# Revit AI Assistant — Roadmap

MVP = Phases 0–3 (ADR-031).

## Phase 0 — Product Foundation

Status: implemented; awaiting the smoke tests in `SMOKE_TESTS.md` on Revit 2025 and 2026.

Goal: establish architecture and development environment.

Tasks:

- select supported Revit version(s)
- create Revit add-in
- create WPF dockable panel
- create solution structure
- establish Revit API integration
- establish logging
- establish configuration
- establish testing structure

No advanced AI functionality yet.

---

## Phase 1 — AI Foundation

Status: implemented; awaiting the Phase 1 smoke tests in `SMOKE_TESTS.md`.

Goal: user can chat with an AI that understands the current Revit context.

Implement:

- OpenAI integration
- AI client abstraction
- context builder
- current project information
- current view
- current level
- current selection
- basic conversation state
- tool registry
- structured tool calling

Initial tools:

- `get_project_info`
- `get_active_view`
- `get_active_level`
- `get_selected_elements`
- `get_element`
- `find_elements`
- `get_element_parameters`

Success — user can ask:

- "What did I select?"
- "What level am I on?"
- "What type of wall is this?"

---

## Phase 2 — Basic Modeling

Status: implemented; awaiting the Phase 2 smoke tests in `SMOKE_TESTS.md`.

Goal: safely modify simple BIM elements.

Implement:

- `create_wall`
- `modify_wall`
- `create_room`
- `create_door`
- `create_window`
- `create_floor`

Add:

- validation
- transactions
- confirmation
- basic preview
- action history

Success — user can say:

- "Create a wall here."
- "Make this wall 30cm longer."
- "Create a bedroom here."

---

## Phase 3 — Context Intelligence

Status: implemented; awaiting the Phase 3 smoke tests in `SMOKE_TESTS.md`. Not included: `find_families` (covered by `find_family_types`) and visible-element context.

Goal: assistant understands more of the model.

Implement:

- nearby elements
- relevant visible elements
- family/type search
- project standard types
- context compression
- contextual follow-up commands

Success — user can say "Create a door here." without manually specifying the family if the project standard is obvious.

---

## Phase 4 — Documentation

Status: implemented (including sections, elevations, 3D views and room dimensions); awaiting the Phase 4 smoke tests in `SMOKE_TESTS.md`.

Implement:

- dimensions
- tags
- text
- views
- sheets
- schedules

Examples:

- "Dimension this room."
- "Create a door schedule."
- "Create a Level 1 documentation sheet."

---

## Phase 5 — Model QA

Status: implemented (checks are read-only; fixes reuse existing write tools; deleting duplicates is deferred until the destructive-action confirmation flow exists); awaiting the Phase 5 smoke tests.

Implement:

- rooms without tags
- unhosted doors
- unhosted windows
- duplicate elements
- non-standard families
- missing parameters

Example: "Check this floor." → "12 issues found."

---

## Phase 6 — Project Standards

Implement:

- standard families
- standard wall types
- standard room names
- view templates
- sheet naming
- company rules

Example: "Check this project against company standards."

---

## Phase 7 — Workflow Automation

Implement high-level workflows:

- Create Room
- Create Apartment
- Prepare Floor Plan
- Prepare Documentation
- Create Sheet Set
- QA Floor

These should combine deterministic tools and validation.

---

## Phase 8 — Advanced Layout Intelligence

Goal: allow users to describe design intent.

Example: "Create a 90m² apartment with 3 bedrooms."

System:

- analyzes available boundary
- proposes layout
- respects area constraints
- uses project standards
- previews result
- allows adjustments
- applies layout

---

## Phase 9 — Advanced Inputs

Potential:

- sketch → BIM
- image → BIM
- PDF → BIM
- architectural brief → BIM

Not part of MVP.

---

## Phase 10 — Cloud Platform

Potential:

- accounts
- organizations
- subscriptions
- usage
- cloud project memory
- centralized standards
- analytics
- team collaboration

Only implement when product requirements justify it.

---

## Explicitly Deferred

Do not implement early:

- custom LLM training
- autonomous agents with unrestricted access
- arbitrary C# generation
- unrestricted file system access
- automatic destructive cleanup
- microservices
- Kubernetes
- complex distributed architecture
- full MEP automation
- full structural automation
