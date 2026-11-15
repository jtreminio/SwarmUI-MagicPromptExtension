using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;

namespace Hartsy.Extensions.MagicPromptExtension;

internal static class DecisionHistory
{
    private const int PageSize = 25;
    private static readonly object LogLock = new();
    private static string HistoryDirectory => Path.Combine(Program.DataDir, "MagicPrompt", "Decisions");

    internal static void Append(JObject result)
    {
        string directory = HistoryDirectory;
        string date = ((string)result["time_utc"])[..10];
        lock (LogLock)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, $"{date}.jsonl"), result.ToString(Formatting.None) + "\n");
        }
    }

    private sealed class ModelCounts
    {
        public int Completed, Errors, Yes;
        public double ScoreTotal;
        public Dictionary<string, int> Choices = new(StringComparer.Ordinal);

        public void Add(string type, JObject row)
        {
            if (Text(row, "status") == "cancelled") return;
            JToken answer = row["answer"];
            if (Text(row, "status") != "ok" || answer == null)
            {
                Errors++;
                return;
            }
            switch (type)
            {
                case "noul" when answer.Type == JTokenType.Boolean:
                    if ((bool)answer) Yes++;
                    break;
                case "score" when answer.Type is JTokenType.Float or JTokenType.Integer && double.IsFinite((double)answer):
                    ScoreTotal += (double)answer;
                    break;
                case "choice" when answer.Type == JTokenType.String:
                    string option = (string)answer;
                    Choices[option] = Choices.GetValueOrDefault(option) + 1;
                    break;
                default:
                    Errors++;
                    return;
            }
            Completed++;
        }
    }

    private static string Text(JToken row, string key) => row?[key]?.Type == JTokenType.String ? (string)row[key] : "";

    private static string EvaluatorName(JObject row) => Text(row, "evaluator_model") is { Length: > 0 } model ? model : Text(row, "evaluator") switch
    {
        "startlux" => "startlux-models/StartLux-Decision-v1.1-0.8B-Instant-GGUF",
        "jev" => "chaoliangUNSW/Jev-Style-2B-Decision-v4.2-GGUF",
        string fallback => fallback
    };

    internal static JObject Read(DateOnly end, int days, string seriesId, int offset)
    {
        Dictionary<string, JObject> series = new(StringComparer.Ordinal);
        Dictionary<(string Provider, string Model), ModelCounts> models = new();
        JArray rows = new();
        int selectedCount = 0;
        foreach (JObject row in ReadRecords(end, days))
        {
            if (row["decision"] is not JObject definition || row["source"] is not JObject source) continue;
            string id = Text(definition, "id"), version = Text(row, "decision_version"), type = Text(definition, "type");
            string provider = Text(source, "provider"), model = Text(source, "model"), evaluator = EvaluatorName(row);
            if (id.Length == 0 || version.Length == 0 || provider.Length == 0 || model.Length == 0 || evaluator.Length == 0
                || type is not ("noul" or "choice" or "score")) continue;
            JArray keyParts = new(id, version, evaluator);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyParts.ToString(Formatting.None))));
            if (!series.ContainsKey(key))
            {
                string question = Text(definition, "question");
                series[key] = new JObject
                {
                    ["id"] = key, ["title"] = Text(definition, "title") is { Length: > 0 } title ? title : question,
                    ["question"] = question, ["type"] = type, ["version"] = version,
                    ["options"] = definition["options"]?.DeepClone(), ["evaluator"] = evaluator
                };
                if (string.IsNullOrEmpty(seriesId)) seriesId = key;
            }
            if (key != seriesId) continue;
            if (!models.TryGetValue((provider, model), out ModelCounts counts))
                models[(provider, model)] = counts = new();
            counts.Add(type, row);
            if (selectedCount++ >= offset && rows.Count < PageSize) rows.Add(row);
        }
        JArray summaries = new();
        foreach (var entry in models.OrderBy(entry => entry.Key.Provider, StringComparer.Ordinal).ThenBy(entry => entry.Key.Model, StringComparer.Ordinal))
        {
            ModelCounts counts = entry.Value;
            summaries.Add(new JObject
            {
                ["provider"] = entry.Key.Provider, ["model"] = entry.Key.Model, ["completed"] = counts.Completed, ["errors"] = counts.Errors,
                ["yes"] = counts.Yes, ["choices"] = JObject.FromObject(counts.Choices),
                ["mean_score"] = counts.Completed > 0 ? JToken.FromObject(counts.ScoreTotal / counts.Completed) : JValue.CreateNull()
            });
        }
        return new JObject
        {
            ["success"] = true, ["series"] = new JArray(series.Values), ["selected"] = seriesId,
            ["rows"] = rows, ["models"] = summaries, ["total"] = selectedCount, ["page_size"] = PageSize
        };
    }

    private static IEnumerable<JObject> ReadRecords(DateOnly end, int days)
    {
        for (int i = 0; i < days && end.DayNumber >= i; i++)
        {
            string date = end.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string file = Path.Combine(HistoryDirectory, $"{date}.jsonl");
            if (!File.Exists(file)) continue;
            using StreamReader input = new(File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            List<string> lines = [];
            while (input.ReadLine() is { } line) lines.Add(line);
            for (int index = lines.Count - 1; index >= 0; index--)
            {
                JObject record;
                try
                {
                    using JsonTextReader reader = new(new StringReader(lines[index])) { DateParseHandling = DateParseHandling.None };
                    record = JObject.Load(reader);
                }
                catch (JsonException) { continue; } // A request can overlap a write or encounter a damaged line.
                if (Text(record, "status") == "error" && Text(record, "error") == "Generation cancelled")
                {
                    record["status"] = "cancelled";
                    record["message"] = record["error"];
                    record.Remove("error");
                }
                yield return record;
            }
        }
    }
}
