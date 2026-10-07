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
| 0.7 | Type `hello` and press **Enter**. | Chat shows `You: hello`, then `Echo: hello` with the current context. Input box clears. |
| 0.8 | Start a wall command (Revit waiting for a click), then click **Send** in the panel. | Reply appears once you finish/cancel the wall command. If you wait longer than 30 s, a "Revit didn't respond in time" message appears instead. Revit does not freeze. |
| 0.9 | Open a second project; switch between the two windows. | Context follows the active project. |
| 0.10 | Close one project while the other stays open. | Context shows the remaining project after its view activates (or "No project open" if none). |
| 0.11 | Undock, resize and re-dock the panel; restart Revit. | Panel works after re-docking; Revit restores its position. |
| 0.12 | Close Revit. | Log contains `Revit AI shut down.` No crash. |
| 0.13 | Edit `%APPDATA%\RevitAi\settings.json` to `{ "DispatcherTimeoutSeconds": 0 }` and restart. | Add-in starts; log contains a WARN saying the default is used. Restore the file afterwards. |

## Results

| Date | Revit version/build | Tester | Result | Notes |
|---|---|---|---|---|
| | | | | |
