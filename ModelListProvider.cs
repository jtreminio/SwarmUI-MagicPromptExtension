using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;

namespace Hartsy.Extensions.MagicPromptExtension;

public static class ModelListProvider
{
    private const string LoadingPlaceholder = "loading///loading";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly object _cacheLock = new();
    private static JObject _cachedResponse;
    private static DateTime _cacheTimeUtc;

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

            HashSet<string> blockedIds = LoadBlockedModelIdsForActiveBackend();

            var list = new List<string>(models.Count);
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

                list.Add($"{modelId}///{displayName}");
            }

            return list.Count > 0 ? list : defaultResponse;
        }
        catch
        {
            return defaultResponse;
        }
    }

    private static HashSet<string> LoadBlockedModelIdsForActiveBackend()
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var settingsResponse = WebAPI.SessionSettings.GetMagicPromptSettings()
                .GetAwaiter()
                .GetResult();
            if (settingsResponse?["success"]?.Value<bool>() != true)
            {
                return blocked;
            }

            var settings = settingsResponse["settings"] as JObject;
            var backend = settings?["backend"]?.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(backend))
            {
                return blocked;
            }

            var blockedMap = settings["blockedModels"] as JObject;
            var blockedArr = blockedMap?[backend] as JArray;
            if (blockedArr == null)
            {
                return blocked;
            }

            foreach (var entry in blockedArr)
            {
                var id = entry?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(id))
                {
                    blocked.Add(id);
                }
            }
        }
        catch
        {
            // fall through with whatever ids we collected
        }
        return blocked;
    }

    public static List<string> GetInstructionList(Session session)
    {
        var defaultResponse = new List<string> { LoadingPlaceholder };

        try
        {
            var list = new List<string>();
            var response = GetCachedResponse(session);
            var settings = response?["settings"] as JObject;
            var instructions = settings?["instructions"] as JObject;

            var prompt = instructions?["prompt"]?.ToString();
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                list.Add("prompt///Enhance Prompt (Default)");
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

        var response = LLMAPICalls.GetMagicPromptModels(session)
            .GetAwaiter()
            .GetResult();

        lock (_cacheLock)
        {
            _cachedResponse = response;
            _cacheTimeUtc = DateTime.UtcNow;
        }

        return response;
    }
}
