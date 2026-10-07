# Revit AI Assistant — Product Specification

## 1. Product Vision

Create an AI assistant inside Autodesk Revit that allows users to work with BIM models using natural language.

The user should be able to describe an intended outcome instead of manually performing every Revit operation.

Example:

"Make this bedroom 2 meters wider."

Instead of manually:

- selecting walls
- checking dimensions
- moving walls
- checking connected elements
- correcting doors
- checking room boundaries

the assistant should understand the requested outcome and safely execute the required Revit operations.

---

## 2. Target Users

Primary users:

- architects
- architectural technicians
- BIM modelers
- BIM coordinators
- interior designers using Revit
- structural designers
- MEP designers
- Revit users without programming knowledge

The user is assumed to understand Revit concepts but not APIs or programming.

---

## 3. User Problem

Revit is powerful but many tasks require repetitive operations.

Users frequently need to:

- find elements
- inspect parameters
- create repetitive geometry
- modify multiple elements
- create documentation
- check standards
- perform repetitive model cleanup
- create views
- create sheets
- create schedules
- verify model consistency

The assistant should reduce repetitive manual work.

---

## 4. Core Product Principle

The user describes WHAT they want.

The system determines HOW to achieve it.

Example:

User: "Create a bathroom next to the bedroom."

The user should not have to say: "Create four walls, then create a room, then place a door, then..."

The AI should determine the required sequence.

---

## 5. Core Interaction

The fundamental interaction is:

Select → Ask → Understand → Plan → Preview → Apply → Validate

---

## 6. Product Modes

### Assistant

Read-only questions.

Examples:

- "What is the area of this room?"
- "How many doors are on this floor?"
- "What wall type is this?"
- "Why is this room not calculating?"

### Model

Create and modify BIM elements.

Examples:

- create wall
- modify wall
- create room
- place door
- create floor

### Document

Documentation workflows.

Examples:

- add dimensions
- tag rooms
- create sheets
- create schedules
- apply view templates

### Check

Model analysis and QA.

Examples:

- find rooms without tags
- find doors without hosts
- find duplicated elements
- find non-standard families

### Standards

Company/project standard checking.

Example: "Check this floor against our BIM standards."

### Automate

Multi-step workflows.

Example: "Prepare this floor plan for documentation."

---

## 7. Initial MVP

MVP = roadmap Phases 0–3 (ADR-031). The MVP should support:

### Context

- current project
- current view
- current level
- selected elements
- project units

### Elements

- walls
- rooms
- doors
- windows
- floors

Levels, grids and documentation (dimensions, tags, text, views, sheets) follow in Phase 4.

### AI capabilities

- natural language commands
- tool selection
- contextual reasoning
- preview
- apply
- validation
- action history

---

## 8. Example User Flows

### Flow A — Modify a wall

User selects wall.

User: "Make this wall 50cm longer."

AI:

> Wall 18342 is currently 4.20m long.
>
> I will extend it to 4.70m from the selected end.
>
> No connected elements need to move.
>
> [Preview] [Apply]

After Apply:

> Done. Wall 18342 is now 4.70m long.

---

## 9. Flow B — Create a room

User: "Create a 15m² bedroom here."

AI:

> Based on the selected area, I can create a bedroom approximately 15m².
>
> I'll use:
>
> - Wall type: Internal 100mm
> - Door: Single Door 900mm
> - Room name: Bedroom
>
> Proposed area: 15.2m²
>
> [Preview] [Adjust] [Apply]

---

## 10. Flow C — Apartment Generation

_Long-term flow (Phase 8). Not part of the MVP._

User: "Create a 90m² apartment with 3 bedrooms, one bathroom, kitchen and living room. Use the project's standard walls, doors and windows. Keep bedrooms between 10 and 14m²."

AI should:

1. inspect selected boundary
2. inspect project units
3. inspect standard wall types
4. inspect door families
5. inspect window families
6. calculate usable area
7. generate a layout proposal
8. validate room areas
9. show preview
10. wait for approval
11. create elements
12. validate resulting model
13. summarize changes

Example result:

| Room | Area |
|---|---:|
| Living room | 24.3m² |
| Kitchen | 9.2m² |
| Bedroom 1 | 12.8m² |
| Bedroom 2 | 11.4m² |
| Bedroom 3 | 10.7m² |
| Bathroom | 5.1m² |
| Circulation | 10.2m² |

---

## 11. Context Awareness

The assistant should understand:

- current project
- current view
- current level
- current selection
- relevant visible elements
- nearby elements
- units
- relevant families/types
- recent actions
- current conversation

The user should not need to repeat information already established.

Example:

User: "Create a bedroom."

Assistant creates it.

User: "Make it 2m wider."

The assistant knows which bedroom is being discussed.

---

## 12. Persistent Conversation

Conversation context should support follow-up requests.

Example:

User: "Create a bedroom."

AI: "Created Bedroom 1."

User: "Move the window to the center."

AI understands:

- Bedroom 1
- its wall
- existing window
- relevant wall geometry

---

## 13. Project Knowledge

The assistant should use project-level conventions. Conventions are stored as configuration the user can inspect and edit; the AI may suggest them but never adopts them silently.

Examples:

- standard interior wall
- standard exterior wall
- standard door
- standard window
- standard room naming
- standard dimensions
- standard view template
- standard sheet naming
- units

Project knowledge should be deterministic and inspectable.

---

## 14. Success Criteria

The MVP is successful when a non-technical Revit user can perform common tasks without knowing the Revit API.

Examples:

- create wall
- modify wall
- create room
- place door
- place window
- create floor

with natural language.

Dimensions and sheets (Phase 4) and model checking (Phase 5) are success criteria for later phases, not the MVP.

---

## 15. Product Quality Requirements

The product must prioritize:

1. correctness
2. safety
3. predictability
4. user trust
5. transparency
6. ease of use
7. speed

AI creativity is less important than reliable BIM operations.

---

## 16. Out of Scope for MVP

Do not implement initially:

- full autonomous architectural design
- PDF to BIM
- image to BIM
- custom LLM training
- automatic destructive model cleanup
- complete MEP automation
- full structural engineering automation
- autonomous construction documentation
- multi-agent architecture

---

## 17. Long-Term Vision

The eventual assistant should understand high-level requests such as:

- "Prepare this apartment for construction documentation."
- "Check this project against our company BIM standards and fix everything you can."
- "Create three alternative layouts for this floor."
- "Find all rooms under 10m²."
- "Move the kitchen wall by 40cm and update affected doors, windows and dimensions."
- "Create a sheet set for this floor."

The user should communicate outcomes rather than implementation details.
