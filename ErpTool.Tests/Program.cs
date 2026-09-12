using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIOrchestrator.API;

namespace ErpToolTests;

/// <summary>
/// Deterministic (no-LLM) test suite exercising the real <see cref="ErpTool"/> class against a
/// live AI-ERP. Connection comes from env: ERP_BASE_URL / ERP_USER / ERP_PASSWORD.
/// Each case is a (name, Func&lt;bool&gt;); assertion helpers throw with a diagnostic on failure.
/// Prints PASS/FAIL per case and a final TOTAL line; exits 0 only if nothing failed.
/// </summary>
internal static class Program
{
    static readonly ErpTool Tool = new();
    static int passed, failed;
    static readonly List<string> Failures = new();
    // Records this run creates that we must remove at the end (customers/products/suppliers only;
    // composed orders/invoices/payments reference seeded rows and are left in place).
    static readonly List<(string Entity, string Id)> Created = new();

    // Shared records resolved/created once in Setup for the composed-flow and consistency tests.
    static string _alpine = "", _supplier = "";
    static string _soId = "", _invId = "", _poId = "";
    static decimal _stock1001Before;
    static JsonNode? _soRes, _invRes, _poRes, _recvRes;
    static JsonNode? _custCreateRes, _partialRes, _fullRes;
    static string _payId = "";   // captured by the partial-payment test, reused by the FK test

    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"ERP target: {Environment.GetEnvironmentVariable("ERP_BASE_URL")}");

        try { Setup(); }
        catch (Exception ex)
        {
            Console.WriteLine($"SETUP FAILED (environment/seed problem): {ex.Message}");
            return 2;
        }

        var t = new List<(string, Func<bool>)>();
        AddSchema(t);
        // Reporting/absolute-count assertions run FIRST, against the pristine seed (Setup just
        // purged leftovers), before any CRUD phase creates records that would inflate counts.
        AddQueryReporting(t);
        AddCustomerCrud(t);
        AddProductCrud(t);
        AddSupplierCrud(t);
        AddSalesFlow(t);
        AddPurchaseFlow(t);
        AddNegative(t);
        AddConsistency(t);

        foreach (var (name, body) in t)
        {
            try
            {
                if (body()) { passed++; Console.WriteLine($"PASS {name}"); }
                else { failed++; Failures.Add(name + ": assertion returned false"); Console.WriteLine($"FAIL {name}: assertion returned false"); }
            }
            catch (Exception ex)
            {
                failed++; Failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }

        CleanupCreated();

        Console.WriteLine();
        Console.WriteLine($"TOTAL: {passed} passed / {failed} failed");
        if (Failures.Count > 0)
        {
            Console.WriteLine("---- FAILURES ----");
            foreach (var f in Failures) Console.WriteLine("  * " + f);
        }
        return failed == 0 ? 0 : 1;
    }

    // ────────────────────────────────────────────────────────────
    //  Setup: clean prior leftovers, resolve seeded ids, create shared records
    // ────────────────────────────────────────────────────────────
    static void Setup()
    {
        // Make the run idempotent: remove any customer/product/supplier not in the seed set
        // (leftovers from earlier crashed runs). These have no FK children (composed orders
        // only ever reference seeded rows), so deletes are safe.
        PurgeNonSeed("customer", "name", new[] { "Alpine Coffee Roasters", "Bavarian Deli", "City Books Ltd", "Delta Office Supplies", "Evergreen Grocers" });
        PurgeNonSeed("product", "sku", new[] { "SKU-1001", "SKU-1002", "SKU-2001", "SKU-2002", "SKU-3001", "SKU-3002", "SKU-4001", "SKU-4002", "SKU-5001", "SKU-5002", "SKU-6001", "SKU-9999" });
        PurgeNonSeed("supplier", "name", new[] { "Global Packaging Co", "Prime Coffee Importers", "OfficePro Distribution", "TeaLeaf Traders" });

        _alpine = IdOf(Query("SELECT id FROM customer WHERE name = @n", P("n", "Alpine Coffee Roasters")), "seed customer Alpine");
        _supplier = IdOf(Query("SELECT id FROM supplier WHERE name = @n", P("n", "Prime Coffee Importers")), "seed supplier Prime Coffee Importers");
        _stock1001Before = Dec(First(Query("SELECT stock_quantity FROM product WHERE sku = @s", P("s", "SKU-1001")))["stock_quantity"]);

        // Main sales order (2 lines) + invoice, used by the sales-flow and FK tests.
        _soRes = J(Tool.PlaceSalesOrder(_alpine,
            "[{\"sku\":\"SKU-1001\",\"quantity\":3},{\"sku\":\"SKU-2001\",\"quantity\":10,\"discountPercent\":5}]"));
        _soId = _soRes["result"]!["order_id"]!.GetValue<string>();
        _invRes = J(Tool.InvoiceSalesOrder(_soId, 30));
        _invId = _invRes["result"]!["invoice_id"]!.GetValue<string>();

        // Purchase order (SKU-1001 x50 @9.00), received later in the purchase-flow tests.
        _poRes = J(Tool.PlacePurchaseOrder(_supplier,
            "[{\"sku\":\"SKU-1001\",\"quantity\":50,\"unitCost\":9.0}]"));
        _poId = _poRes["result"]!["purchase_order_id"]!.GetValue<string>();
    }

    static void PurgeNonSeed(string entity, string keyField, string[] seed)
    {
        var set = new HashSet<string>(seed);
        var res = Query($"SELECT id, {keyField} FROM {entity}");
        if (res is not JsonObject o || o["records"] is not JsonArray arr) return;
        foreach (var r in arr)
        {
            var val = r?[keyField]?.GetValue<string>() ?? "";
            if (!set.Contains(val))
            {
                var id = r?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) Tool.DeleteRecord(entity, id);
            }
        }
    }

    static void CleanupCreated()
    {
        foreach (var (entity, id) in Created)
        {
            try { Tool.DeleteRecord(entity, id); } catch { /* best effort */ }
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Schema discovery
    // ────────────────────────────────────────────────────────────
    static void AddSchema(List<(string, Func<bool>)> t)
    {
        t.Add(("schema_all_is_array", () => { var n = J(Tool.GetSchema()); Need(n is JsonArray, "expected JSON array, got " + Cut(n?.ToJsonString())); return true; }));

        t.Add(("schema_all_has_10_entities", () =>
        {
            var arr = J(Tool.GetSchema())!.AsArray();
            Need(arr.Count >= 10, $"expected >=10 entities, got {arr.Count}");
            return true;
        }));

        t.Add(("schema_all_contains_each_business_entity", () =>
        {
            var names = new HashSet<string>(J(Tool.GetSchema())!.AsArray().Select(e => e!["name"]!.GetValue<string>()));
            foreach (var want in new[] { "company", "customer", "product", "supplier", "sales_order", "sales_order_line", "purchase_order", "purchase_order_line", "invoice", "payment" })
                Need(names.Contains(want), $"missing entity '{want}' (have: {string.Join(",", names)})");
            return true;
        }));

        t.Add(("schema_customer_is_object_named_customer", () =>
        {
            var o = J(Tool.GetSchema("customer"))!.AsObject();
            Need(o["name"]!.GetValue<string>() == "customer", "name != customer");
            return true;
        }));

        t.Add(("schema_customer_name_field_required", () =>
        {
            var f = FieldOf("customer", "name");
            Need(f["required"]!.GetValue<bool>(), "name.required should be true");
            return true;
        }));

        t.Add(("schema_customer_has_expected_fields", () =>
        {
            var have = FieldNames("customer");
            foreach (var want in new[] { "name", "email", "phone", "billing_address", "city", "country", "vat_number", "category", "credit_limit", "notes" })
                Need(have.Contains(want), $"customer missing field '{want}'");
            return true;
        }));

        t.Add(("schema_product_has_key_fields", () =>
        {
            var have = FieldNames("product");
            foreach (var want in new[] { "sku", "name", "unit_price", "unit_cost", "stock_quantity", "reorder_point", "active" })
                Need(have.Contains(want), $"product missing field '{want}'");
            return true;
        }));

        t.Add(("schema_unknown_entity_is_error", () => { Err(Tool.GetSchema("does_not_exist_xyz")); return true; }));
    }

    // ────────────────────────────────────────────────────────────
    //  Customer CRUD
    // ────────────────────────────────────────────────────────────
    static void AddCustomerCrud(List<(string, Func<bool>)> t)
    {
        string id = Guid.NewGuid().ToString();
        string name = "Test Customer " + Tag();

        t.Add(("cust_create_ok", () =>
        {
            _custCreateRes = J(Tool.CreateRecord("customer", $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"city\":\"Turin\",\"credit_limit\":1000}}"));
            Track("customer", id);
            return true;
        }));
        t.Add(("cust_create_record_id_matches", () =>
        {
            Need(_custCreateRes!["record"]!["id"]!.GetValue<string>() == id, "returned record.id != supplied id");
            return true;
        }));
        t.Add(("cust_query_back_by_id_found", () => { var r = First(Query("SELECT id,name FROM customer WHERE id = @i", P("i", id))); Need(r["name"]!.GetValue<string>() == name, "not found / wrong name"); return true; }));
        t.Add(("cust_query_back_name_matches", () => { var r = First(Query("SELECT name FROM customer WHERE id = @i", P("i", id))); Need(r["name"]!.GetValue<string>() == name, "name mismatch"); return true; }));
        t.Add(("cust_update_city_ok", () => { Ok(Tool.UpdateRecord("customer", id, "{\"city\":\"Milan\"}")); return true; }));
        t.Add(("cust_update_city_persisted", () => { var r = First(Query("SELECT city FROM customer WHERE id = @i", P("i", id))); Need(r["city"]!.GetValue<string>() == "Milan", "city not persisted: " + r["city"]); return true; }));
        t.Add(("cust_update_credit_limit_persisted", () =>
        {
            Ok(Tool.UpdateRecord("customer", id, "{\"credit_limit\":2500}"));
            var r = First(Query("SELECT credit_limit FROM customer WHERE id = @i", P("i", id)));
            Need(Close(Dec(r["credit_limit"]), 2500m), "credit_limit not persisted: " + r["credit_limit"]);
            return true;
        }));
        t.Add(("cust_update_record_echo", () =>
        {
            var rec = J(Tool.UpdateRecord("customer", id, "{\"phone\":\"+39 02 5555\"}"));
            Need(rec!["record"]!["phone"]!.GetValue<string>() == "+39 02 5555", "update did not echo phone");
            return true;
        }));
        t.Add(("cust_query_by_name_found", () => { var r = First(Query("SELECT id FROM customer WHERE name = @n", P("n", name))); Need(r["id"]!.GetValue<string>() == id, "query by name did not find created id"); return true; }));
        t.Add(("cust_delete_ok", () => { Ok(Tool.DeleteRecord("customer", id)); return true; }));
        t.Add(("cust_gone_after_delete", () => { var c = Count(Query("SELECT id FROM customer WHERE id = @i", P("i", id))); Need(c == 0, $"still present after delete (count={c})"); return true; }));

        // Contract / validation (these intentionally assert the CORRECT behavior and may FAIL,
        // revealing backend gaps — see the report).
        t.Add(("cust_create_without_id_should_succeed", () =>
        {
            // Documented contract: caller supplies business fields only, system assigns the id.
            var s = Tool.CreateRecord("customer", "{\"name\":\"Auto Id Probe " + Tag() + "\"}");
            TryTrackCreated("customer", s); // if the backend created it, remove it at end of run
            Ok(s);
            return true;
        }));
        t.Add(("cust_create_missing_name_should_error", () =>
        {
            // Schema says name is required; supplying a valid id but no name should be rejected.
            var s = Tool.CreateRecord("customer", "{\"id\":\"" + Guid.NewGuid() + "\",\"city\":\"Nowhere\"}");
            TryTrackCreated("customer", s); // if the backend wrongly accepted it, remove the row
            Err(s); // currently FAILS: backend accepts it with name="" (no required-field validation)
            return true;
        }));
        t.Add(("cust_create_duplicate_name_errors", () =>
        {
            string dup = "Dup Customer " + Tag();
            string a = Guid.NewGuid().ToString(), b = Guid.NewGuid().ToString();
            Ok(Tool.CreateRecord("customer", $"{{\"id\":\"{a}\",\"name\":\"{dup}\"}}"));
            Track("customer", a);
            Err(Tool.CreateRecord("customer", $"{{\"id\":\"{b}\",\"name\":\"{dup}\"}}"));
            return true;
        }));
        t.Add(("cust_update_nonexistent_errors", () => { Err(Tool.UpdateRecord("customer", Guid.NewGuid().ToString(), "{\"city\":\"X\"}")); return true; }));
        t.Add(("cust_delete_nonexistent_errors", () => { Err(Tool.DeleteRecord("customer", Guid.NewGuid().ToString())); return true; }));
    }

    // ────────────────────────────────────────────────────────────
    //  Product CRUD + stock
    // ────────────────────────────────────────────────────────────
    static void AddProductCrud(List<(string, Func<bool>)> t)
    {
        string id = Guid.NewGuid().ToString();
        string sku = "SKU-T" + Tag();

        t.Add(("prod_create_ok", () => { Ok(Tool.CreateRecord("product", $"{{\"id\":\"{id}\",\"sku\":\"{sku}\",\"name\":\"Temp Product\",\"unit_price\":10,\"unit_cost\":4,\"stock_quantity\":20,\"reorder_point\":5,\"active\":true}}")); Track("product", id); return true; }));
        t.Add(("prod_query_by_sku_found", () => { var r = First(Query("SELECT name FROM product WHERE sku = @s", P("s", sku))); Need(r["name"]!.GetValue<string>() == "Temp Product", "not found by sku"); return true; }));
        t.Add(("prod_update_price_persisted", () => { Ok(Tool.UpdateRecord("product", id, "{\"unit_price\":12.75}")); var r = First(Query("SELECT unit_price FROM product WHERE sku = @s", P("s", sku))); Need(Close(Dec(r["unit_price"]), 12.75m), "price not persisted: " + r["unit_price"]); return true; }));
        t.Add(("prod_update_stock_persisted", () => { Ok(Tool.UpdateRecord("product", id, "{\"stock_quantity\":99}")); var r = First(Query("SELECT stock_quantity FROM product WHERE sku = @s", P("s", sku))); Need(Close(Dec(r["stock_quantity"]), 99m), "stock not persisted: " + r["stock_quantity"]); return true; }));
        t.Add(("prod_update_active_false_persisted", () => { Ok(Tool.UpdateRecord("product", id, "{\"active\":false}")); var r = First(Query("SELECT active FROM product WHERE sku = @s", P("s", sku))); Need(!r["active"]!.GetValue<bool>(), "active=false not persisted"); return true; }));
        t.Add(("prod_update_reorder_point_persisted", () => { Ok(Tool.UpdateRecord("product", id, "{\"reorder_point\":40}")); var r = First(Query("SELECT reorder_point FROM product WHERE sku = @s", P("s", sku))); Need(Close(Dec(r["reorder_point"]), 40m), "reorder_point not persisted: " + r["reorder_point"]); return true; }));
        t.Add(("prod_query_by_name_found", () => { var r = First(Query("SELECT sku FROM product WHERE name = @n", P("n", "Temp Product"))); Need(r["sku"]!.GetValue<string>() == sku, "query by name failed"); return true; }));
        t.Add(("prod_delete_ok", () => { Ok(Tool.DeleteRecord("product", id)); return true; }));
        t.Add(("prod_gone_after_delete", () => { Need(Count(Query("SELECT id FROM product WHERE sku = @s", P("s", sku))) == 0, "product still present"); return true; }));

        // Seeded aggregate facts (computed in C#; COUNT(*) unsupported by EQL).
        t.Add(("prod_count_is_12", () => { Need(Count(Query("SELECT sku FROM product")) == 12, "product count != 12"); return true; }));
        t.Add(("prod_active_count_is_11", () => { Need(Count(Query("SELECT sku FROM product WHERE active = true")) == 11, "active count != 11"); return true; }));
        t.Add(("prod_inactive_count_is_1", () => { Need(Count(Query("SELECT sku FROM product WHERE active = false")) == 1, "inactive count != 1"); return true; }));
        t.Add(("prod_sku9999_inactive", () => { var r = First(Query("SELECT active FROM product WHERE sku = @s", P("s", "SKU-9999"))); Need(!r["active"]!.GetValue<bool>(), "SKU-9999 should be inactive"); return true; }));
        t.Add(("prod_price_range_inline", () =>
        {
            // 5.00 <= price <= 10.00 among seeded products: 5.20,6.90,7.50,8.00,9.99 = 5
            var n = Count(Query("SELECT sku FROM product WHERE unit_price >= 5 AND unit_price <= 10"));
            Need(n == 5, $"price-range count expected 5, got {n}");
            return true;
        }));
        t.Add(("prod_create_with_all_fields_echo", () =>
        {
            string pid = Guid.NewGuid().ToString();
            var rec = J(Tool.CreateRecord("product", $"{{\"id\":\"{pid}\",\"sku\":\"SKU-E{Tag()}\",\"name\":\"Echo\",\"unit_price\":3.25,\"unit_cost\":1.5,\"stock_quantity\":7,\"active\":true}}"));
            Track("product", pid);
            Need(Close(Dec(rec!["record"]!["unit_price"]), 3.25m) && Close(Dec(rec["record"]!["unit_cost"]), 1.5m), "echo mismatch");
            return true;
        }));
    }

    // ────────────────────────────────────────────────────────────
    //  Supplier CRUD
    // ────────────────────────────────────────────────────────────
    static void AddSupplierCrud(List<(string, Func<bool>)> t)
    {
        string id = Guid.NewGuid().ToString();
        string name = "Test Supplier " + Tag();

        t.Add(("sup_create_ok", () => { Ok(Tool.CreateRecord("supplier", $"{{\"id\":\"{id}\",\"name\":\"{name}\",\"city\":\"Genoa\"}}")); Track("supplier", id); return true; }));
        t.Add(("sup_query_back", () => { var r = First(Query("SELECT city FROM supplier WHERE name = @n", P("n", name))); Need(r["city"]!.GetValue<string>() == "Genoa", "supplier not found"); return true; }));
        t.Add(("sup_update_ok", () => { Ok(Tool.UpdateRecord("supplier", id, "{\"city\":\"Naples\"}")); var r = First(Query("SELECT city FROM supplier WHERE name = @n", P("n", name))); Need(r["city"]!.GetValue<string>() == "Naples", "supplier city not updated"); return true; }));
        t.Add(("sup_update_record_echo", () =>
        {
            var rec = J(Tool.UpdateRecord("supplier", id, "{\"contact_name\":\"Mario Rossi\"}"));
            Need(rec!["record"]!["contact_name"]!.GetValue<string>() == "Mario Rossi", "update did not echo contact_name");
            return true;
        }));
        t.Add(("sup_duplicate_name_errors", () =>
        {
            string dup = "Dup Supplier " + Tag();
            string a = Guid.NewGuid().ToString(), b = Guid.NewGuid().ToString();
            Ok(Tool.CreateRecord("supplier", $"{{\"id\":\"{a}\",\"name\":\"{dup}\"}}"));
            Track("supplier", a);
            Err(Tool.CreateRecord("supplier", $"{{\"id\":\"{b}\",\"name\":\"{dup}\"}}"));
            return true;
        }));
        t.Add(("sup_delete_ok", () => { Ok(Tool.DeleteRecord("supplier", id)); Need(Count(Query("SELECT id FROM supplier WHERE name = @n", P("n", name))) == 0, "supplier still present"); return true; }));
    }

    // ────────────────────────────────────────────────────────────
    //  Query / reporting
    // ────────────────────────────────────────────────────────────
    static void AddQueryReporting(List<(string, Func<bool>)> t)
    {
        t.Add(("q_customer_count_5", () => { Need(Count(Query("SELECT id FROM customer")) == 5, "customer count != 5"); return true; }));
        t.Add(("q_product_count_12", () => { Need(Count(Query("SELECT id FROM product")) == 12, "product count != 12"); return true; }));
        t.Add(("q_supplier_count_4", () => { Need(Count(Query("SELECT id FROM supplier")) == 4, "supplier count != 4"); return true; }));

        t.Add(("q_customers_wholesale_2", () => { Need(Count(Query("SELECT id FROM customer WHERE category = @c", P("c", "wholesale"))) == 2, "wholesale != 2"); return true; }));
        t.Add(("q_customers_retail_2", () => { Need(Count(Query("SELECT id FROM customer WHERE category = @c", P("c", "retail"))) == 2, "retail != 2"); return true; }));
        t.Add(("q_customers_vip_1", () => { Need(Count(Query("SELECT id FROM customer WHERE category = @c", P("c", "vip"))) == 1, "vip != 1"); return true; }));

        t.Add(("q_below_reorder_set", () =>
        {
            var skus = Skus(Query("SELECT sku FROM product WHERE stock_quantity < reorder_point"));
            var want = new HashSet<string> { "SKU-2002", "SKU-5002" };
            Need(new HashSet<string>(skus).SetEquals(want), $"below-reorder set mismatch: got [{string.Join(",", skus)}]");
            return true;
        }));

        t.Add(("q_order_by_name_asc", () =>
        {
            var names = Names(Query("SELECT name FROM customer ORDER BY name ASC"));
            var sorted = names.OrderBy(x => x, StringComparer.Ordinal).ToList();
            Need(names.SequenceEqual(sorted), "not sorted asc");
            return true;
        }));
        t.Add(("q_order_by_name_desc", () =>
        {
            var names = Names(Query("SELECT name FROM customer ORDER BY name DESC"));
            var sorted = names.OrderByDescending(x => x, StringComparer.Ordinal).ToList();
            Need(names.SequenceEqual(sorted), "not sorted desc");
            return true;
        }));

        t.Add(("q_param_category_wholesale", () =>
        {
            var names = Names(Query("SELECT name FROM customer WHERE category = @c ORDER BY name", P("c", "wholesale")));
            Need(names.Contains("Alpine Coffee Roasters") && names.Contains("Evergreen Grocers") && names.Count == 2, "wholesale names wrong: " + string.Join(",", names));
            return true;
        }));
        t.Add(("q_param_sku_single", () => { Need(Count(Query("SELECT id FROM product WHERE sku = @s", P("s", "SKU-1001"))) == 1, "sku param != 1"); return true; }));
        t.Add(("q_no_match_zero", () => { Need(Count(Query("SELECT id FROM customer WHERE name = @n", P("n", "No Such Customer XYZ"))) == 0, "expected zero matches"); return true; }));
        t.Add(("q_empty_eql_error", () => { Err(Tool.Query("   ")); return true; }));
    }

    // ────────────────────────────────────────────────────────────
    //  Composed sales flow
    // ────────────────────────────────────────────────────────────
    static void AddSalesFlow(List<(string, Func<bool>)> t)
    {
        var res = _soRes!["result"]!;

        t.Add(("so_place_ok", () => { Need(_soRes!["result"] != null, "no result"); return true; }));
        t.Add(("so_order_id_guid", () => { Need(Guid.TryParse(_soId, out _), "order_id not a GUID"); return true; }));
        t.Add(("so_order_number_present", () => { Need(!string.IsNullOrEmpty(res["order_number"]!.GetValue<string>()), "no order_number"); return true; }));
        t.Add(("so_total_121_05", () => { Need(Close(Dec(res["total"]), 121.05m), $"total {res["total"]} != 121.05"); return true; }));
        t.Add(("so_lines_count_2", () => { Need(res["lines"]!.AsArray().Count == 2, "lines != 2"); return true; }));
        t.Add(("so_line1_total_55_50", () => { Need(Close(Dec(res["lines"]![0]!["line_total"]), 55.50m), "line1 total wrong"); return true; }));
        t.Add(("so_line2_total_65_55", () => { Need(Close(Dec(res["lines"]![1]!["line_total"]), 65.55m), "line2 total wrong"); return true; }));
        t.Add(("so_line2_discount_5", () => { Need(Close(Dec(res["lines"]![1]!["discount_percent"]), 5m), "line2 discount != 5"); return true; }));
        t.Add(("so_line1_sku_matches", () => { Need(res["lines"]![0]!["sku"]!.GetValue<string>() == "SKU-1001", "line1 sku wrong"); return true; }));

        var inv = _invRes!["result"]!;
        t.Add(("inv_ok", () => { Need(_invRes!["result"] != null, "no invoice result"); return true; }));
        t.Add(("inv_amount_matches_order", () => { Need(Close(Dec(inv["amount"]), 121.05m), $"invoice amount {inv["amount"]} != 121.05"); return true; }));
        t.Add(("inv_status_sent", () => { Need(inv["status"]!.GetValue<string>() == "sent", "invoice status != sent"); return true; }));
        t.Add(("inv_due_date_plus30", () =>
        {
            var issue = DateTime.Parse(inv["issue_date"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            var due = DateTime.Parse(inv["due_date"]!.GetValue<string>(), CultureInfo.InvariantCulture);
            Need(due == issue.AddDays(30), $"due {due:yyyy-MM-dd} != issue+30 {issue.AddDays(30):yyyy-MM-dd}");
            return true;
        }));
        t.Add(("inv_number_present", () => { Need(!string.IsNullOrEmpty(inv["invoice_number"]!.GetValue<string>()), "no invoice_number"); return true; }));

        // Partial payment (mutates the shared invoice). Capture the result once; assert its fields.
        t.Add(("pay_partial_status_partial", () =>
        {
            _partialRes = J(Tool.RecordPayment(_invId, 50m, "card"));
            _payId = _partialRes!["result"]!["payment_id"]!.GetValue<string>();
            Need(_partialRes["result"]!["invoice_status"]!.GetValue<string>() == "partial", "not partial");
            return true;
        }));
        t.Add(("pay_partial_balance_71_05", () =>
        {
            Need(Close(Dec(_partialRes!["result"]!["balance"]), 71.05m), $"balance != 71.05: {_partialRes!["result"]!["balance"]}");
            return true;
        }));
        t.Add(("pay_partial_paid_total_50", () =>
        {
            Need(Close(Dec(_partialRes!["result"]!["paid_total"]), 50m), $"paid_total != 50: {_partialRes!["result"]!["paid_total"]}");
            return true;
        }));
        t.Add(("pay_full_status_paid", () =>
        {
            _fullRes = J(Tool.RecordPayment(_invId, 71.05m, "bank"));
            Need(_fullRes!["result"]!["invoice_status"]!.GetValue<string>() == "paid", "not paid");
            return true;
        }));
        t.Add(("pay_full_balance_zero", () =>
        {
            Need(Close(Dec(_fullRes!["result"]!["balance"]), 0m), $"balance != 0: {_fullRes!["result"]!["balance"]}");
            return true;
        }));
        t.Add(("pay_full_paid_total_121_05", () =>
        {
            Need(Close(Dec(_fullRes!["result"]!["paid_total"]), 121.05m), $"paid_total != 121.05: {_fullRes!["result"]!["paid_total"]}");
            return true;
        }));

        t.Add(("so_3line_total_correct", () =>
        {
            // 2x SKU-5001(12.00)=24 + 1x SKU-6001(2.50) + 4x SKU-3002(7.50)=30 => 56.50
            var r = J(Tool.PlaceSalesOrder(_alpine,
                "[{\"sku\":\"SKU-5001\",\"quantity\":2},{\"sku\":\"SKU-6001\",\"quantity\":1},{\"sku\":\"SKU-3002\",\"quantity\":4}]"));
            Need(Close(Dec(r!["result"]!["total"]), 56.50m), $"3-line total {r!["result"]!["total"]} != 56.50");
            return true;
        }));
    }

    // ────────────────────────────────────────────────────────────
    //  Composed purchase flow
    // ────────────────────────────────────────────────────────────
    static void AddPurchaseFlow(List<(string, Func<bool>)> t)
    {
        var res = _poRes!["result"]!;

        t.Add(("po_place_ok", () => { Need(_poRes!["result"] != null, "no po result"); return true; }));
        t.Add(("po_id_guid", () => { Need(Guid.TryParse(_poId, out _), "po id not GUID"); return true; }));
        t.Add(("po_number_present", () => { Need(!string.IsNullOrEmpty(res["po_number"]!.GetValue<string>()), "no po_number"); return true; }));
        t.Add(("po_total_450", () => { Need(Close(Dec(res["total"]), 450m), $"po total {res["total"]} != 450"); return true; }));
        t.Add(("po_lines_count_1", () => { Need(res["lines"]!.AsArray().Count == 1, "po lines != 1"); return true; }));
        t.Add(("po_line_total_450", () => { Need(Close(Dec(res["lines"]![0]!["line_total"]), 450m), "po line total != 450"); return true; }));

        t.Add(("recv_ok", () =>
        {
            _recvRes = J(Tool.ReceivePurchaseOrder(_poId)); // received exactly once here
            Need(_recvRes!["result"] != null, "no receive result");
            return true;
        }));
        t.Add(("recv_added_50", () =>
        {
            var stocked = _recvRes!["result"]!["stocked"]!.AsArray();
            Need(Close(Dec(stocked[0]!["added"]), 50m), "added != 50");
            return true;
        }));
        t.Add(("recv_new_stock_equals_before_plus_50", () =>
        {
            var stocked = _recvRes!["result"]!["stocked"]!.AsArray();
            Need(Close(Dec(stocked[0]!["new_stock"]), _stock1001Before + 50m), $"new_stock {stocked[0]!["new_stock"]} != before({_stock1001Before})+50");
            return true;
        }));
        t.Add(("recv_product_stock_persisted", () =>
        {
            var r = First(Query("SELECT stock_quantity FROM product WHERE sku = @s", P("s", "SKU-1001")));
            Need(Close(Dec(r["stock_quantity"]), _stock1001Before + 50m), $"persisted stock {r["stock_quantity"]} != before+50");
            return true;
        }));
        t.Add(("po_status_received", () =>
        {
            var r = First(Query("SELECT status FROM purchase_order WHERE id = @i", P("i", _poId)));
            Need(r["status"]!.GetValue<string>() == "received", "po status != received: " + r["status"]);
            return true;
        }));
    }

    // ────────────────────────────────────────────────────────────
    //  Negative / edge
    // ────────────────────────────────────────────────────────────
    static void AddNegative(List<(string, Func<bool>)> t)
    {
        t.Add(("neg_so_unknown_sku_error", () => { Err(Tool.PlaceSalesOrder(_alpine, "[{\"sku\":\"SKU-NOPE\",\"quantity\":1}]")); return true; }));
        t.Add(("neg_so_unknown_sku_message", () =>
        {
            var s = Tool.PlaceSalesOrder(_alpine, "[{\"sku\":\"SKU-NOPE\",\"quantity\":1}]");
            Need(s.Contains("Unknown product SKU") || ErrMsg(s).Contains("Unknown product SKU"), "message not specific: " + Cut(s));
            return true;
        }));
        t.Add(("neg_so_empty_lines_error", () => { Err(Tool.PlaceSalesOrder(_alpine, "[]")); return true; }));
        t.Add(("neg_so_invalid_guid_error", () => { Err(Tool.PlaceSalesOrder("not-a-guid", "[{\"sku\":\"SKU-1001\",\"quantity\":1}]")); return true; }));
        t.Add(("neg_inv_invalid_guid_error", () => { Err(Tool.InvoiceSalesOrder("bad-guid")); return true; }));
        t.Add(("neg_pay_invalid_guid_error", () => { Err(Tool.RecordPayment("bad-guid", 10m)); return true; }));
        t.Add(("neg_pay_missing_invoice_error", () => { Err(Tool.RecordPayment("11111111-1111-1111-1111-111111111111", 10m)); return true; }));
        t.Add(("neg_po_invalid_guid_error", () => { Err(Tool.PlacePurchaseOrder("bad-guid", "[{\"sku\":\"SKU-1001\",\"quantity\":1}]")); return true; }));
        t.Add(("neg_po_empty_lines_error", () => { Err(Tool.PlacePurchaseOrder(_supplier, "[]")); return true; }));
        t.Add(("neg_recv_invalid_guid_error", () => { Err(Tool.ReceivePurchaseOrder("bad-guid")); return true; }));
        t.Add(("neg_update_invalid_guid_error", () => { Err(Tool.UpdateRecord("customer", "bad-guid", "{\"city\":\"x\"}")); return true; }));
        t.Add(("neg_delete_invalid_guid_error", () => { Err(Tool.DeleteRecord("customer", "bad-guid")); return true; }));
        t.Add(("neg_create_fields_not_object_error", () => { Err(Tool.CreateRecord("customer", "[1,2,3]")); return true; }));
        t.Add(("neg_query_params_not_array_error", () => { Err(Tool.Query("SELECT id FROM customer", "{\"name\":\"x\"}")); return true; }));

        t.Add(("neg_inv_cancelled_order_error", () =>
        {
            // Create a fresh order, cancel it, then try to invoice -> must error.
            var so = J(Tool.PlaceSalesOrder(_alpine, "[{\"sku\":\"SKU-1001\",\"quantity\":1}]"));
            var oid = so!["result"]!["order_id"]!.GetValue<string>();
            Ok(Tool.UpdateRecord("sales_order", oid, "{\"status\":\"cancelled\"}"));
            Err(Tool.InvoiceSalesOrder(oid, 30));
            return true;
        }));
    }

    // ────────────────────────────────────────────────────────────
    //  Idempotency / consistency
    // ────────────────────────────────────────────────────────────
    static void AddConsistency(List<(string, Func<bool>)> t)
    {
        t.Add(("idem_requery_customer_stable", () =>
        {
            var a = First(Query("SELECT name FROM customer WHERE id = @i", P("i", _alpine)))["name"]!.GetValue<string>();
            var b = First(Query("SELECT name FROM customer WHERE id = @i", P("i", _alpine)))["name"]!.GetValue<string>();
            Need(a == b && a == "Alpine Coffee Roasters", "unstable re-query");
            return true;
        }));
        t.Add(("idem_order_customer_fk", () =>
        {
            var r = First(Query("SELECT customer_id FROM sales_order WHERE id = @i", P("i", _soId)));
            Need(r["customer_id"]!.GetValue<string>() == _alpine, "order.customer_id mismatch");
            return true;
        }));
        t.Add(("idem_invoice_customer_fk", () =>
        {
            var r = First(Query("SELECT customer_id FROM invoice WHERE id = @i", P("i", _invId)));
            Need(r["customer_id"]!.GetValue<string>() == _alpine, "invoice.customer_id mismatch");
            return true;
        }));
        t.Add(("idem_invoice_sales_order_fk", () =>
        {
            var r = First(Query("SELECT sales_order_id FROM invoice WHERE id = @i", P("i", _invId)));
            Need(r["sales_order_id"]!.GetValue<string>() == _soId, "invoice.sales_order_id mismatch");
            return true;
        }));
        t.Add(("idem_payment_invoice_fk", () =>
        {
            Need(!string.IsNullOrEmpty(_payId), "no payment captured");
            var r = First(Query("SELECT invoice_id FROM payment WHERE id = @i", P("i", _payId)));
            Need(r["invoice_id"]!.GetValue<string>() == _invId, "payment.invoice_id mismatch");
            return true;
        }));
        t.Add(("idem_order_total_equals_sum_lines", () =>
        {
            var lines = _soRes!["result"]!["lines"]!.AsArray();
            decimal sum = lines.Sum(l => Dec(l!["line_total"]));
            Need(Close(sum, Dec(_soRes["result"]!["total"])), $"sum(lines) {sum} != total");
            return true;
        }));
        t.Add(("idem_po_total_equals_sum_lines", () =>
        {
            var lines = _poRes!["result"]!["lines"]!.AsArray();
            decimal sum = lines.Sum(l => Dec(l!["line_total"]));
            Need(Close(sum, Dec(_poRes["result"]!["total"])), $"sum(po lines) {sum} != po total");
            return true;
        }));
    }

    // ────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────
    static string Tag() => DateTime.Now.ToString("HHmmssfff", CultureInfo.InvariantCulture);

    static string P(string name, string value) =>
        new JsonArray { new JsonObject { ["name"] = name, ["value"] = value } }.ToJsonString();

    static JsonNode? Query(string eql, string? parameters = null) => J(Tool.Query(eql, parameters));

    static JsonNode? J(string s)
    {
        if (s.StartsWith("Error:")) throw new Exception(s);
        try { return JsonNode.Parse(s); }
        catch (Exception ex) { throw new Exception("not valid JSON: " + Cut(s) + " (" + ex.Message + ")"); }
    }

    static JsonObject First(JsonNode? n)
    {
        var arr = n?["records"]?.AsArray();
        if (arr == null || arr.Count == 0) throw new Exception("no records returned");
        return arr[0]!.AsObject();
    }

    static int Count(JsonNode? n) => n?["total_count"]?.GetValue<int>() ?? -1;

    static List<string> Skus(JsonNode? n) =>
        (n?["records"]?.AsArray() ?? new JsonArray()).Select(r => r?["sku"]?.GetValue<string>() ?? "").ToList();

    static List<string> Names(JsonNode? n) =>
        (n?["records"]?.AsArray() ?? new JsonArray()).Select(r => r?["name"]?.GetValue<string>() ?? "").ToList();

    static string IdOf(JsonNode? n, string what)
    {
        var arr = n?["records"]?.AsArray();
        if (arr == null || arr.Count == 0) throw new Exception(what + " not found");
        return arr[0]!["id"]!.GetValue<string>();
    }

    static JsonNode FieldOf(string entity, string field)
    {
        var o = J(Tool.GetSchema(entity))!.AsObject();
        foreach (var f in o["fields"]!.AsArray())
            if (f!["name"]!.GetValue<string>() == field) return f!;
        throw new Exception($"field '{field}' not found on {entity}");
    }

    static HashSet<string> FieldNames(string entity) =>
        new HashSet<string>(J(Tool.GetSchema(entity))!.AsObject()["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()));

    static decimal Dec(JsonNode? n) => n!.GetValue<decimal>();

    static bool Close(decimal a, decimal b) => Math.Abs(a - b) < 0.005m;

    static void Need(bool cond, string msg) { if (!cond) throw new Exception(msg); }

    static bool IsErr(string s)
    {
        if (s.StartsWith("Error:")) return true;
        try { var n = JsonNode.Parse(s); return n is JsonObject o && o["success"] is JsonValue v && v.TryGetValue<bool>(out var b) && !b; }
        catch { return false; }
    }
    static void Ok(string s) { if (IsErr(s)) throw new Exception("expected success but got: " + Cut(s)); }
    static void Err(string s) { if (!IsErr(s)) throw new Exception("expected error but got: " + Cut(s)); }
    static string ErrMsg(string s)
    {
        try { var o = JsonNode.Parse(s)!.AsObject(); return o["error"]?.GetValue<string>() ?? ""; }
        catch { return s; }
    }

    static void Track(string entity, string id) => Created.Add((entity, id));

    // Track a record the tool may have created even when the test expects an error,
    // so a wrongly-accepted row is still removed at end of run.
    static void TryTrackCreated(string entity, string s)
    {
        if (IsErr(s)) return;
        try
        {
            var id = JsonNode.Parse(s)?["record"]?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id)) Track(entity, id);
        }
        catch { /* not a create payload */ }
    }

    static string Cut(string s) => s.Length > 300 ? s.Substring(0, 300) + "…" : s;
}
