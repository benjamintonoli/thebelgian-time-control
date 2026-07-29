using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class BroaderValidationCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public static string DefaultPath(string docsPath) =>
        Path.Combine(docsPath, "broader-validation-full-cache.json");

    public static void Save(string path, BroaderValidationResult result)
    {
        if (result.Summary.ProcessedTechnicianCount <= 0)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
    }

    public static BroaderValidationResult? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BroaderValidationResult>(json, JsonOptions);
    }
}
