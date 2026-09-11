using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIOrchestrator.API;

/// <summary>ERP control for agent use: discover the data model, run EQL queries, create/update/delete records and manage many-to-many relations.
/// Every operation runs as the configured ERP user, so the ERP's own permissions apply.
/// Start with get_schema() to learn the entity and field names before querying or writing.</summary>
public class ErpTool : BaseAgentTool
{
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly object AuthLock = new();
    private static string? _token;

    // ──────────────────────────────────────────────
    //  Discovery
    // ──────────────────────────────────────────────

    /// <summary>Discover the ERP data model: every entity (type) with its fields (name, label, type, required). Call this first to learn the names used by the other methods.</summary>
    /// <param name="entity">Optional entity name to return just that entity's fields; omit for the full model.</param>
    /// <returns>JSON list of entities and their fields, or "Error:" with the cause.</returns>
    public string GetSchema(string? entity = null)
    {
        Log.LogStep($"ErpTool.GetSchema: entity={entity ?? "(all)"}");
        var path = string.IsNullOrWhiteSpace(entity) ? "schema" : $"schema/{Uri.EscapeDataString(entity)}";
        return Call("GET", path, null, "schema");
    }

    // ──────────────────────────────────────────────
    //  Query
    // ──────────────────────────────────────────────

    /// <summary>Run an EQL SELECT query against the ERP. Use entity and field names from get_schema().</summary>
    /// <param name="eql">The EQL statement, e.g. "SELECT id, name FROM account WHERE name = @name".</param>
    /// <param name="parameters">Optional JSON array of named parameters, e.g. [{"name":"name","value":"Acme"}].</param>
    /// <returns>JSON with the matching records and total_count, or "Error:" with the cause.</returns>
    public string Query(string eql, string? parameters = null)
    {
        Log.LogStep($"ErpTool.Query: {eql}");
        if (string.IsNullOrWhiteSpace(eql))
            return $"Error: 'eql' is required. Provide an EQL SELECT statement (see {Utility.ToSnakeCase(nameof(GetSchema))} for entity/field names).";

        var body = new JsonObject { ["eql"] = eql };
        if (!string.IsNullOrWhiteSpace(parameters))
        {
            if (JsonNode.Parse(parameters) is not JsonArray arr)
                return "Error: 'parameters' must be a JSON array like [{\"name\":\"x\",\"value\":\"y\"}].";
            body["parameters"] = arr;
        }
        return Call("POST", "query", body, "query");
    }

    // ──────────────────────────────────────────────
    //  Record CRUD
    // ──────────────────────────────────────────────

    /// <summary>Create a record of an entity. Field names come from get_schema().</summary>
    /// <param name="entity">Entity (type) name, e.g. "account".</param>
    /// <param name="fields">JSON object of field name to value, e.g. {"name":"Acme","email":"a@b.c"}.</param>
    /// <returns>The created record (including its id) as JSON, or "Error:" with the cause.</returns>
    public string CreateRecord(string entity, string fields)
    {
        Log.LogStep($"ErpTool.CreateRecord: entity={entity}");
        if (string.IsNullOrWhiteSpace(entity)) return $"Error: 'entity' is required (see {Utility.ToSnakeCase(nameof(GetSchema))}).";
        if (ParseFields(fields) is not JsonObject obj) return "Error: 'fields' must be a JSON object like {\"name\":\"Acme\"}.";
        return Call("POST", $"records/{Uri.EscapeDataString(entity)}", obj, "create");
    }

    /// <summary>Update an existing record by id. Only the fields provided are changed.</summary>
    /// <param name="entity">Entity (type) name.</param>
    /// <param name="id">Record id (GUID).</param>
    /// <param name="fields">JSON object of the fields to change, e.g. {"name":"Acme Corp"}.</param>
    /// <returns>The updated record as JSON, or "Error:" with the cause.</returns>
    public string UpdateRecord(string entity, string id, string fields)
    {
        Log.LogStep($"ErpTool.UpdateRecord: entity={entity} id={id}");
        if (string.IsNullOrWhiteSpace(entity)) return $"Error: 'entity' is required (see {Utility.ToSnakeCase(nameof(GetSchema))}).";
        if (!Guid.TryParse(id, out var gid)) return "Error: 'id' must be a valid GUID.";
        if (ParseFields(fields) is not JsonObject obj) return "Error: 'fields' must be a JSON object like {\"name\":\"Acme Corp\"}.";
        return Call("PATCH", $"records/{Uri.EscapeDataString(entity)}/{gid}", obj, "update");
    }

    /// <summary>Delete a record by id.</summary>
    /// <param name="entity">Entity (type) name.</param>
    /// <param name="id">Record id (GUID).</param>
    /// <returns>Confirmation message, or "Error:" with the cause.</returns>
    public string DeleteRecord(string entity, string id)
    {
        Log.LogStep($"ErpTool.DeleteRecord: entity={entity} id={id}");
        if (string.IsNullOrWhiteSpace(entity)) return $"Error: 'entity' is required (see {Utility.ToSnakeCase(nameof(GetSchema))}).";
        if (!Guid.TryParse(id, out var gid)) return "Error: 'id' must be a valid GUID.";
        return Call("DELETE", $"records/{Uri.EscapeDataString(entity)}/{gid}", null, "delete");
    }

