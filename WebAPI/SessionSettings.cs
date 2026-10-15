using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension.WebAPI;

public class SessionSettings : MagicPromptAPI
{
    private const string SETTINGS_KEY = "magicprompt";
    private const string SETTINGS_SUBKEY = "config";

    /// <summary>Supported provider keys aligned with the settings UI and <see cref="DefaultBackendConfig"/>.</summary>
    private static readonly string[] BlockedModelBackendKeys =
    [
        "openrouter",
        "openaiapi"
    ];

    /// <summary>Loads <c>blockedModels</c>: each known backend maps to distinct non-empty model id strings.</summary>
    private static void NormalizeBlockedModels(JObject settings)
    {
        NormalizeBackendModelIdMap(settings, "blockedModels");
    }

    /// <summary>Loads <c>favoritedModels</c>: each known backend maps to distinct non-empty model id strings.</summary>
    private static void NormalizeFavoritedModels(JObject settings)
    {
        NormalizeBackendModelIdMap(settings, "favoritedModels");
    }

    /// <summary>
    /// Coerces <paramref name="settingsKey"/> into the canonical shape:
    /// <c>{ &lt;backend&gt;: ["id", ...] }</c> with every known backend present,
    /// duplicates removed (case-insensitive), and unknown backend keys dropped.
    /// </summary>
    private static void NormalizeBackendModelIdMap(JObject settings, string settingsKey)
    {
        if (settings[settingsKey] is not JObject backendsMap)
        {
            settings[settingsKey] = new JObject();
            backendsMap = settings[settingsKey] as JObject;
        }

        foreach (string key in BlockedModelBackendKeys)
        {
            JToken token = backendsMap[key];
            if (token is not JArray incomingRows)
            {
                backendsMap[key] = new JArray();
                continue;
            }

            JArray sanitized = [];
            HashSet<string> seen = [];

            foreach (JToken elem in incomingRows)
            {
                if (elem is not JValue jv || jv.Type != JTokenType.String)
                {
                    continue;
                }

                string id = jv.ToString()?.Trim();
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                if (!seen.Contains(id.ToLowerInvariant()))
                {
                    seen.Add(id.ToLowerInvariant());
                    sanitized.Add(id);
                }
            }

            backendsMap[key] = sanitized;
        }

        foreach (JProperty leftover in backendsMap.Properties().ToList())
        {
            bool known = BlockedModelBackendKeys.Contains(leftover.Name);
            if (!known)
            {
                backendsMap.Remove(leftover.Name);
            }
        }
    }

