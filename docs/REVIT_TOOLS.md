# Revit AI Assistant — Tool Registry

This document defines the initial tool contract.

The AI may only interact with Revit through registered tools.

Tool names are stable API contracts.

**Units (ADR-026):** all lengths and coordinates are in millimetres, areas in m², angles in degrees, in both inputs and outputs, regardless of project display units. IDs are 64-bit integers (`ElementId.Value`).

**Risk (ADR-025):** fixed per tool in the registry; see the table below. Write tools are queued into a plan, not executed immediately (ADR-024).

| Risk | Tools |
|---|---|
| `READ_ONLY` | all Context, Elements, Families and Types, Geometry and Analysis tools |
| `SAFE_MODIFICATION` | `create_wall`, `modify_wall`, `create_floor`, `modify_floor`, `create_room`, `modify_room`, `create_door`, `create_window`, `create_text`, `create_tag`, `dimension_wall`, `dimension_room` |
| `LARGE_MODIFICATION` | `create_level`, `create_grid`, `create_view`, `create_sheet`, `create_schedule` |
| `DESTRUCTIVE` | `delete_element` |

A plan containing many `SAFE_MODIFICATION` operations is escalated to `LARGE_MODIFICATION` by size thresholds.

---

## 1. Tool Categories

### Context

- `get_project_info`
- `get_active_view`
- `get_active_level`
- `get_selected_elements`

### Elements

- `find_elements`
- `get_element`
- `get_element_parameters`
- `get_element_location`
- `get_element_bounding_box`

### Families and Types

- `find_families`
- `find_family_types`
- `get_family_type`
- `get_project_standard_types`

### Geometry

- `calculate_distance`
- `calculate_area`

### Architecture

- `create_wall`
- `modify_wall`
- `delete_element`
- `create_floor`
- `modify_floor`
- `create_room`
- `modify_room`
- `create_door`
- `create_window`

### Project

- `create_level`
- `create_grid`
- `create_view`

### Documentation

- `dimension_wall`
- `dimension_room`
- `create_text`
- `create_tag`
- `create_sheet`
- `create_schedule`

### Analysis

- `find_rooms_without_tags`
- `find_unhosted_doors`
- `find_unhosted_windows`
- `find_duplicate_elements`
- `find_nonstandard_elements`

---

## 2. get_project_info

Purpose: return basic project information.

Input:

```json
{}
```

Output:

```json
{
  "projectName": "Example",
  "revitVersion": "2026",
  "units": "metric",
  "lengthUnit": "mm",
  "areaUnit": "m2"
}
```

Risk: `READ_ONLY`

---

## 3. get_active_view

Input:

```json
{}
```

Output:

```json
{
  "id": 123,
  "name": "Level 1",
  "viewType": "FloorPlan"
}
```

---

## 4. get_active_level

Input:

```json
{}
```

Output:

```json
{
  "id": 456,
  "name": "Level 1",
  "elevation": 0
}
```

---

## 5. get_selected_elements

Input:

```json
{}
```

Output:

```json
{
  "elements": [
    {
      "id": 123,
      "category": "Walls",
      "family": "Basic Wall",
      "type": "Generic - 200mm"
    }
  ]
}
```

---

## 6. find_elements

Purpose: search project elements.

Input:

```json
{
  "category": "Walls",
  "levelId": 456,
  "nameContains": null,
  "limit": 50
}
```

All properties are required; use `null` for "any" (OpenAI strict mode). `limit` defaults to 50, max 200. Output: `totalCount`, `truncated`, `elements` (same shape as `get_selected_elements`).

---

## 7. get_element

Input:

```json
{
  "elementId": 123
}
```

Output should contain relevant information without serializing the entire object.

---

## 8. get_element_parameters

Input:

```json
{
  "elementId": 123,
  "parameterNames": [
    "Width",
    "Base Constraint",
    "Unconnected Height"
  ]
}
```

`parameterNames: null` returns all instance parameters (max 80). Named parameters are looked up on the element, then on its type. Each value has `name`, `source` (`instance`/`type`), `storageType`, `displayValue`, `valueMm` (lengths), `valueM2` (areas), `isReadOnly`; unknown names are listed in `notFound`.

Implementation status: the seven Phase 1 read tools (sections 2–8) are implemented in `src/RevitAi.Addin/Tools/`; their result contracts are in `src/RevitAi.Core/Tools/ReadModels.cs`. The six Phase 2 write tools (`create_wall`, `modify_wall`, `create_room`, `create_door`, `create_window`, `create_floor`) are in `src/RevitAi.Addin/Tools/WriteTools.cs`. Phase 3 adds `find_family_types`, `get_project_standard_types` (`TypeTools.cs`), `find_nearby_elements`, `get_element_room` and `get_room_boundary` (`SpatialTools.cs`).

