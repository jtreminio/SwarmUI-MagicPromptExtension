using System.Diagnostics;
using System.IO;
using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension;

internal static class DecisionEvaluator
{
    internal static void CaptureSource(JObject result, string providerJson, string endpoint, string provider, string requestedModel, double latencyMs)
    {
        if (!IsRemoteEndpoint(endpoint)) return;
        try
        {
            using JsonTextReader reader = new(new StringReader(providerJson)) { DateParseHandling = DateParseHandling.None };
            JObject raw = JObject.Load(reader);
            JToken content = raw["choices"]?.First?["message"]?["content"];
            if (content == null || content.Type == JTokenType.Null) return;
            result["decision_evidence"] = content.Type == JTokenType.String ? (string)content : content.ToString(Formatting.None);
            result["decision_source"] = new JObject
            {
                ["remote"] = true, ["provider"] = provider, ["requested_model"] = requestedModel,
                ["model"] = raw["model"]?.Type == JTokenType.String ? (string)raw["model"] : requestedModel,
                ["routed_provider"] = raw["provider"]?.DeepClone(), ["provider_request_id"] = raw["id"]?.DeepClone(),
                ["usage"] = (raw["usage"] as JObject)?.DeepClone(), ["latency_ms"] = latencyMs,
                ["received_at_utc"] = DateTimeOffset.UtcNow.ToString("O")
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            Logs.Warning($"MagicPrompt: could not capture decision evidence: {ex.Message}");
        }
    }

    internal static JObject NormalizeSettings(JToken config)
    {
        if (config == null)
        {
            return JObject.Parse("""
                {"model":"","favoritedModels":[],"blockedModels":[],"questions":[{"id":"refusal","title":"Prompt refusal","question":"Did this response refuse to generate the requested prompt?","type":"noul","options":[],"selected":true}]}
                """);
        }
        if (config is not JObject obj || obj["questions"] is not JArray questions || questions.Count > 32)
            throw new ArgumentException("Decision settings need a list of up to 32 questions.");
        string model = obj["model"]?.Type == JTokenType.String ? ((string)obj["model"]).Trim() : "";
        JArray blocked = NormalizeModelIds(obj["blockedModels"]);
        JArray favorites = new(NormalizeModelIds(obj["favoritedModels"]).Values<string>()
            .Where(id => !blocked.Values<string>().Contains(id, StringComparer.OrdinalIgnoreCase)));
        if (blocked.Values<string>().Contains(model, StringComparer.OrdinalIgnoreCase)) model = "";

        JArray normalized = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> titles = new(StringComparer.OrdinalIgnoreCase);
        foreach (JObject row in questions.OfType<JObject>().Where(row => row.ContainsKey("title")))
        {
            string title = row["title"]?.Type == JTokenType.String ? ((string)row["title"]).Trim() : "";
            if (title.Length is 0 or > 100 || !titles.Add(title))
                throw new ArgumentException("Give each decision a unique title of up to 100 characters.");
        }
        foreach (JToken item in questions)
        {
            if (item is not JObject row || row["options"] is not JArray options || options.Any(option => option.Type != JTokenType.String))
                throw new ArgumentException("Each decision needs a question, answer type, and list of options.");
            string id = (string)row["id"];
            string question = ((string)row["question"])?.Trim();
            string type = (string)row["type"];
            string[] labels = options.Values<string>().Select(option => option.Trim()).ToArray();
            bool validCount = type switch
            {
                "choice" => labels.Length is >= 2 and <= 26,
                "score" => labels.Length is >= 2 and <= 10,
                "noul" => labels.Length is 0 or 2,
                _ => false
            };
            if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || !ids.Add(id) || string.IsNullOrWhiteSpace(question) || question.Length > 2000)
                throw new ArgumentException("Each decision needs a unique ID and a question of up to 2,000 characters.");
            if (!validCount || labels.Any(label => label.Length is 0 or > 500) || labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
                throw new ArgumentException($"Check the answer options for '{question}'. Use distinct, nonempty options in order.");
            string title = ((string)row["title"])?.Trim();
            if (title == null)
            {
                string label = id == "refusal" ? "Prompt refusal" : question[..Math.Min(question.Length, 90)].TrimEnd();
                title = label;
                for (int suffix = 2; !titles.Add(title); suffix++) title = $"{label} ({suffix})";
            }
            normalized.Add(new JObject
            {
                ["id"] = id, ["title"] = title, ["question"] = question, ["type"] = type, ["options"] = new JArray(labels),
                ["selected"] = (bool?)row["selected"] ?? false, ["skipIf"] = SkipValue(row)
            });
        }
        return new JObject { ["model"] = model, ["favoritedModels"] = favorites, ["blockedModels"] = blocked, ["questions"] = normalized };
    }

    private static JArray NormalizeModelIds(JToken value) => new((value as JArray ?? []).Where(id => id.Type == JTokenType.String)
        .Values<string>().Select(id => id.Trim()).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string SkipValue(JObject question) => (string)question["type"] is "noul" or "choice"
        && question["skipIf"]?.Type == JTokenType.String ? ((string)question["skipIf"]).Trim() : "";

    private static string MatchableSkipValue(JObject question)
    {
        string rule = SkipValue(question);
        if (rule.Length == 0) return "";
        bool matchable = (string)question["type"] == "noul"
            ? rule.Equals("yes", StringComparison.OrdinalIgnoreCase) || rule.Equals("no", StringComparison.OrdinalIgnoreCase)
            : question["options"] is JArray options && options.Values<string>().Contains(rule, StringComparer.Ordinal);
        return matchable ? rule : "";
    }

    private static bool IsRemoteEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) && !uri.IsLoopback;

    internal sealed record Selection(string Evaluator, JObject[] Questions)
    {
        internal int SkipQuestionCount { get; } = Questions.Count(question => MatchableSkipValue(question).Length > 0);
        internal bool RequiresFreshResponse { get; } = Questions.Any(question => SkipValue(question).Length > 0);
    }

    internal static Selection ResolveSelection(JToken config, string selectedId, string endpoint, string selectedModel = "")
    {
        if (!IsRemoteEndpoint(endpoint)) return null;
        bool useDefaults = string.IsNullOrEmpty(selectedId);
        JObject[] selected = (config["questions"] as JArray).OfType<JObject>().Where(question =>
            useDefaults ? (bool?)question["selected"] == true : (string)question["id"] == selectedId).ToArray();
        if (selected.Length == 0 && !useDefaults)
            throw new SwarmReadableErrorException("MagicPrompt: The saved decision question is unavailable. Choose another MP Decision Question on Generate.");
        string modelOverride = selectedModel?.Trim() ?? "";
        string model = modelOverride.Length > 0 ? modelOverride : (string)config["model"];
        bool blocked = (config["blockedModels"] as JArray ?? []).Values<string>().Contains(model, StringComparer.OrdinalIgnoreCase);
        if (modelOverride.Length > 0 && blocked)
            throw new SwarmReadableErrorException($"MagicPrompt: Decision model '{model}' is blocked in Settings → Decision Models. Choose an unblocked MP Decision Model ID on Generate.");
        if (selected.Length > 0 && (string.IsNullOrWhiteSpace(model) || blocked))
            throw new SwarmReadableErrorException("MagicPrompt: Choose an unblocked MP Decision Model ID on Generate or set a default in Settings → Decision Models.");
        return new Selection(model, selected);
    }

    /// <summary>Records checks for the provider response and returns the first match that must skip generation.</summary>
    internal static JObject Observe(JObject fetched, T2IParamInput input, int tagIndex, Selection selection, JObject settings)
    {
        if (selection == null || selection.Questions.Length == 0)
        {
            AddNote(input, tagIndex, "skipped", selection == null ? "Decision checks require a remote LLM response."
                : "No questions are included in Settings defaults. Choose a question on Generate or include one in Settings defaults.");
            return null;
        }
        if (fetched == null || (bool?)fetched["decision_source"]?["remote"] != true)
        {
            AddNote(input, tagIndex, "skipped", fetched == null ? "Cached response. Use a fresh response to run a check." : "No new remote provider response to check.");
            return null;
        }
        try
        {
            List<JObject> records = [];
            JObject match = EvaluateAsync(fetched, selection, tagIndex,
                (request, token) => OpenRouterDecisions.RequestAsync(request, token, settings, input.SourceSession),
                records.Add, input.InterruptToken).GetAwaiter().GetResult();
            if (records.Count > 0) AddMetadata(input, records);
            else if (!input.InterruptToken.IsCancellationRequested)
                AddNote(input, tagIndex, "skipped", "No response text to check.");
            return match;
        }
        catch (Exception ex)
        {
            AddNote(input, tagIndex, "error", "Could not run decision checks. See SwarmUI logs.");
            Logs.Warning($"MagicPrompt: could not record decision checks: {ex.Message}");
            return null;
        }
    }

    internal static void AddMetadata(T2IParamInput input, IEnumerable<JObject> records)
    {
        // Swarm makes shallow copies of ExtraMeta. Replace its array instead of mutating a shared instance.
        JArray results = input.ExtraMeta.TryGetValue("mp_decisions", out object value) && value is JArray previous
            ? (JArray)previous.DeepClone() : new JArray();
        foreach (JObject record in records) results.Add(record.DeepClone());
        input.ExtraMeta["mp_decisions"] = results;
    }

    private static void AddNote(T2IParamInput input, int tagIndex, string status, string message) =>
        AddMetadata(input, [new JObject { ["tag_index"] = tagIndex, ["status"] = status, ["message"] = message }]);

    internal static async Task<JObject> EvaluateAsync(JObject fetched, Selection selection, int tagIndex,
        Func<JObject, CancellationToken, Task<JObject>> evaluate, Action<JObject> save, CancellationToken interruptToken)
    {
        // A typed origin marker comes only from the provider fetch, never from the decision HTTP client.
        if (selection == null || fetched?["decision_source"] is not JObject source || (bool?)source["remote"] != true
            || fetched["decision_evidence"]?.Type != JTokenType.String) return null;
        string evidence = (string)fetched["decision_evidence"];
        if (string.IsNullOrWhiteSpace(evidence)) return null;
        string requestId = Guid.NewGuid().ToString("N");
        int remainingSkipChecks = selection.SkipQuestionCount;
        foreach (JObject question in selection.Questions)
        {
            if (interruptToken.IsCancellationRequested) break;
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(interruptToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            JObject definition = (JObject)question.DeepClone();
            definition.Remove("selected");
            definition.Remove("title");
            definition.Remove("skipIf");
            string version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition.ToString(Formatting.None))));
            definition["title"] = question["title"];
            JObject record = new()
            {
                ["schema_version"] = 1, ["time_utc"] = DateTimeOffset.UtcNow.ToString("O"), ["request_id"] = requestId,
                ["tag_index"] = tagIndex, ["source"] = source.DeepClone(), ["decision"] = definition,
                ["decision_version"] = version, ["evaluator"] = selection.Evaluator
            };
            JObject request = new()
            {
                ["evidence"] = evidence, ["question"] = question["question"], ["type"] = question["type"],
                ["options"] = question["options"], ["evaluator"] = selection.Evaluator
            };
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                JObject response = await evaluate(request, deadline.Token).ConfigureAwait(false);
                interruptToken.ThrowIfCancellationRequested();
                if (response["error"] != null || response["answer"] is not JValue answer || answer.Type == JTokenType.Null
                    || string.IsNullOrWhiteSpace((string)response["evaluator_model"]))
                    throw new InvalidOperationException((string)response["error"] ?? "Decision model returned an incomplete result.");
                record["status"] = "ok";
                foreach (string key in new[] { "answer", "probabilities", "evaluator_model", "metrics", "confidence", "evaluator_source" })
                    if (response[key] is JToken value) record[key] = value.DeepClone();
            }
            catch (OperationCanceledException) when (interruptToken.IsCancellationRequested)
            {
                record["status"] = "cancelled";
                record["message"] = "Generation cancelled";
            }
            catch (Exception ex)
            {
                record["status"] = "error";
                record["error"] = ex is OperationCanceledException ? "Decision model timed out" : ex.Message;
            }
            record["latency_ms"] = elapsed.Elapsed.TotalMilliseconds;
            string skipIf = MatchableSkipValue(question);
            bool completed = (string)record["status"] == "ok";
            bool skip = completed && MatchesSkipRule(question, record["answer"], skipIf);
            if (skip)
            {
                record["generation_skipped"] = true;
                record["skip_if"] = skipIf;
            }
            save(record);
            if (skip)
            {
                ModelStats.RecordSkipIf(fetched, skipped: true);
                return record;
            }
            if (completed && skipIf.Length > 0) remainingSkipChecks--;
        }
        if (selection.SkipQuestionCount > 0 && remainingSkipChecks == 0)
            ModelStats.RecordSkipIf(fetched, skipped: false);
        return null;
    }

    private static bool MatchesSkipRule(JObject question, JToken answer, string rule)
    {
        if (string.IsNullOrEmpty(rule)) return false;
        return (string)question["type"] switch
        {
            "noul" when answer?.Type == JTokenType.Boolean => rule.Equals((bool)answer ? "yes" : "no", StringComparison.OrdinalIgnoreCase),
            "choice" when answer?.Type == JTokenType.String => rule == (string)answer,
            _ => false
        };
    }

}
