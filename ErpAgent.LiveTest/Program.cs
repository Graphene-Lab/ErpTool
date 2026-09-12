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

            var steps = new List<string>();
            void OnStep(string line, bool mon) { if (line.Contains("ErpTool")) steps.Add(line); }
            Log.OnLogStepMonitor += OnStep;

            AgentResult result;
            try
            {
                var harness = new AgentHarness("DeepSeekBridge");
                result = harness.ExecuteAction(sc.Prompt, new[] { "ErpTool" }, maxIterations: 12);
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

            var (ok, detail) = sc.Verify();
            Console.WriteLine(ok ? $"VERIFY: PASS — {detail}" : $"VERIFY: FAIL — {detail}");
            if (ok && result.Success) pass++; else fail++;
        }

        Console.WriteLine($"\n================ LIVE AGENT RESULTS: {pass} passed / {fail} failed ================");
        return fail == 0 ? 0 : 1;
    }

    record Scenario(string Name, Action? Setup, string Prompt, Func<(bool, string)> Verify);

    static List<Scenario> BuildScenarios() => new()
    {
        new("Create a new customer",
            null,
            $"Create a new customer named \"Acme Industrial {Tag}\" with email sales@acme{Tag}.example, city Milan, country Italy, and category wholesale.",
            () => { var n = Count($"SELECT id FROM customer WHERE name = @v", Tag != null ? $"Acme Industrial {Tag}" : ""); return (n == 1, $"customers with that name: {n}"); }),

        new("Create a product",
            null,
            $"Add a product with SKU \"SKU-{Tag}A\", name \"Live Widget\", unit price 7.50, unit cost 3.00, stock 50, category Gadgets.",
            () => { var r = One($"SELECT unit_price FROM product WHERE sku = @v", $"SKU-{Tag}A"); return (r != null && Dec(r["unit_price"]) == 7.50m, $"unit_price: {r?["unit_price"]}"); }),

        new("Update a product price",
            () => CreateProduct($"SKU-{Tag}B", "Tuneable Widget", 10.00m),
            $"Change the unit price of the product with SKU \"SKU-{Tag}B\" to 8.25.",
            () => { var r = One($"SELECT unit_price FROM product WHERE sku = @v", $"SKU-{Tag}B"); return (r != null && Dec(r["unit_price"]) == 8.25m, $"unit_price: {r?["unit_price"]}"); }),

        new("Count products in a category",
            null,
            "How many products do we have in the Coffee category?",
            () => { var n = Count("SELECT id FROM product WHERE category = @v", "Coffee"); return (true, $"actual Coffee count={n} (check agent message says {n})"); }),

        new("Place a sales order for an existing customer",
            null,
            "Place a sales order for the customer \"Delta Office Supplies\" with 3 units of SKU-1001 and 10 units of SKU-2001. Use a 5 percent discount on the SKU-2001 line.",
            () =>
            {
                var cid = Scalar("SELECT id FROM customer WHERE name = @v", "Delta Office Supplies");
                if (cid == null) return (false, "customer not found");
                var r = One($"SELECT total FROM sales_order WHERE customer_id = @v ORDER BY order_number DESC", cid);
                return (r != null && Math.Abs(Dec(r["total"]) - 121.05m) < 0.01m, $"latest order total: {r?["total"]} (expected 121.05)");
            }),

        new("Invoice the latest order",
            null,
            "Find the most recent sales order for \"Delta Office Supplies\" and create an invoice for it with a 30 day due date.",
            () =>
            {
                var cid = Scalar("SELECT id FROM customer WHERE name = @v", "Delta Office Supplies");
                var r = One($"SELECT status FROM invoice WHERE customer_id = @v ORDER BY invoice_number DESC", cid!);
                return (r != null && r["status"]?.ToString() == "sent", $"latest invoice status: {r?["status"]}");
            }),

        new("Receive a purchase order (stock in)",
            null,
            "Create a purchase order from supplier \"Prime Coffee Importers\" for 100 units of SKU-1001 at unit cost 9.00, then receive it so the stock is added.",
            () =>
            {
                var r = One("SELECT stock_quantity FROM product WHERE sku = @v", "SKU-1001");
                var stock = r == null ? 0 : Dec(r["stock_quantity"]);
                return (stock >= 340m, $"SKU-1001 stock now: {stock} (expected >= 340 after +100)");
            }),

        new("Low-stock report",
            null,
            "Which products are below their reorder point? List their SKUs.",
            () => (true, "manual check: expect SKU-2002 and SKU-5002 in the answer")),

        new("Look up a customer email",
            null,
            "What is the email address of the customer \"City Books Ltd\"?",
            () => (true, "manual check: expect orders@citybooks.example")),

        new("Create a supplier",
            null,
            $"Add a supplier named \"Live Components Ltd {Tag}\" with contact person Jane Doe and email jane@livecomp{Tag}.example.",
            () => { var n = Count("SELECT id FROM supplier WHERE name = @v", $"Live Components Ltd {Tag}"); return (n == 1, $"suppliers with that name: {n}"); }),

        new("Delete a product",
            () => CreateProduct($"SKU-{Tag}C", "Doomed Widget", 1.00m),
            $"Delete the product with SKU \"SKU-{Tag}C\".",
            () => { var n = Count("SELECT id FROM product WHERE sku = @v", $"SKU-{Tag}C"); return (n == 0, $"products with that sku: {n}"); }),

        new("Total customer count",
            null,
            "How many customers do we have in total?",
            () => { var n = Count("SELECT id FROM customer", null); return (true, $"actual customer count={n} (check agent message)"); }),
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

    static int Count(string eql, string? param) => Query(eql, param)?["total_count"]?.GetValue<int>() ?? -1;
    static string? Scalar(string eql, string? param) { var recs = Query(eql, param)?["records"] as JsonArray; if (recs == null || recs.Count == 0) return null; var obj = recs[0].AsObject(); foreach (var kv in obj) return kv.Value?.ToString(); return null; }
    static JsonObject? One(string eql, string? param) { var recs = Query(eql, param)?["records"] as JsonArray; return recs != null && recs.Count > 0 ? recs[0].AsObject() : null; }

    static void CreateProduct(string sku, string name, decimal price)
    {
        var fields = new JsonObject { ["sku"] = sku, ["name"] = name, ["unit_price"] = price, ["unit_cost"] = 1m, ["stock_quantity"] = 10, ["active"] = true };
        var req = new HttpRequestMessage(HttpMethod.Post, $"{ErpUrl}/api/v3.0/p/agent/records/product")
        { Content = new StringContent(fields.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token());
        Http.Send(req);
    }

    static decimal Dec(JsonNode? n) => n != null && decimal.TryParse(n.ToString(), out var d) ? d : 0m;
    static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
