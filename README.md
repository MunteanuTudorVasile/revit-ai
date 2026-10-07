# Revit AI Assistant

AI assistant add-in for Autodesk Revit 2025 and 2026. Start with [CLAUDE.md](CLAUDE.md) and [docs/](docs/); decisions are in [docs/DECISIONS.md](docs/DECISIONS.md).

Status: **Phases 0–7 implemented, plus grids from points, placing elements at points and an unmet-request log** (foundation, read-only AI, modeling, context, documentation, model QA, company standards, workflows), plus deleting with confirmation. Nothing has been run inside Revit yet: next step is the smoke tests. Answers questions; proposes walls, rooms, doors, windows and floors using the project's standard types; creates views, sheets, schedules, tags, text and wall dimensions. The model changes only when the user clicks Apply. Panel in English or Romanian.

## Layout

| Path | What |
|---|---|
| `src/RevitAi.Core` | Revit-free code (`net8.0`): AI loop, OpenAI client, tool registry and schema validation, plans and plan references, action history, geometry checks, dispatcher queue, settings, logging. Builds and tests on macOS. |
| `src/RevitAi.Addin` | Everything that touches Revit or WPF (`net8.0-windows`): entry point, dispatcher, read and write tools, plan executor (transactions, failure handling), API key store, panel. |
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

To test: open a project and click **Add-Ins → Revit AI → Self-test** (automatic checks, everything undone, report saved), then the short manual list in `docs/SMOKE_TESTS.md`.

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
| OpenAI API key | `%APPDATA%\RevitAi\openai.key` (DPAPI-encrypted for the Windows user; set from the panel) |
| Project standards | `%APPDATA%\RevitAi\standards.json` (preferred types per category; see ADR-033) |
| Logs | `%LOCALAPPDATA%\RevitAi\logs\revitai-YYYYMMDD.log` |
| AI action history | `%LOCALAPPDATA%\RevitAi\history.jsonl` (one JSON line per Apply, successful or not) |

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `OpenAiModel` | `gpt-5` | OpenAI model used for answers. |
| `AiRequestTimeoutSeconds` | 120 | How long to wait for OpenAI. |
| `MaxAiSteps` | 12 | AI round trips per question before giving up. |
| `DispatcherTimeoutSeconds` | 30 | How long to wait for Revit to run a request. |
| `Language` | `en` | Panel language: `en` or `ro`. Also switchable from the panel. |
| `ConsentAcceptedAt` | null | Set when the user accepts the data notice. |

`settings.json` only contains values you changed; anything missing uses the default above. Restart Revit after editing.

## Notes

- The Revit API is referenced through the `Nice3point.Revit.Api.*` reference-assembly packages (MIT) for compiling only. Revit supplies the real API at runtime; nothing from those packages is shipped.
- All Revit API access from the panel goes through `RevitDispatcher` (ADR-023). Never call `.Result` or `.Wait()` on its tasks from the UI thread: that deadlocks Revit.
