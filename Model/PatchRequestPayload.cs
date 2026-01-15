#nullable enable
using System.Text.Json.Serialization;

namespace Cavea.Model
{
    public class PatchRequestPayload
    {
        [JsonPropertyName("contents")]
        public string? Contents { get; set; }
    }
}
