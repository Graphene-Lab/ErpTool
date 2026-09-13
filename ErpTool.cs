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
    //  Setup / install (headless provisioning)
    // ──────────────────────────────────────────────

    /// <summary>Check whether the ERP is installed and configured. Reports if the setup (bootstrap) has been applied, the number of business entities, the record count of each seeded entity, and whether a reprovision would change anything (pending). Read-only.</summary>
    /// <returns>JSON with installed, bootstrap_present, applied_hash, current_hash, pending, applied_on, entity_count and seed_counts, or "Error:" with the cause.</returns>
    public string SetupStatus()
    {
        Log.LogStep("ErpTool.SetupStatus");
        return Call("GET", "setup-status", null, "read setup status");
    }

    /// <summary>Re-apply the ERP setup (bootstrap) now, in one call. Use after the setup file changes to push new schema or seed without restarting the ERP. Idempotent: if nothing changed it returns "already applied" and touches nothing.</summary>
    /// <returns>JSON with a summary of what was applied (or "already applied"), or "Error:" with the cause.</returns>
    public string Reprovision()
    {
        Log.LogStep("ErpTool.Reprovision");
        return Call("POST", "reprovision", new JsonObject(), "reprovision");
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
    /// <param name="paymentMethod">Payment method: bank, card or cash; defaults to bank. (Named paymentMethod, not method, to avoid colliding with the agent's reserved method dispatch key.)</param>
    /// <param name="paymentDate">Optional payment date (yyyy-MM-dd); defaults to today.</param>
    /// <returns>JSON with the payment id, new invoice status, paid total and balance, or "Error:" with the cause.</returns>
    public string RecordPayment(string invoiceId, decimal amount, string? paymentMethod = null, string? paymentDate = null)
    {
        Log.LogStep($"ErpTool.RecordPayment: invoice={invoiceId} amount={amount}");
        if (!Guid.TryParse(invoiceId, out var iid)) return "Error: 'invoiceId' must be a valid GUID.";
        var body = new JsonObject { ["invoice_id"] = iid.ToString(), ["amount"] = amount };
        if (!string.IsNullOrWhiteSpace(paymentMethod)) body["method"] = paymentMethod;
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

    /// <summary>Receive goods against a purchase order: add each line's quantity to a warehouse's stock, record any variance vs ordered, update the PO status, in one call.</summary>
    /// <param name="purchaseOrderId">Purchase order id (GUID).</param>
    /// <param name="lines">JSON array of receipt lines, e.g. [{"sku":"SKU-1001","quantity":50,"unitCost":9.0,"lot":"L1"}].</param>
    /// <param name="warehouseCode">Warehouse code to receive into (e.g. "WH1"); omit to use the PO's default warehouse.</param>
    /// <returns>JSON with the receipt id/number, per-line variance and the new PO status, or "Error:" with the cause.</returns>
    public string ReceivePurchaseOrder(string purchaseOrderId, string lines, string? warehouseCode = null)
    {
        Log.LogStep($"ErpTool.ReceivePurchaseOrder: po={purchaseOrderId}");
        if (!Guid.TryParse(purchaseOrderId, out var pid)) return "Error: 'purchaseOrderId' must be a valid GUID.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":50}].";
        var body = new JsonObject { ["purchase_order_id"] = pid.ToString(), ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(warehouseCode)) body["warehouse_code"] = warehouseCode;
        return Call("POST", "composed/receive", body, "receive purchase order");
    }

    /// <summary>Create a quote (preventivo) with lines and VAT-inclusive totals in one call.</summary>
    /// <param name="customerId">Customer record id (GUID).</param>
    /// <param name="lines">JSON array of line items, e.g. [{"sku":"SKU-1001","quantity":3,"unitPrice":12.5,"discountPercent":5}]. Optional "vatCode" overrides the product's VAT.</param>
    /// <param name="validUntil">Optional validity date (yyyy-MM-dd).</param>
    /// <param name="currency">Optional currency code; defaults to the customer's currency.</param>
    /// <returns>JSON with the quote id/number and totals, or "Error:" with the cause.</returns>
    public string CreateQuote(string customerId, string lines, string? validUntil = null, string? currency = null)
    {
        Log.LogStep($"ErpTool.CreateQuote: customer={customerId}");
        if (!Guid.TryParse(customerId, out var cid)) return "Error: 'customerId' must be a valid GUID.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":2}].";
        var body = new JsonObject { ["customer_id"] = cid.ToString(), ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(validUntil)) body["valid_until"] = validUntil;
        if (!string.IsNullOrWhiteSpace(currency)) body["currency"] = currency;
        return Call("POST", "composed/quote", body, "create quote");
    }

    /// <summary>Convert an accepted quote into a confirmed sales order, copying its lines, in one call.</summary>
    /// <param name="quoteId">Quote id (GUID).</param>
    /// <returns>JSON with the new order id/number and totals, or "Error:" with the cause.</returns>
    public string ConvertQuoteToOrder(string quoteId)
    {
        Log.LogStep($"ErpTool.ConvertQuoteToOrder: quote={quoteId}");
        if (!Guid.TryParse(quoteId, out var qid)) return "Error: 'quoteId' must be a valid GUID.";
        var body = new JsonObject { ["quote_id"] = qid.ToString() };
        return Call("POST", "composed/quote-to-order", body, "convert quote to order");
    }

    /// <summary>Deliver a sales order (create a DDT): decrement stock, block over-delivery and negative stock, in one call.</summary>
    /// <param name="orderId">Sales order id (GUID).</param>
    /// <param name="lines">JSON array of delivery lines, e.g. [{"sku":"SKU-1001","quantity":2,"lot":"L1"}].</param>
    /// <param name="warehouseCode">Warehouse code to deliver from (e.g. "WH1"); omit to use the order's default warehouse.</param>
    /// <returns>JSON with the delivery id/number and the new order status, or "Error:" with the cause.</returns>
    public string DeliverSalesOrder(string orderId, string lines, string? warehouseCode = null)
    {
        Log.LogStep($"ErpTool.DeliverSalesOrder: order={orderId}");
        if (!Guid.TryParse(orderId, out var oid)) return "Error: 'orderId' must be a valid GUID.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":2}].";
        var body = new JsonObject { ["order_id"] = oid.ToString(), ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(warehouseCode)) body["warehouse_code"] = warehouseCode;
        return Call("POST", "composed/deliver", body, "deliver sales order");
    }

    /// <summary>Issue a credit note against a customer invoice, reducing its outstanding balance, in one call.</summary>
    /// <param name="invoiceId">Invoice id (GUID).</param>
    /// <param name="amount">Credit amount (positive).</param>
    /// <param name="reason">Optional reason for the credit.</param>
    /// <returns>JSON with the credit note id/number and the invoice's new status/outstanding, or "Error:" with the cause.</returns>
    public string CreateCreditNote(string invoiceId, decimal amount, string? reason = null)
    {
        Log.LogStep($"ErpTool.CreateCreditNote: invoice={invoiceId} amount={amount}");
        if (!Guid.TryParse(invoiceId, out var iid)) return "Error: 'invoiceId' must be a valid GUID.";
        if (amount <= 0) return "Error: 'amount' must be positive.";
        var body = new JsonObject { ["invoice_id"] = iid.ToString(), ["amount"] = amount };
        if (!string.IsNullOrWhiteSpace(reason)) body["reason"] = reason;
        return Call("POST", "composed/credit-note", body, "create credit note");
    }

    /// <summary>Create a purchase request (richiesta d'acquisto) with lines, in one call.</summary>
    /// <param name="lines">JSON array of line items, e.g. [{"sku":"SKU-1001","quantity":10,"estimatedCost":4.0}].</param>
    /// <param name="requestedBy">Optional requester label.</param>
    /// <returns>JSON with the request id/number and lines, or "Error:" with the cause.</returns>
    public string CreatePurchaseRequest(string lines, string? requestedBy = null)
    {
        Log.LogStep("ErpTool.CreatePurchaseRequest");
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":10}].";
        var body = new JsonObject { ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(requestedBy)) body["requested_by"] = requestedBy;
        return Call("POST", "composed/purchase-request", body, "create purchase request");
    }

    /// <summary>Convert an approved purchase request into a purchase order for a supplier, in one call.</summary>
    /// <param name="requestId">Purchase request id (GUID).</param>
    /// <param name="supplierId">Supplier record id (GUID).</param>
    /// <returns>JSON with the new PO id/number and totals, or "Error:" with the cause.</returns>
    public string ConvertRequestToPurchaseOrder(string requestId, string supplierId)
    {
        Log.LogStep($"ErpTool.ConvertRequestToPurchaseOrder: request={requestId} supplier={supplierId}");
        if (!Guid.TryParse(requestId, out var rid)) return "Error: 'requestId' must be a valid GUID.";
        if (!Guid.TryParse(supplierId, out var sid)) return "Error: 'supplierId' must be a valid GUID.";
        var body = new JsonObject { ["request_id"] = rid.ToString(), ["supplier_id"] = sid.ToString() };
        return Call("POST", "composed/request-to-po", body, "convert request to purchase order");
    }

    /// <summary>Register a supplier bill against a purchase order and run the triple-match check (ordered vs received vs billed), in one call.</summary>
    /// <param name="purchaseOrderId">Purchase order id (GUID).</param>
    /// <param name="lines">JSON array of bill lines, e.g. [{"sku":"SKU-1001","quantity":50,"unitCost":9.0}].</param>
    /// <param name="billDate">Optional bill date (yyyy-MM-dd); defaults to today.</param>
    /// <param name="dueDate">Optional due date (yyyy-MM-dd); defaults to 30 days.</param>
    /// <returns>JSON with the bill id/number, totals and triple_match_status (matched/mismatched with details), or "Error:" with the cause.</returns>
    public string RegisterPurchaseInvoice(string purchaseOrderId, string lines, string? billDate = null, string? dueDate = null)
    {
        Log.LogStep($"ErpTool.RegisterPurchaseInvoice: po={purchaseOrderId}");
        if (!Guid.TryParse(purchaseOrderId, out var pid)) return "Error: 'purchaseOrderId' must be a valid GUID.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":50}].";
        var body = new JsonObject { ["purchase_order_id"] = pid.ToString(), ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(billDate)) body["bill_date"] = billDate;
        if (!string.IsNullOrWhiteSpace(dueDate)) body["due_date"] = dueDate;
        return Call("POST", "composed/purchase-invoice", body, "register purchase invoice");
    }

    /// <summary>Pay a supplier bill and update its status (paid/partial), in one call.</summary>
    /// <param name="billId">Purchase invoice (bill) id (GUID).</param>
    /// <param name="amount">Payment amount (positive).</param>
    /// <param name="paymentMethod">Payment method: bank, card or cash; defaults to bank. (Named paymentMethod, not method, to avoid colliding with the agent's reserved method dispatch key.)</param>
    /// <param name="paymentDate">Optional payment date (yyyy-MM-dd); defaults to today.</param>
    /// <returns>JSON with the payment id, new bill status, paid total and balance, or "Error:" with the cause.</returns>
    public string PaySupplierBill(string billId, decimal amount, string? paymentMethod = null, string? paymentDate = null)
    {
        Log.LogStep($"ErpTool.PaySupplierBill: bill={billId} amount={amount}");
        if (!Guid.TryParse(billId, out var bid)) return "Error: 'billId' must be a valid GUID.";
        if (amount <= 0) return "Error: 'amount' must be positive.";
        var body = new JsonObject { ["bill_id"] = bid.ToString(), ["amount"] = amount };
        if (!string.IsNullOrWhiteSpace(paymentMethod)) body["method"] = paymentMethod;
        if (!string.IsNullOrWhiteSpace(paymentDate)) body["payment_date"] = paymentDate;
        return Call("POST", "composed/supplier-payment", body, "pay supplier bill");
    }

    /// <summary>Issue a credit note from a supplier against a bill, in one call.</summary>
    /// <param name="billId">Purchase invoice (bill) id (GUID).</param>
    /// <param name="amount">Credit amount (positive).</param>
    /// <param name="reason">Optional reason for the credit.</param>
    /// <returns>JSON with the supplier credit note id/number and amount, or "Error:" with the cause.</returns>
    public string CreateSupplierCreditNote(string billId, decimal amount, string? reason = null)
    {
        Log.LogStep($"ErpTool.CreateSupplierCreditNote: bill={billId} amount={amount}");
        if (!Guid.TryParse(billId, out var bid)) return "Error: 'billId' must be a valid GUID.";
        if (amount <= 0) return "Error: 'amount' must be positive.";
        var body = new JsonObject { ["bill_id"] = bid.ToString(), ["amount"] = amount };
        if (!string.IsNullOrWhiteSpace(reason)) body["reason"] = reason;
        return Call("POST", "composed/supplier-credit-note", body, "create supplier credit note");
    }

    /// <summary>Transfer stock of a product between two warehouses, in one call.</summary>
    /// <param name="sku">Product SKU.</param>
    /// <param name="fromWarehouse">Source warehouse code (e.g. "WH1").</param>
    /// <param name="toWarehouse">Destination warehouse code (e.g. "WH2").</param>
    /// <param name="quantity">Quantity to transfer (positive).</param>
    /// <returns>JSON with the transfer reference and warehouses, or "Error:" with the cause.</returns>
    public string TransferStock(string sku, string fromWarehouse, string toWarehouse, decimal quantity)
    {
        Log.LogStep($"ErpTool.TransferStock: {sku} {fromWarehouse}->{toWarehouse} qty={quantity}");
        if (string.IsNullOrWhiteSpace(sku)) return "Error: 'sku' is required.";
        if (string.IsNullOrWhiteSpace(fromWarehouse) || string.IsNullOrWhiteSpace(toWarehouse)) return "Error: 'fromWarehouse' and 'toWarehouse' are required.";
        if (quantity <= 0) return "Error: 'quantity' must be positive.";
        var body = new JsonObject { ["sku"] = sku, ["from_warehouse"] = fromWarehouse, ["to_warehouse"] = toWarehouse, ["quantity"] = quantity };
        return Call("POST", "composed/transfer-stock", body, "transfer stock");
    }

    /// <summary>Manually adjust a product's stock in a warehouse to a new quantity (records the delta), in one call.</summary>
    /// <param name="sku">Product SKU.</param>
    /// <param name="warehouseCode">Warehouse code (e.g. "WH1").</param>
    /// <param name="newQuantity">The new stock quantity to set.</param>
    /// <param name="reason">Optional reason (e.g. "stock count").</param>
    /// <returns>JSON with old/new quantity and delta, or "Error:" with the cause.</returns>
    public string AdjustStock(string sku, string warehouseCode, decimal newQuantity, string? reason = null)
    {
        Log.LogStep($"ErpTool.AdjustStock: {sku} @ {warehouseCode} -> {newQuantity}");
        if (string.IsNullOrWhiteSpace(sku)) return "Error: 'sku' is required.";
        if (string.IsNullOrWhiteSpace(warehouseCode)) return "Error: 'warehouseCode' is required.";
        var body = new JsonObject { ["sku"] = sku, ["warehouse_code"] = warehouseCode, ["new_quantity"] = newQuantity };
        if (!string.IsNullOrWhiteSpace(reason)) body["reason"] = reason;
        return Call("POST", "composed/adjust-stock", body, "adjust stock");
    }

    /// <summary>Create a customer with billing and shipping addresses and contacts in one call.</summary>
    /// <param name="name">Customer name (required, unique).</param>
    /// <param name="billingAddress">Billing address as a JSON object, e.g. {"line1":"Via Roma 1","city":"Milan","country":"Italy","postal_code":"20100"}.</param>
    /// <param name="shipAddress">Optional shipping address as a JSON object (same shape). Omit to skip the ship address.</param>
    /// <param name="contacts">Optional JSON array of contacts, e.g. [{"name":"Procurement Desk","role":"buyer","email":"buy@x.example","phone":"+39 02 000"}]. The first is marked primary.</param>
    /// <param name="email">Optional customer email.</param>
    /// <param name="currency">Optional currency code; defaults to EUR.</param>
    /// <returns>JSON with the customer id, name, addresses and contacts, or "Error:" with the cause.</returns>
    public string CreateCustomerProfile(string name, string billingAddress, string? shipAddress = null, string? contacts = null, string? email = null, string? currency = null)
    {
        Log.LogStep($"ErpTool.CreateCustomerProfile: name={name}");
        if (string.IsNullOrWhiteSpace(name)) return "Error: 'name' is required.";
        if (JsonNode.Parse(billingAddress) is not JsonObject billing) return "Error: 'billingAddress' must be a JSON object like {\"line1\":\"...\",\"city\":\"...\",\"country\":\"...\"}.";
        var body = new JsonObject { ["name"] = name, ["billing"] = billing };
        if (!string.IsNullOrWhiteSpace(shipAddress))
        {
            if (JsonNode.Parse(shipAddress) is not JsonObject ship) return "Error: 'shipAddress' must be a JSON object like {\"line1\":\"...\",\"city\":\"...\",\"country\":\"...\"}.";
            body["ship"] = ship;
        }
        if (!string.IsNullOrWhiteSpace(contacts))
        {
            if (JsonNode.Parse(contacts) is not JsonArray ca) return "Error: 'contacts' must be a JSON array like [{\"name\":\"...\",\"email\":\"...\"}].";
            body["contacts"] = ca;
        }
        if (!string.IsNullOrWhiteSpace(email)) body["email"] = email;
        if (!string.IsNullOrWhiteSpace(currency)) body["currency"] = currency;
        return Call("POST", "composed/customer-profile", body, "create customer profile");
    }

    /// <summary>Add barcodes and suppliers to an existing product (by SKU) in one call. Duplicate barcode codes are skipped.</summary>
    /// <param name="sku">Product SKU (must already exist).</param>
    /// <param name="barcodes">Optional JSON array of barcodes, e.g. [{"code":"8012345678901","barcode_type":"ean13"}]. barcode_type: ean13, ean8, upc, internal.</param>
    /// <param name="suppliers">Optional JSON array of suppliers, e.g. [{"supplier_name":"Prime Coffee Importers","supplier_sku":"PCI-1K","lead_time_days":14,"unit_cost":8.5,"is_default":true}].</param>
    /// <returns>JSON with the SKU and how many barcodes/suppliers were added, or "Error:" with the cause.</returns>
    public string EnrichProduct(string sku, string? barcodes = null, string? suppliers = null)
    {
        Log.LogStep($"ErpTool.EnrichProduct: sku={sku}");
        if (string.IsNullOrWhiteSpace(sku)) return "Error: 'sku' is required.";
        var body = new JsonObject { ["sku"] = sku };
        if (!string.IsNullOrWhiteSpace(barcodes))
        {
            if (JsonNode.Parse(barcodes) is not JsonArray ba) return "Error: 'barcodes' must be a JSON array like [{\"code\":\"...\",\"barcode_type\":\"ean13\"}].";
            body["barcodes"] = ba;
        }
        if (!string.IsNullOrWhiteSpace(suppliers))
        {
            if (JsonNode.Parse(suppliers) is not JsonArray sa) return "Error: 'suppliers' must be a JSON array like [{\"supplier_name\":\"...\",\"supplier_sku\":\"...\",\"lead_time_days\":14,\"unit_cost\":8.5}].";
            body["suppliers"] = sa;
        }
        return Call("POST", "composed/enrich-product", body, "enrich product");
    }

    /// <summary>Resolve the effective unit price for a SKU for a given customer and quantity: price-list base for the customer's category, the best matching discount rule, and currency conversion.</summary>
    /// <param name="sku">Product SKU.</param>
    /// <param name="customerName">Customer name (used for category, currency and customer-scoped discount rules).</param>
    /// <param name="quantity">Order quantity (drives quantity-gated discount rules).</param>
    /// <returns>JSON with base_price, discount_percent, unit_price and currency, or "Error:" with the cause.</returns>
    public string ResolvePrice(string sku, string customerName, decimal quantity)
    {
        Log.LogStep($"ErpTool.ResolvePrice: sku={sku} customer={customerName} qty={quantity}");
        if (string.IsNullOrWhiteSpace(sku)) return "Error: 'sku' is required.";
        if (string.IsNullOrWhiteSpace(customerName)) return "Error: 'customerName' is required.";
        var body = new JsonObject { ["sku"] = sku, ["customer_name"] = customerName, ["quantity"] = quantity };
        return Call("POST", "composed/resolve-price", body, "resolve price");
    }

    /// <summary>Place a sales order by customer name with confirmation/planned-delivery dates, transport terms and a sales agent, in one call.</summary>
    /// <param name="customerName">Customer name (must exist and not be blocked).</param>
    /// <param name="lines">JSON array of line items, e.g. [{"sku":"SKU-1001","quantity":3}]. Optional "unitPrice" overrides the catalog price.</param>
    /// <param name="confirmedDate">Optional confirmation date (yyyy-MM-dd).</param>
    /// <param name="plannedDate">Optional planned delivery date (yyyy-MM-dd).</param>
    /// <param name="transportTerms">Optional transport terms (e.g. "FCA").</param>
    /// <param name="agentName">Optional sales agent name (resolved to a sales_agent record).</param>
    /// <returns>JSON with the order id/number and the terms/agent, or "Error:" with the cause.</returns>
    public string CreateOrderWithTerms(string customerName, string lines, string? confirmedDate = null, string? plannedDate = null, string? transportTerms = null, string? agentName = null)
    {
        Log.LogStep($"ErpTool.CreateOrderWithTerms: customer={customerName}");
        if (string.IsNullOrWhiteSpace(customerName)) return "Error: 'customerName' is required.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":2}].";
        var body = new JsonObject { ["customer_name"] = customerName, ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(confirmedDate)) body["confirmed_date"] = confirmedDate;
        if (!string.IsNullOrWhiteSpace(plannedDate)) body["planned_date"] = plannedDate;
        if (!string.IsNullOrWhiteSpace(transportTerms)) body["transport_terms"] = transportTerms;
        if (!string.IsNullOrWhiteSpace(agentName)) body["agent_name"] = agentName;
        return Call("POST", "composed/order-with-terms", body, "create order with terms");
    }

    /// <summary>Deliver a sales order (create a DDT) with transport details recorded on it, in one call. Same validation and stock logic as deliver_sales_order.</summary>
    /// <param name="orderId">Sales order id (GUID).</param>
    /// <param name="lines">JSON array of delivery lines, e.g. [{"sku":"SKU-1001","quantity":2,"lot":"L1"}].</param>
    /// <param name="warehouseCode">Warehouse code to deliver from (e.g. "WH1"); omit to use the order's default warehouse.</param>
    /// <param name="transportCause">Optional transport cause (e.g. "vendita").</param>
    /// <param name="carrier">Optional carrier (e.g. "DHL").</param>
    /// <param name="trackingNumber">Optional tracking number.</param>
    /// <returns>JSON with the delivery id/number, the new order status and the transport fields, or "Error:" with the cause.</returns>
    public string DeliverWithTransport(string orderId, string lines, string? warehouseCode = null, string? transportCause = null, string? carrier = null, string? trackingNumber = null)
    {
        Log.LogStep($"ErpTool.DeliverWithTransport: order={orderId}");
        if (!Guid.TryParse(orderId, out var oid)) return "Error: 'orderId' must be a valid GUID.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":2}].";
        var body = new JsonObject { ["order_id"] = oid.ToString(), ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(warehouseCode)) body["warehouse_code"] = warehouseCode;
        if (!string.IsNullOrWhiteSpace(transportCause)) body["transport_cause"] = transportCause;
        if (!string.IsNullOrWhiteSpace(carrier)) body["carrier"] = carrier;
        if (!string.IsNullOrWhiteSpace(trackingNumber)) body["tracking_number"] = trackingNumber;
        return Call("POST", "composed/deliver-transport", body, "deliver with transport");
    }

    /// <summary>Consolidate the delivered-but-not-yet-invoiced lines of several sales orders (all for the same customer) into a single invoice, in one call.</summary>
    /// <param name="orderIds">JSON array of sales order GUIDs, e.g. ["order-guid-1","order-guid-2"]. All must belong to the same customer.</param>
    /// <param name="dueDays">Days until the invoice is due; defaults to 30.</param>
    /// <returns>JSON with the invoice id/number, order_count and grand_total, or "Error:" with the cause.</returns>
    public string ConsolidateInvoice(string orderIds, int dueDays = 30)
    {
        Log.LogStep("ErpTool.ConsolidateInvoice");
        if (JsonNode.Parse(orderIds) is not JsonArray arr || arr.Count == 0) return "Error: 'orderIds' must be a non-empty JSON array of order GUIDs like [\"<guid>\"].";
        var body = new JsonObject { ["order_ids"] = arr, ["due_days"] = dueDays };
        return Call("POST", "composed/consolidate-invoice", body, "consolidate invoice");
    }

    /// <summary>Run a cycle count in a warehouse: record expected vs counted per SKU and apply the variance to stock, in one call.</summary>
    /// <param name="warehouseCode">Warehouse code (e.g. "WH1").</param>
    /// <param name="lines">JSON array of count lines, e.g. [{"sku":"SKU-1001","counted_qty":247}].</param>
    /// <returns>JSON with the count id and per-line expected/counted/variance, or "Error:" with the cause.</returns>
    public string RunCycleCount(string warehouseCode, string lines)
    {
        Log.LogStep($"ErpTool.RunCycleCount: warehouse={warehouseCode}");
        if (string.IsNullOrWhiteSpace(warehouseCode)) return "Error: 'warehouseCode' is required.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"counted_qty\":247}].";
        var body = new JsonObject { ["warehouse_code"] = warehouseCode, ["lines"] = arr };
        return Call("POST", "composed/cycle-count", body, "run cycle count");
    }

    /// <summary>Process a customer return against a delivery: record the return and restock (or scrap) each line, in one call.</summary>
    /// <param name="deliveryId">Original goods delivery id (GUID).</param>
    /// <param name="lines">JSON array of return lines, e.g. [{"sku":"SKU-1001","quantity":2,"disposition":"restock"}]. disposition: "restock" (adds to sellable stock) or "scrap" (recorded only).</param>
    /// <param name="warehouseCode">Warehouse to restock into (e.g. "WH1"); omit to use the delivery's warehouse.</param>
    /// <returns>JSON with the return id, customer id and the restocked/scrapped lines, or "Error:" with the cause.</returns>
    public string ProcessCustomerReturn(string deliveryId, string lines, string? warehouseCode = null)
    {
        Log.LogStep($"ErpTool.ProcessCustomerReturn: delivery={deliveryId}");
        if (!Guid.TryParse(deliveryId, out var did)) return "Error: 'deliveryId' must be a valid GUID.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":2,\"disposition\":\"restock\"}].";
        var body = new JsonObject { ["delivery_id"] = did.ToString(), ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(warehouseCode)) body["warehouse_code"] = warehouseCode;
        return Call("POST", "composed/customer-return", body, "process customer return");
    }

    /// <summary>Create a sales invoice from scratch (no order) with a document discount, accessory/transport lines, a stamp duty, a withholding, optional reverse charge / tax-document type, an optional currency and an installment plan, in one call.</summary>
    /// <param name="customerName">Customer name (must exist and not be blocked).</param>
    /// <param name="lines">JSON array of product lines, e.g. [{"sku":"SKU-1001","quantity":2,"unit_price":18.50,"vatCode":"V10"}]. "unit_price" optional (falls back to catalog); "discount_percent" and "vatCode" optional.</param>
    /// <param name="documentDiscount">Document discount percent applied to the product subtotal.</param>
    /// <param name="accessoryLines">JSON array of accessory/transport lines, e.g. [{"description":"Freight","amount":12.00,"vatCode":"V22","type":"transport"}]. type: "accessory" (default) or "transport".</param>
    /// <param name="stampTax">Stamp duty (bollo), added to the grand total, not VAT-able.</param>
    /// <param name="withholdingPercent">Withholding (ritenuta d'acconto) percent on the discounted subtotal.</param>
    /// <param name="reverseCharge">true to zero VAT (customer self-accounts).</param>
    /// <param name="taxDocumentType">taxable | non_taxable | exempt | out_of_scope | intra_community | extra_community.</param>
    /// <param name="currency">Target currency; EUR amounts are converted. Defaults to the customer's currency / EUR.</param>
    /// <param name="installments">JSON array of installments, e.g. [{"due_date":"2026-10-15","amount":46.22}]. Omit for a single balance due in 30 days.</param>
    /// <returns>JSON with invoice id/number, subtotal, document_discount, vat_total, stamp_tax, withholding_amount, grand_total, currency and installments, or "Error:" with the cause.</returns>
    public string CreateSalesInvoice(string customerName, string lines, decimal? documentDiscount = null, string? accessoryLines = null, decimal? stampTax = null, decimal? withholdingPercent = null, bool reverseCharge = false, string? taxDocumentType = null, string? currency = null, string? installments = null)
    {
        Log.LogStep($"ErpTool.CreateSalesInvoice: customer={customerName}");
        if (string.IsNullOrWhiteSpace(customerName)) return "Error: 'customerName' is required.";
        if (JsonNode.Parse(lines) is not JsonArray la || la.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":2,\"unit_price\":18.5}].";
        var body = new JsonObject { ["customer_name"] = customerName, ["lines"] = la };
        if (documentDiscount.HasValue) body["document_discount"] = documentDiscount.Value;
        if (!string.IsNullOrWhiteSpace(accessoryLines))
        {
            if (JsonNode.Parse(accessoryLines) is not JsonArray aa) return "Error: 'accessoryLines' must be a JSON array like [{\"description\":\"Freight\",\"amount\":12.0,\"type\":\"transport\"}].";
            body["accessory_lines"] = aa;
        }
        if (stampTax.HasValue) body["stamp_tax"] = stampTax.Value;
        if (withholdingPercent.HasValue) body["withholding_percent"] = withholdingPercent.Value;
        body["reverse_charge"] = reverseCharge;
        if (!string.IsNullOrWhiteSpace(taxDocumentType)) body["tax_document_type"] = taxDocumentType;
        if (!string.IsNullOrWhiteSpace(currency)) body["currency"] = currency;
        if (!string.IsNullOrWhiteSpace(installments))
        {
            if (JsonNode.Parse(installments) is not JsonArray ia) return "Error: 'installments' must be a JSON array like [{\"due_date\":\"2026-10-15\",\"amount\":46.22}].";
            body["installments"] = ia;
        }
        return Call("POST", "composed/sales-invoice", body, "create sales invoice");
    }

    /// <summary>Create a supplier bill (purchase invoice) from scratch with a document discount and a stamp duty, in one call.</summary>
    /// <param name="supplierName">Supplier name (must exist and not be blocked).</param>
    /// <param name="lines">JSON array of bill lines, e.g. [{"sku":"SKU-1001","quantity":50,"unit_cost":9.0}]. "unit_cost" optional (falls back to catalog cost).</param>
    /// <param name="documentDiscount">Document discount percent.</param>
    /// <param name="stampTax">Stamp duty added to the grand total.</param>
    /// <param name="currency">Target currency; EUR amounts are converted. Defaults to the supplier's currency / EUR.</param>
    /// <returns>JSON with bill id/number, amount, vat_total and grand_total, or "Error:" with the cause.</returns>
    public string CreatePurchaseInvoice(string supplierName, string lines, decimal? documentDiscount = null, decimal? stampTax = null, string? currency = null)
    {
        Log.LogStep($"ErpTool.CreatePurchaseInvoice: supplier={supplierName}");
        if (string.IsNullOrWhiteSpace(supplierName)) return "Error: 'supplierName' is required.";
        if (JsonNode.Parse(lines) is not JsonArray la || la.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":50}].";
        var body = new JsonObject { ["supplier_name"] = supplierName, ["lines"] = la };
        if (documentDiscount.HasValue) body["document_discount"] = documentDiscount.Value;
        if (stampTax.HasValue) body["stamp_tax"] = stampTax.Value;
        if (!string.IsNullOrWhiteSpace(currency)) body["currency"] = currency;
        return Call("POST", "composed/purchase-invoice-scratch", body, "create purchase invoice");
    }

    /// <summary>Issue a debit note against a supplier, in one call.</summary>
    /// <param name="supplierName">Supplier name (must exist).</param>
    /// <param name="billId">Optional related purchase invoice (bill) id (GUID).</param>
    /// <param name="amount">Debit amount (positive).</param>
    /// <param name="reason">Optional reason.</param>
    /// <returns>JSON with the debit note id/number and amount, or "Error:" with the cause.</returns>
    public string CreateDebitNote(string supplierName, string? billId, decimal amount, string? reason = null)
    {
        Log.LogStep($"ErpTool.CreateDebitNote: supplier={supplierName} amount={amount}");
        if (string.IsNullOrWhiteSpace(supplierName)) return "Error: 'supplierName' is required.";
        if (amount <= 0) return "Error: 'amount' must be positive.";
        var body = new JsonObject { ["supplier_name"] = supplierName, ["amount"] = amount };
        if (!string.IsNullOrWhiteSpace(billId))
        {
            if (!Guid.TryParse(billId, out var bid)) return "Error: 'billId' must be a valid GUID.";
            body["bill_id"] = bid.ToString();
        }
        if (!string.IsNullOrWhiteSpace(reason)) body["reason"] = reason;
        return Call("POST", "composed/debit-note", body, "create debit note");
    }

    /// <summary>Convert a proforma invoice into a real (immediate) invoice, in one call.</summary>
    /// <param name="proformaInvoiceId">The proforma invoice id (GUID).</param>
    /// <returns>JSON with the invoice id/number and the new invoice_type, or "Error:" with the cause.</returns>
    public string ConvertProformaToInvoice(string proformaInvoiceId)
    {
        Log.LogStep($"ErpTool.ConvertProformaToInvoice: invoice={proformaInvoiceId}");
        if (!Guid.TryParse(proformaInvoiceId, out var pid)) return "Error: 'proformaInvoiceId' must be a valid GUID.";
        var body = new JsonObject { ["proforma_invoice_id"] = pid.ToString() };
        return Call("POST", "composed/proforma-to-invoice", body, "convert proforma to invoice");
    }

    /// <summary>Run a lifecycle action on an invoice, in one call. Pick the action by name.</summary>
    /// <param name="action">One of: "post" (mark it registered in the ledger), "storno" (reverse it with a mirror invoice of negated amounts), "duplicate" (copy it and its lines into a new draft), "cancel" (cancel it; only if not posted — a posted invoice must be storned instead).</param>
    /// <param name="invoiceId">Invoice id (GUID).</param>
    /// <returns>JSON describing the result of the action (posted flag, new/storno id, or new status), or "Error:" with the cause.</returns>
    public string ManageInvoice(string action, string invoiceId)
    {
        Log.LogStep($"ErpTool.ManageInvoice: action={action} invoice={invoiceId}");
        if (!Guid.TryParse(invoiceId, out var iid)) return "Error: 'invoiceId' must be a valid GUID.";
        var (verb, endpoint) = (action ?? "").Trim().ToLowerInvariant() switch
        {
            "post" => ("post invoice", "composed/post-invoice"),
            "storno" => ("storno invoice", "composed/storno-invoice"),
            "duplicate" => ("duplicate invoice", "composed/duplicate-invoice"),
            "cancel" => ("cancel invoice", "composed/cancel-invoice"),
            _ => ("", "")
        };
        if (string.IsNullOrEmpty(endpoint))
            return "Error: 'action' must be one of: post, storno, duplicate, cancel.";
        var body = new JsonObject { ["invoice_id"] = iid.ToString() };
        return Call("POST", endpoint, body, verb);
    }

    /// <summary>Collect cash across several invoices with an optional allowance (abbuono), in one call. The amount is allocated over the invoices' outstanding balances in order (partial allowed). Reference invoices by GUID (invoiceIds) or by the human-readable invoice numbers you see on screen (invoiceNumbers).</summary>
    /// <param name="invoiceIds">Optional JSON array of invoice GUIDs, e.g. ["invoice-guid-1","invoice-guid-2"].</param>
    /// <param name="amount">Total cash collected.</param>
    /// <param name="paymentMethod">Payment method: bank, card, cash, sdd, rid, check, rib, pos; defaults to bank. (Named paymentMethod, not method, to avoid colliding with the agent's reserved method dispatch key.)</param>
    /// <param name="allowance">Optional allowance that reduces the remaining balance without cash.</param>
    /// <param name="invoiceNumbers">Optional JSON array of invoice numbers, e.g. ["INV-20260101120000000"]. Use this instead of invoiceIds when you only know the numbers.</param>
    /// <returns>JSON with collected, allowance and per-invoice paid/balance/status, or "Error:" with the cause.</returns>
    public string RecordCollection(string? invoiceIds = null, decimal amount = 0m, string? paymentMethod = null, decimal? allowance = null, string? invoiceNumbers = null)
    {
        Log.LogStep($"ErpTool.RecordCollection: amount={amount}");
        JsonArray? ia = null, inum = null;
        if (!string.IsNullOrWhiteSpace(invoiceIds))
        {
            if (JsonNode.Parse(invoiceIds) is not JsonArray a) return "Error: 'invoiceIds' must be a JSON array of invoice GUIDs like [\"<guid>\"].";
            ia = a;
        }
        if (!string.IsNullOrWhiteSpace(invoiceNumbers))
        {
            if (JsonNode.Parse(invoiceNumbers) is not JsonArray n) return "Error: 'invoiceNumbers' must be a JSON array of invoice numbers like [\"INV-...\"].";
            inum = n;
        }
        if ((ia == null || ia.Count == 0) && (inum == null || inum.Count == 0)) return "Error: provide 'invoiceIds' or 'invoiceNumbers'.";
        if (amount < 0) return "Error: 'amount' must not be negative.";
        var body = new JsonObject { ["amount"] = amount };
        if (ia != null && ia.Count > 0) body["invoice_ids"] = ia;
        if (inum != null && inum.Count > 0) body["invoice_numbers"] = inum;
        if (!string.IsNullOrWhiteSpace(paymentMethod)) body["method"] = paymentMethod;
        if (allowance.HasValue) body["allowance"] = allowance.Value;
        return Call("POST", "composed/record-collection", body, "record collection");
    }

    /// <summary>Customer statement: the customer's open invoices with balance, due date, days late and an aging bucket, in one call.</summary>
    /// <param name="customerName">Customer name (must exist).</param>
    /// <returns>JSON with the customer, open_items and total_open, or "Error:" with the cause.</returns>
    public string CustomerStatement(string customerName)
    {
        Log.LogStep($"ErpTool.CustomerStatement: customer={customerName}");
        if (string.IsNullOrWhiteSpace(customerName)) return "Error: 'customerName' is required.";
        var body = new JsonObject { ["customer_name"] = customerName };
        return Call("POST", "composed/customer-statement", body, "customer statement");
    }

    /// <summary>Manager report over an optional date range (inclusive). Pick the report by type.</summary>
    /// <param name="reportType">One of: "sales_by_customer" (revenue per customer, sorted by gross desc), "sales_by_product" (quantity and net per product, sorted by net desc), "purchases_by_supplier" (cost per supplier, sorted by gross desc).</param>
    /// <param name="fromDate">Optional start date (yyyy-MM-dd); omit for unbounded.</param>
    /// <param name="toDate">Optional end date (yyyy-MM-dd); omit for unbounded.</param>
    /// <returns>JSON with the grouped totals for the chosen report, or "Error:" with the cause.</returns>
    public string Report(string reportType, string? fromDate = null, string? toDate = null)
    {
        Log.LogStep($"ErpTool.Report: type={reportType} from={fromDate} to={toDate}");
        var endpoint = (reportType ?? "").Trim().ToLowerInvariant() switch
        {
            "sales_by_customer" => "composed/sales-by-customer",
            "sales_by_product" => "composed/sales-by-product",
            "purchases_by_supplier" => "composed/purchases-by-supplier",
            _ => ""
        };
        if (string.IsNullOrEmpty(endpoint))
            return "Error: 'reportType' must be one of: sales_by_customer, sales_by_product, purchases_by_supplier.";
        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(fromDate)) body["from_date"] = fromDate;
        if (!string.IsNullOrWhiteSpace(toDate)) body["to_date"] = toDate;
        return Call("POST", endpoint, body, reportType);
    }

    /// <summary>Manager report: receivables aging (scadenzario). Every open sales invoice (not storned, not draft/cancelled, with a positive outstanding balance) is bucketed by how far its due date is from today.</summary>
    /// <returns>JSON with the five buckets (current, 1-30, 31-60, 61-90, over-90; each with amount and invoice_count) and total_outstanding, or "Error:" with the cause.</returns>
    public string AgingReceivables()
    {
        Log.LogStep("ErpTool.AgingReceivables");
        var body = new JsonObject();
        return Call("POST", "composed/aging-receivables", body, "aging receivables");
    }

    /// <summary>Reserve or release stock of a SKU in a warehouse, in one call.</summary>
    /// <param name="action">"reserve" to hold stock for an order, or "release" to free a reservation (up to the given quantity).</param>
    /// <param name="sku">Product SKU.</param>
    /// <param name="warehouseCode">Warehouse code (e.g. "WH1").</param>
    /// <param name="quantity">Quantity (positive).</param>
    /// <param name="orderId">Optional sales order id (GUID); used with "reserve".</param>
    /// <returns>JSON with sku, warehouse and the reserved/released quantity, or "Error:" with the cause.</returns>
    public string ManageStock(string action, string sku, string warehouseCode, decimal quantity, string? orderId = null)
    {
        Log.LogStep($"ErpTool.ManageStock: action={action} {sku} @ {warehouseCode} qty={quantity}");
        if (string.IsNullOrWhiteSpace(sku)) return "Error: 'sku' is required.";
        if (string.IsNullOrWhiteSpace(warehouseCode)) return "Error: 'warehouseCode' is required.";
        if (quantity <= 0) return "Error: 'quantity' must be positive.";
        var (verb, endpoint) = (action ?? "").Trim().ToLowerInvariant() switch
        {
            "reserve" => ("reserve stock", "composed/reserve-stock"),
            "release" => ("release stock", "composed/release-stock"),
            _ => ("", "")
        };
        if (string.IsNullOrEmpty(endpoint))
            return "Error: 'action' must be 'reserve' or 'release'.";
        var body = new JsonObject { ["sku"] = sku, ["warehouse_code"] = warehouseCode, ["quantity"] = quantity };
        if (!string.IsNullOrWhiteSpace(orderId))
        {
            if (!Guid.TryParse(orderId, out var oid)) return "Error: 'orderId' must be a valid GUID.";
            body["order_id"] = oid.ToString();
        }
        return Call("POST", endpoint, body, verb);
    }

    /// <summary>Process a supplier return: record the return and reduce warehouse stock, in one call.</summary>
    /// <param name="supplierName">Supplier name (must exist).</param>
    /// <param name="receiptId">Optional original goods receipt id (GUID).</param>
    /// <param name="lines">JSON array of return lines, e.g. [{"sku":"SKU-1001","quantity":3,"reason":"damaged"}].</param>
    /// <param name="warehouseCode">Warehouse to return from (e.g. "WH1"); omit to use the default warehouse.</param>
    /// <returns>JSON with the return id, supplier id and returned lines, or "Error:" with the cause.</returns>
    public string ProcessSupplierReturn(string supplierName, string? receiptId, string lines, string? warehouseCode = null)
    {
        Log.LogStep($"ErpTool.ProcessSupplierReturn: supplier={supplierName}");
        if (string.IsNullOrWhiteSpace(supplierName)) return "Error: 'supplierName' is required.";
        if (JsonNode.Parse(lines) is not JsonArray arr || arr.Count == 0) return "Error: 'lines' must be a non-empty JSON array like [{\"sku\":\"SKU-1001\",\"quantity\":3}].";
        var body = new JsonObject { ["supplier_name"] = supplierName, ["lines"] = arr };
        if (!string.IsNullOrWhiteSpace(receiptId))
        {
            if (!Guid.TryParse(receiptId, out var rid)) return "Error: 'receiptId' must be a valid GUID.";
            body["receipt_id"] = rid.ToString();
        }
        if (!string.IsNullOrWhiteSpace(warehouseCode)) body["warehouse_code"] = warehouseCode;
        return Call("POST", "composed/supplier-return", body, "process supplier return");
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
