using System.Text.Json.Serialization;

namespace CrabDesk.Core;

public sealed class WidgetTranslationSettings
{
    public string BaseUrl { get; set; } = string.Empty;
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    [JsonIgnore]
    public bool MigratedFromSharedProfile { get; internal set; }
}
