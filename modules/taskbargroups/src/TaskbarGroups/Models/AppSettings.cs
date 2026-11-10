using System.Text.Json.Serialization;

namespace TaskbarGroups.Models;

public sealed class AppSettings
{
    [JsonPropertyName("shortcutsPath")]
    public string? ShortcutsPath { get; set; }

    [JsonPropertyName("stayOpenUntilClickOff")]
    public bool StayOpenUntilClickOff { get; set; } = true;
}