---

## 9. get_element_location

Input:

```json
{
  "elementId": 123
}
```

Output should represent the location in a deterministic format.

---

## 10. get_element_bounding_box

Input:

```json
{
  "elementId": 123
}
```

---

## 11. find_families

Input:

```json
{
  "category": "Doors",
  "search": "single",
  "limit": 20
}
```

---

## 12. find_family_types

Input:

```json
{
  "category": "Doors",
  "nameContains": "single",
  "limit": null
}
```

Output per type: `typeId`, `family`, `type`, `isDefault`, `preferredRank` (from standards.json, 1 = most preferred), `instanceCount`.
Sorted by standard rank, then default, then usage. Default limit 30, max 100.

---

## 13. get_project_standard_types

Purpose: return the project's preferred types for a category, as configured by the user in
`%APPDATA%\RevitAi\standards.json` (ADR-033).

Input:

```json
{
  "category": "Walls"
}
```

Output: `configured` (false when the file has nothing for the category), `preferred` (matching project types, in
preference order), `notFoundInProject` (entries in the file that are not loaded in this project), `note`.

---

## 14. calculate_distance

Input:

```json
{
  "pointA": { "x": 0, "y": 0, "z": 0 },
  "pointB": { "x": 5000, "y": 0, "z": 0 }
}
```

Output:

```json
{
  "distance": 5000
}
```

---

## 15. calculate_area

Input:

```json
{
  "boundary": [
    { "x": 0, "y": 0 },
    { "x": 5000, "y": 0 },
    { "x": 5000, "y": 4000 },
    { "x": 0, "y": 4000 }
  ]
}
```

Output:

```json
{
  "area": 20
}
```

---

## 16. create_wall

Input:

```json
{
  "start": { "x": 0, "y": 0 },
  "end": { "x": 5000, "y": 0 },
  "levelId": 456,
  "wallTypeId": null,
  "heightMm": null
}
```

`wallTypeId: null` uses the project's default wall type; `heightMm: null` means 3000 mm. Straight walls only.

Validation:

- level exists
- type exists and is a wall type
- length ≥ 10 mm
- 0 < height ≤ 100 000 mm

Risk: `SAFE_MODIFICATION`

---

## 17. modify_wall

Input:

```json
{
  "wallId": 123,
  "end": "end",
  "distanceMm": 500
}
```

Moves one end of a straight wall along its direction: positive extends, negative shortens. `wallId` may be `"$opN.elementId"`.
This is the only supported wall modification in Phase 2; there is no generic geometry editor.

Validation: wall exists, is straight, resulting length ≥ 10 mm, distance ≠ 0.

---

## 18. delete_element

Input:

```json
{
  "elementId": 123
}
```

Risk: `DESTRUCTIVE`

Requires explicit confirmation.

---

## 19. create_floor

Input:

```json
{
  "levelId": 456,
  "boundary": [ { "x": 0, "y": 0 }, { "x": 5000, "y": 0 }, { "x": 5000, "y": 4000 }, { "x": 0, "y": 4000 } ],
  "floorTypeId": null
}
```

Validation: level and type exist; at least 3 points; every edge ≥ 10 mm; boundary does not cross or touch itself.

---

## 20. create_room

Input:

```json
{
  "levelId": 456,
  "point": { "x": 5000, "y": 3000 },
  "name": "Bedroom",
  "number": null
}
```

Post-execution validation: after regeneration the room must have an area > 0. A room at a point that is not enclosed
fails the step, and the whole plan is rolled back.

---

## 21. modify_room

Input:

```json
{
  "elementId": 123,
  "name": "Bedroom 1",
  "number": "101"
}
```

---

## 22. create_door

Input:

```json
{
  "wallId": 123,
  "doorTypeId": null,
  "offsetAlongWallMm": 2500
}
```

The door is centred `offsetAlongWallMm` from the wall's start point. `wallId` may be `"$opN.elementId"`;
`doorTypeId: null` uses the project's default door type.

Validation:

- host is a straight wall
- type is a door type
- offset lies within the wall
- after placement the door is hosted by that wall

---

## 23. create_window

Input:

```json
{
  "wallId": 123,
  "windowTypeId": null,
  "offsetAlongWallMm": 3000,
  "sillHeightMm": 900
}
```

Same rules as `create_door`. `sillHeightMm: null` keeps the type's default; otherwise the instance sill height must be editable.

---

## 24. create_level

Input:

```json
{
  "name": "Level 2",
  "elevation": 3000
}
```

Risk: `LARGE_MODIFICATION`

---

## 25. create_grid

Input:

