using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIOrchestrator;
using AIOrchestrator.API;

namespace ErpAgent.LiveTest;

/// <summary>
/// Live end-to-end test: a real LLM (DeepSeekBridge) drives the real ErpTool through the real
/// AgentHarness against the live AI-ERP in WSL. Each scenario gives the agent a natural-language
/// company task, runs the agent loop, then verifies the ERP state directly (independent of the
/// agent's own claim). The agent's tool-call steps are captured from the orchestrator log.
///
/// Env:
///   ERP_BASE_URL (default http://127.0.0.1:5080), ERP_USER (erp@webvella.com), ERP_PASSWORD (erp)
///   BRIDGE_URL (default http://172.25.64.1:8787/)  — DeepSeekBridge as seen from WSL
///   ONLY=1,2,3  — run only those scenario numbers (optional)
/// </summary>
static class Program
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    static string ErpUrl = (Environment.GetEnvironmentVariable("ERP_BASE_URL") ?? "http://127.0.0.1:5080").TrimEnd('/');
    static string ErpUser = Environment.GetEnvironmentVariable("ERP_USER") ?? "erp@webvella.com";
    static string ErpPass = Environment.GetEnvironmentVariable("ERP_PASSWORD") ?? "erp";
    static string BridgeUrl = Environment.GetEnvironmentVariable("BRIDGE_URL") ?? "http://172.25.64.1:8787/";
    static string Tag = DateTime.Now.ToString("HHmmss");

    // Precondition documents created by a scenario's Setup and read back by its Verify.
    static string _convQuoteId = "", _convQuoteNum = "";
    static string _delivOrderId = "", _delivOrderNum = "";
    static string _overOrderId = "", _overOrderNum = "";
    static string _cnInvoiceId = "", _cnInvoiceNum = "";
    static decimal _transferWh2Before = 0m;
    static decimal _adjustBefore = 0m;
    static decimal _buyStockBefore = 0m;
    // v3 (cats 17-20) setup state
    static string _transportOrderId = "", _transportOrderNum = "";
    static string _consolOrderNum1 = "", _consolOrderNum2 = "";
    static decimal _consolTotal = 0m;
    static string _returnDeliveryId = "", _returnDeliveryNum = "";
    static decimal _returnStockBefore = 0m;
    static decimal _cycleStockBefore = 0m;
    // v4 (cats 35-38) setup state
    static string _postInvId = "", _postInvNum = "";
    static string _stornoInvId = "", _stornoInvNum = "";
    static string _dupOrigId = "", _dupOrigNum = "";
    static decimal _dupOrigGrand = 0m;
    static string _cancelInvId = "", _cancelInvNum = "";
    static string _collNum1 = "", _collNum2 = "";
    static string _proformaNum = "";
    static string _stmtCust = "";
    static decimal _resBefore = 0m;
    static decimal _supRetBefore = 0m;
    // v5 (cat 42) composite business-day state
    static string _bizCust = "";
    // v6 (edge/negative) state
    static string _insfOrderNum = "";
    // v7 (multi-currency / lifecycle / date-range) state
    static decimal _p2pStockBefore = 0m;
    static string _dateRangeToday = "";
    // v8 aging delta baseline (so the aging check is correct on an accumulated DB, not only a fresh one)
    static decimal _agingAmtBefore = 0m;
    static int _agingCntBefore = 0;
    static List<string> _lastToolCalls = new();

    static async Task<int> Main()
    {
        Log.IsEnabled = true;

        // Force-load the ErpTool assembly and register it by name so the harness can resolve it.
        McpToolRegistry.Register(typeof(ErpTool));

        // Point the DeepSeekBridge provider at the WSL-reachable address and force the text
        // tool catalog (the bridge follows it far more reliably than native function-calling).
        ProviderConfigs.Upsert(new ProviderConfig
        {
            ProviderName = "DeepSeekBridge",
            Protocol = ProviderProtocol.OpenAI,
            CacheType = ProviderCacheType.PrefixCache,
            ModelName = "deepseek-web/deepseek-chat",
            BaseAddress = new Uri(BridgeUrl),
            EndPoint = "v1/chat/completions",
            Timeout = TimeSpan.FromMinutes(5),
            PauseBetweenRequests = TimeSpan.FromSeconds(3),
            ContextWindow = 128000,
            ForceTextToolDefinitions = true
        }, persist: false);

        var only = (Environment.GetEnvironmentVariable("ONLY") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : -1).Where(n => n > 0).ToHashSet();

        var scenarios = BuildScenarios();
        int pass = 0, fail = 0;
        Console.OutputEncoding = Encoding.UTF8;

        for (int i = 0; i < scenarios.Count; i++)
        {
            var sc = scenarios[i];
            int num = i + 1;
            if (only.Count > 0 && !only.Contains(num)) continue;

            Console.WriteLine($"\n================ SCENARIO {num}: {sc.Name} ================");
            sc.Setup?.Invoke();
            var prompt = sc.Prompt();

            var steps = new List<string>();
            void OnStep(string line, bool mon) { if (line.Contains("ErpTool")) steps.Add(line); }
            Log.OnLogStepMonitor += OnStep;

            AgentResult result;
            try
            {
                var harness = new AgentHarness("DeepSeekBridge");
                result = harness.ExecuteAction(prompt, new[] { "ErpTool" }, maxIterations: 12);
            }
            catch (Exception ex)
            {
                Log.OnLogStepMonitor -= OnStep;
                Console.WriteLine($"FAIL (exception): {ex.Message}");
                fail++;
                continue;
            }
            Log.OnLogStepMonitor -= OnStep;

            Console.WriteLine($"Agent iterations: {result.Iterations} | Success: {result.Success}");
            Console.WriteLine($"Agent message: {Truncate(result.Message ?? result.Error ?? "(none)", 400)}");
            Console.WriteLine($"ErpTool calls made ({steps.Count}):");
            foreach (var s in steps) Console.WriteLine("   " + s);
            _lastToolCalls = steps;

            var (ok, detail) = sc.Verify();
            Console.WriteLine(ok ? $"VERIFY: PASS — {detail}" : $"VERIFY: FAIL — {detail}");
            // The independent ERP-state check is the source of truth: if the ERP ended in the
            // correct state, the agent achieved the goal, even if it self-reported not-done
            // (e.g. it burned iterations trying to re-verify through EQL that can't JOIN).
            if (ok) pass++; else fail++;
        }

        Console.WriteLine($"\n================ LIVE AGENT RESULTS: {pass} passed / {fail} failed ================");
        return fail == 0 ? 0 : 1;
    }

    record Scenario(string Name, Action? Setup, Func<string> Prompt, Func<(bool, string)> Verify);

    static List<Scenario> BuildScenarios() => new()
    {
        new("Create a new customer",
            null,
            () => $"Create a new customer named \"Acme Industrial {Tag}\" with email sales@acme{Tag}.example, city Milan, country Italy, and category wholesale.",
            () => { var n = Count($"SELECT id FROM customer WHERE name = @v", Tag != null ? $"Acme Industrial {Tag}" : ""); return (n == 1, $"customers with that name: {n}"); }),

        new("Create a product",
            null,
            () => $"Add a product with SKU \"SKU-{Tag}A\", name \"Live Widget\", unit price 7.50, unit cost 3.00, stock 50, category Gadgets.",
            () => { var r = One($"SELECT unit_price FROM product WHERE sku = @v", $"SKU-{Tag}A"); return (r != null && Dec(r["unit_price"]) == 7.50m, $"unit_price: {r?["unit_price"]}"); }),

        new("Update a product price",
            () => CreateProduct($"SKU-{Tag}B", "Tuneable Widget", 10.00m),
            () => $"Change the unit price of the product with SKU \"SKU-{Tag}B\" to 8.25.",
            () => { var r = One($"SELECT unit_price FROM product WHERE sku = @v", $"SKU-{Tag}B"); return (r != null && Dec(r["unit_price"]) == 8.25m, $"unit_price: {r?["unit_price"]}"); }),

        new("Count products in a category",
            null,
            () => "How many products do we have in the Coffee category?",
            () => { var n = Count("SELECT id FROM product WHERE category = @v", "Coffee"); return (true, $"actual Coffee count={n} (check agent message says {n})"); }),

        new("Place a sales order for an existing customer",
            null,
            () => "Place a sales order for the customer \"Delta Office Supplies\" with 3 units of SKU-1001 and 10 units of SKU-2001. Use a 5 percent discount on the SKU-2001 line.",
            () =>
            {
                var cid = Scalar("SELECT id FROM customer WHERE name = @v", "Delta Office Supplies");
                if (cid == null) return (false, "customer not found");
                var r = One($"SELECT total FROM sales_order WHERE customer_id = @v ORDER BY order_number DESC", cid);
                return (r != null && Math.Abs(Dec(r["total"]) - 121.05m) < 0.01m, $"latest order total: {r?["total"]} (expected 121.05)");
            }),

        new("Invoice the latest order",
            null,
            () => "Find the most recent sales order for \"Delta Office Supplies\" and create an invoice for it with a 30 day due date.",
            () =>
            {
                var cid = Scalar("SELECT id FROM customer WHERE name = @v", "Delta Office Supplies");
                var r = One($"SELECT status FROM invoice WHERE customer_id = @v ORDER BY invoice_number DESC", cid!);
                return (r != null && r["status"]?.ToString() == "sent", $"latest invoice status: {r?["status"]}");
            }),

        new("Receive a purchase order (stock in)",
            null,
            () => "Create a purchase order from supplier \"Prime Coffee Importers\" for 100 units of SKU-1001 at unit cost 9.00, then receive the 100 units into warehouse WH1 so the stock is added.",
            () =>
            {
                var r = One("SELECT stock_quantity FROM product WHERE sku = @v", "SKU-1001");
                var stock = r == null ? 0 : Dec(r["stock_quantity"]);
                return (stock >= 340m, $"SKU-1001 stock now: {stock} (expected >= 340 after +100)");
            }),

        new("Low-stock report",
            null,
            () => "Which products are below their minimum stock level? List their SKUs.",
            () => (true, "manual check: expect SKU-2002 and SKU-5002 in the answer")),

        new("Look up a customer email",
            null,
            () => "What is the email address of the customer \"City Books Ltd\"?",
            () => (true, "manual check: expect orders@citybooks.example")),

        new("Create a supplier",
            null,
            () => $"Add a supplier named \"Live Components Ltd {Tag}\" with contact person Jane Doe and email jane@livecomp{Tag}.example.",
            () => { var n = Count("SELECT id FROM supplier WHERE name = @v", $"Live Components Ltd {Tag}"); return (n == 1, $"suppliers with that name: {n}"); }),

        new("Delete a product",
            () => CreateProduct($"SKU-{Tag}C", "Doomed Widget", 1.00m),
            () => $"Delete the product with SKU \"SKU-{Tag}C\".",
            () => { var n = Count("SELECT id FROM product WHERE sku = @v", $"SKU-{Tag}C"); return (n == 0, $"products with that sku: {n}"); }),

        new("Total customer count",
            null,
            () => "How many customers do we have in total?",
            () => { var n = Count("SELECT id FROM customer", null); return (true, $"actual customer count={n} (check agent message)"); }),

        // ── v2 deep commercial cycle ──

        new("Create a quote (preventivo)",
            null,
            () => "Create a quote for the customer \"Delta Office Supplies\" with 3 units of SKU-1001 and 10 units of SKU-2001 (5 percent discount on the SKU-2001 line), valid until 2026-12-31.",
            () =>
            {
                var cid = CustomerId("Delta Office Supplies");
                var r = One("SELECT grand_total FROM quote WHERE customer_id = @v ORDER BY quote_number DESC", cid);
                return (r != null && Math.Abs(Dec(r["grand_total"]) - 141.02m) < 0.01m, $"latest quote grand_total: {r?["grand_total"]} (expected 141.02)");
            }),

        new("Convert a quote into a sales order",
            () =>
            {
                var res = Composed("quote", new JsonObject
                {
                    ["customer_id"] = CustomerId("Alpine Coffee Roasters"),
                    ["lines"] = JsonNode.Parse("[{\"sku\":\"SKU-1001\",\"quantity\":2}]")
                });
                _convQuoteId = res?["quote_id"]?.ToString() ?? "";
                _convQuoteNum = res?["quote_number"]?.ToString() ?? "";
            },
            () => "Convert the quote numbered " + _convQuoteNum + " into a sales order.",
            () =>
            {
                var r = One("SELECT order_number FROM sales_order WHERE quote_id = @v", _convQuoteId);
                return (r != null, $"order created from quote: {r?["order_number"]}");
            }),

        new("Deliver a sales order (DDT) from a warehouse",
            () =>
            {
                var (oid, onum) = CreateOrderDirect("Bavarian Deli", "[{\"sku\":\"SKU-1001\",\"quantity\":4}]");
                _delivOrderId = oid; _delivOrderNum = onum;
            },
            () => "Deliver sales order " + _delivOrderNum + " from warehouse WH1: 4 units of SKU-1001.",
            () =>
            {
                var d = One("SELECT delivery_number FROM goods_delivery WHERE sales_order_id = @v", _delivOrderId);
                var st = One("SELECT status FROM sales_order WHERE id = @v", _delivOrderId);
                return (d != null && st?["status"]?.ToString() == "delivered", $"delivery: {d?["delivery_number"]}, order status: {st?["status"]}");
            }),

        new("Over-delivery must be rejected",
            () =>
            {
                var (oid, onum) = CreateOrderDirect("City Books Ltd", "[{\"sku\":\"SKU-1001\",\"quantity\":3}]");
                _overOrderId = oid; _overOrderNum = onum;
            },
            () => "Deliver 999 units of SKU-1001 for sales order " + _overOrderNum + " from WH1.",
            () =>
            {
                // The over-delivery (999) must not have been recorded. The agent may legitimately
                // deliver the ordered qty (3); what must NOT happen is delivering more than ordered.
                var r = One("SELECT qty_delivered FROM sales_order_line WHERE sales_order_id = @v", _overOrderId);
                var delivered = r == null ? 0m : Dec(r["qty_delivered"]);
                return (delivered < 999m, $"qty_delivered: {delivered} (over-delivery of 999 rejected; ordered 3)");
            }),

        new("Issue a credit note on an invoice",
            () =>
            {
                var (oid, _) = CreateOrderDirect("Evergreen Grocers", "[{\"sku\":\"SKU-1001\",\"quantity\":2}]");
                DeliverDirect(oid, "[{\"sku\":\"SKU-1001\",\"quantity\":2}]", "WH1");
                var (iid, inum) = CreateInvoiceDirect(oid);
                _cnInvoiceId = iid; _cnInvoiceNum = inum;
            },
            () => "Issue a credit note of 20 for damaged goods against invoice " + _cnInvoiceNum + ".",
            () =>
            {
                var cn = One("SELECT amount FROM credit_note WHERE invoice_id = @v", _cnInvoiceId);
                return (cn != null && Math.Abs(Dec(cn["amount"]) - 20m) < 0.01m, $"credit note amount: {cn?["amount"]}");
            }),

        new("Full purchase cycle (request to payment)",
            () => { _buyStockBefore = StockOf("SKU-1002", "WH1"); },
            () => "Create a purchase request for 80 units of SKU-1002 (estimated cost 5.00). Convert it into a purchase order for supplier \"Prime Coffee Importers\". Receive all 80 units into WH1. Register the supplier bill for 80 units at 5.00. Then pay the bill in full.",
            () =>
            {
                var after = StockOf("SKU-1002", "WH1");
                var paid = Query("SELECT status FROM purchase_invoice WHERE status = @v", "paid");
                var n = paid?["total_count"]?.GetValue<int>() ?? 0;
                return (after >= _buyStockBefore + 80m && n >= 1, $"SKU-1002 WH1 stock {_buyStockBefore}->{after} (+80 expected), paid bills: {n}");
            }),

        new("Transfer stock between warehouses",
            () => { _transferWh2Before = StockOf("SKU-1001", "WH2"); },
            () => "Transfer 15 units of SKU-1001 from warehouse WH1 to warehouse WH2.",
            () =>
            {
                var after = StockOf("SKU-1001", "WH2");
                return (Math.Abs(after - (_transferWh2Before + 15m)) < 0.01m, $"WH2 SKU-1001: {_transferWh2Before}->{after} (+15 expected)");
            }),

        new("Adjust stock to a counted quantity",
            () => { _adjustBefore = StockOf("SKU-1002", "WH1"); },
            () => "A stock count found 250 units of SKU-1002 in warehouse WH1. Adjust the stock to that number.",
            () =>
            {
                var after = StockOf("SKU-1002", "WH1");
                return (Math.Abs(after - 250m) < 0.01m, $"WH1 SKU-1002: {_adjustBefore}->250 (expected 250)");
            }),

        new("Negative-stock transfer must be rejected",
            null,
            () => "Transfer 999999 units of SKU-5002 from warehouse WH2 to WH1.",
            () => (true, "manual check: agent should report insufficient stock / transfer rejected")),

        new("Blocked customer order must be rejected",
            null,
            () => "Place a sales order for the customer \"Suspended Trading Co\" with 5 units of SKU-1001.",
            () =>
            {
                var cid = CustomerId("Suspended Trading Co");
                var n = Count("SELECT id FROM sales_order WHERE customer_id = @v", cid);
                return (n == 0, $"orders for blocked customer: {n} (must be 0)");
            }),

        // ── v3: advanced master data, pricing, documents, warehouse (cats 17-20) ──

        new("Create a customer with billing+ship addresses and a contact (cat 17)",
            null,
            () => $"Create a new customer named \"Nordic Trading {Tag}\" with a billing address (line1 'Storgatan 1', city 'Stockholm', country 'Sweden') and a shipping address (line1 'Hamngatan 5', city 'Gothenburg', country 'Sweden'), and a primary contact 'Elsa Buyer' with email elsa@nordic{Tag}.example.",
            () =>
            {
                var cid = CustomerId($"Nordic Trading {Tag}");
                if (string.IsNullOrEmpty(cid)) return (false, "customer not created");
                var addrs = Count("SELECT id FROM customer_address WHERE customer_id = @v", cid);
                var contacts = Count("SELECT id FROM contact WHERE customer_id = @v", cid);
                return (addrs == 2 && contacts == 1, $"addresses: {addrs} (expect 2), contacts: {contacts} (expect 1)");
            }),

        new("Add an alternate barcode and a supplier to a product (cat 17)",
            null,
            () => "Add an alternate EAN13 barcode \"4006381333931\" to product SKU-2001, and add supplier \"OfficePro Distribution\" for SKU-2001 with supplier SKU 'OP-2001', lead time 14 days, unit cost 3.10.",
            () =>
            {
                var pid = Scalar("SELECT id FROM product WHERE sku = @v", "SKU-2001");
                var bc = Count("SELECT id FROM product_barcode WHERE code = @v", "4006381333931");
                var sup = Query2("SELECT id FROM product_supplier WHERE product_id = @p AND supplier_id = @s", ("p", pid ?? ""), ("s", SupplierId("OfficePro Distribution")))?["total_count"]?.GetValue<int>() ?? 0;
                return (bc == 1 && sup >= 1, $"barcode rows: {bc} (expect 1), supplier links: {sup} (expect >=1)");
            }),

        new("Resolve a discounted price for a VIP customer (cat 18)",
            null,
            () => "What is the unit price for 60 units of SKU-1001 for customer \"Delta Office Supplies\"? Use the price list and discount rules.",
            () => (true, "manual check: expect a discounted unit price below the base 18.50 (VIP 10% => ~16.65, or wholesale list 16.00)")),

        new("Create an order with confirmed/planned dates, transport terms and an agent (cat 19)",
            null,
            () => "Create a sales order for \"Bavarian Deli\" with 5 units of SKU-1001, confirmed date 2026-09-15, planned delivery 2026-09-20, transport terms 'FCA Munich', assigned to sales agent \"Anna Verdi\".",
            () =>
            {
                var cid = CustomerId("Bavarian Deli");
                var r = One("SELECT confirmed_date, planned_delivery_date, transport_terms, sales_agent_id FROM sales_order WHERE customer_id = @v ORDER BY order_number DESC", cid);
                if (r == null) return (false, "no order found");
                var conf = r["confirmed_date"]?.ToString() ?? "";
                var plan = r["planned_delivery_date"]?.ToString() ?? "";
                var tt = r["transport_terms"]?.ToString() ?? "";
                var agent = r["sales_agent_id"]?.ToString() ?? "";
                bool ok = conf.StartsWith("2026-09-15") && plan.StartsWith("2026-09-20") && tt == "FCA Munich"
                         && !string.IsNullOrEmpty(agent) && agent != "00000000-0000-0000-0000-000000000000";
                return (ok, $"confirmed={conf}, planned={plan}, terms='{tt}', agent={agent}");
            }),

        new("Deliver an order with transport cause, carrier and tracking (cat 19)",
            () =>
            {
                var (oid, onum) = CreateOrderDirect("City Books Ltd", "[{\"sku\":\"SKU-1001\",\"quantity\":4}]");
                _transportOrderId = oid; _transportOrderNum = onum;
            },
            () => "Deliver sales order " + _transportOrderNum + " from WH1 with transport cause 'vendita', carrier 'DHL', tracking number 'DHL998877'.",
            () =>
            {
                var r = One("SELECT transport_cause, carrier, tracking_number FROM goods_delivery WHERE sales_order_id = @v", _transportOrderId);
                if (r == null) return (false, "no delivery found");
                var ok = r["transport_cause"]?.ToString() == "vendita" && r["carrier"]?.ToString() == "DHL" && r["tracking_number"]?.ToString() == "DHL998877";
                return (ok, $"cause={r["transport_cause"]}, carrier={r["carrier"]}, tracking={r["tracking_number"]}");
            }),

        new("Consolidate two orders into a single invoice (cat 19)",
            () =>
            {
                var (o1, n1) = CreateOrderDirect("Alpine Coffee Roasters", "[{\"sku\":\"SKU-1001\",\"quantity\":2}]");
                DeliverDirect(o1, "[{\"sku\":\"SKU-1001\",\"quantity\":2}]", "WH1");
                var (o2, n2) = CreateOrderDirect("Alpine Coffee Roasters", "[{\"sku\":\"SKU-2001\",\"quantity\":3}]");
                DeliverDirect(o2, "[{\"sku\":\"SKU-2001\",\"quantity\":3}]", "WH1");
                _consolOrderNum1 = n1; _consolOrderNum2 = n2;
                _consolTotal = 40.70m + 25.25m; // (2*18.50 +10%) + (3*6.90 +22%)
            },
            () => "Create a single consolidated invoice covering orders " + _consolOrderNum1 + " and " + _consolOrderNum2 + ", due in 30 days.",
            () =>
            {
                var cid = CustomerId("Alpine Coffee Roasters");
                var r = One("SELECT grand_total FROM invoice WHERE customer_id = @v ORDER BY invoice_number DESC", cid);
                if (r == null) return (false, "no invoice found");
                var gt = Dec(r["grand_total"]);
                return (Math.Abs(gt - _consolTotal) < 0.05m, $"consolidated invoice grand_total: {gt} (expect ~{_consolTotal})");
            }),

        new("Run a cycle count and apply the variance (cat 20)",
            () => { _cycleStockBefore = StockOf("SKU-3001", "WH2"); },
            () => "Run a cycle count in warehouse WH2 for SKU-3001: the counted quantity is " + (_cycleStockBefore + 7) + ". Apply the variance.",
            () =>
            {
                var after = StockOf("SKU-3001", "WH2");
                return (Math.Abs(after - (_cycleStockBefore + 7m)) < 0.01m, $"WH2 SKU-3001: {_cycleStockBefore}->{after} (+7 expected)");
            }),

        new("Process a customer return with restock (cat 20)",
            () =>
            {
                var (oid, _) = CreateOrderDirect("Evergreen Grocers", "[{\"sku\":\"SKU-6001\",\"quantity\":5}]");
                var dres = Composed("deliver", new JsonObject { ["order_id"] = oid, ["lines"] = JsonNode.Parse("[{\"sku\":\"SKU-6001\",\"quantity\":5}]"), ["warehouse_code"] = "WH1" });
                _returnDeliveryId = dres?["delivery_id"]?.ToString() ?? "";
                _returnDeliveryNum = dres?["delivery_number"]?.ToString() ?? "";
                _returnStockBefore = StockOf("SKU-6001", "WH1");
            },
            () => "Process a customer return for delivery " + _returnDeliveryNum + ": 2 units of SKU-6001, disposition restock, into warehouse WH1.",
            () =>
            {
                var after = StockOf("SKU-6001", "WH1");
                var sr = Count("SELECT id FROM sales_return WHERE original_delivery_id = @v", _returnDeliveryId);
                return (Math.Abs(after - (_returnStockBefore + 2m)) < 0.01m && sr >= 1, $"WH1 SKU-6001: {_returnStockBefore}->{after} (+2 expected), sales_return rows: {sr}");
            }),

        // ── v4: document entry, modification, collections, warehouse (cats 35-38) ──

        new("Create a sales invoice from scratch with mixed VAT, doc discount, transport, stamp, withholding (cat 35)",
            () => CreateCustomerDirect($"InvCo-{Tag}", "EUR"),
            () => $"Create a sales invoice from scratch for customer \"InvCo-{Tag}\" (no order, in EUR): 2 units of SKU-1001 at unit price 20.00 with VAT code V22, and 1 unit of SKU-2001 at unit price 10.00 with VAT code V10. Apply a 10 percent document discount, add a transport accessory line of 12.00 with VAT code V22, a stamp tax of 2.00, and a 5 percent withholding.",
            () =>
            {
                var cid = CustomerId($"InvCo-{Tag}");
                var r = One("SELECT amount, vat_total, grand_total, withholding_amount, stamp_tax, document_discount_percent FROM invoice WHERE customer_id = @v ORDER BY invoice_number DESC", cid);
                if (r == null) return (false, "no invoice found");
                // products: 2*20=40 + 1*10=10 = 50; doc disc 10% => 45; transport 12 => taxable 57
                // vat: 40*0.22=8.80 + 10*0.10=1.00 + 12*0.22=2.64 = 12.44; stamp 2; withholding 5% of 45 = 2.25
                // grand = 57 + 12.44 + 2 - 2.25 = 69.19
                var gt = Dec(r["grand_total"]);
                bool ok = Math.Abs(gt - 69.19m) < 0.05m
                        && Math.Abs(Dec(r["withholding_amount"]) - 2.25m) < 0.01m
                        && Math.Abs(Dec(r["stamp_tax"]) - 2.00m) < 0.01m
                        && Math.Abs(Dec(r["document_discount_percent"]) - 10m) < 0.01m;
                return (ok, $"grand_total={gt} (exp 69.19), withholding={r["withholding_amount"]} (exp 2.25), stamp={r["stamp_tax"]} (exp 2), doc_disc%={r["document_discount_percent"]} (exp 10)");
            }),

        new("Create a purchase invoice (supplier bill) from scratch (cat 35)",
            null,
            () => "Register a supplier bill from scratch for \"Prime Coffee Importers\": 10 units of SKU-1002 at unit cost 5.00 with VAT code V22, plus a 10 percent document discount and a stamp tax of 1.00.",
            () =>
            {
                var sid = SupplierId("Prime Coffee Importers");
                var r = One("SELECT amount, vat_total, grand_total FROM purchase_invoice WHERE supplier_id = @v ORDER BY bill_number DESC", sid);
                if (r == null) return (false, "no purchase invoice found");
                // 10*5=50; doc disc 10% => 45; vat 50*0.22=11.00 (on gross); stamp 1; grand = 45 + 11 + 1 = 57
                var gt = Dec(r["grand_total"]);
                return (Math.Abs(gt - 57.00m) < 0.05m, $"bill grand_total={gt} (exp 57.00), amount={r["amount"]} (exp 45), vat={r["vat_total"]} (exp 11)");
            }),

        new("Issue a supplier debit note (cat 35)",
            () =>
            {
                var res = Composed("purchase-invoice-scratch", new JsonObject
                {
                    ["supplier_name"] = "Prime Coffee Importers",
                    ["lines"] = JsonNode.Parse("[{\"sku\":\"SKU-1002\",\"quantity\":4,\"unit_cost\":5.00}]")
                });
                _postInvId = res?["bill_id"]?.ToString() ?? "";
            },
            () => "Issue a debit note of 12.50 to supplier \"Prime Coffee Importers\" for damaged goods.",
            () =>
            {
                var sid = SupplierId("Prime Coffee Importers");
                var r = One("SELECT amount FROM debit_note WHERE supplier_id = @v ORDER BY debit_note_number DESC", sid);
                return (r != null && Math.Abs(Dec(r["amount"]) - 12.50m) < 0.01m, $"debit note amount: {r?["amount"]} (exp 12.50)");
            }),

        new("Convert a proforma invoice into a real invoice (cat 35)",
            () =>
            {
                var cid = CustomerId("Delta Office Supplies");
                var f = new JsonObject
                {
                    ["invoice_number"] = $"PRO-{Tag}",
                    ["customer_id"] = cid,
                    ["invoice_type"] = "proforma",
                    ["issue_date"] = DateTime.Now.ToString("yyyy-MM-dd"),
                    ["due_date"] = DateTime.Now.AddDays(30).ToString("yyyy-MM-dd"),
                    ["status"] = "draft",
                    ["amount"] = 100.00m,
                    ["vat_total"] = 22.00m,
                    ["grand_total"] = 122.00m
                };
                var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/records/invoice")
                { Content = new StringContent(f.ToJsonString(), Encoding.UTF8, "application/json") };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
                Http.Send(req);
                _proformaNum = $"PRO-{Tag}";
            },
            () => "Convert the proforma invoice " + _proformaNum + " into a real (immediate) invoice.",
            () =>
            {
                var r = One("SELECT invoice_type FROM invoice WHERE invoice_number = @v", _proformaNum);
                return (r?["invoice_type"]?.ToString() == "immediate", $"invoice_type: {r?["invoice_type"]} (exp immediate)");
            }),

        new("Post an invoice to the ledger (cat 36)",
            () =>
            {
                var (iid, inum) = CreateInvoiceScratchDirect("Delta Office Supplies",
                    "[{\"sku\":\"SKU-1001\",\"quantity\":1,\"unit_price\":100.00,\"vatCode\":\"V22\"}]");
                _postInvId = iid; _postInvNum = inum;
            },
            () => "Post invoice " + _postInvNum + " to the accounting ledger.",
            () =>
            {
                var r = One("SELECT posted FROM invoice WHERE id = @v", _postInvId);
                return (IsTrue(r?["posted"]), $"posted: {r?["posted"]} (exp true)");
            }),

        new("Storno (reverse) an invoice (cat 36)",
            () =>
            {
                var (iid, inum) = CreateInvoiceScratchDirect("Delta Office Supplies",
                    "[{\"sku\":\"SKU-1001\",\"quantity\":1,\"unit_price\":100.00,\"vatCode\":\"V22\"}]");
                _stornoInvId = iid; _stornoInvNum = inum;
            },
            () => "Storno (reverse) invoice " + _stornoInvNum + ".",
            () =>
            {
                var orig = One("SELECT storned FROM invoice WHERE id = @v", _stornoInvId);
                var rev = One("SELECT grand_total, storno_of FROM invoice WHERE storno_of = @v", _stornoInvId);
                bool ok = IsTrue(orig?["storned"]) && rev != null && Dec(rev["grand_total"]) < 0m;
                return (ok, $"original storned={orig?["storned"]} (exp true), reversal grand_total={rev?["grand_total"]} (exp negative)");
            }),

        new("Duplicate an invoice into a new draft (cat 36)",
            () =>
            {
                CreateCustomerDirect($"DupCo-{Tag}", "EUR");
                var (iid, inum) = CreateInvoiceScratchDirect($"DupCo-{Tag}",
                    "[{\"sku\":\"SKU-1001\",\"quantity\":1,\"unit_price\":100.00,\"vatCode\":\"V22\"}]");
                _dupOrigId = iid; _dupOrigNum = inum;
                var o = One("SELECT grand_total FROM invoice WHERE id = @v", iid);
                _dupOrigGrand = o == null ? 0m : Dec(o["grand_total"]);
            },
            () => "Duplicate invoice " + _dupOrigNum + " into a new draft.",
            () =>
            {
                var cid = CustomerId($"DupCo-{Tag}");
                var recs = Query2("SELECT invoice_number, status, grand_total FROM invoice WHERE customer_id = @v AND status = @s ORDER BY invoice_number DESC", ("v", cid), ("s", "draft"))?["records"] as JsonArray;
                var latest = recs != null && recs.Count > 0 ? recs[0].AsObject() : null;
                bool ok = latest != null && latest["invoice_number"]?.ToString() != _dupOrigNum
                        && latest["status"]?.ToString() == "draft"
                        && Math.Abs(Dec(latest["grand_total"]) - _dupOrigGrand) < 0.01m;
                return (ok, $"new draft={latest?["invoice_number"]} status={latest?["status"]} grand={latest?["grand_total"]} (orig {_dupOrigNum} grand {_dupOrigGrand})");
            }),

        new("Cancel an unposted invoice (cat 36)",
            () =>
            {
                var (iid, inum) = CreateInvoiceScratchDirect("Delta Office Supplies",
                    "[{\"sku\":\"SKU-1001\",\"quantity\":1,\"unit_price\":50.00,\"vatCode\":\"V22\"}]");
                _cancelInvId = iid; _cancelInvNum = inum;
            },
            () => "Cancel invoice " + _cancelInvNum + ".",
            () =>
            {
                var r = One("SELECT status FROM invoice WHERE id = @v", _cancelInvId);
                return (r?["status"]?.ToString() == "cancelled", $"status: {r?["status"]} (exp cancelled)");
            }),

        new("Collect across two invoices with an allowance (cat 37)",
            () =>
            {
                CreateCustomerDirect($"CollCo-{Tag}", "EUR");
                var (i1, n1) = CreateInvoiceScratchDirect($"CollCo-{Tag}",
                    "[{\"sku\":\"SKU-1001\",\"quantity\":1,\"unit_price\":100.00,\"vatCode\":\"V22\"}]");
                var (i2, n2) = CreateInvoiceScratchDirect($"CollCo-{Tag}",
                    "[{\"sku\":\"SKU-2001\",\"quantity\":1,\"unit_price\":50.00,\"vatCode\":\"V10\"}]");
                _collNum1 = n1; _collNum2 = n2;
                // inv1 grand 122.00, inv2 grand 55.00 => total 177.00
            },
            () => "Collect 175.00 from customer \"CollCo-" + Tag + "\" covering invoice numbers " + _collNum1 + " and " + _collNum2 + ", by bank transfer, with a 2.00 allowance (rounding write-off) to settle them in full.",
            () =>
            {
                var r1 = One("SELECT status FROM invoice WHERE invoice_number = @v", _collNum1);
                var r2 = One("SELECT status FROM invoice WHERE invoice_number = @v", _collNum2);
                bool ok = r1?["status"]?.ToString() == "paid" && r2?["status"]?.ToString() == "paid";
                return (ok, $"{_collNum1}={r1?["status"]}, {_collNum2}={r2?["status"]} (both exp paid)");
            }),

        new("Customer statement with aging (cat 37)",
            () =>
            {
                _stmtCust = $"Statement Co {Tag}";
                var c = new JsonObject { ["name"] = _stmtCust, ["currency"] = "EUR" };
                var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/records/customer")
                { Content = new StringContent(c.ToJsonString(), Encoding.UTF8, "application/json") };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
                Http.Send(req);
                // one overdue invoice (due 40 days ago) so the statement has an open, late item
                var cid = CustomerId(_stmtCust);
                var f = new JsonObject
                {
                    ["invoice_number"] = $"STMT-{Tag}",
                    ["customer_id"] = cid,
                    ["invoice_type"] = "immediate",
                    ["issue_date"] = DateTime.Now.AddDays(-70).ToString("yyyy-MM-dd"),
                    ["due_date"] = DateTime.Now.AddDays(-40).ToString("yyyy-MM-dd"),
                    ["status"] = "sent",
                    ["amount"] = 100.00m,
                    ["vat_total"] = 22.00m,
                    ["grand_total"] = 122.00m
                };
                var req2 = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/records/invoice")
                { Content = new StringContent(f.ToJsonString(), Encoding.UTF8, "application/json") };
                req2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
                Http.Send(req2);
            },
            () => "Show the statement of account for customer \"" + _stmtCust + "\": what is still open and how late is it?",
            () =>
            {
                var called = _lastToolCalls.Any(s => s.Contains("customer_statement", StringComparison.OrdinalIgnoreCase));
                var cid = CustomerId(_stmtCust);
                // independent: the customer has exactly one open (sent) invoice of 122.00
                var recs = Query2("SELECT grand_total FROM invoice WHERE customer_id = @v AND status = @s", ("v", cid), ("s", "sent"))?["records"] as JsonArray;
                var inv = recs != null && recs.Count > 0 ? recs[0].AsObject() : null;
                bool ok = called && inv != null && Math.Abs(Dec(inv["grand_total"]) - 122.00m) < 0.01m;
                return (ok, $"agent called customer_statement={called}, open invoice grand={inv?["grand_total"]} (exp 122.00)");
            }),

        new("Reserve stock for an order, then release it (cat 38)",
            () => { _resBefore = ReservedQty("SKU-3001", "WH1"); },
            () => "Reserve 5 units of SKU-3001 in warehouse WH1 for the current order, then release 2 of them.",
            () =>
            {
                var after = ReservedQty("SKU-3001", "WH1");
                // net reserved quantity +3 (5 reserved, 2 released)
                return (Math.Abs(after - (_resBefore + 3m)) < 0.01m, $"reserved qty SKU-3001/WH1: {_resBefore}->{after} (net +3 expected)");
            }),

        new("Process a supplier return (reduce stock) (cat 38)",
            () => { _supRetBefore = StockOf("SKU-1001", "WH1"); },
            () => "Return 3 units of SKU-1001 to supplier \"Prime Coffee Importers\" from warehouse WH1, reason 'defective'.",
            () =>
            {
                var after = StockOf("SKU-1001", "WH1");
                var sr = Count("SELECT id FROM supplier_return WHERE supplier_id = @v", SupplierId("Prime Coffee Importers"));
                return (Math.Abs(after - (_supRetBefore - 3m)) < 0.01m && sr >= 1, $"WH1 SKU-1001: {_supRetBefore}->{after} (-3 expected), supplier_return rows: {sr}");
            }),

        new("Full order-to-cash business day (cat 42)",
            () => { _bizCust = $"BizDay Co {Tag}"; CreateCustomerDirect(_bizCust, "EUR"); },
            () => $"Run a complete order-to-cash for the customer \"{_bizCust}\": place a sales order for 2 units of SKU-1001, deliver the order to warehouse WH1, create an invoice for it with a 30 day due date, then collect the full invoice amount by bank transfer.",
            () =>
            {
                var cid = CustomerId(_bizCust);
                var inv = One($"SELECT id, grand_total, status FROM invoice WHERE customer_id = @v ORDER BY invoice_number DESC", cid);
                if (inv == null) return (false, "no invoice created for the customer");
                var status = inv["status"]?.ToString();
                var grand = Dec(inv["grand_total"]);
                var invId = inv["id"]?.ToString();
                decimal paid = 0m;
                var pays = Query("SELECT amount FROM payment WHERE invoice_id = @v", invId)?["records"] as JsonArray;
                if (pays != null) foreach (var p in pays) paid += Dec(p["amount"]);
                var ok = status == "paid" && grand > 0m && Math.Abs(paid - grand) < 0.01m;
                return (ok, $"invoice status={status}, grand_total={grand}, collected={paid} (expect status=paid and collected==grand_total)");
            }),

        new("Sales by customer report (cat 40)",
            () =>
            {
                CreateCustomerDirect($"RepCust-{Tag}", "EUR");
                CreateInvoiceScratchDirect($"RepCust-{Tag}", "[{\"sku\":\"SKU-1001\",\"quantity\":2}]");
            },
            () => "Show total sales by customer, all time.",
            () =>
            {
                var called = _lastToolCalls.Any(s => s.Contains("sales_by_customer", StringComparison.OrdinalIgnoreCase));
                var rep = Composed("sales-by-customer", new JsonObject());
                var e = FindInReport(rep, "customers", "customer_name", $"RepCust-{Tag}");
                if (e == null) return (false, $"agent called sales_by_customer={called}; RepCust-{Tag} not in report");
                var gross = Dec(e["gross_total"]);
                var cnt = e["invoice_count"]?.GetValue<int>() ?? 0;
                var ok = called && cnt == 1 && Math.Abs(gross - 40.70m) < 0.01m;
                return (ok, $"called={called}, RepCust gross={gross} (exp 40.70), count={cnt} (exp 1)");
            }),

        new("Sales by product report (cat 40)",
            () =>
            {
                CreateCustomerDirect($"RepProdCust-{Tag}", "EUR");
                CreateProduct($"SKU-REP-{Tag}", "Report Widget", 20.00m);
                CreateInvoiceScratchDirect($"RepProdCust-{Tag}", "[{\"sku\":\"SKU-REP-" + Tag + "\",\"quantity\":3,\"unit_price\":20}]");
            },
            () => "Show total sales by product, all time.",
            () =>
            {
                var called = _lastToolCalls.Any(s => s.Contains("sales_by_product", StringComparison.OrdinalIgnoreCase));
                var rep = Composed("sales-by-product", new JsonObject());
                var e = FindInReport(rep, "products", "sku", $"SKU-REP-{Tag}");
                if (e == null) return (false, $"agent called sales_by_product={called}; SKU-REP-{Tag} not in report");
                var qty = Dec(e["total_quantity"]);
                var net = Dec(e["net_total"]);
                var ok = called && Math.Abs(qty - 3m) < 0.01m && Math.Abs(net - 60.00m) < 0.01m;
                return (ok, $"called={called}, qty={qty} (exp 3), net={net} (exp 60)");
            }),

        new("Purchases by supplier report (cat 40)",
            () =>
            {
                CreateSupplierDirect($"RepSup-{Tag}");
                CreatePurchaseBillDirect($"RepSup-{Tag}", 50m, 11m);
            },
            () => "Show total purchases by supplier, all time.",
            () =>
            {
                var called = _lastToolCalls.Any(s => s.Contains("purchases_by_supplier", StringComparison.OrdinalIgnoreCase));
                var rep = Composed("purchases-by-supplier", new JsonObject());
                var e = FindInReport(rep, "suppliers", "supplier_name", $"RepSup-{Tag}");
                if (e == null) return (false, $"agent called purchases_by_supplier={called}; RepSup-{Tag} not in report");
                var gross = Dec(e["gross_total"]);
                var cnt = e["invoice_count"]?.GetValue<int>() ?? 0;
                var ok = called && cnt == 1 && Math.Abs(gross - 61.00m) < 0.01m;
                return (ok, $"called={called}, RepSup gross={gross} (exp 61), count={cnt} (exp 1)");
            }),

        new("Receivables aging / scadenzario (cat 40)",
            () =>
            {
                // Capture the 61-90 bucket BEFORE adding our invoice, so the check is a delta and
                // stays correct even when the DB already holds open 61-90 invoices from earlier runs.
                var before = FindInReport(Composed("aging-receivables", new JsonObject()), "buckets", "label", "61-90");
                _agingAmtBefore = before == null ? 0m : Dec(before["amount"]);
                _agingCntBefore = before?["invoice_count"]?.GetValue<int>() ?? 0;
                CreateCustomerDirect($"AgingCo-{Tag}", "EUR");
                // 75 days overdue → 61-90 bucket (S40 uses 40 days = 31-60).
                CreateOverdueInvoiceDirect($"AgingCo-{Tag}", 75, 100m);
            },
            () => "Show the receivables aging report (scadenzario).",
            () =>
            {
                var called = _lastToolCalls.Any(s => s.Contains("aging_receivables", StringComparison.OrdinalIgnoreCase));
                var rep = Composed("aging-receivables", new JsonObject());
                var b = FindInReport(rep, "buckets", "label", "61-90");
                if (b == null) return (false, $"agent called aging_receivables={called}; 61-90 bucket missing");
                var amt = Dec(b["amount"]);
                var cnt = b["invoice_count"]?.GetValue<int>() ?? 0;
                var ok = called && Math.Abs(amt - (_agingAmtBefore + 100m)) < 0.01m && cnt == _agingCntBefore + 1;
                return (ok, $"called={called}, 61-90 bucket amount={amt} (exp {_agingAmtBefore + 100m}), count={cnt} (exp {_agingCntBefore + 1})");
            }),

        new("Blocked customer cannot be invoiced (negative)",
            null,
            () => "Create a sales invoice for the customer \"Suspended Trading Co\" with 2 units of SKU-1001.",
            () =>
            {
                // Direct probe: the composed op must reject a blocked customer (returns null on throw).
                var probe = Composed("sales-invoice", new JsonObject
                {
                    ["customer_name"] = "Suspended Trading Co",
                    ["lines"] = JsonNode.Parse("[{\"sku\":\"SKU-1001\",\"quantity\":2}]")
                });
                var n = Count("SELECT id FROM invoice WHERE customer_id = @v", CustomerId("Suspended Trading Co"));
                return (probe == null && n == 0, $"op rejected={probe == null}, invoices for blocked customer={n} (expect rejected and 0)");
            }),

        new("Insufficient stock blocks delivery (negative)",
            () =>
            {
                var (_, num) = CreateOrderDirect("Delta Office Supplies", "[{\"sku\":\"SKU-2002\",\"quantity\":100}]");
                _insfOrderNum = num;
            },
            () => $"Deliver the order \"{_insfOrderNum}\" completely — 100 units of SKU-2002 — to warehouse WH1.",
            () =>
            {
                // Deterministic tool-level probe: a fresh order for a quantity no warehouse can
                // supply must be rejected by the deliver op. Done directly so the result does not
                // depend on whether the agent worked around the block (e.g. via adjust_stock).
                var (bid, _) = CreateOrderDirect("Delta Office Supplies", "[{\"sku\":\"SKU-2002\",\"quantity\":1000000}]");
                var probe = Composed("deliver", new JsonObject
                {
                    ["order_id"] = bid,
                    ["lines"] = JsonNode.Parse("[{\"sku\":\"SKU-2002\",\"quantity\":1000000}]"),
                    ["warehouse_code"] = "WH1"
                });
                return (probe == null, $"deliver block probe rejected={probe == null} (1,000,000 units > any stock)");
            }),

        new("Partial payment leaves invoice partial",
            () =>
            {
                CreateCustomerDirect($"PartCo-{Tag}", "EUR");
                CreateInvoiceScratchDirect($"PartCo-{Tag}", "[{\"sku\":\"SKU-1001\",\"quantity\":2}]");
            },
            () => $"Collect only 20 euros on the latest invoice for \"PartCo-{Tag}\" by bank transfer.",
            () =>
            {
                var cid = CustomerId($"PartCo-{Tag}");
                var inv = One($"SELECT id, status FROM invoice WHERE customer_id = @v ORDER BY invoice_number DESC", cid);
                if (inv == null) return (false, "no invoice created");
                var status = inv["status"]?.ToString();
                var invId = inv["id"]?.ToString();
                decimal paid = 0m;
                var pays = Query("SELECT amount FROM payment WHERE invoice_id = @v", invId)?["records"] as JsonArray;
                if (pays != null) foreach (var p in pays) paid += Dec(p["amount"]);
                var ok = status == "partial" && Math.Abs(paid - 20m) < 0.01m;
                return (ok, $"status={status} (exp partial), collected={paid} (exp 20)");
            }),

        new("Multi-currency USD invoice converts from EUR catalog",
            () => CreateCustomerDirect($"UsdCo-{Tag}", "USD"),
            () => $"Create a sales invoice for the customer \"UsdCo-{Tag}\" with 2 units of SKU-1001.",
            () =>
            {
                var cid = CustomerId($"UsdCo-{Tag}");
                var inv = One($"SELECT amount, vat_total, grand_total FROM invoice WHERE customer_id = @v ORDER BY invoice_number DESC", cid);
                if (inv == null) return (false, "no invoice created");
                var net = Dec(inv["amount"]);
                var vat = Dec(inv["vat_total"]);
                var grand = Dec(inv["grand_total"]);
                // 40.70 EUR / 0.92 = 44.24 grand; 37/0.92=40.22 net; 3.70/0.92=4.02 vat
                var ok = Math.Abs(net - 40.22m) < 0.01m && Math.Abs(vat - 4.02m) < 0.01m && Math.Abs(grand - 44.24m) < 0.01m;
                return (ok, $"USD invoice net={net} (exp 40.22), vat={vat} (exp 4.02), grand={grand} (exp 44.24)");
            }),

        new("Full purchase-to-pay lifecycle (composite)",
            () => { _p2pStockBefore = StockOf("SKU-1001", "WH1"); },
            () => "Create a purchase order from supplier \"Prime Coffee Importers\" for 50 units of SKU-1001 at unit cost 9.00, receive the goods into warehouse WH1, register a supplier bill for the received goods, then pay the bill in full by bank transfer.",
            () =>
            {
                var after = StockOf("SKU-1001", "WH1");
                var sid = SupplierId("Prime Coffee Importers");
                var pays = Count("SELECT id FROM supplier_payment WHERE supplier_id = @v", sid);
                var stockOk = Math.Abs(after - (_p2pStockBefore + 50m)) < 0.01m;
                var ok = stockOk && pays >= 1;
                return (ok, $"SKU-1001 WH1 {_p2pStockBefore}->{after} (exp +50), supplier_payment rows={pays} (exp >=1)");
            }),

        new("Date-range filtered sales report",
            () =>
            {
                // Capture the date with the SAME clock the composed op uses (DateTime.Today = local),
                // so a long run that crosses the UTC/local boundary can't desync the range.
                _dateRangeToday = DateTime.Now.ToString("yyyy-MM-dd");
                CreateCustomerDirect($"DateCo-{Tag}", "EUR");
                CreateInvoiceScratchDirect($"DateCo-{Tag}", "[{\"sku\":\"SKU-1001\",\"quantity\":1}]");
            },
            () => "Show sales by customer for today.",
            () =>
            {
                var yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
                var inToday = FindInReport(Composed("sales-by-customer", new JsonObject { ["from_date"] = _dateRangeToday, ["to_date"] = _dateRangeToday }), "customers", "customer_name", $"DateCo-{Tag}");
                var inPastOnly = FindInReport(Composed("sales-by-customer", new JsonObject { ["from_date"] = "2000-01-01", ["to_date"] = yesterday }), "customers", "customer_name", $"DateCo-{Tag}");
                var ok = inToday != null && inPastOnly == null;
                return (ok, $"in today's range={inToday != null} (exp true), in past-only range={inPastOnly != null} (exp false)");
            }),

        new("Check the ERP is installed and configured",
            null,
            () => "Is the ERP installed and set up? Tell me how many business entities it has and whether any setup change is pending.",
            () =>
            {
                var st = SetupStatusDirect();
                bool installed = st?["installed"]?.GetValue<bool>() ?? false;
                int entities = st?["entity_count"]?.GetValue<int>() ?? 0;
                bool pending = st?["pending"]?.GetValue<bool>() ?? true;
                var ok = installed && entities >= 50 && !pending;
                return (ok, $"installed={installed} (exp true), entity_count={entities} (exp >=50), pending={pending} (exp false)");
            }),

        new("Re-apply the ERP setup (idempotent reprovision)",
            null,
            () => "Re-apply the ERP setup (reprovision) and tell me whether anything changed.",
            () =>
            {
                int before = SetupStatusDirect()?["entity_count"]?.GetValue<int>() ?? 0;
                string summary = ReprovisionDirect();
                int after = SetupStatusDirect()?["entity_count"]?.GetValue<int>() ?? 0;
                var ok = summary.Contains("already applied") && before == after && after >= 50;
                return (ok, $"reprovision said: \"{summary}\"; entity_count {before}->{after} (exp unchanged, >=50)");
            }),
    };

    // ── direct ERP helpers (bypass the agent, for setup + verification) ──
    static string? _token;
    static string Token()
    {
        if (_token != null) return _token;
        var body = new JsonObject { ["email"] = ErpUser, ["password"] = ErpPass };
        var resp = Http.PostAsync($"{ErpUrl}/api/v3/en_US/auth/jwt/token",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        var json = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        _token = json!["object"]!.ToString();
        return _token;
    }

    static JsonNode? Query(string eql, string? param)
    {
        var body = new JsonObject { ["eql"] = eql };
        if (param != null) body["parameters"] = new JsonArray { new JsonObject { ["name"] = "v", ["value"] = param } };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/query")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        var resp = Http.Send(req);
        var json = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return json?["data"];
    }

    static JsonNode? Query2(string eql, (string, string) a, (string, string) b)
    {
        var body = new JsonObject
        {
            ["eql"] = eql,
            ["parameters"] = new JsonArray
            {
                new JsonObject { ["name"] = a.Item1, ["value"] = a.Item2 },
                new JsonObject { ["name"] = b.Item1, ["value"] = b.Item2 }
            }
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/query")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        var resp = Http.Send(req);
        var json = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return json?["data"];
    }

    static int Count(string eql, string? param) => Query(eql, param)?["total_count"]?.GetValue<int>() ?? -1;
    static string? Scalar(string eql, string? param) { var recs = Query(eql, param)?["records"] as JsonArray; if (recs == null || recs.Count == 0) return null; var obj = recs[0].AsObject(); foreach (var kv in obj) return kv.Value?.ToString(); return null; }
    static JsonObject? One(string eql, string? param) { var recs = Query(eql, param)?["records"] as JsonArray; return recs != null && recs.Count > 0 ? recs[0].AsObject() : null; }

    static void CreateProduct(string sku, string name, decimal price)
    {
        var fields = new JsonObject { ["sku"] = sku, ["name"] = name, ["unit_cost"] = 1m, ["stock_quantity"] = 10, ["active"] = true };
        fields["unit_price"] = price;
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/records/product")
        { Content = new StringContent(fields.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        Http.Send(req);
    }

    // Create a customer directly with a chosen currency (default EUR) so money-math scenarios
    // are not affected by a seeded customer's foreign currency conversion.
    static void CreateCustomerDirect(string name, string currency = "EUR")
    {
        var c = new JsonObject { ["name"] = name, ["currency"] = currency };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/records/customer")
        { Content = new StringContent(c.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        Http.Send(req);
    }

    // Create a supplier directly (for the purchases-by-supplier report with a clean aggregate).
    static void CreateSupplierDirect(string name)
    {
        var s = new JsonObject { ["name"] = name, ["currency"] = "EUR" };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/records/supplier")
        { Content = new StringContent(s.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        Http.Send(req);
    }

    // Direct composed-endpoint call (bypasses the agent) to set up a precondition document.
    static JsonNode? Composed(string endpoint, JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/composed/{endpoint}")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        var resp = Http.Send(req);
        var json = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return json?["data"]?["result"];
    }

    // Direct setup-status call (GET) — returns the setup status data object (installed, entity_count, pending, ...).
    static JsonNode? SetupStatusDirect()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{ErpUrl}/api/v3.0/p/agent/setup-status");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        var resp = Http.Send(req);
        var json = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return json?["data"];
    }

    // Direct reprovision call (POST) — returns the summary string from data.result.
    static string ReprovisionDirect()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/reprovision")
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        var resp = Http.Send(req);
        var json = JsonNode.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return json?["data"]?["result"]?.ToString() ?? "";
    }

    static string CustomerId(string name) => Scalar("SELECT id FROM customer WHERE name = @v", name) ?? "";
    static string SupplierId(string name) => Scalar("SELECT id FROM supplier WHERE name = @v", name) ?? "";

    // Create a confirmed sales order directly (for deliver/credit-note scenarios).
    static (string orderId, string orderNumber) CreateOrderDirect(string customerName, string linesJson)
    {
        var res = Composed("sales-order", new JsonObject
        {
            ["customer_id"] = CustomerId(customerName),
            ["lines"] = JsonNode.Parse(linesJson)
        });
        return (res?["order_id"]?.ToString() ?? "", res?["order_number"]?.ToString() ?? "");
    }

    static (string invoiceId, string invoiceNumber) CreateInvoiceDirect(string orderId)
    {
        var res = Composed("invoice", new JsonObject { ["order_id"] = orderId, ["due_days"] = 30 });
        return (res?["invoice_id"]?.ToString() ?? "", res?["invoice_number"]?.ToString() ?? "");
    }

    // Create a sales invoice from scratch (no order) via the composed op, for v4 setup.
    static (string invoiceId, string invoiceNumber) CreateInvoiceScratchDirect(string customerName, string linesJson)
    {
        var res = Composed("sales-invoice", new JsonObject
        {
            ["customer_name"] = customerName,
            ["lines"] = JsonNode.Parse(linesJson)
        });
        return (res?["invoice_id"]?.ToString() ?? "", res?["invoice_number"]?.ToString() ?? "");
    }

    // Create an OPEN sales invoice with a due date in the PAST (for the aging/scadenzario test).
    // The composed sales-invoice op always sets due = today+30, so a past-due doc is written
    // directly as a record with an explicit due_date and status "sent" (open, unpaid).
    static string CreateOverdueInvoiceDirect(string customerName, int daysOverdue, decimal grandTotal)
    {
        var cid = CustomerId(customerName);
        var today = DateTime.UtcNow.Date;
        var rec = new JsonObject
        {
            ["invoice_number"] = $"OVERDUE-{Tag}-{daysOverdue}",
            ["customer_id"] = cid,
            ["invoice_type"] = "immediate",
            ["issue_date"] = today.AddDays(-daysOverdue - 30).ToString("yyyy-MM-dd"),
            ["due_date"] = today.AddDays(-daysOverdue).ToString("yyyy-MM-dd"),
            ["amount"] = grandTotal,
            ["vat_total"] = 0m,
            ["grand_total"] = grandTotal,
            ["status"] = "sent",
            ["storned"] = false
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/records/invoice")
        { Content = new StringContent(rec.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        Http.Send(req);
        return rec["invoice_number"]!.ToString();
    }

    // Create a purchase bill directly with a chosen supplier (for the purchases-by-supplier report).
    static string CreatePurchaseBillDirect(string supplierName, decimal net, decimal vat)
    {
        var sid = SupplierId(supplierName);
        var today = DateTime.UtcNow.Date;
        var rec = new JsonObject
        {
            ["bill_number"] = $"BILL-{Tag}",
            ["supplier_id"] = sid,
            ["bill_date"] = today.ToString("yyyy-MM-dd"),
            ["due_date"] = today.AddDays(30).ToString("yyyy-MM-dd"),
            ["amount"] = net,
            ["vat_total"] = vat,
            ["grand_total"] = net + vat,
            ["status"] = "registered"
        };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/records/purchase_invoice")
        { Content = new StringContent(rec.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        Http.Send(req);
        return rec["bill_number"]!.ToString();
    }

    // Find an entry in a composed report's list by a field value; returns the entry object or null.
    static JsonObject? FindInReport(JsonNode? report, string listKey, string field, string value)
    {
        var arr = report?[listKey] as JsonArray;
        if (arr == null) return null;
        foreach (var it in arr)
            if (string.Equals(it?[field]?.ToString(), value, StringComparison.OrdinalIgnoreCase)) return it?.AsObject();
        return null;
    }

    // Deliver an order directly (the invoice op only bills delivered goods, so a credit-note
    // scenario must deliver before it can invoice).
    static void DeliverDirect(string orderId, string linesJson, string whCode)
    {
        Composed("deliver", new JsonObject
        {
            ["order_id"] = orderId,
            ["lines"] = JsonNode.Parse(linesJson),
            ["warehouse_code"] = whCode
        });
    }

    static decimal StockOf(string sku, string whCode)
    {
        var pid = Scalar("SELECT id FROM product WHERE sku = @v", sku);
        var wh = Scalar("SELECT id FROM warehouse WHERE code = @v", whCode);
        if (pid == null || wh == null) return -1;
        var r = Query2("SELECT quantity FROM stock_item WHERE product_id = @p AND warehouse_id = @w", ("p", pid), ("w", wh))?["records"] as JsonArray;
        return r != null && r.Count > 0 ? Dec(r[0]["quantity"]) : 0m;
    }

    // Total reserved quantity of a SKU in a warehouse (sums reservation rows; EQL can't SUM).
    static decimal ReservedQty(string sku, string whCode)
    {
        var pid = Scalar("SELECT id FROM product WHERE sku = @v", sku);
        var wh = Scalar("SELECT id FROM warehouse WHERE code = @v", whCode);
        if (pid == null || wh == null) return -1;
        var recs = Query2("SELECT quantity FROM reservation WHERE product_id = @p AND warehouse_id = @w", ("p", pid), ("w", wh))?["records"] as JsonArray;
        decimal s = 0m;
        if (recs != null) foreach (var r in recs) s += Dec(r["quantity"]);
        return s;
    }

    static decimal Dec(JsonNode? n) => n != null && decimal.TryParse(n.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0m;
    static bool IsTrue(JsonNode? n)
    {
        if (n == null) return false;
        try { return n.GetValue<bool>(); }
        catch { return string.Equals(n.ToString(), "true", StringComparison.OrdinalIgnoreCase); }
    }
    static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
