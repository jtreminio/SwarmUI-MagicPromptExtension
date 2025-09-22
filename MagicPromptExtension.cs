using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.MagicPromptExtension;

public class MagicPromptExtension : Extension
{
    /// <summary>
    /// Immutable snapshot of the last cached prompt rewrite result.
    /// Storing as a single volatile reference ensures readers see a consistent pair.
    /// </summary>
    private sealed record CacheSnapshot(string NormalizedPrompt, string LlmPrompt);

    private static volatile CacheSnapshot _cacheSnapshot;
    private static readonly object CacheLock = new();

    // Cache for models/settings response to avoid duplicate API calls between GetValues lambdas
    private static readonly object ModelsCacheLock = new();
    private static JObject _modelsCacheResponse;
    private static DateTime _modelsCacheTimeUtc;
    private static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromSeconds(10);

    private static T2IRegisteredParam<bool> _paramAutoEnable;
    private static T2IRegisteredParam<bool> _paramUseCache;
    private static T2IRegisteredParam<string> _paramModelId;
    private static T2IRegisteredParam<string> _paramInstructions;
    private static T2IRegisteredParam<bool> _paramAppendOriginal;

    public override void OnPreInit()
    {
        Logs.Info("MagicPromptExtension Version 2.2 Now with Vision! has started.");
        ScriptFiles.Add("Assets/magicprompt.js");
        ScriptFiles.Add("Assets/vision.js");
        ScriptFiles.Add("Assets/chat.js");
        ScriptFiles.Add("Assets/settings.js");
        StyleSheetFiles.Add("Assets/magicprompt.css");
        StyleSheetFiles.Add("Assets/vision.css");
        StyleSheetFiles.Add("Assets/chat.css");
        StyleSheetFiles.Add("Assets/settings.css");
    }

    public override void OnInit()
    {
        // Register API endpoints so they can be used in the frontend
        MagicPromptAPI.Register();
        AddT2IParameters();
    }

