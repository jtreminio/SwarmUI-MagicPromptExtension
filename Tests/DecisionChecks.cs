using Hartsy.Extensions.MagicPromptExtension;
using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Accounts;
using SwarmUI.Text2Image;

internal static class DecisionChecks
{
    private const string RemoteEndpoint = "https://openrouter.ai/api/v1/chat/completions";

    private static DecisionEvaluator.Selection Select(JObject settings, string id = "") =>
        DecisionEvaluator.ResolveSelection(DecisionEvaluator.NormalizeSettings(settings["decisions"]), id, RemoteEndpoint);
    internal static async Task Run()
    {
        CheckRuntimeLifecycle();
        JObject settings = JObject.Parse("""
            {"decisions":{"evaluator":"jev","questions":[
              {"id":"refusal","question":"Did the model refuse?","type":"noul","options":[],"selected":true},
              {"id":"rating","question":"How useful was the prompt?","type":"score","options":["Poor","Fair","Good"],"selected":true},
              {"id":"unused","question":"Unused?","type":"noul","options":[],"selected":false}]}}
            """);
        const string providerJson = """
            {"id":"gen-123","model":"actual-model","usage":{"prompt_tokens":3},"choices":[{"message":{"content":"AI: original response"}}]}
            """;
        JObject source = new() { ["response"] = "original response" };
        DecisionEvaluator.CaptureSource(source, providerJson, "http://127.0.0.1:1234", "openaiapi", "test-model", 123);
        if (source["decision_source"] != null) throw new Exception("Capture treated a loopback provider as remote.");
        DecisionEvaluator.CaptureSource(source, providerJson, "https://openrouter.ai/api/v1/chat/completions", "openrouter", "test-model", 123);
        await CheckCancellation(source, Select(settings));
        CheckFreshCache();
        await CheckSkipRules(source);
        if (Environment.GetEnvironmentVariable("MP_TEST_DECISION_BATCH") == "1") await CheckBatchSkip();
        List<JObject> requests = [];
        List<JObject> records = [];
        List<CancellationToken> requestDeadlines = [];
        Task Evaluate(bool enabled) => DecisionEvaluator.EvaluateAsync(source, enabled ? Select(settings) : null, 2, async (request, deadline) =>
        {
            requests.Add(request);
            requestDeadlines.Add(deadline);
            await Task.Yield();
            if (requests.Count == 2) throw new InvalidOperationException("scorer unavailable");
            return JObject.Parse("""{"answer":false,"probabilities":{"true":0.05,"false":0.95},"evaluator_model":"Jev Q8_0","metrics":{"generated_tokens":0}}""");
        }, records.Add, CancellationToken.None);

        await Evaluate(false);
        if (requests.Count != 0 || records.Count != 0) throw new Exception("Disabled decisions made a request.");
        source["decision_source"]["remote"] = false;
        await Evaluate(true);
        if (requests.Count != 0 || records.Count != 0) throw new Exception("Local generation entered decision inference.");
        source["decision_source"]["remote"] = true;
        await Evaluate(true);
        if (requests.Count != 2 || records.Count != 2 || requests.Any(request => (string)request["evidence"] != "AI: original response"))
            throw new Exception("Each selected decision must get the same original response exactly once.");
        if (requestDeadlines[0] == requestDeadlines[1]) throw new Exception("Each decision needs its own timeout.");
        if ((string)requests[1]["options"][0] != "Poor" || (string)requests[1]["options"][2] != "Good")
            throw new Exception("Rating options changed order.");
        if ((string)records[0]["status"] != "ok" || (bool)records[0]["answer"] || (int)records[0]["metrics"]["generated_tokens"] != 0
            || (string)records[1]["status"] != "error" || records[1]["answer"] != null)
            throw new Exception("Negative decisions, zero tokens, and failures must remain distinguishable.");
        if ((string)records[0]["source"]["provider_request_id"] != "gen-123" || records[0]["decision_version"] == null
            || (string)records[0]["request_id"] != (string)records[1]["request_id"])
            throw new Exception("Decision records lack source provenance or versioning.");
        var originalVersion = (string)records[0]["decision_version"];
        JObject duplicate = (JObject)settings["decisions"].DeepClone();
        duplicate["questions"][0]["title"] = " Compare ";
        duplicate["questions"][1]["title"] = "compare";
        try
        {
            DecisionEvaluator.NormalizeSettings(duplicate);
            throw new Exception("Duplicate question titles were accepted.");
        }
        catch (ArgumentException) { }
        settings["decisions"]["questions"][0]["title"] = "Refusal rate";
        settings["decisions"]["questions"][0]["selected"] = false;
        List<JObject> workflowRows = [];
        await DecisionEvaluator.EvaluateAsync(source, Select(settings, "refusal"), 2, (_, _) => Task.FromResult(JObject.Parse("""
            {"answer":true,"probabilities":[0.05,0.95],"evaluator_model":"Jev Q8_0"}
            """)), workflowRows.Add, CancellationToken.None);
        if (workflowRows.Count != 1 || (string)workflowRows[0]["decision"]["title"] != "Refusal rate"
            || (string)workflowRows[0]["decision_version"] != originalVersion)
            throw new Exception("A workflow must run only its selected question. Renaming it must preserve the version.");
        var image = new SwarmUI.Text2Image.T2IParamInput(null);
        DecisionEvaluator.AddMetadata(image, workflowRows);
        if ((bool)image.BuildExtraDataJObject()["mp_decisions"][0]["answer"] != true)
            throw new Exception("Checked image metadata lost its decision.");
        string dataDir = Program.DataDir;
        DirectoryInfo temporary = Directory.CreateTempSubdirectory("mp-decision-test-");
        try
        {
            Program.DataDir = temporary.FullName;
            foreach (JObject record in records) DecisionHistory.Append(record);
            string directory = Path.Combine(temporary.FullName, "MagicPrompt", "Decisions");
            string[] files = Directory.GetFiles(directory, "*.jsonl").OrderBy(path => path).ToArray();
            if (File.ReadAllBytes(files[0])[0] != (byte)'{') throw new Exception("JSONL must start without a UTF-8 BOM.");
            string[] persisted = files.SelectMany(File.ReadLines).ToArray();
            if (!persisted.SequenceEqual(records.Select(record => record.ToString(Formatting.None))))
                throw new Exception("Decision log did not append complete records in JSONL format.");
            foreach (JObject row in workflowRows) DecisionHistory.Append(row);
            JObject failure = (JObject)workflowRows[0].DeepClone();
            failure["status"] = "error";
            failure.Remove("answer");
            DecisionHistory.Append(failure);
            JObject cancelled = (JObject)failure.DeepClone();
            cancelled["status"] = "cancelled";
            cancelled["message"] = "Generation cancelled";
            DecisionHistory.Append(cancelled);
            JObject legacyCancellation = (JObject)failure.DeepClone();
            legacyCancellation["error"] = "Generation cancelled";
            DecisionHistory.Append(legacyCancellation);
            JObject secondModel = (JObject)workflowRows[0].DeepClone();
            secondModel["source"]["model"] = "second-model";
            DecisionHistory.Append(secondModel);
            JObject otherVersion = (JObject)workflowRows[0].DeepClone();
            otherVersion["decision"]["title"] = "Updated refusal test";
            otherVersion["decision_version"] = "latest-version";
            DecisionHistory.Append(otherVersion);
            JObject history = DecisionHistory.Read(DateOnly.FromDateTime(DateTime.UtcNow), 7, "", 0);
            var chosen = history["series"].Single(series => (string)series["title"] == "Refusal rate");
            history = DecisionHistory.Read(DateOnly.FromDateTime(DateTime.UtcNow), 7, (string)chosen["id"], 0);
            var model = history["models"].Single(row => (string)row["model"] == "actual-model");
            if ((int)model["completed"] != 2 || (int)model["errors"] != 1 || (int)model["yes"] != 1 || history["rows"].Count() != 6
                || history["rows"].Count(row => (string)row["status"] == "cancelled") != 2
                || history["models"].Count() != 2)
                throw new Exception("History comparison must group renamed questions and exclude errors from Yes/No counts.");

            DateOnly oldDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-120));
            string oldDateText = oldDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            JObject legacyRow = (JObject)records[0].DeepClone();
            ((JObject)legacyRow["decision"]).Remove("title");
            File.WriteAllText(Path.Combine(directory, $"{oldDateText}.jsonl"), legacyRow.ToString(Formatting.None) + "\n", System.Text.Encoding.UTF8);
            var oldHistory = await MagicPromptAPI.GetMagicPromptDecisionHistory(oldDateText, 30, "", 0);
            if ((int)oldHistory["total"] != 1 || (string)oldHistory["series"][0]["title"] != "Did the model refuse?")
                throw new Exception("History must read older pre-title records, including JSONL files with a BOM.");
        }
        finally
        {
            Program.DataDir = dataDir;
            temporary.Delete(recursive: true);
        }
        if (Environment.GetEnvironmentVariable("MP_TEST_DECISION_RUNTIME") == "1")
        {
            settings["decisions"]["questions"][0]["selected"] = true;
            foreach (string evaluator in new[] { "startlux", "jev" })
            {
                settings["decisions"]["evaluator"] = evaluator;
                settings["decisions"]["questions"][0]["skipIf"] = "";
                source["decision_evidence"] = "I’m sorry, but I can’t help generate that prompt.";
                records.Clear();
                await DecisionEvaluator.EvaluateAsync(source, Select(settings), 2, DecisionEvaluator.RequestAsync, records.Add, CancellationToken.None);
                if (records.Count != 2 || records.Any(record => (string)record["status"] != "ok") || (bool)records[0]["answer"] != true)
                    throw new Exception($"{evaluator} real decision check failed: {new JArray(records)}");
                records.Clear();
                settings["decisions"]["questions"][0]["skipIf"] = "yes";
                var matched = await DecisionEvaluator.EvaluateAsync(source, Select(settings, "refusal"), 2, DecisionEvaluator.RequestAsync, records.Add,
                    CancellationToken.None);
                if (matched == null || records.Count != 1 || (bool?)records[0]["generation_skipped"] != true)
                    throw new Exception($"{evaluator} did not record the expected live decision match.");
                Console.WriteLine($"Verified local decision bridge and Skip If match: {evaluator} ({matched["evaluator_model"]})");
            }
        }
    }

    private static async Task CheckCancellation(JObject source, DecisionEvaluator.Selection selection)
    {
        using CancellationTokenSource interrupt = new();
        List<JObject> recorded = [];
        int calls = 0;
        JObject result = await DecisionEvaluator.EvaluateAsync(source, selection, 0, (_, token) =>
        {
            calls++;
            interrupt.Cancel();
            token.ThrowIfCancellationRequested();
            throw new Exception("Expected the evaluator token to be cancelled.");
        }, recorded.Add, interrupt.Token);
        if (result != null || calls != 1 || recorded.Count != 1 || (string)recorded[0]["status"] != "cancelled")
            throw new Exception("Cancelling an in-flight check must record only that check and distinguish it from evaluator errors.");

        recorded.Clear();
        await DecisionEvaluator.EvaluateAsync(source, selection, 0, (_, _) =>
            throw new Exception("A pre-cancelled generation must not start a check."), recorded.Add, interrupt.Token);
        if (recorded.Count != 0) throw new Exception("A pre-cancelled generation must not create history rows.");
    }

    private static void CheckFreshCache()
    {
        PromptCache cache = new();
        string Fetch(Func<string> request, bool fresh = false) => cache.GetOrCreate("test prompt", "instructions", "model", "none",
            "remote", 42, true, request, 1000, CancellationToken.None, out _, requireFresh: fresh);

        Fetch(() => "cached");
        int attempts = 0;
        string result = Fetch(() => ++attempts == 1 ? null : "fresh", fresh: true);
        if (result != "fresh" || attempts != 2) throw new Exception("A fresh fetch must bypass the cache and retry a failed call.");
        if (Fetch(() => "fresh again", fresh: true) != "fresh again" || Fetch(() => "unexpected") != "cached")
            throw new Exception("A fresh fetch must not reuse or replace cached results.");
    }

    private static void CheckRuntimeLifecycle()
    {
        if (!OperatingSystem.IsLinux()) return;
        DirectoryInfo temporary = Directory.CreateTempSubdirectory("mp-runtime-startup-");
        try
        {
            string extension = Path.Combine(temporary.FullName, "extension with spaces");
            string models = Path.Combine(temporary.FullName, "models with spaces");
            string scripts = Path.Combine(extension, "DecisionRuntime");
            Directory.CreateDirectory(scripts);
            string runtime = Path.Combine(models, "decision-runtime");
            File.WriteAllText(Path.Combine(scripts, "run.sh"), """
                trap 'touch "$1/stopped"; exit 0' TERM
                printf '%s' "$1" > "$1/started"
                while true; do sleep 0.1; done
                """);
            if (DecisionRuntimeHost.Start(extension, models) != null)
                throw new Exception("Startup should skip an absent runtime.");
            Directory.CreateDirectory(Path.Combine(runtime, "venv", "bin"));
            File.WriteAllText(Path.Combine(runtime, "venv", "bin", "startlux-decide"), "installed fixture");
            using var launcher = DecisionRuntimeHost.Start(extension, $"{temporary.FullName};{models}");
            if (launcher == null) throw new Exception("Startup did not launch the mounted runtime.");
            try
            {
                string started = Path.Combine(runtime, "started");
                if (!SpinWait.SpinUntil(() => File.Exists(started) && new FileInfo(started).Length > 0, TimeSpan.FromSeconds(5)) || File.ReadAllText(started) != runtime)
                    throw new Exception("Startup did not pass the mounted runtime directory as one argument.");
            }
            finally { DecisionRuntimeHost.Stop(launcher); }
            if (!launcher.HasExited || !File.Exists(Path.Combine(runtime, "stopped")))
                throw new Exception("Shutdown did not let the launcher stop gracefully.");
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    private static async Task CheckSkipRules(JObject source)
    {
        async Task Check(string type, JToken answer, string rule, bool shouldSkip, bool evaluatorFailed = false)
        {
            var question = new JObject
            {
                ["id"] = "test", ["title"] = "Test decision", ["question"] = "Test question?", ["type"] = type, ["selected"] = true,
                ["options"] = type == "noul" ? new JArray() : new JArray("Declined", "Partial", "Accepted"), ["skipIf"] = rule
            };
            var settings = new JObject { ["decisions"] = new JObject { ["evaluator"] = "jev", ["questions"] = new JArray(question) } };
            List<JObject> logged = [];
            JObject match = await DecisionEvaluator.EvaluateAsync(source, Select(settings, "test"), 0, (_, _) => evaluatorFailed
                ? Task.FromException<JObject>(new InvalidOperationException("offline"))
                : Task.FromResult(new JObject { ["answer"] = answer, ["evaluator_model"] = "Jev" }),
                logged.Add, CancellationToken.None);
            if (logged.Count != 1 || (match != null) != shouldSkip || ((bool?)logged[0]["generation_skipped"] == true) != shouldSkip)
                throw new Exception($"Unexpected skip result for {type}, {answer}, '{rule}'.");
            if (shouldSkip && ((string)logged[0]["status"] != "ok" || (string)logged[0]["decision"]["id"] != "test"))
                throw new Exception("A matching decision must remain a successful, attributed observation in history.");
            try
            {
                PromptHandler.SkipForDecision(match, 0);
                if (shouldSkip) throw new Exception("The decision match did not stop generation.");
            }
            catch (SwarmReadableErrorException ex) when (shouldSkip && ex.Message.Contains("matched Skip If"))
            {
                if (ex.Data["SwarmUI.SkipGeneration"] is not true)
                    throw new Exception("A decision match must be recognizable as a per-generation skip.");
            }
        }

        await Check("noul", true, " YES ", true);
        await Check("noul", false, "no", true);
        await Check("noul", false, "yes", false);
        await Check("noul", true, "", false);
        await Check("noul", true, "yes", false, evaluatorFailed: true);
        await Check("noul", "yes", "yes", false);
        await Check("choice", "Declined", "Declined", true);
        await Check("choice", "Declined", "declined", false);
        await Check("choice", "Declined", "Declined,Partial", false);
        await Check("choice", "Unknown", "Unknown", false);
        await Check("score", 1, "1", false);

        JObject scoped = JObject.Parse("""
            {"instructions":{"prompt":"Default","skipIf":{"prompt":"yes"}},"decisions":{"evaluator":"jev","questions":[
              {"id":"first","title":"No rule","question":"Did it refuse?","type":"noul","options":[],"selected":true},
              {"id":"second","title":"Skip on yes","question":"Did it mention a refusal?","type":"noul","options":[],"selected":true,"skipIf":"yes"},
              {"id":"third","title":"Rating","question":"How good was it?","type":"score","options":["Poor","Good"],"selected":true,"skipIf":"1"}]}}
            """);
        if (Select(scoped, "first").RequiresFreshResponse || Select(scoped, "third").RequiresFreshResponse
            || !Select(scoped, "second").RequiresFreshResponse || !Select(scoped).RequiresFreshResponse)
            throw new Exception("Only armed questions in the current selection may bypass the cache.");
        JToken config = DecisionEvaluator.NormalizeSettings(scoped["decisions"]);
        if (DecisionEvaluator.ResolveSelection(config, "second", "http://[::1]:1234/v1/chat/completions") != null)
            throw new Exception("A loopback provider must not bypass the prompt cache.");
        try
        {
            Select(scoped, "deleted-question");
            throw new Exception("A deleted question must require reselection.");
        }
        catch (SwarmReadableErrorException ex) when (ex.Message.Contains("saved decision question is unavailable"))
        {
        }
        foreach (JToken row in config["questions"]) row["selected"] = false;
        if (DecisionEvaluator.ResolveSelection(config, "", RemoteEndpoint).Questions.Length != 0)
            throw new Exception("An empty Settings defaults selection must remain empty.");
        List<JObject> decisions = [];
        Task<JObject> CheckScope(string selected) => DecisionEvaluator.EvaluateAsync(source, Select(scoped, selected), 0,
            (_, _) => Task.FromResult(new JObject { ["answer"] = true, ["evaluator_model"] = "Jev" }), decisions.Add, CancellationToken.None);
        if (await CheckScope("first") != null || decisions.Count != 1)
            throw new Exception("A different question or a legacy instruction rule must not skip this response.");
        string version = (string)decisions[0]["decision_version"];
        decisions.Clear();
        if ((string)(await CheckScope(""))?["decision"]?["id"] != "second" || decisions.Count != 2)
            throw new Exception("Settings defaults must use each question's own rule and stop on the first match.");
        scoped["decisions"]["questions"][0]["skipIf"] = "yes";
        if ((string)(await CheckScope("first"))?["decision_version"] != version)
            throw new Exception("Changing Skip If must not split the question's history.");
    }

    private static async Task CheckBatchSkip()
    {
        var session = new Session();
        using Session.GenClaim claim = session.Claim(gens: 8);
        int reachedBackendHandoff = 0;
        var previousPreGenerate = T2IEngine.PreGenerateEvent;
        Action<T2IParamInput> match = input =>
        {
            if (input.Get(T2IParamTypes.Seed) == 1)
                PromptHandler.SkipForDecision(JObject.Parse("""{"decision":{"title":"Refusal"},"skip_if":"yes"}"""), 0);
        };
        T2IParamInput.LateSpecialParameterHandlers.Add(match);
        T2IEngine.PreGenerateEvent = _ =>
        {
            reachedBackendHandoff++;
            throw new SwarmReadableErrorException("test handoff");
        };
        try
        {
            for (int i = 1; i <= 8; i++)
            {
                var input = new T2IParamInput(null);
                input.Set(T2IParamTypes.Seed, (long)i);
                await T2IEngine.CreateImageTask(input, $"{i}", claim, _ => { }, error =>
                {
                    if (error != "test handoff") claim.LocalClaimInterrupt.Cancel();
                }, false, (_, _) => throw new Exception("The batch fixture must not render media."));
            }
            if (claim.ShouldCancel || reachedBackendHandoff != 7 || claim.WaitingGenerations != 7 || claim.WaitingBackends != 0)
                throw new Exception("One decision match must skip one claim slot and let all seven other items reach backend handoff.");
        }
        finally
        {
            T2IEngine.PreGenerateEvent = previousPreGenerate;
            T2IParamInput.LateSpecialParameterHandlers.Remove(match);
        }
        Console.WriteLine("Verified decision skip preserved the seven remaining slots of a shared batch.");
    }
}