    /// <summary>
    /// Ensures a model id never lives in both <c>blockedModels</c> and <c>favoritedModels</c>
    /// for the same backend — blocked wins, so a blocked id is removed from favorites.
    /// </summary>
    private static void EnforceBlockFavoriteExclusivity(JObject settings)
    {
        if (settings["blockedModels"] is not JObject blockedMap ||
            settings["favoritedModels"] is not JObject favoritedMap)
        {
            return;
        }
        foreach (string key in BlockedModelBackendKeys)
        {
            if (blockedMap[key] is not JArray blockedArr || favoritedMap[key] is not JArray favoritedArr)
            {
                continue;
            }
            HashSet<string> blockedIds = [];
            foreach (JToken t in blockedArr)
            {
                string id = t?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    blockedIds.Add(id.ToLowerInvariant());
                }
            }
            if (blockedIds.Count == 0)
            {
                continue;
            }
            JArray filtered = [];
            foreach (JToken t in favoritedArr)
            {
                string id = t?.ToString()?.Trim();
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }
                if (!blockedIds.Contains(id.ToLowerInvariant()))
                {
                    filtered.Add(id);
                }
            }
            favoritedMap[key] = filtered;
        }
    }

    // Hardcoded API endpoints and configurations
    private static readonly JObject DefaultBackendConfig = new()
    {
        ["openaiapi"] = new JObject
        {
            ["baseurl"] = "http://localhost:11434",
            ["unloadModel"] = false,
            ["timeout"] = 120,
            ["endpoints"] = new JObject
            {
                ["chat"] = "v1/chat/completions",
                ["models"] = "/v1/models"
            }
        },
        ["openrouter"] = new JObject
        {
            ["baseurl"] = "https://openrouter.ai",
            ["timeout"] = 20,
            ["endpoints"] = new JObject
            {
                ["chat"] = "/api/v1/chat/completions",
                ["models"] = "/api/v1/models"
            }
        }
    };

    private static readonly JObject DefaultSettings = new()
    {
        ["backend"] = "openrouter",
        ["model"] = "",
        ["instructions"] = new JObject
        {
            ["prompt"] = "You are a prompt enhacing assistent. The user will give you a prompt, and its your job to convert it into an actually useful prompt.\n\nHere are examples:\nUser:\npinkie pie and rarity standing in the water, worried.\n\nYour Enhanced Prompt:\nPinkie Pie stands on a beach, arms outstretched as if floating above the water's surface. Rarity, who is submerged up to her head, looks up at her with a concerned expression. The sky is clear and blue, with a few clouds scattered across it. The ocean below is calm, mirroring the serene atmosphere. Both characters are part of the \"My Little Pony: Friendship Is Magic\" series. Rarity has dark purple hair with a light blue tail and horn. Pinkie Pie has bright pink skin, a large head, and her hair is styled into a curly bun. They appear to be in the middle of a conversation or an emotional moment. Caption: \"An emotional scene on a sunny beach where Pinkie Pie appears to be floating while Rarity looks concerned.\"\n\nUser:\ncomic of tracer from overwatch stuck inside a spinning washing machine while mercy is pointing and laughing on the side\n\nYour Enhanced Prompt:\nA high-quality digital illustration in a modern western cartoon style, reminiscent of a high-end webcomic. The artwork features bold, clean lineart and vibrant, cel-shaded colors.\nThe scene depicts the Overwatch character Tracer, trapped and spinning inside a white front-loading washing machine. She is seen through the round glass door, her hands splayed against it in panic. Her mouth is wide open in a scream, and her signature orange goggles show large, hypnotic orange and white swirls, indicating she is extremely dizzy. She is wearing her classic brown leather flight jacket over an orange shirt.\nStanding next to the machine is the Overwatch character Mercy, who is laughing uncontrollably at Tracer's predicament. Mercy is bent over, clutching her stomach with one hand while pointing a finger directly at Tracer with the other. Her eyes are squeezed shut with mirth, and her mouth is open in a huge laugh. She is wearing her iconic white and gold Valkyrie suit with her yellow mechanical wings visible.\nTo emphasize the action, the inside of the washing machine drum has subtle motion blur and faint speed lines, suggesting a rapid spin cycle. Floating in the air next to Mercy's head is the onomatopoeia \"HA HA\" written in a bold, white, outlined comic book font.\nThe background is a simple laundry room with light aquamarine or green tiled walls.\n\nReply with only the enhanced prompt. Do not explain yourself.",
            ["randomprompt"] = "You are a creative text prompt generator for Stable Diffusion image generation. Your ONLY job is to create detailed, funny text prompts that will be sent to an AI image generator to create hilarious images. You do NOT generate images - you only write TEXT PROMPTS that describe images.\n\nCreate a detailed, funny random image prompt depicting a crazy, absurd, or unexpectedly humorous situation. Include vivid, specific descriptions with characters, settings, actions, and amusing details. Make the scenarios wonderfully ridiculous with enough descriptive detail to create a compelling, hilarious image.\n\nExamples of good prompts:\n- a sophisticated penguin wearing a monocle conducting a symphony orchestra of confused farm animals\n- an elderly grandmother breakdancing on top of a giant hamburger while rainbow-colored squirrels cheer from the sidelines  \n- a grumpy dragon working as a barista making latte art shaped like tiny castles for a line of impatient unicorns\n\nRespond with ONLY the text prompt - no explanations, no commentary, no mentions about not being able to generate images. Just the descriptive text prompt that will be used by Stable Diffusion.",
            ["instructiongen"] = "Write a complete system prompt for a language model that creates or rewrites image and video prompts. Address the model directly, using the supplied description to define its behavior, output format, and constraints. Return only the system prompt, without a preamble or commentary."
        },
        ["backends"] = DefaultBackendConfig.DeepClone(), // TODO: Change the name of this to something more descriptive
        ["blockedModels"] = new JObject
        {
            ["openrouter"] = new JArray(),
            ["openaiapi"] = new JArray()
        },
        ["favoritedModels"] = new JObject
        {
            ["openrouter"] = new JArray(),
            ["openaiapi"] = new JArray()
        }
    };

    /// <summary>Removes retired providers and safely falls back from old saved selections.</summary>
    private static void RemoveUnsupportedBackends(JObject settings)
    {
        string selected = settings["backend"]?.ToString()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(selected) || !DefaultBackendConfig.ContainsKey(selected))
        {
            settings["backend"] = "openrouter";
            settings["model"] = "";
        }
        else
        {
            settings["backend"] = selected;
        }
        settings.Remove("visionbackend");
        settings.Remove("visionmodel");
        settings.Remove("linkChatAndVisionModels");

        if (settings["backends"] is JObject backends)
        {
            foreach (JProperty backend in backends.Properties().ToList())
            {
                if (!DefaultBackendConfig.ContainsKey(backend.Name))
                {
                    backends.Remove(backend.Name);
                }
            }
        }
    }

    public static async Task<JObject> GetMagicPromptSettings()
    {
        try
        {
            string settingsJson = Program.Sessions.GenericSharedUser.GetGenericData(SETTINGS_KEY, SETTINGS_SUBKEY);
            JObject settings;
            if (string.IsNullOrEmpty(settingsJson))
            {
                settings = DefaultSettings.DeepClone() as JObject;
            }
            else
            {
                Logs.Verbose($"Retrieved settings from {SETTINGS_KEY}/{SETTINGS_SUBKEY} ({settingsJson.Length} chars)");
                settings = JObject.Parse(settingsJson);
                // Ensure we have all required backend configurations
                JObject backendsConfig = settings["backends"] as JObject ?? [];
                foreach (KeyValuePair<string, JToken> backend in DefaultBackendConfig)
                {
                    if (backendsConfig[backend.Key] == null)
                    {
                        backendsConfig[backend.Key] = backend.Value.DeepClone();
                        Logs.Verbose($"Added missing backend config for {backend.Key}");
                    }
                    else
                    {
                        // Ensure all required fields exist
                        JObject defaultBackend = backend.Value as JObject;
                        JObject existingBackend = backendsConfig[backend.Key] as JObject;
                        foreach (KeyValuePair<string, JToken> prop in defaultBackend)
                        {
                            if (existingBackend[prop.Key] == null)
                            {
                                existingBackend[prop.Key] = prop.Value.DeepClone();
                                Logs.Verbose($"Added missing property {prop.Key} for backend {backend.Key}");
                            }
                        }
                    }
                }
                settings["backends"] = backendsConfig;
            }
            RemoveUnsupportedBackends(settings);
            NormalizeBlockedModels(settings);
            NormalizeFavoritedModels(settings);
            EnforceBlockFavoriteExclusivity(settings);
            return CreateSuccessResponse(null, null, settings);
        }
        catch (Exception ex)
        {
            Logs.Error($"Failed to get settings: {ex.Message}\nStack trace: {ex.StackTrace}");
            return CreateErrorResponse($"Failed to get settings: {ex.Message}");
        }
    }

    public static async Task<JObject> SaveMagicPromptSettings(JObject settings)
    {
        try
        {
            if (settings["settings"] == null)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"] = "No settings section provided"
                };
            }
            // Get existing settings
            JObject existingSettings = await GetMagicPromptSettings();
            JObject newSettings = [];
            // Helper function to merge objects recursively
            static void MergeSettings(JObject target, JObject source, string parentKey = null)
            {
                if (source == null) return;
                foreach (JProperty prop in source.Properties())
                {
                    string key = prop.Name;
                    JToken value = prop.Value;
                    string path = parentKey == null ? key : $"{parentKey}.{key}";
                    // Special handling for backends
                    if (key == "backends")
                    {
                        if (target[key] == null || target[key] is not JObject)
                        {
                            target[key] = new JObject();
                        }
                        JObject targetBackends = target[key] as JObject;
                        JObject sourceBackends = value as JObject;
                        // Merge each backend's configuration, preserving unloadModel
                        foreach (JProperty backend in sourceBackends.Properties())
                        {
                            if (targetBackends[backend.Name] == null)
                            {
                                targetBackends[backend.Name] = new JObject();
                            }
                            MergeSettings(targetBackends[backend.Name] as JObject, backend.Value as JObject, $"{path}.{backend.Name}");
                        }
                        continue;
                    }
                    // Special handling for custom instructions
                    if (path == "instructions.custom")
                    {
                        if (target[key] == null || target[key] is not JObject)
                        {
                            target[key] = new JObject();
                        }
                        JObject targetCustom = target[key] as JObject;
                        JObject sourceCustom = value as JObject;
                        // Handle each custom instruction
                        foreach (JProperty instruction in sourceCustom.Properties())
                        {
                            // Check if the instruction is marked as deleted
                            if (instruction.Value["deleted"]?.Value<bool>() == true)
                            {
                                // Remove the instruction if it exists
                                if (targetCustom[instruction.Name] != null)
                                {
                                    targetCustom.Remove(instruction.Name);
                                    Logs.Debug($"Deleted custom instruction: {instruction.Name}");
                                }
                            }
                            else if (instruction.Value is JObject instructionObj)
                            {
                                if (targetCustom[instruction.Name] == null || targetCustom[instruction.Name] is not JObject)
                                {
                                    targetCustom[instruction.Name] = new JObject();
                                }
                                MergeSettings(targetCustom[instruction.Name] as JObject, instructionObj, $"{path}.{instruction.Name}");
                            }
                            else
                            {
                                targetCustom[instruction.Name] = instruction.Value;
                            }
                        }
                        continue;
                    }
                    // Handle nested objects like "keys" or "backends"
                    if (value is JObject sourceObj)
                    {
                        if (target[key] == null || target[key] is not JObject)
                        {
                            target[key] = new JObject();
                        }
                        MergeSettings(target[key] as JObject, sourceObj, path);
                    }
                    // Handle arrays or simple values like "model" or "backend"
                    else
                    {
                        if (value != null && value.Type != JTokenType.Null)
                        {
                            target[key] = value;
                        }
                    }
                }
            }
            // Start with existing settings
            newSettings = existingSettings["settings"] as JObject ?? [];
            // Merge in new settings
            MergeSettings(newSettings, settings["settings"] as JObject);
            RemoveUnsupportedBackends(newSettings);
            if (newSettings["baseurl"] != null)
            {
                string backend = newSettings["backend"]?.ToString();
                if (backend == "openaiapi")
                {
                    // Only sync URL for configurable backends
                    if (newSettings["backends"]?[backend] != null)
                    {
                        ((JObject)newSettings["backends"][backend])["baseurl"] = newSettings["baseurl"];
                    }
                }
            }
            // Fixed backend URLs should never change
            newSettings["backends"]["openrouter"]["baseurl"] = "https://openrouter.ai";

            NormalizeBlockedModels(newSettings);
            NormalizeFavoritedModels(newSettings);
            EnforceBlockFavoriteExclusivity(newSettings);

            // Don't save API keys in settings as they are now stored in UserUpstreamApiKeys
            JObject backends = newSettings["backends"] as JObject;
            if (backends != null)
            {
                foreach (JProperty backend in backends.Properties())
                {
                    JObject backendObj = backend.Value as JObject;
                    if (backendObj != null && backendObj["apikey"] != null)
                    {
                        backendObj.Remove("apikey");
                    }
                }
            }
            Program.Sessions.GenericSharedUser.SaveGenericData(SETTINGS_KEY, SETTINGS_SUBKEY, newSettings.ToString());
            ModelListProvider.InvalidateCache();
            return new JObject
            {
                ["success"] = true,
                ["settings"] = newSettings
            };
        }
        catch (Exception ex)
        {
            Logs.Error($"Error in SaveMagicPromptSettings: {ex.Message}");
            return new JObject
            {
                ["success"] = false,
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>You screw something up? Resets user settings to defaults.</summary>
    public static async Task<JObject> ResetMagicPromptSettings()
    {
        try
        {
            JObject settings = DefaultSettings;
            // Override the user settings with defaults and save
            Program.Sessions.GenericSharedUser.SaveGenericData(SETTINGS_KEY, SETTINGS_SUBKEY, settings.ToString());
            ModelListProvider.InvalidateCache();
            Logs.Verbose("Reset settings to lowercase defaults");
            return CreateSuccessResponse(null, null, settings);
        }
        catch (Exception ex)
        {
            Logs.Error($"Failed to reset settings: {ex.Message}\nStack trace: {ex.StackTrace}");
            return CreateErrorResponse($"Failed to reset settings: {ex.Message}");
        }
    }
}
