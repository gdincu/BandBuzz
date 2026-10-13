using System.Text.Json;

namespace BandBuzz;

internal sealed class Config
{
    public string Mac { get; set; } = "04:DA:28:1C:36:32";
    public string AuthKey { get; set; } = "21c22a78e8526957f65b412ff8a64e27";
    public List<string> Filters { get; set; } = ["Chrome", "Teams"];
    public int DedupSeconds { get; set; } = 10;
    public int ReconnectSeconds { get; set; } = 5;
    public int MaxWrite { get; set; } = 244;

    public static Config Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
            return new Config();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Config>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Config();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"warn: ignoring {path}: {ex.Message}");
            return new Config();
        }
    }
}
