using System.Linq;
using System.Text.RegularExpressions;
using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension;

public class PromptHandler
{
    // Matches <mpprompt:...> and <mpprompt[InstructionName]:...>
    // Group 1 = instruction identifier (optional, may include a trailing ", false" flag), Group 2 = prompt content (handles nested tags)
    private static readonly Regex MppromptRegex = new(@"<mpprompt(?:\[([^\]]+)\])?:((?:[^<>]|<[^>]*>)+)>", RegexOptions.Compiled);
    private static readonly Regex MpresponseRegex = new(@"<mpresponse:(\d+)>", RegexOptions.Compiled);
    private readonly PromptCache _cache;
    private readonly T2IRegisteredParam<bool> _paramUseCache;
    private readonly T2IRegisteredParam<string> _paramModelId;
    private readonly T2IRegisteredParam<string> _paramInstructions;
    private readonly T2IRegisteredParam<string> _paramPostFilter;

    public PromptHandler(
        PromptCache cache,
        T2IRegisteredParam<bool> paramUseCache,
        T2IRegisteredParam<string> paramModelId,
        T2IRegisteredParam<string> paramInstructions,
        T2IRegisteredParam<string> paramPostFilter)
    {
        _cache = cache;
        _paramUseCache = paramUseCache;
        _paramModelId = paramModelId;
        _paramInstructions = paramInstructions;
        _paramPostFilter = paramPostFilter;
    }

    /// <summary>
    /// Processes a prompt, handling all mpprompt tags by calling the LLM and replacing them with responses.
    /// </summary>
    public void ProcessPrompt(T2IParamInput userInput)
    {
        var prompt = userInput.Get(T2IParamTypes.Prompt);
        var modelId = userInput.Get(_paramModelId);
        var useCache = userInput.Get(_paramUseCache);

        var matches = MppromptRegex.Matches(prompt);

        if (matches.Count == 0)
        {
            prompt = CleanOrphanedMpresponse(prompt);
            FinalizePrompt(prompt, "", userInput);
            return;
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            prompt = StripMppromptTags(prompt);
            prompt = StripMpresponseTags(prompt);
            FinalizePrompt(prompt, "", userInput);
            return;
        }

        if (!useCache)
        {
            _cache.Clear();
        }

        var firstMppromptContent = matches[0].Groups[2].Value;
        var llmResponses = new List<string>();
        var modelsUsed = new List<string>();

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var fullTag = match.Value;
            var preDataRaw = match.Groups[1].Success ? match.Groups[1].Value : null;
            ParsePreData(preDataRaw, out string instructionId, out string modelSpec, out bool printResponse);
            var mppromptContent = ResolveMpresponseReferences(match.Groups[2].Value, llmResponses);
            var tagModelId = ResolveModel(modelSpec, userInput);
            modelsUsed.Add(string.IsNullOrWhiteSpace(tagModelId) ? modelId : tagModelId);

            var llmResponse = GetLlmResponse(mppromptContent, userInput, instructionId, tagModelId, useCache, i, fullTag);
            llmResponses.Add(llmResponse);
            prompt = prompt.Replace(fullTag, printResponse ? llmResponse : "");
        }

