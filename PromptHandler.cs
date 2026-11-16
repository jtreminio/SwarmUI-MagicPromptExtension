using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension;

public class PromptHandler
{
    // Matches <mpprompt:...>, <mpprompt[InstructionName]:...>, and either form with inline post-filters.
    // Group 1 = optional comma-separated pre-data, Group 2 = optional inline filters,
    // Group 3 = prompt content (handles nested tags).
    private static readonly Regex MppromptRegex = new(@"<mpprompt(?:\[([^\]]+)\])?(?:\|\s*((?:""[^""]+""\s*)(?:,\s*""[^""]+""\s*)*))?:((?:[^<>]|<[^>]*>)+)>", RegexOptions.Compiled);
    private static readonly Regex InlinePostFilterRegex = new(@"""[^""]+""", RegexOptions.Compiled);
    // Group 1 = optional encoded inline filter, Group 2 = optional user-facing inline filters,
    // Group 3 = response index.
    private static readonly Regex MpresponseRegex = new(@"<mpresponse(?:\[([^\]]+)\])?(?:\|\s*((?:""[^""]+""\s*)(?:,\s*""[^""]+""\s*)*))?:(\d+)>", RegexOptions.Compiled);
    private const string InlinePostFilterTokenPrefix = "__mp_inline_post_filter__";
    private readonly PromptCache _cache;
    private readonly T2IRegisteredParam<bool> _paramUseCache;
    private readonly T2IRegisteredParam<string> _paramModelId;
    private readonly T2IRegisteredParam<string> _paramInstructions;
    private readonly T2IRegisteredParam<string> _paramPostFilter;
    private readonly T2IRegisteredParam<string> _paramThinking;
    private readonly T2IRegisteredParam<string> _paramOnError;
    private readonly T2IRegisteredParam<bool> _paramDisableLlmRequest;
    private readonly T2IRegisteredParam<bool> _paramDecisionChecks;
    private readonly T2IRegisteredParam<string> _paramDecisionQuestion;
    private readonly T2IRegisteredParam<string> _paramDecisionModelId;
    private sealed record PromptRequest(JObject SettingsResponse, int TimeoutMs, string BackendIdentity,
        bool CheckDecisions, DecisionEvaluator.Selection Decisions);

    public const string OnErrorSkip = "skip";
    public const string OnErrorFallback = "fallback";

    /// <summary>
    /// Rewrites inline filters and protects literal brackets before Swarm parses nested prompt tags.
    /// The filter is stored as an opaque pre-data token and decoded later by <see cref="ParsePreData"/>.
    /// </summary>
    public static void NormalizeInlinePostFilters(T2IParamInput userInput)
    {
        var prompt = userInput.Get(T2IParamTypes.Prompt);
        if (string.IsNullOrEmpty(prompt))
        {
            return;
        }

        PromptTagSyntax.ValidateHeaders(prompt);
        var normalized = PromptTagSyntax.MppromptHeaderRegex.Replace(prompt, match =>
        {
            var raw = match.Groups[1].Value;
            var options = PromptTagOptions.Parse(raw);
            if (options.Variables.Count > 0)
            {
                // Keep assignments opaque until the tag installs its local variable scope.
                if (match.Groups[2].Success)
                {
                    var filter = Convert.ToBase64String(Encoding.UTF8.GetBytes(match.Groups[2].Value));
                    raw = options.Positional + $",{InlinePostFilterTokenPrefix}{filter}" + raw[raw.IndexOf(';')..];
                }
                return $"<mpprompt[{PromptTagSyntax.EncodeParsedPrompt(raw, userInput)}]:";
            }
            if (!match.Groups[2].Success)
            {
                return match.Value;
            }

            var encodedFilter = Convert.ToBase64String(Encoding.UTF8.GetBytes(match.Groups[2].Value));
            var filterToken = $"{InlinePostFilterTokenPrefix}{encodedFilter}";
            var preData = match.Groups[1].Success
                ? $"{match.Groups[1].Value},{filterToken}"
                : filterToken;
            return $"<mpprompt[{preData}]:";
        });

        normalized = MpresponseRegex.Replace(normalized, match =>
        {
            if (!match.Groups[2].Success)
            {
                return match.Value;
            }

            var encodedFilter = Convert.ToBase64String(Encoding.UTF8.GetBytes(match.Groups[2].Value));
            return $"<mpresponse[{InlinePostFilterTokenPrefix}{encodedFilter}]:{match.Groups[3].Value}>";
        });

        normalized = PromptTagSyntax.ProtectLiteralBrackets(normalized, userInput);

        if (normalized == prompt)
        {
            return;
        }

        if (!userInput.ExtraMeta.ContainsKey("original_prompt"))
        {
            userInput.ExtraMeta["original_prompt"] = prompt;
        }
        userInput.Set(T2IParamTypes.Prompt, normalized);
    }

