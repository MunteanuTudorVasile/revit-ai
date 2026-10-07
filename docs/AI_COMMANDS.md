# Revit AI Assistant — AI Command Protocol

## 1. Purpose

The AI Command Protocol defines how the LLM interacts with the Revit tool layer.

The LLM does not directly control Revit.

It produces structured tool calls.

---

## 2. AI Loop

The general loop is:

```
User Request
  ↓
Context Construction
  ↓
AI
  ↓
Tool Call
  ↓
Tool Execution
  ↓
Tool Result
  ↓
AI
  ↓
Additional Tool Calls if required
  ↓
Final Response
```

---

## 3. Example

User: "How many bedrooms are on this floor?"

AI: `find_elements(category="Rooms")`

Then analyzes the returned rooms.

Final: "There are 3 bedrooms on Level 1."

No model modification occurs.

---

## 4. Modification Example

User: "Make this wall 50cm longer."

AI:

1. `get_selected_elements`
2. `get_element`
3. `get_element_location`
4. calculate geometry
5. `modify_wall` — validated and **queued** in the pending plan, not executed

The user then previews and applies the plan (ADR-024).

---

## 5. Command Object

Conceptual structure. `riskLevel` and `requiresConfirmation` are computed by the tool registry, not produced by the AI (ADR-025):

```json
{
  "actionId": "unique-id",
  "intent": "modify_wall",
  "riskLevel": "SAFE_MODIFICATION",
  "requiresConfirmation": true,
  "operations": [
    {
      "tool": "modify_wall",
      "arguments": {}
    }
  ]
}
```

---

## 6. Multi-Step Commands

Example: "Create a bedroom with a door."

AI may produce:

```json
{
  "intent": "create_bedroom",
  "operations": [
    { "tool": "create_wall" },
    { "tool": "create_room" },
    { "tool": "create_door" }
  ]
}
```

The AI may only call tools that exist in the tool registry.

Write tools are not executed during the AI loop. Each call is validated and appended to the pending plan, and the AI receives a symbolic reference to its future result, e.g. `"hostWallId": "$op1.elementId"`. References are resolved in order when the plan is applied (ADR-024).

---

## 7. High-Level Skills

High-level workflows are implemented as deterministic orchestration where possible.

Example: `create_room_workflow` may consist of:

```
get boundary
  → choose wall type
  → create walls
  → create room
  → validate room
  → place door
  → validate door
```

The LLM may plan the workflow, but actual execution remains deterministic.

---

## 8. Missing Information

The AI should ask only when necessary.

Bad: "Which wall type should I use?" when the project clearly has a designated standard wall type.

Good:

> I found two project-standard interior wall types:
>
> - Interior Standard 100mm
> - Interior Acoustic 150mm
>
> Which should I use?

---

## 9. Ambiguity

If ambiguity materially changes the result, ask.

Example: "Move the wall."

AI: "Which wall would you like to move? I currently see 4 selected/nearby walls."

If one wall is explicitly selected, do not ask unnecessarily.

---

## 10. Tool Selection

The AI should only receive tools relevant to the task where possible.

For "What is the room area?", provide room/context/geometry tools.

Do not provide 50 modification tools unnecessarily.

---

## 11. Structured Outputs

AI outputs must follow defined schemas.

Free-form text must never be parsed as executable commands.

---

## 12. Confirmation

The tool registry computes `requiresConfirmation` from the plan's risk (ADR-025). The AI does not decide it, and any value it suggests is ignored.

Examples:

| Request | requiresConfirmation |
|---|---|
| Read | false |
| Modify one wall | true, depending on UX settings |
| Create apartment | true |
| Delete all unused doors | true |

---

## 13. Preview

For complex operations the AI should produce a preview plan.

Example:

```json
{
  "preview": {
    "created": [
      "5 walls",
      "4 rooms",
      "4 doors",
      "6 windows"
    ],
    "modified": [],
    "deleted": []
  }
}
```

---

## 14. Explainability

The system should be able to answer: "Why did you use this wall type?"

Example: "I used Interior Standard 100mm because it is marked as the project's preferred interior wall type."

---

## 15. No Hallucinated IDs

The AI must never invent:

- element IDs
- family IDs
- type IDs
- level IDs
- view IDs

IDs must come from tool results.

---

## 16. No Hallucinated Geometry

The AI may reason about geometry but must use deterministic geometry tools for calculations.

Example: do not estimate area from visual reasoning if exact geometry is available.

---

## 17. Final Response

After successful execution:

- state what happened
- summarize affected elements
- mention important warnings
- avoid unnecessary technical details

Example:

> Done.
>
> I created:
>
> - 4 walls
> - 1 room
> - 1 door
>
> Room area: 14.8m².
> The project-standard interior wall and door types were used.

---

## 18. Partial Failure

If only part of an operation succeeds, never say "Done."

Instead:

> The room and walls were created successfully, but the door could not be placed because no compatible door family was found. Nothing else was changed.

If transaction rollback occurred:

> The operation was cancelled because the door could not be placed. No changes were applied.

---

## 19. Conversation State

The AI should maintain logical references.

Example:

User: "Create a bedroom."

AI: "Created Bedroom 1."

User: "Make it larger."

The system resolves "it" to Bedroom 1.

---

## 20. Command Safety

Commands must be validated before execution.

The AI cannot bypass validation even if the user explicitly asks.
