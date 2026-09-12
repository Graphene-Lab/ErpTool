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
    //  Composed business operations (one call = many steps)
    // ──────────────────────────────────────────────

    /// <summary>Create a sales order with its line items in one call. Product prices are read from the ERP and totals computed for you.</summary>
    /// <param name="customerId">Customer record id (GUID).</param>
    /// <param name="lines">JSON array of line items, e.g. [{"sku":"SKU-1001","quantity":3},{"sku":"SKU-2001","quantity":10,"discountPercent":5}]. Optional "unitPrice" overrides the catalog price.</param>
    /// <param name="orderDate">Optional order date (yyyy-MM-dd); defaults to today.</param>
    /// <param name="requiredDate">Optional required date (yyyy-MM-dd).</param>
    /// <param name="currency">Optional currency code; defaults to EUR.</param>
    /// <returns>JSON with the created order id, order_number, total and lines, or "Error:" with the cause.</returns>
    public string PlaceSalesOrder(string customerId, string lines, string? orderDate = null, string? requiredDate = null, string? currency = null)
    {
        Log.LogStep($"ErpTool.PlaceSalesOrder: customer={customerId}");
        if (!Guid.TryParse(customerId, out var cid)) return "Error: 'customerId' must be a valid GUID.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":2}].";
        var body = new JsonObject { ["customer_id"] = cid.ToString(), ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(orderDate)) body["order_date"] = orderDate;
        if (!string.IsNullOrWhiteSpace(requiredDate)) body["required_date"] = requiredDate;
        if (!string.IsNullOrWhiteSpace(currency)) body["currency"] = currency;
        return Call("POST", "composed/sales-order", body, "place sales order");
    }

    /// <summary>Turn an existing sales order into a sent invoice in one call.</summary>
    /// <param name="orderId">Sales order id (GUID).</param>
    /// <param name="dueDays">Days until the invoice is due; defaults to 30.</param>
    /// <returns>JSON with the invoice id, number, amount and due date, or "Error:" with the cause.</returns>
    public string InvoiceSalesOrder(string orderId, int dueDays = 30)
    {
        Log.LogStep($"ErpTool.InvoiceSalesOrder: order={orderId}");
        if (!Guid.TryParse(orderId, out var oid)) return "Error: 'orderId' must be a valid GUID.";
        var body = new JsonObject { ["order_id"] = oid.ToString(), ["due_days"] = dueDays };
        return Call("POST", "composed/invoice", body, "create invoice");
    }

    /// <summary>Record a payment against an invoice and update its status (paid/partial) in one call.</summary>
    /// <param name="invoiceId">Invoice id (GUID).</param>
    /// <param name="amount">Payment amount.</param>
    /// <param name="method">Payment method: bank, card or cash; defaults to bank.</param>
    /// <param name="paymentDate">Optional payment date (yyyy-MM-dd); defaults to today.</param>
    /// <returns>JSON with the payment id, new invoice status, paid total and balance, or "Error:" with the cause.</returns>
    public string RecordPayment(string invoiceId, decimal amount, string? method = null, string? paymentDate = null)
    {
        Log.LogStep($"ErpTool.RecordPayment: invoice={invoiceId} amount={amount}");
        if (!Guid.TryParse(invoiceId, out var iid)) return "Error: 'invoiceId' must be a valid GUID.";
        var body = new JsonObject { ["invoice_id"] = iid.ToString(), ["amount"] = amount };
        if (!string.IsNullOrWhiteSpace(method)) body["method"] = method;
        if (!string.IsNullOrWhiteSpace(paymentDate)) body["payment_date"] = paymentDate;
        return Call("POST", "composed/payment", body, "record payment");
    }

    /// <summary>Create a purchase order with its line items in one call.</summary>
    /// <param name="supplierId">Supplier record id (GUID).</param>
    /// <param name="lines">JSON array of line items, e.g. [{"sku":"SKU-1001","quantity":50,"unitCost":9.0}]. Optional "unitCost" overrides the catalog cost.</param>
    /// <param name="orderDate">Optional order date (yyyy-MM-dd); defaults to today.</param>
    /// <param name="expectedDate">Optional expected delivery date (yyyy-MM-dd).</param>
    /// <returns>JSON with the purchase order id, number, total and lines, or "Error:" with the cause.</returns>
    public string PlacePurchaseOrder(string supplierId, string lines, string? orderDate = null, string? expectedDate = null)
    {
        Log.LogStep($"ErpTool.PlacePurchaseOrder: supplier={supplierId}");
        if (!Guid.TryParse(supplierId, out var sid)) return "Error: 'supplierId' must be a valid GUID.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":50}].";
        var body = new JsonObject { ["supplier_id"] = sid.ToString(), ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(orderDate)) body["order_date"] = orderDate;
        if (!string.IsNullOrWhiteSpace(expectedDate)) body["expected_date"] = expectedDate;
        return Call("POST", "composed/purchase-order", body, "place purchase order");
    }

    /// <summary>Receive a purchase order: add each line's quantity to product stock and mark the order received, in one call.</summary>
    /// <param name="purchaseOrderId">Purchase order id (GUID).</param>
    /// <returns>JSON with the updated stock per product, or "Error:" with the cause.</returns>
    public string ReceivePurchaseOrder(string purchaseOrderId)
    {
        Log.LogStep($"ErpTool.ReceivePurchaseOrder: po={purchaseOrderId}");
        if (!Guid.TryParse(purchaseOrderId, out var pid)) return "Error: 'purchaseOrderId' must be a valid GUID.";
        var body = new JsonObject { ["purchase_order_id"] = pid.ToString() };
        return Call("POST", "composed/receive", body, "receive purchase order");
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
