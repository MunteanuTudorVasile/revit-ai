# Revit AI Assistant — UX Specification

## 1. Design Goal

The interface should feel like an expert BIM engineer working inside Revit (ADR-040), not an external chatbot.

The user should understand:

- what the AI understood
- what it plans to do
- what has actually happened
- what requires approval
- what failed

---

## 2. Main UI

Use a Revit dockable WPF panel.

Concept:

```
┌──────────────────────────────┐
│ ✦ Revit AI                   │
├──────────────────────────────┤
│                              │
│ What would you like to do?   │
│                              │
│ ┌──────────────────────────┐ │
│ │ Ask anything...          │ │
│ └──────────────────────────┘ │
│                              │
│ Model   Document   Check     │
│ Analyze Standards Automate   │
│                              │
│ Recent                       │
│ • Modified wall              │
│ • Created room               │
└──────────────────────────────┘
```

---

## 3. Context Indicator

The UI should show useful context.

Example:

```
Context:
Level 1
Floor Plan
1 selected element
```

This reassures the user that the assistant understands the current Revit context.

---

## 4. Chat

Conversation example:

User: "Make this wall 50cm longer."

AI:

> I found Wall 18342.
>
> Current length: 4.20m
> New length: 4.70m
>
> I will extend the selected end.
>
> [Preview] [Apply]

---

## 5. Preview Card

For simple operations:

```
┌──────────────────────────────┐
│ Proposed change              │
│                              │
│ Wall 18342                   │
│ 4.20m → 4.70m                │
│                              │
│ No connected elements move   │
│                              │
│ [Cancel] [Apply]             │
└──────────────────────────────┘
```

---

## 6. Large Operation Preview

Example: Create apartment

```
┌──────────────────────────────┐
│ Apartment proposal           │
│                              │
│ Rooms                        │
│ Living       24.3m²          │
│ Kitchen       9.2m²          │
│ Bedroom 1    12.8m²          │
│ Bedroom 2    11.4m²          │
│ Bedroom 3    10.7m²          │
│ Bathroom      5.1m²          │
│                              │
│ Elements                     │
│ 12 walls                     │
│ 4 doors                      │
│ 6 windows                    │
│                              │
│ Standards                    │
│ ✓ Wall type                  │
│ ✓ Door type                  │
│ ✓ Window type                │
│                              │
│ [Adjust] [Preview] [Apply]   │
└──────────────────────────────┘
```

---

## 7. Risk Levels

### Level 1

Read-only. No confirmation.

Examples:

- inspect model
- calculate area
- count elements

### Level 2

Small modification.

Usually: preview optional, Apply required.

### Level 3

Large modification. Mandatory preview.

Examples:

- apartment generation
- multiple walls
- sheet set creation

### Level 4

Destructive. Mandatory explicit confirmation.

Examples:

- delete multiple elements
- delete views
- bulk changes

---

## 8. Quick Actions

Quick actions follow the conceptual modes (PRODUCT_SPEC §6). They are shortcuts into the same assistant, not separate screens:

- Ask
- Analyze
- Fix
- Create
- Automate
- Standards

### Issue list (ADR-041)

Results of a model check are shown as an issue list:

- grouped by severity (errors first), with counts per category
- each issue: plain-language description, affected elements (click to select and zoom), suggested fix
- "Fix" for one issue, and "Fix all safe issues" for issues marked auto-fixable; both create a plan that is previewed and applied as usual
- after Apply, each fixed issue shows "fixed (verified)" or "still open" from the re-check

---

## 9. Selection Awareness

When the user selects an element, the UI should update context.

Example:

```
Selected:
Wall
Basic Wall
Generic 200mm
Level 1
```

Then the user can simply type: "Make it 30cm longer."

---

## 10. Why?

Every significant AI decision should optionally expose "Why?"

Example — Why this door?

"The project contains three door families. I selected Single-Flush 900mm because it is marked as the preferred internal door."

---

## 11. Action History

Example:

```
Today

20:32  Modified wall
20:35  Created bedroom
20:41  Added dimensions
```

Clicking an action shows:

- user request
- operations
- affected elements
- result
- validation
- undo option where available

---

## 12. Errors

Errors should be written for users.

Bad:

```
InvalidOperationException:
Autodesk.Revit.Exceptions.ArgumentException...
```

Good: "I couldn't place the door because the selected wall does not support this door family."

A technical details option may expose the underlying exception.

---

## 13. Empty State

Initial screen:

"Ask me to work with your Revit model."

Examples:

- "Create a room here."
- "How many bedrooms are on this floor?"
- "Make this wall 30cm longer."
- "Check this floor for missing room tags."

---

## 14. Loading States

AI: "Thinking..."

Tool execution:

- "Checking project wall types..."
- "Creating walls..."
- "Validating room boundaries..."

Avoid generic infinite spinners.

---

## 15. Conversation Persistence

The conversation remains available while the user works.

Context changes should be visible.

Example: "Your selection changed from Wall 18342 to Wall 18345."

---

## 16. User Control

The user should always be able to:

- cancel
- preview
- apply
- see affected elements
- inspect reasoning/explanation
- undo where supported

The AI should never silently make large destructive changes.
