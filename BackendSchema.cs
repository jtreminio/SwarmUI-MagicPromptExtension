using SixLabors.ImageSharp.Processing;
using SwarmUI.Utils;
using SwarmUI.Media;
using Image = SwarmUI.Utils.Image;
using ISImage = SixLabors.ImageSharp.Image;
using ISImage32 = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;
using ISImageFrame32 = SixLabors.ImageSharp.ImageFrame<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace Hartsy.Extensions.MagicPromptExtension;

public static class BackendSchema
{
    public enum MessageType
    {
        Text,
        Vision
    }

    public class MessageContent
    {
        public string Text { get; set; }
        public string Instructions { get; set; }
        public List<MediaContent> Media { get; set; }
        public int? KeepAlive { get; set; }
    }

    public class MediaContent
    {
        public string Type { get; set; }  // "base64" or "url"
        public string Data { get; set; }
        public string MediaType { get; set; }  // "image/jpeg", "image/png", etc.
    }

    /// <summary>Get the schema type for the backend.</summary>
    /// <param name="type">Backend type (ollama, openai, openaiapi, or openrouter)</param>
    /// <param name="content">Message content including text and media</param>
    /// <param name="model">Model name to use</param>
    /// <param name="messageType">Type of message (Text or Vision)</param>
    /// <param name="thinking">Thinking/reasoning effort: "none", "low", "medium", or "high"</param>
    /// <returns>Returns an object with the schema type for the backend.</returns>
    public static object GetSchemaType(string type, MessageContent content, string model, MessageType messageType = MessageType.Text, string thinking = "none")
    {
        if (content == null || string.IsNullOrEmpty(model))
        {
            throw new ArgumentException("Content or model cannot be null or empty.");
        }
        type = type.ToLower();
        thinking = NormalizeThinking(thinking);
        _ = content.KeepAlive;
        return type switch
        {
            "ollama" => OllamaRequestBody(content, model, messageType, thinking),
            "openai" or "openaiapi" => OpenAICompatibleRequestBody(content, model, messageType, preferPngForBase64: false, isOpenRouter: false, thinking),
            "openrouter" => OpenAICompatibleRequestBody(content, model, messageType, preferPngForBase64: false, isOpenRouter: true, thinking),
            _ => throw new ArgumentException($"Unsupported backend type: {type}")
        };
    }

