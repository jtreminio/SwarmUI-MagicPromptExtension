using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.MagicPromptExtension;

public static class ModelListProvider
{
    private const string LoadingPlaceholder = "loading///loading";
    private const string DecisionSettingsDefault = "///Settings default";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly object _cacheLock = new();
    private static JObject _cachedResponse;
    private static DateTime _cacheTimeUtc;
    private static JArray _cachedDecisionModels;

    /// <summary>Discard the prompt-model catalog after a provider or preference change.</summary>
    public static void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedResponse = null;
        }
    }

    public static List<string> GetModelList(Session session)
    {
        var defaultResponse = new List<string> { LoadingPlaceholder };

        try
        {
            var response = GetCachedResponse(session);
            if (response?["success"]?.Value<bool>() != true)
            {
                return defaultResponse;
            }

            var models = response["models"] as JArray;
            if (models == null || models.Count == 0)
            {
                return defaultResponse;
            }

            HashSet<string> blockedIds = LoadBackendModelIdsForActive("blockedModels");
            HashSet<string> favoritedIds = LoadBackendModelIdsForActive("favoritedModels");

            var favorites = new List<string>();
            var others = new List<string>();
            foreach (var m in models)
            {
                var modelId = m?["model"]?.ToString();
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    continue;
                }

                if (blockedIds.Contains(modelId))
                {
                    continue;
                }

                var name = m?["name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = modelId;
                }

                string costOut = m?["costOut"]?.ToString();
                string displayName = $"{name} ‧ {(string.IsNullOrWhiteSpace(costOut) ? "—" : costOut.Trim())}";

                string entry = $"{modelId}///{displayName}";
                if (favoritedIds.Contains(modelId))
                {
                    favorites.Add(entry);
                }
                else
                {
                    others.Add(entry);
                }
            }

            var list = new List<string>(favorites.Count + others.Count);
            list.AddRange(favorites);
            list.AddRange(others);
            return list.Count > 0 ? list : defaultResponse;
        }
        catch
        {
            return defaultResponse;
        }
    }

    private static HashSet<string> LoadBackendModelIdsForActive(string settingsKey)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var settingsResponse = WebAPI.SessionSettings.GetMagicPromptSettings()
                .GetAwaiter()
                .GetResult();
            if (settingsResponse?["success"]?.Value<bool>() != true)
            {
                return ids;
            }

            var settings = settingsResponse["settings"] as JObject;
            var backend = settings?["backend"]?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(backend))
            {
                return ids;
            }

            var map = settings[settingsKey] as JObject;
            var arr = map?[backend] as JArray;
            if (arr == null)
            {
                return ids;
            }

            foreach (var entry in arr)
            {
                var id = entry?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    ids.Add(id);
                }
            }
        }
        catch
        {
            // fall through with whatever ids we collected
        }
        return ids;
    }

    public static List<string> GetDecisionQuestions(Session session)
    {
        JObject settings = SessionSettings.GetMagicPromptSettings().GetAwaiter().GetResult()["settings"] as JObject;
        JArray questions = settings?["decisions"]?["questions"] as JArray ?? [];
        List<string> options = [questions.Any(row => (bool?)row["selected"] == true)
            ? "///Settings defaults" : "///Settings defaults (none selected)"];
        options.AddRange(questions.Select(row => $"{row["id"]}///{row["title"]}"));
        return options;
    }

    public static List<string> GetDecisionModelList(Session session)
    {
        // Swarm lists parameter values synchronously, including when decision checks are disabled.
        JArray models;
        lock (_cacheLock)
        {
            models = _cachedDecisionModels;
        }
        if (models == null) return [DecisionSettingsDefault];

        JObject settings = SessionSettings.GetMagicPromptSettings().GetAwaiter().GetResult()["settings"] as JObject;
        return FormatDecisionModelOptions(settings?["decisions"], models);
    }

    internal static void CacheDecisionModels(JArray models)
    {
        lock (_cacheLock)
        {
            _cachedDecisionModels = models;
        }
    }

    internal static List<string> FormatDecisionModelOptions(JToken settings, JArray models)
    {
        List<string> options = [DecisionSettingsDefault];
        HashSet<string> blocked = new((settings?["blockedModels"] as JArray ?? []).Values<string>(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> favorites = new((settings?["favoritedModels"] as JArray ?? []).Values<string>(), StringComparer.OrdinalIgnoreCase);
        var orderedModels = models.OfType<JObject>()
            .Where(row => !blocked.Contains((string)row["model"]))
            .OrderByDescending(row => favorites.Contains((string)row["model"]))
            .ThenByDescending(row => long.TryParse(row["created"]?.ToString(), out long created) ? created : 0);

        foreach (JObject row in orderedModels)
        {
            string id = (string)row["model"];
            string name = (string)row["name"];
            List<string> prices = [$"{(string)row["costIn"] ?? "—"} / 1M in"];
            if ((string)row["costOut"] is string output and not "$0") prices.Add($"{output} / 1M out");
            if ((string)row["costRequest"] is string request and not "$0") prices.Add($"{request} / request");
            options.Add($"{id}///{(string.IsNullOrWhiteSpace(name) ? id : name)} · {string.Join(" · ", prices)}");
        }
        return options;
    }

    public static List<string> GetInstructionList(Session session)
    {
        var defaultResponse = new List<string> { LoadingPlaceholder };

        try
        {
            var list = new List<string>();
            var response = SessionSettings.GetMagicPromptSettings().GetAwaiter().GetResult();
            var settings = response?["settings"] as JObject;
            var instructions = settings?["instructions"] as JObject;

            var prompt = instructions?["prompt"]?.ToString();
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                list.Add("prompt///Default prompt");
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

    private static JObject GetCachedResponse(Session session)
    {
        lock (_cacheLock)
        {
            if (_cachedResponse != null && DateTime.UtcNow - _cacheTimeUtc < CacheTtl)
            {
                return _cachedResponse;
            }
        }

        var response = LLMAPICalls.GetMagicPromptModels(session).GetAwaiter().GetResult();

        lock (_cacheLock)
        {
            _cachedResponse = response;
            _cacheTimeUtc = DateTime.UtcNow;
        }

        return response;
    }
}
