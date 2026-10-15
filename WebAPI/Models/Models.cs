using Newtonsoft.Json;

namespace Hartsy.Extensions.MagicPromptExtension.WebAPI.Models;

/// <summary>Provider-independent model catalog entry.</summary>
public class ModelData
{
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("model")]
    public string Model { get; set; }

    [JsonProperty("costOut", NullValueHandling = NullValueHandling.Ignore)]
    public string CostOut { get; set; }

    [JsonProperty("created", NullValueHandling = NullValueHandling.Ignore)]
    public long? Created { get; set; }

    [JsonProperty("outUsdPerMillion", NullValueHandling = NullValueHandling.Ignore)]
    public double? OutUsdPerMillion { get; set; }
}