    // ──────────────────────────────────────────────
    //  Many-to-many relations
    // ──────────────────────────────────────────────

    /// <summary>Add or remove a many-to-many link between two records.</summary>
    /// <param name="relationId">The many-to-many relation id (GUID).</param>
    /// <param name="originId">Origin record id (GUID).</param>
    /// <param name="targetId">Target record id (GUID).</param>
    /// <param name="remove">true to remove the link, false (default) to add it.</param>
    /// <returns>Confirmation message, or "Error:" with the cause.</returns>
    public string ManageRelation(string relationId, string originId, string targetId, bool remove = false)
    {
        Log.LogStep($"ErpTool.ManageRelation: remove={remove} relation={relationId}");
        if (!Guid.TryParse(relationId, out var rel) || !Guid.TryParse(originId, out var org) || !Guid.TryParse(targetId, out var tgt))
            return "Error: relationId, originId and targetId must all be valid GUIDs.";

        var body = new JsonObject
        {
            ["relationId"] = rel.ToString(),
            ["originId"] = org.ToString(),
            ["targetId"] = tgt.ToString(),
            ["remove"] = remove
        };
        return Call("POST", "relations", body, remove ? "remove relation" : "add relation");
    }

    // ──────────────────────────────────────────────
    //  HTTP + auth plumbing
    // ──────────────────────────────────────────────

    private static JsonNode? ParseFields(string fields)
    {
        if (string.IsNullOrWhiteSpace(fields)) return null;
        try { return JsonNode.Parse(fields); }
        catch { return null; }
    }

    private static string Call(string method, string path, JsonObject? body, string what)
    {
        ErpConfig.EnsureLoaded();
        if (!ErpConfig.IsConfigured)
            return "Error: no ERP connection is configured. Ask the user to configure the ERP connection.";

        try
        {
            EnsureToken();
            var (status, json) = Send(method, path, body);

            // One transparent re-auth on 401, then retry.
            if (status == HttpStatusCode.Unauthorized)
            {
                lock (AuthLock) { _token = null; }
                EnsureToken();
                (status, json) = Send(method, path, body);
            }

            if (status == HttpStatusCode.Unauthorized)
                return "Error: authentication failed against the ERP. Check the configured credentials.";
            if (status == HttpStatusCode.Forbidden)
                return $"Error: not permitted to {what} — the ERP user lacks the required permission.";
            if ((int)status >= 400)
                return $"Error: {what} failed (HTTP {(int)status}). {ExtractError(json)}";

            return RenderData(json);
        }
        catch (HttpRequestException ex)
        {
            Log.LogStep($"ErpTool.Call: network error — {ex.Message}");
            return $"Error: cannot reach the ERP. {ex.Message}";
        }
        catch (Exception ex)
        {
            Log.LogStep($"ErpTool.Call: {what} error — {ex.Message}");
            return $"Error: {what} failed — {ex.Message}";
        }
    }

    private static void EnsureToken()
    {
        if (!string.IsNullOrEmpty(_token)) return;
        lock (AuthLock)
        {
            if (!string.IsNullOrEmpty(_token)) return;
            var login = new JsonObject { ["email"] = ErpConfig.User ?? "", ["password"] = ErpConfig.Password ?? "" };
            var (status, json) = SendRaw("POST", "api/v3/en_US/auth/jwt/token", login, bearer: null);
            if (status != HttpStatusCode.OK)
                throw new InvalidOperationException($"ERP login failed (HTTP {(int)status}). {ExtractError(json)}");
            _token = json?["object"]?.ToString()
                     ?? throw new InvalidOperationException("ERP login returned no token.");
            Log.LogStep("ErpTool: authenticated to the ERP");
        }
    }

    private static (HttpStatusCode, JsonNode?) Send(string method, string path, JsonObject? body)
        => SendRaw(method, "api/v3.0/p/agent/" + path, body, bearer: _token);

    private static (HttpStatusCode, JsonNode?) SendRaw(string method, string url, JsonObject? body, string? bearer)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), $"{ErpConfig.BaseUrl}/{url}");
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = Http.Send(req);
        var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        JsonNode? parsed = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { parsed = JsonNode.Parse(text); } catch { /* non-JSON body handled by caller via status */ }
        }
        return (resp.StatusCode, parsed);
    }

    private static string ExtractError(JsonNode? json)
    {
        if (json is not JsonObject obj) return "";
        var err = obj["error"]?.ToString();
        if (!string.IsNullOrWhiteSpace(err)) return err;
        if (obj["errors"] is JsonArray errors && errors.Count > 0)
            return string.Join("; ", errors.Select(e => e?["message"]?.ToString() ?? e?.ToString() ?? ""));
        return obj["message"]?.ToString() ?? "";
    }

    private static string RenderData(JsonNode? json)
    {
        if (json is not JsonObject obj) return "{}";
        var data = obj["data"];
        return data?.ToJsonString(Indented) ?? obj.ToJsonString(Indented);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
}
