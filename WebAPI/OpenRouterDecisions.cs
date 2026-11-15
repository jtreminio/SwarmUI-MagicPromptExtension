using System.Globalization;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.MagicPromptExtension.WebAPI;

/// <summary>OpenRouter's native typed Decisions API, separate from chat completions.</summary>
public class OpenRouterDecisions : LLMAPICalls
{
    private const string DecisionsUrl = "https://openrouter.ai/api/alpha/decisions";
    private const string ModelsUrl = "https://openrouter.ai/api/v1/models?output_modalities=decisions&input_modalities=text";

    public static Task<JObject> GetMagicPromptDecisionModels(Session session) => FetchDecisionModels(HttpClient);

    internal static async Task<JObject> FetchDecisionModels(HttpClient client)
    {
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            using HttpResponseMessage response = await client.GetAsync(ModelsUrl, deadline.Token).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return CreateErrorResponse(ErrorHandler.ProcessErrorResponse(json, response.StatusCode, "openrouter"));
            JArray models = ParseModels(JObject.Parse(json));
            ModelListProvider.CacheDecisionModels(models);
            return new JObject { ["success"] = true, ["models"] = models };
        }
        catch (OperationCanceledException) { return CreateErrorResponse("OpenRouter decision model listing timed out. Try Refresh again."); }
        catch (Exception ex) { return CreateErrorResponse($"Could not load OpenRouter decision models: {ex.Message}"); }
    }

    internal static JArray ParseModels(JObject response)
    {
        if (response["data"] is not JArray data) throw new InvalidOperationException("OpenRouter returned no decision catalog.");
        JArray models = [];
        foreach (JObject model in data.OfType<JObject>())
        {
            if (model["endpoints"] is not JArray endpoints || !endpoints.Values<string>().Contains("/api/alpha/decisions")
                || model["architecture"]?["input_modalities"] is not JArray inputs || !inputs.Values<string>().Contains("text")
                || model["id"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)model["id"])) continue;
            models.Add(new JObject
            {
                ["model"] = model["id"], ["name"] = model["name"] ?? model["id"], ["created"] = model["created"],
                ["costIn"] = Price(model["pricing"]?["prompt"], 1_000_000),
                ["costOut"] = Price(model["pricing"]?["completion"], 1_000_000),
                ["costRequest"] = Price(model["pricing"]?["request"], 1), ["decisionModel"] = true
            });
        }
        return models;
    }

    private static string Price(JToken value, decimal multiplier) =>
        decimal.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal price) && price >= 0
            ? "$" + (price * multiplier).ToString("0.########", CultureInfo.InvariantCulture) : "—";

    internal static JObject BuildRequest(JObject request)
    {
        string type = (string)request["type"];
        JArray options = request["options"] as JArray ?? [];
        JObject question = new() { ["type"] = type, ["instructions"] = request["question"] };
        if (type == "noul" && options.Count == 2)
            question["criteria"] = new JObject { ["false"] = options[0], ["true"] = options[1] };
        else if (type == "choice")
            question["criteria"] = new JObject(options.Select((option, index) => new JProperty($"option_{index}", option.DeepClone())));
        else if (type == "score") question["criteria"] = options.DeepClone();
        return new JObject
        {
            ["model"] = request["evaluator"], ["state"] = request["evidence"],
            ["questions"] = new JObject { ["decision"] = question }
        };
    }

    internal static async Task<JObject> RequestAsync(JObject request, CancellationToken token, JObject settings, Session session)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, DecisionsUrl);
        if (!ConfigureRequest(message, "openrouter", settings, session, out string error)) throw new InvalidOperationException(error);
        message.Content = new StringContent(BuildRequest(request).ToString(Formatting.None), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await HttpClient.SendAsync(message, token).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ErrorHandler.ProcessErrorResponse(json, response.StatusCode, "openrouter"));
        return ParseResponse(request, JObject.Parse(json));
    }

    internal static JObject ParseResponse(JObject request, JObject response)
    {
        if (response["error"] != null) throw new InvalidOperationException("OpenRouter could not evaluate this decision.");
        if (response["answers"]?["decision"] is not JObject answer)
            throw new InvalidOperationException("OpenRouter returned no decision answer.");
        string type = (string)request["type"];
        if ((string)answer["type"] == "refusal") throw new InvalidOperationException("The OpenRouter decision model declined to evaluate this question.");
        if ((string)answer["type"] != type || response["model"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)response["model"]))
            throw new InvalidOperationException("OpenRouter returned an unexpected decision result.");
        JArray options = request["options"] as JArray ?? [];
        JObject result = new() { ["evaluator_model"] = response["model"] };
        switch (type)
        {
            case "noul":
                double probability = Number(answer["noul"], 0, 1);
                result["answer"] = probability > 0.5;
                result["probabilities"] = new JArray(1 - probability, probability);
                break;
            case "choice":
                string choice = answer["choice"]?.Type == JTokenType.String ? (string)answer["choice"] : "";
                int index = Enumerable.Range(0, options.Count).FirstOrDefault(i => choice == $"option_{i}", -1);
                if (index < 0) throw new InvalidOperationException("OpenRouter returned an unknown decision choice.");
                result["answer"] = options[index].DeepClone();
                CopyProbabilities(answer, result, options.Count, i => $"option_{i}");
                if (result["probabilities"] is JArray distribution && (double)distribution[index] + 0.001 < distribution.Values<double>().Max())
                    throw new InvalidOperationException("OpenRouter returned a decision choice inconsistent with its probabilities.");
                break;
            case "score":
                result["answer"] = Number(answer["score"], 0, options.Count - 1);
                CopyProbabilities(answer, result, options.Count, i => i.ToString(CultureInfo.InvariantCulture));
                break;
            default: throw new InvalidOperationException("Unsupported decision answer type.");
        }
        if (answer["confidence"] != null) result["confidence"] = Number(answer["confidence"], 0, 1);
        result["evaluator_source"] = new JObject
        {
            ["provider"] = "openrouter", ["requested_model"] = request["evaluator"], ["model"] = response["model"],
            ["routed_provider"] = response["provider"]?.DeepClone(), ["provider_request_id"] = response["id"]?.DeepClone(),
            ["usage"] = response["usage"]?.DeepClone()
        };
        result["metrics"] = new JObject
        {
            ["input_tokens"] = response["usage"]?["input_tokens"], ["generated_tokens"] = response["usage"]?["output_tokens"],
            ["cost"] = response["usage"]?["cost"]
        };
        return result;
    }

    private static double Number(JToken token, double minimum, double maximum)
    {
        if (token?.Type is not (JTokenType.Integer or JTokenType.Float))
            throw new InvalidOperationException("OpenRouter returned a nonnumeric decision value.");
        double value = (double)token;
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidOperationException("OpenRouter returned an out-of-range decision value.");
        return value;
    }

    private static void CopyProbabilities(JObject answer, JObject result, int count, Func<int, string> key)
    {
        if (answer["probabilities"] == null) return;
        if (answer["probabilities"] is not JObject probabilities || probabilities.Count != count)
            throw new InvalidOperationException("OpenRouter returned an incomplete decision distribution.");
        double[] values = Enumerable.Range(0, count).Select(i => Number(probabilities[key(i)], 0, 1)).ToArray();
        // Permit rounding drift, but never treat an unnormalised distribution as a decision.
        if (Math.Abs(values.Sum() - 1) > 0.001)
            throw new InvalidOperationException("OpenRouter returned an invalid decision probability distribution.");
        result["probabilities"] = new JArray(values);
    }
}
