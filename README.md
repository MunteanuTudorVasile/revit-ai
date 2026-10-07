# Revit AI Assistant

AI assistant add-in for Autodesk Revit 2025 and 2026. Start with [CLAUDE.md](CLAUDE.md) and [docs/](docs/); decisions are in [docs/DECISIONS.md](docs/DECISIONS.md).

Current phase: **Phase 0 — foundation** (panel, dispatcher, context; no AI yet).

## Layout

| Path | What |
|---|---|
| `src/RevitAi.Core` | Revit-free code (`net8.0`): dispatcher queue, context model, settings, logging. Builds and tests on macOS. |
| `src/RevitAi.Addin` | Everything that touches Revit or WPF (`net8.0-windows`): entry point, dispatcher, panel. |
| `tests/RevitAi.Core.Tests` | xUnit tests for Core. No Revit, no live AI calls. |
| `docs/SMOKE_TESTS.md` | Manual checks to run in Revit. |

## Build and run on Windows

Requires the .NET 8 SDK and Revit 2025 and/or 2026. **Close Revit before building**: it locks the add-in DLLs.

```bash
dotnet build src/RevitAi.Addin -p:RevitVersion=2026
```

```bash
dotnet build src/RevitAi.Addin -p:RevitVersion=2025
```

A Debug build on Windows installs itself into `%APPDATA%\Autodesk\Revit\Addins\<year>\` (`RevitAi.addin` plus a `RevitAi\` folder). Start Revit, accept the add-in security prompt, and use **Add-Ins → Revit AI → Assistant**.

To uninstall, delete `RevitAi.addin` and the `RevitAi\` folder from that directory.

## Tests (any OS)

```bash
dotnet test tests/RevitAi.Core.Tests
```

## Runtime files

| File | Location |
|---|---|
| Settings | `%APPDATA%\RevitAi\settings.json` (created with defaults on first start) |
| Logs | `%LOCALAPPDATA%\RevitAi\logs\revitai-YYYYMMDD.log` |

## Notes

- The Revit API is referenced through the `Nice3point.Revit.Api.*` reference-assembly packages (MIT) for compiling only. Revit supplies the real API at runtime; nothing from those packages is shipped.
- All Revit API access from the panel goes through `RevitDispatcher` (ADR-023). Never call `.Result` or `.Wait()` on its tasks from the UI thread: that deadlocks Revit.
