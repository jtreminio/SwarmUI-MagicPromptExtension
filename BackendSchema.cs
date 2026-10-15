namespace Hartsy.Extensions.MagicPromptExtension;

/// <summary>Text prompt request bodies for supported providers.</summary>
public static class BackendSchema
{
    public class MessageContent
    {
        public string Text { get; set; }
        public string Instructions { get; set; }
    }

    public static string NormalizeThinking(string thinking) => thinking?.Trim().ToLowerInvariant() switch
    {
        "low" => "low",
        "medium" => "medium",
        "high" => "high",
        _ => "none"
    };

    public static object GetSchemaType(string type, MessageContent content, string model, string thinking = "none")
    {
        if (content == null || string.IsNullOrEmpty(model))
        {
            throw new ArgumentException("Content or model cannot be null or empty.");
        }
        type = type.ToLowerInvariant();
        if (type != "openrouter" && type != "openaiapi")
        {
            throw new ArgumentException($"Unsupported backend type: {type}");
        }
        thinking = NormalizeThinking(thinking);
        List<object> messages = [];
        if (!string.IsNullOrEmpty(content.Instructions))
        {
            messages.Add(new { role = "system", content = content.Instructions });
        }
        messages.Add(new { role = "user", content = content.Text });
        Dictionary<string, object> body = new()
        {
            ["model"] = model,
            ["messages"] = messages.ToArray(),
            ["temperature"] = 1.0,
            ["top_p"] = 0.9,
            ["stream"] = false
        };
        if (type == "openrouter")
        {
            body["max_completion_tokens"] = 96000;
            body["reasoning"] = thinking == "none"
                ? new { enabled = false, exclude = true }
                : new { effort = thinking, exclude = true };
        }
        else
        {
            body["max_tokens"] = thinking switch
            {
                "low" => 2024,
                "medium" => 4072,
                "high" => 8168,
                _ => 1000
            };
            if (thinking != "none")
            {
                body["reasoning_effort"] = thinking;
            }
        }
        return body;
    }
}
