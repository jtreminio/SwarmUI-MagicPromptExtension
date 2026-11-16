using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LiteDB;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension;

internal static class ModelStats
{
    private const int RecentSkipLimit = 100;
    private static readonly object DatabaseLock = new();

    internal sealed record Attempt(string Path, string Key, string Provider, string Model, long Revision);

    private sealed record Usage(BsonDocument Record, decimal? Cost);

    private sealed class Receipt(Attempt attempt, Usage usage)
    {
        internal readonly Attempt Attempt = attempt;
        internal readonly Usage Usage = usage;
        internal int Used;
    }

    private static string DatabasePath => Path.GetFullPath(Path.Combine(Program.DataDir, "MagicPrompt", "Stats.db"));

    // The provider IDs contain no NUL. A fixed-size digest avoids index size and collation limits.
    private static string Key(string provider, string model) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}\0{model}")));

    private static LiteDatabase Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        return new LiteDatabase(new ConnectionString { Filename = path });
    }

    internal static Attempt BeginAttempt(string provider, string model)
    {
        try
        {
            string path = DatabasePath;
            string key = Key(provider, model);
            lock (DatabaseLock)
            {
                using LiteDatabase database = Open(path);
                BsonDocument row = database.GetCollection("models").FindById(key);
                return new Attempt(path, key, provider, model, row?["revision"].AsInt64 ?? 0);
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"MagicPrompt: could not read the model stats revision: {ex.Message}");
            return null;
        }
    }

    internal static void CompleteAttempt(Attempt attempt, double? latencyMs, JObject fetched, string responseJson = null)
    {
        if (attempt == null) return;
        Usage usage = CaptureUsage(attempt, responseJson, latencyMs.HasValue);
        bool committed = Update(attempt, (row, records) =>
        {
            if (latencyMs is double latency)
            {
                row["completed"] = row["completed"].AsInt64 + 1;
                row["latency_total_ms"] = row["latency_total_ms"].AsDouble + latency;
                if (usage?.Cost is decimal cost)
                {
                    row["costed_completed"] = Count(row, "costed_completed") + 1;
                    row["cost_total"] = Cost(row, "cost_total") + cost;
                }
            }
            else
            {
                row["errors"] = row["errors"].AsInt64 + 1;
            }
            if (usage != null)
            {
                records.EnsureIndex("model_key");
                records.Insert(usage.Record);
            }
        });
        if (committed && latencyMs.HasValue && fetched != null)
            fetched.AddAnnotation(new Receipt(attempt, usage));
    }

    // Store the original usage JSON so optional fields and numeric precision survive unchanged.
    // Usage parsing is independent of the prompt parser, including for billed but unusable replies.
    private static Usage CaptureUsage(Attempt attempt, string responseJson, bool completed)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return null;
        try
        {
            using JsonDocument response = JsonDocument.Parse(responseJson);
            JsonElement root = response.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("usage", out JsonElement usage)
                || usage.ValueKind != JsonValueKind.Object) return null;
            decimal? cost = null;
            if (attempt.Provider == "openrouter" && usage.TryGetProperty("cost", out JsonElement amount)
                && amount.ValueKind == JsonValueKind.Number && amount.TryGetDecimal(out decimal value) && value >= 0)
            {
                // A tiny nonzero charge outside decimal's range is unknown, never free.
                string significand = amount.GetRawText().Split('e', 'E')[0];
                if (value != 0 || !significand.Any(c => c >= '1' && c <= '9')) cost = value;
            }
            BsonDocument record = new()
            {
                ["_id"] = Guid.NewGuid().ToString("N"), ["model_key"] = attempt.Key,
                ["provider"] = attempt.Provider, ["model"] = attempt.Model,
                ["revision"] = attempt.Revision, ["received_at_utc"] = DateTime.UtcNow,
                ["status"] = completed ? "completed" : "error", ["skipped"] = BsonValue.Null,
                ["usage_json"] = usage.GetRawText()
            };
            foreach ((string source, string target) in new[] { ("id", "provider_request_id"), ("model", "reported_model") })
                record[target] = root.TryGetProperty(source, out JsonElement field) && field.ValueKind == JsonValueKind.String
                    ? new BsonValue(field.GetString()) : BsonValue.Null;
            return new Usage(record, cost);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long Count(BsonDocument row, string field) => row[field].IsNull ? 0 : row[field].AsInt64;
    private static decimal Cost(BsonDocument row, string field) => row[field].IsNull ? 0m : row[field].AsDecimal;

    internal static void RecordSkipIf(JObject fetched, bool skipped)
    {
        // The in-memory receipt is issued only after committing this response's successful attempt.
        Receipt receipt = fetched.Annotation<Receipt>();
        if (receipt == null || Interlocked.Exchange(ref receipt.Used, 1) != 0) return;
        Update(receipt.Attempt, (row, records) =>
        {
            row["skip_completed"] = row["skip_completed"].AsInt64 + 1;
            if (skipped) row["skipped"] = row["skipped"].AsInt64 + 1;
            BsonArray recent = row["recent_skip_outcomes"] as BsonArray ?? new BsonArray();
            recent.Add(skipped);
            if (recent.Count > RecentSkipLimit) recent.RemoveAt(0);
            row["recent_skip_outcomes"] = recent;
            if (receipt.Usage?.Cost is decimal cost)
            {
                row["costed_skip_completed"] = Count(row, "costed_skip_completed") + 1;
                row["skip_cost_total"] = Cost(row, "skip_cost_total") + cost;
                if (skipped) row["costed_skipped"] = Count(row, "costed_skipped") + 1;
            }
            if (receipt.Usage != null)
            {
                receipt.Usage.Record["skipped"] = skipped;
                records.Update(receipt.Usage.Record);
            }
        }, requireCompletion: true);
    }

    private static bool Update(Attempt attempt, Action<BsonDocument, ILiteCollection<BsonDocument>> increment, bool requireCompletion = false)
    {
        try
        {
            lock (DatabaseLock)
            {
                using LiteDatabase database = Open(attempt.Path);
                database.BeginTrans();
                ILiteCollection<BsonDocument> models = database.GetCollection("models");
                BsonDocument row = models.FindById(attempt.Key) ?? NewRow(attempt.Key, attempt.Provider, attempt.Model, 0);
                if (row["revision"].AsInt64 != attempt.Revision || (requireCompletion && row["completed"].AsInt64 == 0))
                {
                    database.Rollback();
                    return false;
                }
                increment(row, database.GetCollection("usage"));
                models.Upsert(row);
                Commit(database);
                return true;
            }
        }
        catch (Exception ex)
        {
            Logs.Warning($"MagicPrompt: could not update model stats: {ex.Message}");
            return false;
        }
    }

    private static BsonDocument NewRow(string key, string provider, string model, long revision) => new()
    {
        ["_id"] = key, ["provider"] = provider, ["model"] = model, ["revision"] = revision,
        ["completed"] = 0L, ["errors"] = 0L, ["latency_total_ms"] = 0d, ["skip_completed"] = 0L, ["skipped"] = 0L,
        ["recent_skip_outcomes"] = new BsonArray(),
        ["costed_completed"] = 0L, ["cost_total"] = 0m,
        ["costed_skip_completed"] = 0L, ["costed_skipped"] = 0L, ["skip_cost_total"] = 0m
    };

    internal static JArray Read()
    {
        lock (DatabaseLock)
        {
            using LiteDatabase database = Open(DatabasePath);
            return Read(database.GetCollection("models"));
        }
    }

    internal static JArray Reset(string provider, string model)
    {
        lock (DatabaseLock)
        {
            using LiteDatabase database = Open(DatabasePath);
            database.BeginTrans();
            ILiteCollection<BsonDocument> models = database.GetCollection("models");
            string key = Key(provider, model);
            long revision = models.FindById(key)?["revision"].AsInt64 ?? 0;
            models.Upsert(NewRow(key, provider, model, checked(revision + 1)));
            database.GetCollection("usage").DeleteMany(Query.EQ("model_key", key));
            JArray stats = Read(models);
            Commit(database);
            return stats;
        }
    }

    private static void Commit(LiteDatabase database)
    {
        if (!database.Commit()) throw new InvalidOperationException("The model stats transaction did not commit.");
    }

    private static JArray Read(ILiteCollection<BsonDocument> models) => new(models.FindAll().Select(row =>
    {
        long completed = row["completed"].AsInt64;
        BsonArray recent = row["recent_skip_outcomes"] as BsonArray;
        return new JObject
        {
            ["provider"] = row["provider"].AsString, ["model"] = row["model"].AsString,
            ["completed"] = completed, ["errors"] = row["errors"].AsInt64,
            ["latency_ms"] = completed == 0 ? JValue.CreateNull() : new JValue(row["latency_total_ms"].AsDouble / completed),
            ["skip_completed"] = row["skip_completed"].AsInt64, ["skipped"] = row["skipped"].AsInt64,
            ["costed_completed"] = Count(row, "costed_completed"), ["cost_total"] = Cost(row, "cost_total"),
            ["costed_skip_completed"] = Count(row, "costed_skip_completed"), ["costed_skipped"] = Count(row, "costed_skipped"),
            ["skip_cost_total"] = Cost(row, "skip_cost_total"),
            ["recent_skip_completed"] = recent?.Count ?? 0, ["recent_skipped"] = recent?.Count(outcome => outcome.AsBoolean) ?? 0
        };
    }));
}
