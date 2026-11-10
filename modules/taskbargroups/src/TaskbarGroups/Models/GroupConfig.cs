using System.Text.Json.Serialization;

namespace TaskbarGroups.Models;

public sealed class GroupConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("iconPath")]
    public string IconPath { get; set; } = "";

    [JsonPropertyName("backgroundColor")]
    public string BackgroundColor { get; set; } = "#1E1E1E";

    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 0.95;

    [JsonPropertyName("iconSize")]
    public int IconSize { get; set; } = 32;

    [JsonPropertyName("shortcuts")]
    public List<ShortcutItem> Shortcuts { get; set; } = new();
}
