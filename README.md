# ErpTool

Agent tool for **[AIOrchestrator](https://github.com/Graphene-Lab)** that lets an LLM drive a
WebVella-style ERP (the `AI.Erp` fork or upstream `WebVella.Erp`) with full control: discover
the data model, run EQL queries, create/update/delete records and manage many-to-many
relations — all over a JWT-secured REST API.

`ErpTool` is a **plugin tool**: it ships as a GitHub Release zip (host deployment into
`Tools/ErpTool/`) and as the NuGet package `Graphene.ErpTool`. It derives from
`BaseAgentTool`; its public methods are rendered to the LLM as the snake_case methods below.

## Methods (agent surface)

| Method | Purpose |
|---|---|
| `get_schema(entity?)` | Discover entities and fields (name, label, type, required). **Call first.** |
| `query(eql, parameters?)` | Run an EQL `SELECT` (read-only) with named parameters. |
| `create_record(entity, fields)` | Create a record from a JSON object of field values. |
| `update_record(entity, id, fields)` | Update a record by id (only the given fields change). |
| `delete_record(entity, id)` | Delete a record by id. |
| `manage_relation(relationId, originId, targetId, remove)` | Add/remove a many-to-many link. |

Every operation runs **as the configured ERP user**, so the ERP's own per-entity permissions,
hooks and validation apply. `ErpTool` is not a database client — it never bypasses the ERP.

## Configuration (host-provided, never agent-provided)

Connection settings are resolved in this order:

1. Environment variables — `ERP_BASE_URL`, `ERP_USER`, `ERP_PASSWORD`.
2. `PersistentData/erp.json` in the host's base directory (a folder updates never touch):

```json
{
  "baseUrl": "https://erp.example.com",
  "user": "agent@example.com",
  "password": "••••••"
}
```

If none is set, every method returns a clear `Error:` explaining what to configure.

## ERP-side requirement

The tool talks to the **`AgentApi` plugin** (`AI.Erp.Plugins.AgentApi`), which must be
installed on the ERP and registered in its host site. The plugin is **dual-target** — the same
source builds for the `AI.Erp` fork and for upstream `WebVella.Erp`. See the plugin's
`ARCHITECTURE.md` for the fork/vendor duality (global `using` aliases + an `ErpFlavor` build
switch). `ErpTool` itself is target-agnostic: it is pure HTTP against a stable REST contract.

## Install (hosts)

Drop the release zip into `Tools/ErpTool/`; the host discovers it at startup (or hot-adds it).
The plugin ships only its own files — the AIOrchestrator dependency graph is provided by the host.

## License

See [LICENSE.md](LICENSE.md).
