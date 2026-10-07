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

## Results

| Date | Revit version/build | Tester | Result | Notes |
|---|---|---|---|---|
| | | | | |
