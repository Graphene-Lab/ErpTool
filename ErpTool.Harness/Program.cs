using AIOrchestrator.API;

namespace ErpToolHarness;

/// <summary>
/// Checks the agent-facing surface of ErpTool.
/// Usage:
///   ErpTool.Harness --definitions   render the exact tool catalog the LLM receives
/// (prompt-based end-to-end tests against a live ERP are added here next — see the
///  AGENT_TOOLS_GUIDE "Testing Agent Tools" section.)
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--definitions" or "-d")
        {
            var defs = UISupportGeneric.Analyzer.GetToolDefinitions(typeof(ErpTool));
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine($"=== ErpTool tool definitions ({defs.Length} chars) ===");
            Console.WriteLine(defs);
            return 0;
        }

        Console.Error.WriteLine($"Unknown argument '{args[0]}'. Use --definitions.");
        return 2;
    }
}
