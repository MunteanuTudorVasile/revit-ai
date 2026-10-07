# Smoke Tests (manual, in Revit)

Run for **each supported Revit version** (2025, 2026) after building on Windows. Record the result and Revit build number.

Before starting, open the log: `%LOCALAPPDATA%\RevitAi\logs\revitai-YYYYMMDD.log`.

## Phase 0

| # | Steps | Expected |
|---|---|---|
| 0.1 | Start Revit. Accept the add-in prompt ("Always Load"). | No error dialog. Log contains `Revit AI started in Revit 20xx`. |
| 0.2 | On the start page (no project open), click **Add-Ins → Revit AI → Assistant**. | Button is enabled and shows a blue sparkle icon. Panel opens on the right showing "No project open". Clicking again hides it. |
| 0.3 | Open a project with a floor plan. Activate the floor plan. | Panel shows project title, view name, `(FloorPlan)`, the level name, `0 selected elements`. |
| 0.4 | Select one wall, then three elements, then nothing. | Count updates to 1, 3, 0 without clicking anything. |
| 0.5 | Activate a 3D view, then a sheet. | View name/type update. No level is shown for these views. |
| 0.6 | Click **Refresh**. | Context unchanged and correct (read through the dispatcher). |
| 0.8 | Start a wall command (Revit waiting for a click), then click **Refresh** in the panel. | The context updates once you finish/cancel the wall command. If you wait longer than 30 s, a "Revit didn't respond in time" message appears instead. Revit does not freeze. |
| 0.9 | Open a second project; switch between the two windows. | Context follows the active project. |
| 0.10 | Close one project while the other stays open. | Context shows the remaining project after its view activates (or "No project open" if none). |
| 0.11 | Undock, resize and re-dock the panel; restart Revit. | Panel works after re-docking; Revit restores its position. |
| 0.12 | Close Revit. | Log contains `Revit AI shut down.` No crash. |
| 0.13 | Edit `%APPDATA%\RevitAi\settings.json` to `{ "DispatcherTimeoutSeconds": 0 }` and restart. | Add-in starts; log contains a WARN saying the default is used. Restore the file afterwards. |

## Phase 1 — read-only AI

Needs an OpenAI API key with billing enabled. Use a small test project with a few walls, doors and two levels.

| # | Steps | Expected |
|---|---|---|
| 1.1 | Open the panel for the first time (delete `%APPDATA%\RevitAi\settings.json` and `openai.key` first if re-testing). | The data notice and the API key panel are shown. |
| 1.2 | Type a question and press **Enter** before accepting. | Asks you to accept the data notice. Nothing is sent. |
| 1.3 | Click **I understand, continue**, restart Revit, reopen the panel. | Notice gone and stays gone. `settings.json` contains `ConsentAcceptedAt`. |
| 1.4 | Save the key `sk-wrong`, ask "What did I select?". | "OpenAI rejected the API key"; the key panel reopens. |
| 1.5 | Save your real key. Open `%APPDATA%\RevitAi\openai.key` in Notepad. | "API key saved". The file is unreadable binary, not your key. |
| 1.6 | Select one wall. Ask "What did I select?". | Status shows "Reading your selection…". Answer names the wall's type and matches Revit's Properties palette. |
| 1.7 | In a floor plan ask "What level am I on?". Repeat in a 3D view. | Correct level; in 3D it says the view has no level. |
| 1.8 | With a wall selected ask "What type of wall is this?". | Correct family and type. |
| 1.9 | Follow up: "How long is it?". | Refers to the same wall; length matches Properties (mm or m). |
| 1.10 | Ask "How many doors are on Level 1?". | Count matches a Revit filter/schedule. |
| 1.11 | Ask "Delete this wall." | Says deleting is not available. No plan card appears. The wall still exists; Revit's undo list is unchanged. |
| 1.12 | Ask a question and click **Cancel** while "Thinking…" shows. | "Cancelled." The next question works normally. |
| 1.13 | Disconnect from the network and ask a question. | "I couldn't reach OpenAI…". Reconnect: questions work again. |
| 1.14 | With two projects open, ask a question, then switch to the other project. | "The active project changed, so I started a new conversation." |
| 1.15 | Ask in Romanian: "Ce am selectat?". | Answers in Romanian. Names with diacritics display correctly. |
| 1.16 | Click **API key → Remove**, then ask a question. | Key panel shows; asks you to add a key. |
| 1.17 | Open the log. | Lines like `Answered using 2 tool call(s): get_selected_elements, …`. Your API key appears nowhere. |

## Phase 2 — basic modeling

Use a test project you can throw away. After each step, check Revit's undo list (the arrow next to Undo).

