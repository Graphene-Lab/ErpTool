using System.Text.Json;

namespace AIOrchestrator.API;

/// <summary>
/// Connection settings for the ERP the tool drives. Never supplied by the agent — resolved
/// from the environment (ERP_BASE_URL / ERP_USER / ERP_PASSWORD) or, as a fallback, from
/// <c>PersistentData/erp.json</c> in the host's base directory (a folder updates never touch).
/// </summary>
internal static class ErpConfig
{
    public static string? BaseUrl { get; private set; }
    public static string? User { get; private set; }
    public static string? Password { get; private set; }

    private static bool _loaded;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        BaseUrl = Environment.GetEnvironmentVariable("ERP_BASE_URL");
        User = Environment.GetEnvironmentVariable("ERP_USER");
        Password = Environment.GetEnvironmentVariable("ERP_PASSWORD");

        // Fill any value not set by the environment from the persistent file.
        if (string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(User) || string.IsNullOrWhiteSpace(Password))
        {
            try
            {
                var file = Path.Combine(AppContext.BaseDirectory, "PersistentData", "erp.json");
                if (File.Exists(file))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (string.IsNullOrWhiteSpace(BaseUrl) && root.TryGetProperty("baseUrl", out var b)) BaseUrl = b.GetString();
                    if (string.IsNullOrWhiteSpace(User) && root.TryGetProperty("user", out var u)) User = u.GetString();
                    if (string.IsNullOrWhiteSpace(Password) && root.TryGetProperty("password", out var p)) Password = p.GetString();
                }
            }
            catch { /* config read is best-effort; missing values surface as a clear tool error */ }
        }

        BaseUrl = BaseUrl?.TrimEnd('/');
    }

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Password);
}
