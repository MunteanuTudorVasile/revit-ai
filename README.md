# Revit AI Assistant

AI assistant add-in for Autodesk Revit 2025 and 2026. Start with [CLAUDE.md](CLAUDE.md) and [docs/](docs/); decisions are in [docs/DECISIONS.md](docs/DECISIONS.md).

Current phase: **Phase 1 — read-only AI** (answers questions about the model; cannot change it).

## Layout

| Path | What |
|---|---|
| `src/RevitAi.Core` | Revit-free code (`net8.0`): AI loop, OpenAI client, tool registry and schema validation, dispatcher queue, settings, logging. Builds and tests on macOS. |
| `src/RevitAi.Addin` | Everything that touches Revit or WPF (`net8.0-windows`): entry point, dispatcher, read-only tools, API key store, panel. |
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
| OpenAI API key | `%APPDATA%\RevitAi\openai.key` (DPAPI-encrypted for the Windows user; set from the panel) |
| Logs | `%LOCALAPPDATA%\RevitAi\logs\revitai-YYYYMMDD.log` |

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `OpenAiModel` | `gpt-5` | OpenAI model used for answers. |
| `AiRequestTimeoutSeconds` | 120 | How long to wait for OpenAI. |
| `MaxAiSteps` | 8 | AI round trips per question before giving up. |
| `DispatcherTimeoutSeconds` | 30 | How long to wait for Revit to run a request. |
| `ConsentAcceptedAt` | null | Set when the user accepts the data notice. |

Restart Revit after editing.

## Notes

- The Revit API is referenced through the `Nice3point.Revit.Api.*` reference-assembly packages (MIT) for compiling only. Revit supplies the real API at runtime; nothing from those packages is shipped.
- All Revit API access from the panel goes through `RevitDispatcher` (ADR-023). Never call `.Result` or `.Wait()` on its tasks from the UI thread: that deadlocks Revit.
