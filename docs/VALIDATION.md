# Revit AI Assistant — Validation Specification

## 1. Purpose

AI-generated commands cannot be trusted solely because they are syntactically valid.

Validation is an independent safety layer.

---

## 2. Validation Pipeline

```
AI proposal
  ↓
JSON schema validation
  ↓
Tool validation
  ↓
Geometry validation
  ↓
BIM validation
  ↓
Project standards validation
  ↓
Preview
  ↓
Execution
  ↓
Post-execution validation
```

---

## 3. Schema Validation

Verify:

- required fields
- field types
- allowed enum values
- valid IDs
- valid object structures

Example: `create_wall` requires `start`, `end`, `typeId`, `levelId`.

---

## 4. Geometry Validation

### Walls

Check:

- start != end
- valid length
- valid coordinates
- valid height
- no invalid geometry

### Rooms

Check:

- boundary is closed where required
- boundary is valid
- room placement point is valid

### Floors

Check:

- boundary valid
- boundary closed
- no invalid self-intersections

---

## 5. BIM Validation

Check:

- element exists
- type exists
- level exists
- family exists
- host exists
- host category is compatible
- parameters exist
- parameters have compatible storage types

---

## 6. Family Validation

Before placing family instances:

1. search family
2. verify type
3. verify category
4. verify active/usable state
5. verify host compatibility

Never invent a family/type.

---

## 7. Parameter Validation

Before setting a parameter, check:

- parameter exists
- parameter is writable
- expected storage type
- value is within acceptable range

---

## 8. Project Standards Validation

Possible rules:

- approved wall types
- approved door families
- approved window families
- naming conventions
- room naming
- view templates
- sheet numbering
- required parameters

---

## 9. Collision Validation

Where relevant:

- wall intersections
- door placement conflicts
- window placement conflicts
- room overlap
- element overlap

Not every overlap is invalid.

The validator must distinguish `VALID INTERSECTION` from `INVALID CONFLICT`.

MVP: no custom collision engine. Conflicts are detected from Revit's own failure messages, collected by the failures preprocessor (ADR-027).

---

## 10. Pre-Execution Validation

Prefer catching errors before transaction execution.

Example — `create_door`, before execution:

- host wall exists
- door family exists
- host supports family
- location is valid
- placement is possible

---

## 11. Post-Execution Validation

After execution, verify the expected result.

Example — `create_room`, check:

- room exists
- room has expected name
- room has valid area
- room is on expected level

### Fix verification (ADR-041)

When a plan fixes detected issues, post-execution validation also **re-runs the detectors** that reported them:

- issue no longer reported → `fixed (verified)`
- issue still reported → `still open`, reported to the user, never claimed as fixed
- new issues of the same category on the affected elements → reported as introduced by the change

Re-checking is read-only and runs after the transaction group has been committed.

---

## 12. Validation Result

Conceptual structure:

```json
{
  "valid": false,
  "errors": [
    {
      "code": "INVALID_HOST",
      "message": "The selected wall cannot host this door family."
    }
  ],
  "warnings": []
}
```

---

## 13. Error Severity

- `ERROR` — operation cannot continue.
- `WARNING` — operation can continue but user should know.
- `INFO` — informational result.

---

## 14. Transaction Safety

If a critical validation failure occurs during a multi-step operation, rollback the operation where possible.

Never leave the model partially modified without explicitly informing the user.

---

## 15. AI Independence

Validation must not ask the AI "Does this look valid?"

The validator must determine validity programmatically.

The AI may explain the validator's result.

---

## 16. Standards Checking

Standards checking should be deterministic.

Example rule: all internal doors must use the approved family list.

Validator:

- inspect doors
- compare family/type
- return violations

AI: explain results and optionally propose fixes.

---

## 17. Future Validation

Issue detection is now first-class (ADR-041); detectors map to the Issue model. Planned detectors beyond the existing checks:

- invalid room boundaries: placed rooms with no area, and Revit's "not enclosed" / "redundant room" warnings
- documentation inconsistencies: views not on sheets, sheets without views, untagged elements in views on sheets
- geometry/model inconsistencies reported by Revit warnings (e.g. overlapping walls)

Further potential checks:

- room area requirements
- accessibility requirements
- fire-rating parameters
- structural constraints
- MEP clearances
- company-specific BIM rules
- naming compliance
- parameter completeness
