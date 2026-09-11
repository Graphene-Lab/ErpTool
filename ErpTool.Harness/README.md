# ErpTool.Harness

This is the test harness for `ErpTool`. It is not part of the plugin and it is not published.
The plugin csproj excludes its sources, so the harness never compiles into the plugin assembly.

## Usage

```bash
dotnet run --project ErpTool.Harness -- --definitions
```

`--definitions` prints the tool catalog that the LLM receives. It is the same text the
orchestrator builds from the class and its XML docs. Use it to check the agent-facing methods,
the parameters and the descriptions.

Prompt-based end-to-end tests against a live ERP are planned for this harness (they need a
running ERP and the connection settings). See the "Testing Agent Tools" section in the
AIOrchestrator `AGENT_TOOLS_GUIDE.md`.