        RecordModelsUsed(userInput, modelsUsed);
        prompt = ResolveMpresponseReferences(prompt, llmResponses, logStandalone: true);
        FinalizePrompt(prompt, firstMppromptContent, userInput);
    }

    private string GetLlmResponse(string content, T2IParamInput userInput, string instructionId, string modelId, bool useCache, int tagIndex, string fullTag)
    {
        try
        {
            string response;
            if (useCache)
            {
                var timeoutMs = LLMAPICalls.GetChatBackendTimeoutMs();
                response = _cache.GetOrCreate(content, instructionId, modelId, () => MakeLlmRequest(content, userInput, instructionId, modelId), timeoutMs);
            }
            else
            {
                response = MakeLlmRequest(content, userInput, instructionId, modelId);
            }

            if (string.IsNullOrEmpty(response))
            {
                Logs.Error($"MagicPromptExtension.PromptHandler: empty response from LLM for tag #{tagIndex}: {fullTag}");
                return content;
            }

            return ApplyPostFilter(response, userInput);
        }
        catch (Exception ex)
        {
            Logs.Error($"MagicPromptExtension.PromptHandler: LLM call failed for tag #{tagIndex} '{fullTag}': {ex.Message}");
            return content;
        }
    }

    private string MakeLlmRequest(string prompt, T2IParamInput userInput, string instructionId = null, string modelId = null)
    {
        var effectiveModel = string.IsNullOrWhiteSpace(modelId)
            ? userInput.Get(_paramModelId, defVal: string.Empty)
            : modelId;

        var request = new JObject
        {
            ["messageContent"] = new JObject
            {
                ["text"] = prompt,
                ["instructions"] = InstructionResolver.Resolve(userInput, instructionId, _paramInstructions)
            },
            ["modelId"] = effectiveModel,
            ["messageType"] = "Text",
            ["action"] = "prompt",
            ["session_id"] = userInput.SourceSession?.ID ?? string.Empty,
            ["seed"] = userInput.Get(T2IParamTypes.Seed, -1).ToString()
        };

        var resp = LLMAPICalls.MagicPromptPhoneHome(request, userInput.SourceSession)
            .GetAwaiter()
            .GetResult();

        var success = resp?["success"];
        if (success == null || !success.Value<bool>())
        {
            return null;
        }

        var llmResponse = resp?["response"]?.ToString();
        return string.IsNullOrWhiteSpace(llmResponse) ? null : llmResponse;
    }

    /// <summary>
    /// Parses the optional mpprompt pre-data (the [...] section).
    /// Grammar: [instruction][|model][,false]
    /// - The optional "|model" part selects the LLM for this tag. A model of "random" picks a random non-blocked model.
    /// - A trailing ", false" flag (or a bare "false" in the instruction slot) suppresses printing the LLM response
    ///   while still recording it for &lt;mpresponse:N&gt;.
    /// Examples:
    /// - [Action]              => instruction "Action", default model, print
    /// - [Action|gpt-4o]       => instruction "Action", model "gpt-4o", print
    /// - [|random]             => default instruction, random model, print
    /// - [Action|gpt-4o,false] => instruction "Action", model "gpt-4o", don't print
    /// - [false]               => default instruction, default model, don't print
    /// </summary>
    private static void ParsePreData(string raw, out string instructionId, out string modelSpec, out bool printResponse)
    {
        instructionId = null;
        modelSpec = null;
        printResponse = true;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var trimmed = raw.Trim();

        // Strip an optional trailing ", false" print-suppression flag.
        int commaIndex = trimmed.LastIndexOf(',');
        if (commaIndex >= 0 && trimmed[(commaIndex + 1)..].Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            printResponse = false;
            trimmed = trimmed[..commaIndex].Trim();
        }

        // Split instruction from model on the first '|'.
        int pipeIndex = trimmed.IndexOf('|');
        string instructionPart;
        if (pipeIndex >= 0)
        {
            instructionPart = trimmed[..pipeIndex].Trim();
            var modelPart = trimmed[(pipeIndex + 1)..].Trim();
            modelSpec = string.IsNullOrWhiteSpace(modelPart) ? null : modelPart;
        }
        else
        {
            instructionPart = trimmed;
        }

        // A bare "false" in the instruction slot is shorthand for print-suppression with the default instruction.
        if (instructionPart.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            printResponse = false;
            instructionPart = "";
        }

        instructionId = string.IsNullOrWhiteSpace(instructionPart) ? null : instructionPart;
    }

    /// <summary>
    /// Resolves the per-tag model specifier to a concrete model id.
    /// Returns null to fall back to the globally selected "MP Model ID".
    /// A specifier of "random" selects a random model from the current non-blocked list.
    /// </summary>
    private static string ResolveModel(string modelSpec, T2IParamInput userInput)
    {
        if (string.IsNullOrWhiteSpace(modelSpec))
        {
            return null;
        }

        if (modelSpec.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            return PickRandomModel(userInput);
        }

        return modelSpec.Trim();
    }

    /// <summary>
    /// Picks a random model id from the non-blocked list. Uses the wildcard-seeded RNG so the selection is
    /// reproducible within a batch (and cache-friendly) unless a new wildcard seed is generated per image.
    /// Returns null (falling back to the global model) when no models are available.
    /// </summary>
    private static string PickRandomModel(T2IParamInput userInput)
    {
        var session = userInput.SourceSession;
        if (session == null)
        {
            Logs.Warning("MagicPromptExtension.PromptHandler: cannot resolve 'random' model without a session; using default model.");
            return null;
        }

        var ids = ModelListProvider.GetModelList(session)
            .Select(entry =>
            {
                int sep = entry.IndexOf("///", StringComparison.Ordinal);
                return sep >= 0 ? entry[..sep] : entry;
            })
            .Where(id => !string.IsNullOrWhiteSpace(id) && !id.Equals("loading", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ids.Count == 0)
        {
            Logs.Warning("MagicPromptExtension.PromptHandler: 'random' model requested but no non-blocked models are available; using default model.");
            return null;
        }

        var chosen = ids[userInput.GetWildcardRandom().Next(ids.Count)];
        Logs.Debug($"MagicPromptExtension.PromptHandler: 'random' selected model '{chosen}'.");
        return chosen;
    }

    /// <summary>
    /// Updates the "MP Model ID" parameter to reflect the model(s) actually used, so image metadata matches what
    /// ran rather than the globally selected dropdown value. When multiple distinct models were used (e.g. per-tag
    /// overrides or "random"), they are joined with ", ".
    /// </summary>
    private void RecordModelsUsed(T2IParamInput userInput, List<string> modelsUsed)
    {
        var distinct = modelsUsed
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (distinct.Count == 0)
        {
            return;
        }

        userInput.Set(_paramModelId, string.Join(", ", distinct));
    }

    /// <summary>
    /// Resolves mpresponse references within text, substituting with previously obtained LLM responses.
    /// </summary>
    private static string ResolveMpresponseReferences(string text, List<string> llmResponses, bool logStandalone = false)
    {
        return MpresponseRegex.Replace(text, m =>
        {
            if (!int.TryParse(m.Groups[1].Value, out int refIndex))
            {
                return m.Value;
            }

            if (refIndex >= 0 && refIndex < llmResponses.Count)
            {
                return llmResponses[refIndex];
            }

            if (logStandalone)
            {
                Logs.Warning($"MagicPromptExtension.PromptHandler: <mpresponse:{refIndex}> references invalid index (only {llmResponses.Count} responses available)");
                return "";
            }

            Logs.Error($"MagicPromptExtension.PromptHandler: <mpresponse:{refIndex}> references future mpprompt (only {llmResponses.Count} responses available)");
            return $"[ERROR: mpresponse:{refIndex} not yet available]";
        });
    }

    private static string CleanOrphanedMpresponse(string prompt)
    {
        if (MpresponseRegex.IsMatch(prompt))
        {
            Logs.Warning("MagicPromptExtension.PromptHandler: <mpresponse> found but no mpprompt tags exist");
            return MpresponseRegex.Replace(prompt, "");
        }
        return prompt;
    }

    private static string StripMppromptTags(string prompt)
    {
        return MppromptRegex.Replace(prompt, m => m.Groups[2].Value);
    }

    private static string StripMpresponseTags(string prompt)
    {
        return MpresponseRegex.Replace(prompt, "");
    }

    private string ApplyPostFilter(string response, T2IParamInput userInput)
    {
        string postFilter = userInput.Get(_paramPostFilter, defVal: string.Empty);
        if (string.IsNullOrEmpty(postFilter))
        {
            return response;
        }

        string[] filters = postFilter.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (string filter in filters)
        {
            if (string.IsNullOrEmpty(filter))
            {
                continue;
            }

            string trimmed = filter.Trim();
            if (trimmed.Length > 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            {
                string inner = trimmed[1..^1];
                int eq = inner.IndexOf('=');
                if (eq > 0)
                {
                    response = response.Replace(inner[..eq], inner[(eq + 1)..], StringComparison.Ordinal);
                    continue;
                }
            }

            response = response.Replace(filter, "", StringComparison.Ordinal);
        }

        return response.Trim();
    }

    private static void FinalizePrompt(string prompt, string originalMpprompt, T2IParamInput userInput)
    {
        userInput.Set(T2IParamTypes.Prompt, prompt.Replace("<mporiginal>", originalMpprompt).Trim());
    }
}
