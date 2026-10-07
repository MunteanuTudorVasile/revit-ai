# Handoff — where the project stands

For a new session (e.g. Claude Code on the Windows PC). Read `CLAUDE.md`, `README.md` and `docs/DECISIONS.md` first.

## Status (2026-10-08)

- Phases 0–7 implemented, plus deleting with confirmation (ADR-037), EN/RO panel (ADR-034), grids from points and
  placement (ADR-044), unmet-request log (ADR-043) and the automatic self-test (ADR-045). ~50 tools.
- Builds for Revit 2025 and 2026 with zero warnings; 154 unit tests pass (`dotnet test tests/RevitAi.Core.Tests`).
- **Nothing has ever run inside Revit or against OpenAI with a real key.** The self-test harness itself is also untested:
  a failing check may be a harness bug, not a tool bug.
- Product direction: "AI BIM engineer" (ADR-040–042). ADR-042 (MVP boundary) is still only proposed.

## First test round (on Windows)

1. Close Revit; run `scripts\windows\setup-and-build.ps1` (builds and installs for each Revit year found).
2. Open a project copy; **Add-Ins → Revit AI → Self-test**; collect `%LOCALAPPDATA%\RevitAi\self-test-<date>.md`.
3. Manual checks M.1–M.7 at the top of `docs/SMOKE_TESTS.md`.
4. Logs: `%LOCALAPPDATA%\RevitAi\logs\revitai-<date>.log`; Apply history: `%LOCALAPPDATA%\RevitAi\history.jsonl`.

## First self-test (Revit 2026, 2026-10-07)

45 passed, 3 failed, 0 skipped. Fixed afterwards:

- `modify_wall` did not extend a wall joined at that end (Revit kept it at the join) and still reported success: the moved
  end is now disconnected (`WallUtils.DisallowWallJoinAtEnd`) and the new length is verified after regeneration.
- `place_family_instances` placed instances one level-height too high: the Z passed to `NewFamilyInstance` is an offset
  from the level, so it is now 0.

Confirmed working in Revit: add-in loading, every read/QA tool, plans with references, preview and failing-step rollback,
views, tags, dimensions, schedules, sheets, sections, elevations (real direction), 3D, templates, grids, delete with
dependents, refusals, `select_elements` inside a transaction group. Still untested: the panel, OpenAI, the dispatcher.

## Things only Revit can confirm (watch for these)

- The add-in loads, the dockable pane registers, the dispatcher (ExternalEvent) round trip works.
- `create_section` looks to the left of start→end (smoke test 4.12 / self-test section check).
- `place_family_instances` puts instances at the level's height (self-test "placement" check).
- `select_elements` calling `ShowElements` while the self-test's transaction group is open.
- Default model name `gpt-5` is accepted by OpenAI (setting `OpenAiModel`).

## Working rules

- Fix on `main`; keep `next` for new features (both currently equal).
- After a change: build both years, run the unit tests, commit with the attribution line, push.
- Agreed next steps after testing: issue model and "fix what's safe" (Phases 8–9), CAD plan → walls, sending only
  relevant tools to the AI, MEP read tools.