```json
{
  "name": "A",
  "start": {},
  "end": {}
}
```

---

## 26. create_view

Input:

```json
{
  "viewType": "FloorPlan",
  "levelId": 456,
  "name": "Level 1 - AI"
}
```

---

## 27. dimension_wall / dimension_room

The AI cannot produce Revit `Reference` objects, so dimension tools are intent-level: C# computes the references.

`dimension_wall` input:

```json
{
  "viewId": 123,
  "wallId": 456,
  "offset": 500,
  "styleId": null
}
```

Dimensions the wall's length, offset from the wall by `offset` mm.

`dimension_room` input:

```json
{
  "viewId": 123,
  "roomId": 789,
  "offset": 500,
  "styleId": null
}
```

Dimensions the room's overall width and depth between its bounding walls.

---

## 28. create_text

Input:

```json
{
  "viewId": 123,
  "position": {},
  "text": "Example"
}
```

---

## 29. create_tag

Input:

```json
{
  "viewId": 123,
  "elementId": 456,
  "tagTypeId": 789
}
```

---

## 30. create_sheet

Input:

```json
{
  "name": "Level 1",
  "number": "A101",
  "titleBlockTypeId": 123
}
```

---

## 31. create_schedule

Input:

```json
{
  "name": "Door Schedule",
  "category": "Doors",
  "fields": [
    "Mark",
    "Family",
    "Type",
    "Width"
  ]
}
```

---

## 32. Spatial Tools (Phase 3)

`find_nearby_elements` — `{ "elementId": 123, "radiusMm": null, "category": null, "limit": null }`: model elements whose
bounding box is within `radiusMm` (default 1000, max 20 000) of the element's bounding box, sorted by centre distance.

`get_element_room` — `{ "elementId": 123 }`: the room containing the element; doors and windows return `fromRoom` /
`toRoom`. Walls usually lie between rooms and return no room.

`get_room_boundary` — `{ "roomId": 789 }`: boundary loops (outer first) of segments with the bounding element
(usually a wall), start, end and length in mm.

All three are `READ_ONLY`.

---

## 33. Documentation Tools (Phase 4)

Read: `find_views` — `{ "viewType": null, "nameContains": null, "templatesOnly": false, "limit": null }` → views with ID, name,
type, level and the sheet they are on. Title blocks and tag types are found with `find_family_types`.

Write (all go through plan → preview → apply):

| Tool | Input | Risk | Notes |
|---|---|---|---|
| `create_view` | `viewType` (`FloorPlan`/`CeilingPlan`), `levelId`, `name`, `viewTemplateId` | LARGE | Unique name per view type; template must be a view template. |
| `create_sheet` | `number`, `name`, `titleBlockTypeId`, `viewIds` (IDs or `$opN.elementId`) | LARGE | Unique number; views placed side by side (user arranges); a view can be on one sheet only, schedules on several. |
| `create_schedule` | `category`, `name`, `fields` | LARGE | Field names are matched at preview/apply; an unknown field fails the step and lists available fields. |
| `tag_elements` | `viewId`, `category` **or** `elementIds` | SAFE | Default tag per category; rooms get room tags; already tagged elements and unenclosed rooms are skipped. |
| `create_text` | `viewId`, `position`, `text` | SAFE | Default text type; model coordinates in plans, sheet coordinates on sheets. |
| `dimension_wall` | `viewId`, `wallId`, `offsetMm` | SAFE | Overall length between the wall's end faces; may fail for walls joined at both ends. |
| `dimension_room` | `viewId`, `roomId` | SAFE | Clear width and depth between the inner faces of opposite straight walls, through the room point. |
| `create_section` | `start`, `end`, `levelId`, `depthMm`, `heightMm`, `name` | LARGE | Looks to the **left** of start→end (verify in smoke test 4.12); default 5000 mm deep, 4000 mm high. |
| `create_elevations` | `planViewId`, `point`, `directions` (north/east/south/west) | LARGE | One marker; each slot's real direction is checked, so no reliance on Revit's slot order. North = project +Y. |
| `create_3d_view` | `name`, `cropToElementIds` | LARGE | Isometric; optional section box around elements + 500 mm. |

---

## 34. Analysis Tools

These tools are read-only and should be preferred for model checking.

Examples:

- `find_rooms_without_tags`
- `find_unhosted_doors`
- `find_unhosted_windows`
- `find_duplicate_elements`
- `find_nonstandard_elements`

---

## 35. Tool Design Rules

Tools must:

- be deterministic
- have explicit schemas
- return structured data
- have clear failure states
- avoid unnecessary data
- be testable independently
- have a defined risk level

Tools must not:

- execute arbitrary AI-generated code
- silently modify unrelated elements
- silently delete elements
- return misleading success
