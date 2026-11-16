using Hartsy.Extensions.MagicPromptExtension;
using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

internal static class DecisionCacheChecks
{
    private const string Endpoint = "https://cache-fixture.invalid/v1/chat/completions";
    private const string Backend = "openaiapi\n" + Endpoint;
    private const string Model = "cache/prompt-model";
    private static T2IRegisteredParam<T> Param<T>(string name) => new(T2IParamTypes.Types[name]);

    internal static async Task Run()
    {
        string previousData = Program.DataDir;
        SessionHandler previousSessions = Program.Sessions;
        DirectoryInfo temporary = Directory.CreateTempSubdirectory("mp-decision-cache-");
        try
        {
            Program.DataDir = temporary.FullName;
            Program.Sessions = new SessionHandler();
            JObject config = DecisionEvaluator.NormalizeSettings(JObject.Parse("""
                {"model":"typesafe/test","questions":[
                  {"id":"refusal","title":"Refusal","question":"Did it refuse?","type":"noul",
                   "options":[],"selected":true,"skipIf":"yes"}]}
                """));
            Program.Sessions.GenericSharedUser.SaveGenericData("magicprompt", "config", new JObject
            {
                ["backend"] = "openaiapi", ["decisions"] = config,
                ["instructions"] = new JObject { ["prompt"] = "instructions" },
                ["backends"] = new JObject { ["openaiapi"] = new JObject
                {
                    ["baseurl"] = "https://cache-fixture.invalid", ["timeout"] = 1,
                    ["endpoints"] = new JObject { ["chat"] = "/v1/chat/completions" }
                } }
            }.ToString());
            DecisionEvaluator.Selection selection = DecisionEvaluator.ResolveSelection(config, "", Endpoint);
            await CheckBatchReplay(selection);
            CheckIdentityAndRecovery(selection);
            await CheckConcurrentReplay(selection);
            Console.WriteLine("Decision cache checks passed: ten seeds replay six prompts/four skips, invalidation, isolation, recovery, cancellation, eviction and concurrent reuse.");
        }
        finally
        {
            Program.Sessions.Shutdown();
            Program.Sessions = previousSessions;
            Program.DataDir = previousData;
            temporary.Delete(recursive: true);
        }
    }

    private static PromptCache.Response Fetch(PromptCache cache, DecisionEvaluator.Selection selection, long seed,
        Func<PromptCache.Response> create, bool useCache = true, CancellationToken token = default) =>
        cache.GetOrCreateResponse("same input", "instructions", Model, "none", Backend, seed,
            useCache && !selection.HasSkipRules, create, 5000, token, out _, selection.CacheIdentity);

