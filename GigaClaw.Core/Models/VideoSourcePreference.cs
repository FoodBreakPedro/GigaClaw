using System.Text.Json.Serialization;

namespace GigaClaw.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<VideoSourcePreference>))]
public enum VideoSourcePreference
{
    None,
    OpenMontage,
    PromptAndUpload
}
