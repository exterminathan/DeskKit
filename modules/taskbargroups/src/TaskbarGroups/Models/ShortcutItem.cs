using System.Text.Json.Serialization;

namespace TaskbarGroups.Models;

public sealed class ShortcutItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("targetPath")]
    public string TargetPath { get; set; } = "";

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; set; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = "";

    [JsonPropertyName("iconCachePath")]
    public string IconCachePath { get; set; } = "";
}