    private static PromptCache.Response Evaluate(long seed, DecisionEvaluator.Selection selection,
        Func<JObject, CancellationToken, Task<JObject>> evaluate, CancellationToken token = default)
    {
        string text = $"prompt-{seed}";
        string json = new JObject
        {
            ["id"] = $"provider-{seed}", ["model"] = Model,
            ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["content"] = text } }),
            ["usage"] = new JObject { ["cost"] = 0.002 }
        }.ToString();
        JObject fetched = new() { ["response"] = text };
        DecisionEvaluator.CaptureSource(fetched, json, Endpoint, "openrouter", Model, 10);
        ModelStats.CompleteAttempt(ModelStats.BeginAttempt("openrouter", Model), 10, fetched, json);
        return DecisionEvaluator.CheckResponse(text, fetched, selection, evaluate, token);
    }

    private static Task<JObject> Answer(bool skip) => Task.FromResult(new JObject
    {
        ["answer"] = skip, ["evaluator_model"] = "typesafe/test"
    });

    private static async Task CheckBatchReplay(DecisionEvaluator.Selection selection)
    {
        PromptCache cache = new();
        int providers = 0, evaluators = 0;
        for (long seed = 42; seed < 52; seed++)
        {
            PromptCache.Response result = Fetch(cache, selection, seed, () =>
            {
                providers++;
                return Evaluate(seed, selection, (_, _) => { evaluators++; return Answer(seed < 46); });
            });
            Check(result.Text == $"prompt-{seed}", "Each seed must have its own response with Skip If enabled.");
            Check((DecisionEvaluator.Replay(result, new T2IParamInput(null), 0) != null) == (seed < 46),
                "The first four seeds must skip and the last six must pass.");
        }
        Check(providers == 10 && evaluators == 10, "The first batch must fetch and evaluate all ten seeds.");
        JObject before = await MagicPromptAPI.GetMagicPromptStats();
        JToken counts = before["models"].Single(row => (string)row["model"] == Model);
        Check((int)counts["completed"] == 10 && (int)counts["skip_completed"] == 10 && (int)counts["skipped"] == 4,
            "The first batch must record ten completions, ten decisions and four skips.");

        // Exercise the actual handler against the cached entries. Any accidental miss targets a reserved .invalid host.
        PromptHandler handler = new(cache, Param<bool>("mpusecache"), Param<string>("mpmodelid"),
            Param<string>("mpinstructions"), Param<string>("mppostfilter"), Param<string>("mpthinking"),
            Param<string>("mponerror"), Param<bool>("disablellmrequest"), Param<bool>("mpdecisionchecks"),
            Param<string>("mpdecisionquestion"), Param<string>("mpdecisionmodelid"));
        foreach (bool useCache in new[] { true, false })
        {
            int accepted = 0, skipped = 0;
            for (long seed = 42; seed < 52; seed++)
            {
                T2IParamInput input = new(null);
                input.Set(T2IParamTypes.Prompt, "<mpprompt:same input>");
                input.Set(T2IParamTypes.Seed, seed);
                input.Set(Param<bool>("mpusecache"), useCache);
                input.Set(Param<bool>("mpdecisionchecks"), true);
                input.Set(Param<string>("mpmodelid"), Model);
                input.Set(Param<string>("mpinstructions"), "prompt");
                try
                {
                    handler.ProcessPrompt(input);
                    Check(seed >= 46 && input.Get(T2IParamTypes.Prompt) == $"prompt-{seed}", "Handler replay lost the accepted prompt.");
                    accepted++;
                }
                catch (SwarmReadableErrorException ex) when (ex.Data["SwarmUI.SkipGeneration"] is true)
                {
                    Check(seed < 46, "Handler replay skipped the wrong seed.");
                    skipped++;
                }
                JArray records = (JArray)input.ExtraMeta["mp_decisions"];
                Check(records.Count == 1 && (int)records[0]["tag_index"] == 0
                    && (string)records[0]["source"]["provider_request_id"] == $"provider-{seed}", "Replay lost decision provenance.");
                records[0]["answer"] = "mutated caller metadata";
            }
            Check(accepted == 6 && skipped == 4, "Repeating seeds 42–51 must replay six accepted prompts and four skips.");
        }
        JObject after = await MagicPromptAPI.GetMagicPromptStats();
        Check(JToken.DeepEquals(before, after), "Replays must not record new latency, costs, completions or Skip If samples.");
    }

    private static void CheckIdentityAndRecovery(DecisionEvaluator.Selection selection)
    {
        PromptCache cache = new();
        int providers = 0;
        PromptCache.Response Create(DecisionEvaluator.Selection current, bool skip = true) =>
            Evaluate(++providers, current, (_, _) => Answer(skip));
        PromptCache.Response first = Fetch(cache, selection, 42, () => Create(selection));
        PromptCache.Response repeat = Fetch(cache, selection, 42, () => Create(selection));
        Check(first.Text == repeat.Text && providers == 1, "The same request must replay its response.");
        JObject[] metadata = repeat.DecisionsForTag(7);
        Check((int)metadata[0]["tag_index"] == 7 && (bool)metadata[0]["generation_skipped"], "Replay must use the current tag index.");
        metadata[0]["decision"]["title"] = "mutated";
        Check((string)first.DecisionsForTag(0)[0]["decision"]["title"] == "Refusal", "Callers must not mutate cached records.");

        foreach (string field in new[] { "model", "question", "skipIf", "options", "title" })
        {
            JObject question = (JObject)selection.Questions[0].DeepClone();
            if (field == "options") question[field] = new JArray("No", "Yes");
            else if (field != "model") question[field] = field == "skipIf" ? "no" : "changed";
            DecisionEvaluator.Selection changed = new(field == "model" ? "typesafe/other" : selection.Evaluator, [question]);
            int previous = providers;
            PromptCache.Response result = Fetch(cache, changed, 42, () => Create(changed));
            Check(providers == previous + 1, $"Changing {field} must invalidate the cached decision.");
            if (field == "skipIf") Check(!result.DecisionsForTag(0)[0].ContainsKey("generation_skipped"), "A changed rule must not replay the old skip.");
        }

        PromptCache failures = new();
        int failedCalls = 0;
        PromptCache.Response failed = Fetch(failures, selection, 42, () => Evaluate(42, selection, (_, _) =>
        {
            failedCalls++;
            return Task.FromException<JObject>(new InvalidOperationException("fixture unavailable"));
        }));
        Check(!failed.Cacheable && failedCalls == 1 && (string)failed.DecisionsForTag(0)[0]["status"] == "error",
            "Evaluator errors must preserve usable prompt text and error metadata without caching acceptance.");
        PromptCache.Response recovered = Fetch(failures, selection, 42, () => Create(selection));
        Check((bool)recovered.DecisionsForTag(0)[0]["generation_skipped"], "A repeated request must retry a failed evaluator.");

        using CancellationTokenSource interrupt = new();
        PromptCache cancelled = new();
        try
        {
            Fetch(cancelled, selection, 42, () => Evaluate(42, selection, (_, _) =>
            {
                interrupt.Cancel();
                return Answer(false);
            }, interrupt.Token), token: interrupt.Token);
            throw new Exception("An interrupted evaluation must cancel generation.");
        }
        catch (OperationCanceledException) when (interrupt.IsCancellationRequested) { }
        int beforeRetry = providers;
        Fetch(cancelled, selection, 42, () => Create(selection));
        Check(providers == beforeRetry + 1, "Cancelled evaluations must not enter the cache.");

        PromptCache bounded = new(maxSize: 1);
        Fetch(bounded, selection, 42, () => Create(selection));
        Fetch(bounded, selection, 43, () => Create(selection));
        int beforeEviction = providers;
        Fetch(bounded, selection, 42, () => Create(selection));
        Check(providers == beforeEviction + 1, "Decision outcomes must obey the bounded cache's eviction.");
        bounded.Clear();
        Fetch(bounded, selection, 42, () => Create(selection));
        Check(providers == beforeEviction + 2, "Clearing the cache must also clear decision outcomes.");

        PromptCache plain = new();
        string Plain(long seed, bool broad) => plain.GetOrCreate("same input", "instructions", Model, "none", Backend,
            seed, broad, () => $"plain-{++providers}", 1000, CancellationToken.None, out _);
        string broad = Plain(42, true);
        Check(Plain(43, true) == broad, "Unchecked requests must retain broad cross-seed reuse.");
        string strict = Plain(42, false);
        Check(Plain(42, false) == strict && Plain(43, false) != strict, "Cache-off requests must retain exact-seed reuse.");
        PromptCache.Response checkedResult = Fetch(plain, selection, 42, () => Create(selection));
        Check(checkedResult.DecisionsForTag(0).Length == 1, "A plain cached response must not bypass required decisions.");

        JObject observationalQuestion = (JObject)selection.Questions[0].DeepClone();
        observationalQuestion["skipIf"] = "";
        DecisionEvaluator.Selection observational = new(selection.Evaluator, [observationalQuestion]);
        PromptCache observations = new();
        PromptCache.Response observed = Fetch(observations, observational, 42, () => Create(observational));
        int beforeObservation = providers;
        PromptCache.Response reused = Fetch(observations, observational, 43, () => Create(observational));
        Check(providers == beforeObservation && observed.Text == reused.Text
            && reused.DecisionsForTag(0).Length == 1 && (bool)reused.DecisionsForTag(0)[0]["answer"],
            "Decision checks without Skip If must replay their answers with broad cache reuse.");
        Fetch(observations, observational, 43, () => Create(observational), useCache: false);
        Check(providers == beforeObservation + 1, "Disabling broad reuse must give observational checks a seed-specific entry.");

        PromptCache retries = new();
        int attempts = 0;
        PromptCache.Response retried = Fetch(retries, selection, 42, () =>
            ++attempts == 1 ? new PromptCache.Response("") : Create(selection));
        Check(attempts == 2 && retried.DecisionsForTag(0).Length == 1, "An empty provider response must retry before caching the evaluated response.");
    }

    private static async Task CheckConcurrentReplay(DecisionEvaluator.Selection selection)
    {
        PromptCache cache = new();
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        Task<PromptCache.Response> owner = Task.Run(() => Fetch(cache, selection, 42, () =>
        {
            Interlocked.Increment(ref calls);
            return Evaluate(42, selection, async (_, _) =>
            {
                reached.SetResult();
                await release.Task;
                return await Answer(true);
            });
        }));
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Logs.LogTracker debug = Logs.Trackers[(int)Logs.LogLevel.Debug];
        long sequence;
        lock (debug.Lock) sequence = debug.LastSeq;
        Task<PromptCache.Response> waiter = Task.Run(() => Fetch(cache, selection, 42, () =>
        {
            Interlocked.Increment(ref calls);
            return Evaluate(42, selection, (_, _) => Answer(false));
        }));
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
            while (true)
            {
                lock (debug.Lock)
                    if (debug.Messages.Any(message => message.Sequence > sequence && message.Message.Contains("another thread is already fetching"))) break;
                await Task.Delay(10, deadline.Token);
            }
        }
        finally { release.TrySetResult(); }
        PromptCache.Response[] results = await Task.WhenAll(owner, waiter).WaitAsync(TimeSpan.FromSeconds(5));
        Check(calls == 1 && results.All(result => (bool)result.DecisionsForTag(0)[0]["generation_skipped"]),
            "Concurrent callers must share one completed response/decision and both replay its skip.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