    public PromptHandler(
        PromptCache cache,
        T2IRegisteredParam<bool> paramUseCache,
        T2IRegisteredParam<string> paramModelId,
        T2IRegisteredParam<string> paramInstructions,
        T2IRegisteredParam<string> paramPostFilter,
        T2IRegisteredParam<string> paramThinking,
        T2IRegisteredParam<string> paramOnError,
        T2IRegisteredParam<bool> paramDisableLlmRequest,
        T2IRegisteredParam<bool> paramDecisionChecks = null,
        T2IRegisteredParam<string> paramDecisionQuestion = null,
        T2IRegisteredParam<string> paramDecisionModelId = null)
    {
        _cache = cache;
        _paramUseCache = paramUseCache;
        _paramModelId = paramModelId;
        _paramInstructions = paramInstructions;
        _paramPostFilter = paramPostFilter;
        _paramThinking = paramThinking;
        _paramOnError = paramOnError;
        _paramDisableLlmRequest = paramDisableLlmRequest;
        _paramDecisionChecks = paramDecisionChecks;
        _paramDecisionQuestion = paramDecisionQuestion;
        _paramDecisionModelId = paramDecisionModelId;
    }

    /// <summary>
    /// Processes a prompt, handling all mpprompt tags by calling the LLM and replacing them with responses.
    /// </summary>
    public void ProcessPrompt(T2IParamInput userInput)
    {
        var prompt = userInput.Get(T2IParamTypes.Prompt);
        var modelId = userInput.Get(_paramModelId);
        var useCache = userInput.Get(_paramUseCache);
        var disableLlmRequest = userInput.Get(_paramDisableLlmRequest, defVal: false);

        if (userInput.ExtraMeta.Remove("mp_is_refining", out var isRefiningValue)
            && bool.TryParse(isRefiningValue as string, out var isRefining)
            && isRefining)
        {
            if (!userInput.ExtraMeta.Remove("mp_refined_prompt", out var refinedPromptValue) || refinedPromptValue is not string refinedPrompt)
            {
                throw new SwarmUserErrorException("Refine Img requires a finalized prompt in the selected image metadata.");
            }
            // Restore variables after prompt parsing so they only carry forward as metadata.
            if (userInput.ExtraMeta.Remove("mp_refined_variables", out var refinedVariablesValue) && refinedVariablesValue is string refinedVariablesJson)
            {
                userInput.ExtraMeta["mp_variables"] = JObject.Parse(refinedVariablesJson).ToObject<Dictionary<string, string>>();
            }
            FinalizePrompt(refinedPrompt, "", userInput);
            return;
        }

        var matches = MppromptRegex.Matches(prompt);

        if (matches.Count == 0)
        {
            prompt = CleanOrphanedMpresponse(prompt);
            if (disableLlmRequest)
            {
                prompt = ApplyPostFilter(prompt, userInput);
            }
            FinalizePrompt(prompt, "", userInput);
            return;
        }

        if (!userInput.ExtraMeta.ContainsKey("original_prompt"))
        {
            userInput.ExtraMeta["original_prompt"] = prompt;
        }

        if (!disableLlmRequest && string.IsNullOrWhiteSpace(modelId))
        {
            prompt = StripMppromptTags(prompt, userInput);
            prompt = StripMpresponseTags(prompt);
            FinalizePrompt(prompt, "", userInput);
            return;
        }

        var firstMppromptContent = PromptTagSyntax.DecodeParsedPrompt(matches[0].Groups[3].Value, userInput);
        var llmResponses = new List<string>();
        var modelsUsed = new List<string>();
        PromptRequest request = disableLlmRequest ? null : PrepareRequest(userInput);

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var fullTag = match.Value;
            var preDataRaw = PromptTagOptions.Parse(PromptTagSyntax.DecodeParsedPrompt(match.Groups[1].Value, userInput)).Positional;
            var inlinePostFilterRaw = match.Groups[2].Success ? match.Groups[2].Value : null;
            ParsePreData(preDataRaw, inlinePostFilterRaw, out string instructionId, out string modelSpec, out bool printResponse, out string inlinePostFilter);
            var mppromptContent = ResolveMpresponseReferences(PromptTagSyntax.DecodeParsedPrompt(match.Groups[3].Value, userInput), llmResponses);
            string llmResponse;
            if (disableLlmRequest)
            {
                llmResponse = ApplyPostFilter(mppromptContent, userInput, inlinePostFilter);
            }
            else
            {
                var tagModelId = ResolveModel(modelSpec, userInput);
                var effectiveModelId = string.IsNullOrWhiteSpace(tagModelId) ? modelId : tagModelId;
                modelsUsed.Add(effectiveModelId);

                var variables = PromptTagSyntax.GetTagVariables(match.Groups[3].Value, userInput);
                llmResponse = GetLlmResponse(mppromptContent, userInput, instructionId, effectiveModelId, useCache, i, fullTag, inlinePostFilter, variables, request);
            }
            llmResponses.Add(llmResponse);
            prompt = printResponse
                ? InjectLlmResponse(prompt, fullTag, llmResponse)
                : prompt.Replace(fullTag, "");
        }