| # | Steps | Expected |
|---|---|---|
| 2.1 | Select a straight wall. Ask "Make this wall 500 mm longer." | A plan card appears: "Wall N: X mm → X+500 mm". **The wall has not changed.** The chat says to review and click Apply. |
| 2.2 | Click **Preview**. | "Preview succeeded… rolled back" with the outcome. The wall is still unchanged; undo list unchanged. |
| 2.3 | Click **Apply**. | Wall is 500 mm longer. Message lists the change and the undo name. Undo list has exactly one new entry "Revit AI: Make this wall 500 mm longer". |
| 2.4 | Press Ctrl+Z once. | The wall is back to its original length. |
| 2.5 | Ask "Create a 4 m × 3 m room with walls on Level 1, starting at the corner of this wall." (select a wall first). | Plan with 4 × create_wall + create_room (5 operations). Apply is enabled without preview (≤ 5 operations). |
| 2.6 | Apply 2.5. | Four walls and an enclosed room of ~12 m² (less wall thickness). One undo entry. |
| 2.7 | Follow up: "Add a door in the first of those walls, in the middle." | Plan uses the wall ID from 2.6 (recent actions). Apply places a hosted door. |
| 2.8 | Ask for something with more than 5 operations, e.g. "Create a 6 × 4 m room with walls, a door and two windows." | Header says "preview required before Apply"; **Apply is disabled** until Preview succeeds. |
| 2.9 | Ask "Create a room at x 999999, y 999999 on Level 1." | Plan is created; Preview fails: "isn't inside an area enclosed…". Apply after a failed preview stays disabled. Nothing changed. |
| 2.10 | Build a plan where the last step fails (e.g. a door with offset larger than a wall created in the same plan, via Preview), then Apply a variant. | "Nothing was changed. Step N failed…"; earlier steps rolled back too. Undo list unchanged. History file has the failed attempt. |
| 2.11 | Ask to create a wall overlapping an existing wall exactly. | Revit's "walls overlap" warning appears **in the panel** (⚠), not as a Revit dialog. |
| 2.12 | Get a plan, then type a new question instead of applying. | "I discarded the previous proposal; it was never applied." |
| 2.13 | Get a plan, click **Discard**. | Card disappears; "Nothing was changed." |
| 2.14 | Get a plan, then switch to another open project. | Proposal discarded with a message. |
| 2.15 | "Create a 5 × 4 m floor on Level 1 at the origin." Apply. | Floor of 20 m² with the project's default floor type. |
| 2.16 | "Put a window 1 m from the start of this wall with a 900 mm sill." Apply. | Hosted window; sill height 900 mm in Properties. |
| 2.17 | Open `%LOCALAPPDATA%\RevitAi\history.jsonl`. | One line per Apply with request, operations, steps, affected IDs and undo name. |

## Phase 3 — context intelligence and Romanian

