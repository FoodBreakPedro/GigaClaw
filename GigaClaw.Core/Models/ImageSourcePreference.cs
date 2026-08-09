using System.Text.Json.Serialization;

namespace GigaClaw.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ImageSourcePreference>))]
public enum ImageSourcePreference
{
    None,
    Pexels,
    LocalGeneration,
    PromptAndUpload
}