    private static void AddT2IParameters()
    {
        var paramGroup = new T2IParamGroup(
            Name: "Magic Prompt",
            Toggles: true,
            Open: false,
            OrderPriority: 9
        );
        _paramAutoEnable = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "MP Auto Enable",
            Description: "Automatically use Magic Prompt to rewrite your prompt " +
                         "before generation.",
            Default: "false",
            Group: paramGroup,
            OrderPriority: 1
        ));
        _paramUseCache = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "MP Use Cache",
            Description: "Cache LLM results for static prompts to avoid repeated " +
                         "requests to LLM.",
            Default: "true",
            Group: paramGroup,
            OrderPriority: 2
        ));
        T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "MP Generate Wildcard Seed",
            Description: "Every time you press Generate, a new Wildcard Seed is " +
                         "generated. This is extremely useful for batching images, " +
                         "so they can reuse cached LLM responses.",
            Default: "false",
            Group: paramGroup,
            OrderPriority: 3
        ));
        _paramModelId = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "MP Model ID",
            Description: "Select an LLM to use for this batch",
            Default: "loading",
            IgnoreIf: "loading",
            Group: paramGroup,
            OrderPriority: 4,
            ValidateValues: false,
            GetValues: GetModelList
        ));
        _paramInstructions = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "MP Instructions",
            Description: "Select a prompt to use for this batch",
            Default: "loading",
            IgnoreIf: "loading",
            Group: paramGroup,
            OrderPriority: 5,
            ValidateValues: false,
            GetValues: GetInstructionList
        ));
        _paramAppendOriginal = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "MP Append Original Prompt",
            Description: "Append the original prompt after the generated LLM prompt.",
            Default: "false",
            Group: paramGroup,
            OrderPriority: 6
        ));

        T2IParamInput.LateSpecialParameterHandlers.Add(userInput =>
        {
            if (userInput.GetNullable(_paramAutoEnable) is null)
            {
                ClearCache();
                return;
            }

            // Get the current positive prompt early; if missing, nothing to do
            var prompt = userInput.InternalSet.Get(T2IParamTypes.Prompt);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return;
            }

            // If Auto Enable is not checked, clear cache and return early
            if (!userInput.InternalSet.Get(_paramAutoEnable))
            {
                // Clear cache whenever Auto Enable is off
                ClearCache();
                return;
            }

            // Parse the prompt to handle regional tags, segments, etc, and only
            // send the core text to the LLM
            var promptRegions = new PromptRegion(prompt);
            var parsedPrompt = string.IsNullOrWhiteSpace(promptRegions.GlobalPrompt)
                ? prompt
                : promptRegions.GlobalPrompt;

            try
            {
                var useCache = userInput.InternalSet.Get(_paramUseCache);
                if (!useCache)
                {
                    // Clear cache whenever Use Cache is off
                    ClearCache();
                }

                var llmResponse = useCache
                    ? HandleCacheableRequest(parsedPrompt, userInput)
                    // Use Cache is disabled: proceed with normal behavior
                    // (no cache coordination needed)
                    : MakeLlmRequest(parsedPrompt, userInput);

                // No response from LLM, fallback to original prompt
                if (string.IsNullOrEmpty(llmResponse)) return;

                // Remove the core text that was sent to the LLM from the original
                // prompt, leaving only regional tags/parts
                if (
                    !userInput.InternalSet.Get(_paramAppendOriginal)
                    && !string.IsNullOrWhiteSpace(promptRegions.GlobalPrompt)
                )
                {
                    prompt = prompt.Replace(promptRegions.GlobalPrompt, string.Empty);
                }

                if (!string.IsNullOrEmpty(prompt))
                {
                    llmResponse = $"{llmResponse}\n{prompt}";
                }

                userInput.InternalSet.Set(T2IParamTypes.Prompt, llmResponse);
            }
            catch (Exception ex)
            {
                Logs.Debug($"MagicPrompt phone home call failed: {ex.Message}");
            }
        });
    }

    private static string HandleCacheableRequest(string prompt, T2IParamInput userInput)
    {
        var normalizedPrompt = string.IsNullOrWhiteSpace(prompt)
            ? string.Empty
            : new string(prompt.Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c)).ToArray());

        // Fast path: double-checked locking
        // 1) Check outside the lock to avoid serializing the common case (cache hits).
        // 2) Acquire the lock and check again to avoid a race if another thread
        //    populated the cache meanwhile.
        // Note: Checking only once inside the lock would be correct but increases
        // contention and latency under concurrency.
        var cachedPrompt = CheckCache(normalizedPrompt);
        if (!string.IsNullOrEmpty(cachedPrompt))
        {
            Logs.Debug("MagicPrompt: cache hit (pre-lock) for static-tag prompt.");
            return cachedPrompt;
        }

        // Single-lock synchronization to avoid duplicate LLM calls for the same
        // prompt in parallel
        lock (CacheLock)
        {
            cachedPrompt = CheckCache(normalizedPrompt);
            // Double-check cache under the lock in case another thread populated
            // it meanwhile
            if (!string.IsNullOrEmpty(cachedPrompt))
            {
                Logs.Debug("MagicPrompt: cache hit (post-lock) for static-tag prompt.");
                return cachedPrompt;
            }

            try
            {
                var llmPrompt = MakeLlmRequest(prompt, userInput);
                if (!string.IsNullOrEmpty(llmPrompt))
                {
                    // Atomically swap the cache snapshot so readers observe a
                    // consistent pair
                    _cacheSnapshot = new CacheSnapshot(normalizedPrompt, llmPrompt);

                    return llmPrompt;
                }
            }
            catch (Exception ex)
            {
                Logs.Debug($"MagicPrompt phone home call failed: {ex.Message}");
            }
        }

        return null;
    }

    private static string CheckCache(string normalizedPrompt)
    {
        // Atomic snapshot read of the last cache entry
        var snapshot = _cacheSnapshot;
        if (snapshot is null)
        {
            return null;
        };

        if (!string.Equals(snapshot.NormalizedPrompt, normalizedPrompt, StringComparison.Ordinal))
        {
            return null;
        };

        return string.IsNullOrEmpty(snapshot.LlmPrompt) ? null : snapshot.LlmPrompt;
    }

    private static void ClearCache()
    {
        lock (CacheLock)
        {
            _cacheSnapshot = null;
        }
    }

    private static string MakeLlmRequest(string prompt, T2IParamInput userInput)
    {
        var request = new JObject
        {
            ["messageContent"] = new JObject
            {
                ["text"] = prompt,
                ["instructions"] = ResolveInstructions(userInput.InternalSet.Get(_paramInstructions))
            },
            ["modelId"] = userInput.InternalSet.Get(_paramModelId, defVal: string.Empty),
            ["messageType"] = "Text",
            ["action"] = "prompt",
            ["session_id"] = userInput.SourceSession?.ID ?? string.Empty
        };

        var resp = LLMAPICalls.MagicPromptPhoneHome(request, userInput.SourceSession)
            .GetAwaiter()
            .GetResult();

        if (!(bool)resp?["success"])
        {
            return null;
        }

        var llmResponse = resp?["response"]?.ToString();
        return string.IsNullOrWhiteSpace(llmResponse) ? null : llmResponse;
    }

    private static string ResolveInstructions(string instructions)
    {
        var sessionSettings = SessionSettings.GetMagicPromptSettings()
            .GetAwaiter()
            .GetResult();
        if (!sessionSettings["success"]!.Value<bool>())
        {
            return instructions;
        }

        var settings = sessionSettings!["settings"];
        if (settings == null)
        {
            return instructions;
        }

        var instructionsObj = settings!["instructions"];
        if (instructionsObj == null)
        {
            return instructions;
        }

        if (string.IsNullOrWhiteSpace(instructions))
        {
            return instructionsObj["prompt"]?.ToString();
        }

        // instructions currently holds the selected key
        var resolved = instructionsObj[instructions]?.ToString();

        return string.IsNullOrWhiteSpace(resolved)
            ? instructionsObj["prompt"]?.ToString()
            : resolved;
    }

    private static List<string> GetModelList(Session session)
    {

        var defaultResponse = new List<string>{"loading///loading"};

        try
        {
            var response = GetModelsResponseCached(session);
            if (response?["success"]?.Value<bool>() != true)
            {
                return defaultResponse;
            }

            var models = response["models"] as JArray;
            if (models == null || models.Count == 0)
            {
                return defaultResponse;
            }

            var list = new List<string>(models.Count);
            foreach (var m in models)
            {
                var modelId = m?["model"]?.ToString();
                var name = m?["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(modelId)) continue;
                if (string.IsNullOrWhiteSpace(name)) name = modelId;
                list.Add($"{modelId}///{name}");
            }

            return list.Count > 0 ? list : defaultResponse;
        }
        catch
        {
            return defaultResponse;
        }
    }

    private static List<string> GetInstructionList(Session session)
    {

        var defaultResponse = new List<string>{"loading///loading"};

        try
        {
            var list = new List<string>();
            var response = GetModelsResponseCached(session);
            var settings = response?["settings"] as JObject;
            var instructions = settings?["instructions"] as JObject;

            var chat = instructions?["chat"]?.ToString();
            if (!string.IsNullOrWhiteSpace(chat))
            {
                list.Add($"prompt///Enhance Prompt (Default)");
            }

            var custom = instructions?["custom"] as JObject;
            if (custom != null)
            {
                foreach (var prop in custom.Properties())
                {
                    var title = prop.Value?["title"]?.ToString();
                    if (!string.IsNullOrEmpty(title))
                    {
                        list.Add($"{prop.Name}///{title}");
                    }
                }
            }

            return list.Count > 0 ? list : defaultResponse;
        }
        catch
        {
            return defaultResponse;
        }
    }

    private static JObject GetModelsResponseCached(Session session)
    {
        // Serve from cache if fresh
        lock (ModelsCacheLock)
        {
            if (_modelsCacheResponse != null && (DateTime.UtcNow - _modelsCacheTimeUtc) < ModelsCacheTtl)
            {
                return _modelsCacheResponse;
            }
        }

        var resp = LLMAPICalls.GetMagicPromptModels(session)
            .GetAwaiter()
            .GetResult();

        lock (ModelsCacheLock)
        {
            _modelsCacheResponse = resp;
            _modelsCacheTimeUtc = DateTime.UtcNow;
        }

        return resp;
    }
}