| # | Steps | Expected |
|---|---|---|
| 3.1 | Click the **Română** button. | Every panel text switches to Romanian (buttons, notices, footer, context line, e.g. "3 elemente selectate"). The button now says **English**. Restart Revit: Romanian is kept. |
| 3.2 | Select 25 elements. | Context line shows "25 de elemente selectate". |
| 3.3 | Ask "Ce tipuri de uși avem în proiect?" | Answer in Romanian listing door types, mentioning the default and most used. |
| 3.4 | Edit `%APPDATA%\RevitAi\standards.json`: `{ "default": { "Walls": ["<an interior wall type in your project>"] } }`. Ask "Create a 3 m wall from the end of this wall." | Plan uses that wall type and the answer says it is the project standard. (No Revit restart needed.) |
| 3.5 | Add a standard entry for a type that doesn't exist; ask "What are our standard wall types?" | Lists the matched type and says the other is not loaded in the project. |
| 3.6 | Select a door; ask "Which rooms does this door connect?" | Names the rooms on both sides (or says there are none). |
| 3.7 | Select a room; ask "Which walls bound this room and how long are they?" | Lists the bounding walls with lengths matching Revit. |
| 3.8 | Select a wall; ask "What is next to this wall?" | Lists joined walls, hosted doors/windows and nearby elements, nearest first. |
| 3.9 | Select a wall; ask "Make it 300 mm longer." | Plan created without a get_selected_elements call (the selection is already in context; check the log's tool list). |
| 3.10 | Ask "Make this room 1 m wider." | Says moving walls sideways is not available yet; no plan. |
| 3.11 | Set `"Language": "fr"` in settings.json and restart. | Panel in English; log WARN about the language. |

## Phase 4 — documentation

| # | Steps | Expected |
|---|---|---|
| 4.1 | In a floor plan with rooms, ask "Tag all rooms in this view." | Plan: "Tag N untagged Rooms in view …". Apply adds room tags at room locations; already-tagged rooms are skipped. One undo entry. |
| 4.2 | Ask again "Tag all rooms in this view." | Says there is nothing untagged (no plan, or the step fails with that reason). |
| 4.3 | Ask "Tag the doors in this view." | Door tags with the default door tag; if no door tag family is loaded, the step fails with Revit's reason and nothing changes. |
| 4.4 | Ask "Create a door schedule with Mark, Family and Type, Width and Height." | Header says preview required. Preview succeeds; Apply creates the schedule with those columns. |
| 4.5 | Ask for a schedule with a made-up field, e.g. "Door Colour". | Preview fails listing available fields. Nothing changed. |
| 4.6 | Ask "Create a floor plan for Level 2 called 'Level 2 - AI' and put it on a new sheet A102 'Level 2'." | Plan: create_view + create_sheet referencing $op1. Preview, then Apply: the view exists and is placed on sheet A102 with the default title block. |
| 4.7 | Ask to create a sheet with a number that already exists. | Rejected before planning: "A sheet numbered … already exists." |
| 4.8 | Select a free-standing wall in a plan; ask "Dimension this wall." | Dimension parallel to the wall, 1 m away, value equals the wall length. |
| 4.9 | Repeat 4.8 on a wall joined at both ends. | Either works, or fails with "Couldn't find both end faces…" and nothing changes. |
| 4.10 | Ask "Add the text 'Verificat' near this wall." | Text note in the view near the wall. |
| 4.11 | Switch to Română, ask "Etichetează toate camerele din această vedere." | Plan line and result message in Romanian. |
| 4.12 | Select a wall; ask "Make a section through this wall looking north." Apply, open the section. | **Verify the look direction** matches the request and the wall is cut. If it looks the opposite way, report it: the section frame needs flipping. |
| 4.13 | In a plan, ask "Create north and east elevations from the centre of this room." Apply. | One marker; two elevation views that really look north and east (check the marker arrows). |
| 4.14 | Select a room; ask "Make a 3D view of this room." Apply. | 3D view with a section box around the room and its walls. |
| 4.15 | In a plan, select a rectangular room; ask "Dimension this room." Apply. | Two dimensions (width and depth) between the inner wall faces; values match the room's clear size. |
| 4.16 | Repeat 4.15 for an L-shaped room. | Dimensions between the outermost opposite walls, or a clear failure; nothing half-done. |

## Phase 5 — model QA and moving

| # | Steps | Expected |
|---|---|---|
| 5.1 | Ask "Check this model." | Runs several checks and summarises counts (untagged rooms, unhosted doors/windows, duplicates, warnings). No plan card; nothing changed. |
| 5.2 | Ask "Which rooms on this view have no tag? Show them." | Lists them and selects/zooms to them in Revit. Undo list unchanged. |
| 5.3 | Copy-paste a door onto itself (Ctrl+C, Paste Aligned → Same Place). Ask "Are there duplicates?" | Finds the pair from Revit's duplicate warning. |
| 5.4 | Ask "What are the most common warnings in this model?" | Matches Manage → Warnings, most frequent first. |
| 5.5 | With standards.json configured for Walls, ask "Which walls don't use our standard types?" | Lists walls with other types; says nothing can be checked if standards are empty. |
| 5.6 | Ask "Which doors have no Mark?" | Lists doors with an empty Mark. |
| 5.7 | Select a rectangular room; ask "Make this room 1 m wider." | Plan: move_elements on one bounding wall by 1000 mm perpendicular to it; the answer says which wall. Preview, then Apply: room area grows by ~1 m × depth; the wall's doors/windows moved with it. One undo entry. |
| 5.8 | Pin a wall (Modify → Pin) and ask to move it. | Refused: "Element … is pinned". |
| 5.9 | After 5.3, ask "Delete the duplicate door." | Plan card with a red warning; **Apply disabled**. The confirmation checkbox is disabled until Preview. |
| 5.10 | Click Preview. | "Deleted 1 element(s)…" (rolled back); the door still exists. Checkbox becomes available; Apply still disabled until ticked. |
| 5.11 | Tick the checkbox, click Apply. | Duplicate removed; one undo entry; history.jsonl lists the deleted ID. Ctrl+Z restores it. |
| 5.12 | Ask "Delete this wall" for a wall with a door; Preview. | Preview reports 2 elements deleted, including 1 dependent (the door). |
| 5.13 | Ask to delete a level, a view or a pinned wall. | Refused before any plan, with the reason. |
| 5.14 | Ask "Make this room wider" and check the plan. | No delete_elements in the plan (deletes never appear as a side effect). |

## Results

| Date | Revit version/build | Tester | Result | Notes |
|---|---|---|---|---|
| | | | | |