        RecordModelsUsed(userInput, modelsUsed);
        prompt = ResolveMpresponseReferences(prompt, llmResponses, logStandalone: true);
        FinalizePrompt(prompt, firstMppromptContent, userInput);
    }

    private PromptRequest PrepareRequest(T2IParamInput userInput)
    {
        JObject response = SessionSettings.GetMagicPromptSettings().GetAwaiter().GetResult();
        JObject settings = response["settings"] as JObject;
        int timeoutMs = LLMAPICalls.GetChatBackendTimeoutMs(settings, out string backendIdentity, out string endpoint);
        bool checkDecisions = _paramDecisionChecks != null && userInput.Get(_paramDecisionChecks, defVal: false);
        string selectedId = checkDecisions && _paramDecisionQuestion != null ? userInput.Get(_paramDecisionQuestion, defVal: "") : "";
        string selectedModel = checkDecisions && _paramDecisionModelId != null ? userInput.Get(_paramDecisionModelId, defVal: "") : "";
        DecisionEvaluator.Selection selection = checkDecisions
            ? DecisionEvaluator.ResolveSelection(settings?["decisions"], selectedId, endpoint, selectedModel) : null;
        return new PromptRequest(response, timeoutMs, backendIdentity, checkDecisions, selection);
    }

    private string GetLlmResponse(string content, T2IParamInput userInput, string instructionId, string modelId, bool useCache, int tagIndex, string fullTag, string inlinePostFilter, IReadOnlyDictionary<string, string> variables, PromptRequest request)
    {
        string response = null;
        string error = null;
        JObject decisionMatch = null;
        try
        {
            var thinking = userInput.Get(_paramThinking, defVal: "none");
            var seed = userInput.Get(T2IParamTypes.Seed, -1);
            var instructions = InstructionResolver.Resolve(request.SettingsResponse["settings"]?["instructions"], userInput, instructionId, _paramInstructions, variables);
            bool includeDecisionSource = request.Decisions?.Questions.Length > 0;
            // Skip rules need an independent outcome per seed, even with broad cache reuse enabled.
            bool shareAcrossSeeds = useCache && request.Decisions?.HasSkipRules != true;
            string decisionIdentity = request.CheckDecisions ? request.Decisions?.CacheIdentity ?? "no-remote-decisions" : null;
            int waitTimeoutMs = (int)Math.Min(int.MaxValue, request.TimeoutMs + 90_000L * (request.Decisions?.Questions.Length ?? 0));
            PromptCache.Response cached = _cache.GetOrCreateResponse(content, instructions, modelId, thinking,
                request.BackendIdentity, seed, shareAcrossSeeds, () =>
                {
                    string text = MakeLlmRequest(content, userInput, instructions, modelId, thinking, includeDecisionSource,
                        request.SettingsResponse, out JObject fetched);
                    return request.CheckDecisions
                        ? DecisionEvaluator.CheckResponse(text, fetched, request.Decisions,
                            (check, token) => OpenRouterDecisions.RequestAsync(check, token, request.SettingsResponse["settings"] as JObject, userInput.SourceSession),
                            userInput.InterruptToken)
                        : new PromptCache.Response(text);
                }, waitTimeoutMs, userInput.InterruptToken, out error, decisionIdentity);
            response = cached?.Text;
            if (request.CheckDecisions && !string.IsNullOrEmpty(response))
                decisionMatch = DecisionEvaluator.Replay(cached, userInput, tagIndex);
        }
        catch (OperationCanceledException) when (userInput.InterruptToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        SkipForDecision(decisionMatch, tagIndex);
        if (string.IsNullOrEmpty(response))
        {
            return HandleFailedResponse(content, userInput, tagIndex, fullTag, error);
        }

        return ApplyPostFilter(response, userInput, inlinePostFilter);
    }

    internal static void SkipForDecision(JObject match, int tagIndex)
    {
        if (match == null) return;
        string message = $"MagicPrompt: Skipped generation for tag #{tagIndex} because decision '{match["decision"]?["title"]}' matched Skip If '{match["skip_if"]}'.";
        // Swarm's pre-backend handler logs this once and skips just the current batch slot.
        var skip = new SwarmReadableErrorException(message);
        skip.Data["SwarmUI.SkipGeneration"] = true;
        throw skip;
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

    private static string MakeLlmRequest(string prompt, T2IParamInput userInput, string instructions, string modelId, string thinking,
        bool includeDecisionSource, JObject settingsResponse, out JObject fetched)
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

        fetched = LLMAPICalls.MagicPromptPhoneHomeForGeneration(request, userInput.SourceSession, userInput.InterruptToken, includeDecisionSource, settingsResponse)
            .GetAwaiter()
            .GetResult();

        var success = fetched?["success"];
        if (success == null || !success.Value<bool>())
        {
            var error = fetched?["error"]?.ToString();
            throw new SwarmReadableErrorException(string.IsNullOrWhiteSpace(error) ? "LLM backend returned an unspecified error" : error);
        }

        var llmResponse = fetched?["response"]?.ToString();
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
    private static void ParsePreData(string raw, string inlinePostFilterRaw, out string instructionId, out string modelSpec, out bool printResponse, out string inlinePostFilter)
    {
        instructionId = null;
        modelSpec = null;
        printResponse = true;
        inlinePostFilter = inlinePostFilterRaw;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var parameters = new List<string>();
        foreach (var rawParameter in raw.Split(',', StringSplitOptions.None))
        {
            var parameter = rawParameter.Trim();
            if (!TryDecodeInlinePostFilter(parameter, ref inlinePostFilter))
            {
                parameters.Add(parameter);
            }
        }

        if (parameters.Count == 0)
        {
            return;
        }

        // The output flag may occupy any of the supported parameter positions. Remove it from the
        // instruction/model values so it can never be sent to the instruction resolver or model selector.
        if (parameters.Any(IsFalseParameter))
        {
            printResponse = false;
        }

        var instructionPart = parameters.Count > 0 && !IsFalseParameter(parameters[0])
            ? parameters[0]
            : null;
        var modelPart = parameters.Count > 1 && !IsFalseParameter(parameters[1])
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

    private static bool TryDecodeInlinePostFilter(string parameter, ref string inlinePostFilter)
    {
        if (!parameter.StartsWith(InlinePostFilterTokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var encodedFilter = parameter[InlinePostFilterTokenPrefix.Length..];
            inlinePostFilter = Encoding.UTF8.GetString(Convert.FromBase64String(encodedFilter));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
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
            if (!int.TryParse(m.Groups[3].Value, out int refIndex))
            {
                return m.Value;
            }

            if (refIndex >= 0 && refIndex < llmResponses.Count)
            {
                var inlinePostFilter = m.Groups[2].Success ? m.Groups[2].Value : null;
                if (m.Groups[1].Success)
                {
                    TryDecodeInlinePostFilter(m.Groups[1].Value, ref inlinePostFilter);
                }
                return ApplyInlinePostFilter(llmResponses[refIndex], inlinePostFilter);
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

    private static string StripMppromptTags(string prompt, T2IParamInput userInput)
    {
        return MppromptRegex.Replace(prompt, m => PromptTagSyntax.DecodeParsedPrompt(m.Groups[3].Value, userInput));
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

    private string ApplyPostFilter(string response, T2IParamInput userInput, string inlinePostFilter = null)
    {
        string postFilter = userInput.Get(_paramPostFilter, defVal: string.Empty);
        if (!string.IsNullOrEmpty(postFilter))
        {
            response = ApplyPostFilters(response, postFilter.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }

        if (!string.IsNullOrEmpty(inlinePostFilter))
        {
            response = ApplyInlinePostFilter(response, inlinePostFilter);
        }

        return response.Trim();
    }

    private static string ApplyInlinePostFilter(string response, string inlinePostFilter)
    {
        if (string.IsNullOrEmpty(inlinePostFilter))
        {
            return response;
        }

        return ApplyPostFilters(response, InlinePostFilterRegex.Matches(inlinePostFilter).Select(match => match.Value)).Trim();
    }

    private static string ApplyPostFilters(string response, IEnumerable<string> filters)
    {
        foreach (var filter in filters)
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
                    response = response.Replace(inner[..eq], inner[(eq + 1)..], StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                response = response.Replace(inner, "", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            response = response.Replace(filter, "", StringComparison.OrdinalIgnoreCase);
        }

        return response;
    }

    private static void FinalizePrompt(string prompt, string originalMpprompt, T2IParamInput userInput)
    {
        prompt = PromptTagSyntax.RestoreLiteralBrackets(prompt, userInput);
        if (userInput.TryGet(T2IParamTypes.NegativePrompt, out string negativePrompt))
        {
            userInput.Set(T2IParamTypes.NegativePrompt, PromptTagSyntax.RestoreParsedPrompts(negativePrompt, userInput));
        }
        PromptTagSyntax.ClearParsingState(userInput);
        userInput.Set(T2IParamTypes.Prompt, prompt.Replace("<mporiginal>", originalMpprompt).Trim());
    }
}