    /// <summary>Clamps a thinking value to one of "none", "low", "medium", "high" (unknown values become "none").</summary>
    public static string NormalizeThinking(string thinking)
    {
        return thinking?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            _ => "none"
        };
    }

    /// <summary>Response token limit, scaled up when thinking is enabled since reasoning tokens count against it on most backends.</summary>
    private static int MaxTokensForThinking(string thinking, int baseTokens)
    {
        return thinking switch
        {
            "low" => baseTokens + 1024,
            "medium" => baseTokens + 3072,
            "high" => baseTokens + 7168,
            _ => baseTokens
        };
    }

    /// <summary>Compresses image data to optimize for LLM vision models</summary>
    /// <param name="media">The media content containing image data</param>
    /// <param name="targetFormat">The target format ("PNG" or "WEBP")</param>
    /// <returns>Compressed base64 image data without the data URL prefix</returns>
    public static string CompressImageForVision(MediaContent media, string targetFormat = "WEBP")
    {
        if (media.Type != "base64")
        {
            return media.Data;
        }
        try
        {
            ImageFile image = ImageFile.FromDataString($"data:{media.MediaType};base64,{media.Data}");
            // Skip compression for videos etc..
            if (image.Type.MetaType != MediaMetaType.Image)
            {
                return media.Data;
            }
            ISImage img = image.ToIS;
            int maxDimension = 256; // TODO: This needs to be tested and adjusted
            if (img.Width > maxDimension || img.Height > maxDimension)
            {
                float scaleFactor = maxDimension / (float)Math.Max(img.Width, img.Height);
                int newWidth = (int)(img.Width * scaleFactor);
                int newHeight = (int)(img.Height * scaleFactor);
                img.Mutate(i => i.Resize(newWidth, newHeight));
            }
            // Set compression quality based on format TODO: This needs to be tested and adjusted
            int quality = targetFormat == "PNG" ? 60 : 40;
            ImageFile tempImage = new Image(ImageFile.ISImgToPngBytes(img), image.Type);
            ImageFile compressedImage = tempImage.ConvertTo(targetFormat, quality: quality);
            // Return just the base64 data (without the data:image/webp;base64, prefix)
            return compressedImage.AsBase64;
        }
        catch (Exception ex)
        {
            Logs.Error($"Failed to compress image: {ex.Message}");
            return media.Data;
        }
    }

    /// <summary>Generates a request body for Ollama backend.</summary>
    private static object OllamaRequestBody(MessageContent content, string model, MessageType messageType, string thinking = "none")
    {
        List<object> messages = [];
        if (!string.IsNullOrEmpty(content.Instructions))
        {
            messages.Add(new { role = "system", content = content.Instructions });
        }

        object options = new { temperature = 1.0, top_p = 0.9 };

        if (messageType == MessageType.Vision && content.Media?.Any() == true)
        {
            messages.Add(new
            {
                role = "user",
                content = content.Text,
                images = content.Media.Select(m => CompressImageForVision(m, "JPG")).ToArray()
            });
        }
        else
        {
            messages.Add(new { role = "user", content = content.Text });
        }

        Dictionary<string, object> body = new()
        {
            ["model"] = model,
            ["messages"] = messages.ToArray(),
            ["stream"] = false,
            ["keep_alive"] = content.KeepAlive,
            ["options"] = options
        };
        if (thinking != "none")
        {
            // Ollama's "think" accepts effort levels for models that support them (e.g. gpt-oss);
            // models that only support boolean thinking reject level strings with a clear error.
            body["think"] = thinking;
        }
        return body;
    }

    /// <summary>Generates a request body for OpenAI and compatible backends.</summary>
    private static object OpenAICompatibleRequestBody(MessageContent content, string model, MessageType messageType, bool preferPngForBase64, bool isOpenRouter, string thinking = "none")
    {
        List<object> messages = [];
        // Add system message if instructions exist
        if (!string.IsNullOrEmpty(content.Instructions))
        {
            messages.Add(new { role = "system", content = content.Instructions });
        }
        // Built as a dictionary (rather than an anonymous type) so we can conditionally add fields
        // like "reasoning" below. System.Text.Json serializes this to the same JSON shape.
        Dictionary<string, object> body = new()
        {
            ["model"] = model,
            ["temperature"] = 1.0,
            ["stream"] = false
        };
        if (isOpenRouter)
        {
            body["max_completion_tokens"] = 4096;
        }
        else
        {
            // Reasoning tokens count against max_tokens, so grow the limit with the thinking level.
            body["max_tokens"] = MaxTokensForThinking(thinking, 1000);
        }
        if (messageType == MessageType.Vision && content.Media?.Any() == true)
        {
            List<object> contentList = [];
            foreach (MediaContent media in content.Media)
            {
                string imageData = CompressImageForVision(media, preferPngForBase64 ? "PNG" : "WEBP");
                contentList.Add(new
                {
                    type = "image_url",
                    image_url = media.Type == "base64"
                        ? new { url = preferPngForBase64 ? $"data:image/png;base64,{imageData}" : $"data:image/webp;base64,{imageData}" }
                        : new { url = media.Data }
                });
            }
            contentList.Add(new
            {
                type = "text",
                text = content.Text
            });
            messages.Add(new
            {
                role = "user",
                content = contentList
            });
        }
        else
        {
            messages.Add(new { role = "user", content = content.Text });
            body["top_p"] = 0.9;
        }
        body["messages"] = messages.ToArray();
        if (isOpenRouter)
        {
            // OpenRouter's unified reasoning switch. Reasoning-capable models honor it; models
            // without reasoning ignore it, so this is safe to send unconditionally. OpenRouter
            // silently routes many reasoning-capable models, so "none" disables reasoning
            // explicitly rather than omitting the field.
            body["reasoning"] = thinking == "none"
                ? new { enabled = false, exclude = true }
                : new { effort = thinking, exclude = true };
        }
        else if (thinking != "none")
        {
            // OpenAI effort switch; non-reasoning models reject it with a clear error.
            body["reasoning_effort"] = thinking;
        }
        return body;
    }
}
