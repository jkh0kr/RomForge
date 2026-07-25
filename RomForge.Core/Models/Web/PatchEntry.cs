using System.Text.Json.Serialization;

namespace RomForge.Core.Models.Web;

public class PatchEntry
{
    [JsonPropertyName("system")]
    public string System { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}