using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace Hartsy.Extensions.MagicPromptExtension.WebAPI.Models;

public class OpenAIAPIResponse
{
    [JsonProperty("data")]
    public List<OpenAIModel> Data { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("object")]
    public string Object { get; set; }

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; }

    [JsonPropertyName("choices")]
    public List<OpenAIChoice> Choices { get; set; }
}

// Shared wire types for OpenAI-compatible local servers.
public class OpenAIChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public OpenAIMessage Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; }
}

public class OpenAIMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; }
}

public class OpenAIModel
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("object")]
    public string Object { get; set; }

    [JsonProperty("created")]
    public long Created { get; set; }

    [JsonProperty("owned_by")]
    public string OwnedBy { get; set; }
}

public class OpenAIErrorResponse
{
    [JsonProperty("error")]
    public OpenAIError Error { get; set; }
}

public class OpenAIError
{
    [JsonProperty("message")]
    public string Message { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("param")]
    public string Param { get; set; }

    [JsonProperty("code")]
    public object Code { get; set; }
}
