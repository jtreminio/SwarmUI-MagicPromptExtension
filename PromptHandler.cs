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
    // Group 1 = optional comma-separated pre-data, Group 2 = prompt content (handles nested tags)
    private static readonly Regex MppromptRegex = new(@"<mpprompt(?:\[([^\]]+)\])?:((?:[^<>]|<[^>]*>)+)>", RegexOptions.Compiled);
    private static readonly Regex MpresponseRegex = new(@"<mpresponse:(\d+)>", RegexOptions.Compiled);
    private readonly PromptCache _cache;
    private readonly T2IRegisteredParam<bool> _paramUseCache;
    private readonly T2IRegisteredParam<string> _paramModelId;
    private readonly T2IRegisteredParam<string> _paramInstructions;
    private readonly T2IRegisteredParam<string> _paramPostFilter;
    private readonly T2IRegisteredParam<string> _paramThinking;
    private readonly T2IRegisteredParam<string> _paramOnError;

    public const string OnErrorSkip = "skip";
    public const string OnErrorFallback = "fallback";

    public PromptHandler(
        PromptCache cache,
        T2IRegisteredParam<bool> paramUseCache,
        T2IRegisteredParam<string> paramModelId,
        T2IRegisteredParam<string> paramInstructions,
        T2IRegisteredParam<string> paramPostFilter,
        T2IRegisteredParam<string> paramThinking,
        T2IRegisteredParam<string> paramOnError)
    {
        _cache = cache;
        _paramUseCache = paramUseCache;
        _paramModelId = paramModelId;
        _paramInstructions = paramInstructions;
        _paramPostFilter = paramPostFilter;
        _paramThinking = paramThinking;
        _paramOnError = paramOnError;
    }

    /// <summary>
    /// Processes a prompt, handling all mpprompt tags by calling the LLM and replacing them with responses.
    /// </summary>
    public void ProcessPrompt(T2IParamInput userInput)
    {
        var prompt = userInput.Get(T2IParamTypes.Prompt);
        var modelId = userInput.Get(_paramModelId);
        var useCache = userInput.Get(_paramUseCache);

        if (userInput.ExtraMeta.Remove("mp_is_refining", out var isRefiningValue)
            && bool.TryParse(isRefiningValue as string, out var isRefining)
            && isRefining)
        {
            if (!userInput.ExtraMeta.Remove("mp_refined_prompt", out var refinedPromptValue) || refinedPromptValue is not string refinedPrompt)
            {
                throw new SwarmUserErrorException("Refine Img requires a finalized prompt in the selected image metadata.");
            }
            FinalizePrompt(refinedPrompt, "", userInput);
            return;
        }

        var matches = MppromptRegex.Matches(prompt);

        if (matches.Count == 0)
        {
            prompt = CleanOrphanedMpresponse(prompt);
            FinalizePrompt(prompt, "", userInput);
            return;
        }

        if (!userInput.ExtraMeta.ContainsKey("original_prompt"))
        {
            userInput.ExtraMeta["original_prompt"] = prompt;
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            prompt = StripMppromptTags(prompt);
            prompt = StripMpresponseTags(prompt);
            FinalizePrompt(prompt, "", userInput);
            return;
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
            var effectiveModelId = string.IsNullOrWhiteSpace(tagModelId) ? modelId : tagModelId;
            modelsUsed.Add(effectiveModelId);

            var llmResponse = GetLlmResponse(mppromptContent, userInput, instructionId, effectiveModelId, useCache, i, fullTag);
            llmResponses.Add(llmResponse);
            prompt = printResponse
                ? InjectLlmResponse(prompt, fullTag, llmResponse)
                : prompt.Replace(fullTag, "");
        }

        RecordModelsUsed(userInput, modelsUsed);
        prompt = ResolveMpresponseReferences(prompt, llmResponses, logStandalone: true);
        FinalizePrompt(prompt, firstMppromptContent, userInput);
    }

    private string GetLlmResponse(string content, T2IParamInput userInput, string instructionId, string modelId, bool useCache, int tagIndex, string fullTag)
    {
        string response = null;
        string error = null;
        try
        {
            var timeoutMs = LLMAPICalls.GetChatBackendTimeoutMs(out var backendIdentity);
            var thinking = userInput.Get(_paramThinking, defVal: "none");
            var seed = userInput.Get(T2IParamTypes.Seed, -1);
            var instructions = InstructionResolver.Resolve(userInput, instructionId, _paramInstructions);
            response = _cache.GetOrCreate(content, instructions, modelId, thinking, backendIdentity, seed, useCache,
                () => MakeLlmRequest(content, userInput, instructions, modelId, thinking), timeoutMs, userInput.InterruptToken, out error);
        }
        catch (OperationCanceledException) when (userInput.InterruptToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (string.IsNullOrEmpty(response))
        {
            return HandleFailedResponse(content, userInput, tagIndex, fullTag, error);
        }

        return ApplyPostFilter(response, userInput);
    }

    /// <summary>
    /// Handles an LLM call that produced no usable response. Depending on "MP On Error" this either falls back to the
    /// raw prompt text, or throws so that Swarm skips this one generation. Skipping only leaves the rest of a batch
    /// running when Swarm's "Continue After Errors" parameter is enabled; otherwise Swarm cancels the whole queue.
    /// </summary>
    private string HandleFailedResponse(string content, T2IParamInput userInput, int tagIndex, string fullTag, string error)
    {
        var reason = string.IsNullOrWhiteSpace(error) ? "empty response from LLM" : error;
        Logs.Error($"MagicPromptExtension.PromptHandler: LLM call failed for tag #{tagIndex} '{fullTag}': {reason}");

        if (userInput.Get(_paramOnError, defVal: OnErrorSkip).Equals(OnErrorFallback, StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        throw new SwarmReadableErrorException($"MagicPrompt: LLM request failed for tag #{tagIndex}, skipping this generation. {reason}");
    }

    private static string MakeLlmRequest(string prompt, T2IParamInput userInput, string instructions, string modelId, string thinking)
    {
        var request = new JObject
        {
            ["messageContent"] = new JObject
            {
                ["text"] = prompt,
                ["instructions"] = instructions
            },
            ["modelId"] = modelId,
            ["messageType"] = "Text",
            ["action"] = "prompt",
            ["session_id"] = userInput.SourceSession?.ID ?? string.Empty,
            ["thinking"] = thinking
        };

        var resp = LLMAPICalls.MagicPromptPhoneHomeForGeneration(request, userInput.SourceSession, userInput.InterruptToken)
            .GetAwaiter()
            .GetResult();

        var success = resp?["success"];
        if (success == null || !success.Value<bool>())
        {
            var error = resp?["error"]?.ToString();
            throw new SwarmReadableErrorException(string.IsNullOrWhiteSpace(error) ? "LLM backend returned an unspecified error" : error);
        }

        var llmResponse = resp?["response"]?.ToString();
        return string.IsNullOrWhiteSpace(llmResponse) ? null : llmResponse;
    }

    /// <summary>
    /// Parses the optional mpprompt pre-data (the [...] section).
    /// Grammar: [instruction, model, printResponse]
    /// - The optional model can also be specified after the instruction with "|" for compatibility.
    /// - Any parameter whose value is "false" suppresses printing the LLM response while still recording it for
    ///   &lt;mpresponse:N&gt;. This allows the flag to be the first, second, or third parameter.
    /// Examples:
    /// - [Action]                 => instruction "Action", default model, print
    /// - [Action, gpt-4o]         => instruction "Action", model "gpt-4o", print
    /// - [Action|gpt-4o]          => instruction "Action", model "gpt-4o", print
    /// - [|random]                => default instruction, random model, print
    /// - [Action, gpt-4o, false]  => instruction "Action", model "gpt-4o", don't print
    /// - [false]                  => default instruction, default model, don't print
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

        var parameters = raw.Split(',', StringSplitOptions.None)
            .Select(parameter => parameter.Trim())
            .ToArray();

        // The output flag may occupy any of the supported parameter positions. Remove it from the
        // instruction/model values so it can never be sent to the instruction resolver or model selector.
        if (parameters.Any(IsFalseParameter))
        {
            printResponse = false;
        }

        var instructionPart = parameters.Length > 0 && !IsFalseParameter(parameters[0])
            ? parameters[0]
            : null;
        var modelPart = parameters.Length > 1 && !IsFalseParameter(parameters[1])
            ? parameters[1]
            : null;

        // Split instruction from model on the first '|'. This remains supported for existing prompts.
        int pipeIndex = instructionPart?.IndexOf('|') ?? -1;
        if (pipeIndex >= 0)
        {
            var pipeModelPart = instructionPart[(pipeIndex + 1)..].Trim();
            instructionPart = instructionPart[..pipeIndex].Trim();

            if (IsFalseParameter(instructionPart))
            {
                printResponse = false;
                instructionPart = null;
            }

            if (IsFalseParameter(pipeModelPart))
            {
                printResponse = false;
                pipeModelPart = null;
            }

            modelPart = pipeModelPart;
        }

        instructionId = string.IsNullOrWhiteSpace(instructionPart) ? null : instructionPart;
        modelSpec = string.IsNullOrWhiteSpace(modelPart) ? null : modelPart;
    }

    private static bool IsFalseParameter(string parameter)
    {
        return parameter.Equals("false", StringComparison.OrdinalIgnoreCase);
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

    private static string InjectLlmResponse(string prompt, string fullTag, string response)
    {
        var replacement = response.Trim();
        var pattern = $@"(?<before>(?:\r\n|\r|\n)+)?{Regex.Escape(fullTag)}(?<after>(?:\r\n|\r|\n)+)?";

        return Regex.Replace(prompt, pattern, match =>
        {
            var before = NormalizeLineBreak(match.Groups["before"].Value);
            var after = NormalizeLineBreak(match.Groups["after"].Value);
            return $"{before}{replacement}{after}";
        });
    }

    private static string NormalizeLineBreak(string lineBreaks)
    {
        if (lineBreaks.Length == 0)
        {
            return "";
        }

        return lineBreaks.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : lineBreaks[0].ToString();
    }

    private string ApplyPostFilter(string response, T2IParamInput userInput)
    {
        string postFilter = userInput.Get(_paramPostFilter, defVal: string.Empty);
        if (string.IsNullOrEmpty(postFilter))
        {
            return response.Trim();
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
