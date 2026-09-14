# ErpTool

ErpTool is an agent tool for **[AIOrchestrator](https://github.com/Graphene-Lab)**. It lets an
LLM control a WebVella ERP (the `AI.Erp` fork or the upstream `WebVella.Erp`). The agent can
read the data model, run EQL queries, create, update and delete records, and add or remove
many-to-many relations. All calls go to a REST API protected by JWT.

ErpTool is a plugin tool. It is published in two channels:

- a GitHub Release zip, which the host installs into `Tools/ErpTool/`;
- the NuGet package `Graphene.ErpTool`.

The class derives from `BaseAgentTool`. Each public method becomes a tool method for the LLM
(with a snake_case name).

## Where it fits

ErpTool is one of three small programs that work together to let an AI agent run a company:

| Part | Repo | Job |
|---|---|---|
| **AI ERP** | [Graphene-Lab/AI-ERP](https://github.com/Graphene-Lab/AI-ERP) | The ERP: stores the data, runs the real operations, enforces the rules and permissions. |
| **AgentBridge** | [Graphene-Lab/AgentBridge](https://github.com/Graphene-Lab/AgentBridge) | The chat front-end and the agent runtime (AIOrchestrator). You talk to the agent here. |
| **ErpTool** | this repository | The bridge: turns the agent's decisions into secure calls to the ERP. |

```
You → AgentBridge → AIOrchestrator (the agent) → ErpTool → HTTPS + JWT → AgentApi → AI ERP
```

ErpTool runs inside AgentBridge as a plugin (dropped into `Tools/ErpTool/`). The agent reads
the ERP schema, picks a method, and ErpTool calls the ERP's `AgentApi` over HTTPS with a JWT.
The ERP checks the permissions of the logged-in account and does the real work. The step-by-step
guide for the whole ecosystem is in the
[AI ERP wiki](https://github.com/Graphene-Lab/AI-ERP/wiki).

## Methods

| Method | What it does |
|---|---|
| `get_schema(entity?)` | Lists every entity and its fields (name, label, type, required). **Call this first.** |
| `query(eql, parameters?)` | Runs an EQL `SELECT` (read only) with named parameters. |
| `create_record(entity, fields)` | Creates a record from a JSON object of field values. |
| `update_record(entity, id, fields)` | Updates a record by id. Only the given fields change. |
| `delete_record(entity, id)` | Deletes a record by id. |
| `manage_relation(relationId, originId, targetId, remove)` | Adds or removes a many-to-many link. |

Every call runs as the configured ERP user. The ERP permissions, hooks and validation still
apply. ErpTool does not write to the database directly.

## Configuration

ErpTool reads the connection settings from the host, not from the agent. It uses these sources,
in order:

1. Environment variables: `ERP_BASE_URL`, `ERP_USER`, `ERP_PASSWORD`.
2. The file `PersistentData/erp.json` in the host base directory. An app update does not change
   this folder:

```json
{
  "baseUrl": "https://erp.example.com",
  "user": "agent@example.com",
  "password": "••••••"
}
```

If no setting is found, every method returns an `Error:` message that says what to configure.

## ERP side

The tool calls the **`AgentApi` plugin** (`AI.Erp.Plugins.AgentApi`). This plugin must be
installed on the ERP and registered in its host site. The plugin has two build targets: the
`AI.Erp` fork and the upstream `WebVella.Erp`. See the plugin `ARCHITECTURE.md` for the details
(`using` aliases and the `ErpFlavor` build switch). ErpTool itself does not depend on the
target: it only uses HTTP and a stable REST contract.

## Install (hosts)

Copy the release zip into `Tools/ErpTool/`. The host finds it at startup, or adds it while the
app is running. The zip contains only the plugin files. The AIOrchestrator libraries come from
the host.

## License

See [LICENSE.md](LICENSE.md).
