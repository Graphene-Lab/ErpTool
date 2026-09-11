# ErpTool — TOOL_CHECKLIST Compliance

Checked 2026-09-11 against `AIOrchestrator/API/TOOL_CHECKLIST.md`. `ErpTool` is a pure-network
agent tool: it drives a JWT-secured ERP REST API and reads/writes **no files**, so the
file-oriented points are N/A.

## Release & layout (plugin tools)

- OK — csproj `<Version>$([System.DateTime]::Now.ToString("1.yy.MM.dd"))</Version>`: date auto-version.
- OK — both channels on `v*` tags: `.github/workflows/plugin-release.yml` (GitHub Release zip for hosts) + `.github/workflows/publish.yml` (NuGet `Graphene.ErpTool`).
- OK — AIOrchestrator referenced as sibling (`..\AIOrchestrator\AIOrchestrator.csproj` ProjectReference when present, `Graphene.AIOrchestrator` 1.* package otherwise); never copied into the plugin tree.
- OK — never ships `AIOrchestrator.dll`/dependency graph: plugin-release.yml strips the graph; ErpTool has no unique dependency dlls beyond the BCL + the host-provided Newtonsoft.Json.
- OK — writes no state: connection settings are READ from the environment or `PersistentData/erp.json`; the tool never writes config or files into `Tools/ErpTool/` or next to the host executable.
- OK — independent of the host launch directory: config resolves via `AppContext.BaseDirectory` (a non-CWD base), requests use absolute URLs, no CWD use.

## Agent-facing descriptions

- OK — docs never mention internals (HttpClient, JSON bodies, JWT, endpoints, Agent API). The class summary states competency + cross-method rules only.
- OK — minimal text: class summary three lines, each method `summary`/`param`/`returns`; rendered definitions stay small.
- OK — methods state what they do (outcomes), not how.
- OK — nothing mentions a sandbox or virtual paths (no file paths at all).
- N/A — no path-bearing methods.
- OK — every public method (`get_schema`, `query`, `create_record`, `update_record`, `delete_record`, `manage_relation`) has `summary`, `param`, `returns` incl. error format.
- OK — class summary: one-line competency + cross-method rules (permissions apply, start with `get_schema`).
- OK — no summary/param redundancy; JSON/format details live only in `param`/`returns`.
- OK — one instruction per line; each `///` line is one continuous source line.
- OK — formats specified: EQL statement text, JSON object/array shapes with examples, GUID ids.
- OK — cross-references: params that come from another method's result name `get_schema()`.
- OK — method-name references written naturally; the generator normalizes them.
- OK — runtime messages reference methods via `Utility.ToSnakeCase(nameof(...))`, never hardcoded PascalCase.
- OK — errors are actionable (cause + fix/alternative) and prefixed `Error:`; no raw exceptions, no URL/credential leakage.
- N/A — no `[[name]]` dynamic placeholders.

## Surface & sandbox

- OK — only agent operations are public (6 methods); system-side internals (HTTP send, auth token cache, config loading, JSON parsing) are private/static.
- N/A — no file-handling methods: `SandboxPath` not applicable.
- OK — no sandbox escape, no credentials/configuration/host paths exposed: `ErpConfig` values are never returned to the agent (errors report the outcome, not the endpoint or the password).

## Code & conventions

- OK — class name ends with `Tool`; package `Graphene.ErpTool`.
- OK — public methods declared on the class; the only inherited public member is `BaseAgentTool.LoadSkill`.
- OK — `Log.LogStep()` at entry/outcome/failure of every public method and in the HTTP/auth plumbing.
- OK — derives from `BaseAgentTool`; `IFileTool` N/A (no files handled).
- OK — `GenerateDocumentationFile=True` (tool definitions come from the `.xml` next to the dll).

## Standardized support — shared AIOrchestrator helpers

- N/A — no file content described/listed: `FileManager` not applicable.
- N/A — no files created/modified: `GitSupport.Snapshot` not applicable.
- N/A — no file output: no sandbox-relative path to return.
- N/A — the tool parses no LLM-generated text: `Utility.RemoveFencesEncapsulationAndFixTrim` not applicable.
- N/A — no HTML/SVG output: `Utility.EmbedSvgIcons` not applicable.
- N/A — no language detection: `Utility.DetectLanguage` not applicable.
- N/A — no path resolution: `SandboxPath` not applicable (see "Surface & sandbox").
- OK — method-name references in runtime messages use the shared `Utility.ToSnakeCase`.

## Completion — compliance file

- OK — this file is shipped at the repository root of the plugin and states every checklist point above.
