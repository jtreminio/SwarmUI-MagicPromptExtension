using System.Net;
using System.Net.Sockets;
using Hartsy.Extensions.MagicPromptExtension;
using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Hartsy.Extensions.MagicPromptExtension.WebAPI.Models;
using LiteDB;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

internal static class ModelStatsChecks
{
    private const string Reply = """{"model":"routed-alias","choices":[{"message":{"content":"Test provider prompt"}}]}""";
    private const string Local = "openaiapi";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    internal static async Task Run()
    {
        string dataDir = Program.DataDir;
        SessionHandler previousSessions = Program.Sessions;
        DirectoryInfo temporary = Directory.CreateTempSubdirectory("mp-stats-test-");
        Program.DataDir = temporary.FullName;
        try
        {
            Program.Sessions = new SessionHandler();
            using HttpListener provider = StartProvider(out string baseUrl);
            string oldHistory = Path.Combine(temporary.FullName, "MagicPrompt", "Decisions", "2026-11-01.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(oldHistory));
            File.WriteAllText(oldHistory, "existing daily history\n");
            Check((await Read()).Count == 0, "Stats must start empty with an old decision log present.");

            await CheckIdentityAndLatency(provider, baseUrl);
            await CheckDirectActions(provider, baseUrl);
            await CheckCatalogExclusion(provider, baseUrl);
            await CheckSharedTagRequests(provider);
            await CheckFailuresAndCancellation(provider, baseUrl);
            await CheckDecisionOutcomes(provider, baseUrl);
            await CheckRecentOutcomes(provider, baseUrl);
            await CheckResetTransport(provider, baseUrl);
            await CheckConcurrentWrites(provider, baseUrl);
            await CheckStoreFailures(provider, baseUrl);
            CheckPersistence(oldHistory);
            CheckPermissions();
            await CheckPromptModelPreferences();
            Console.WriteLine("MagicPrompt model stats storage, transport, tags, decision outcomes, resets, and API checks passed.");
        }
        finally
        {
            if (Program.Sessions != previousSessions) Program.Sessions.Shutdown();
            Program.Sessions = previousSessions;
            Program.DataDir = dataDir;
            temporary.Delete(recursive: true);
        }
    }

    private static async Task CheckIdentityAndLatency(HttpListener listener, string baseUrl)
    {
        const string model = "Requested/Model";
        Task<JObject> pending = Send(baseUrl, model);
        HttpListenerContext call = await Receive(listener);
        byte[] payload = Encoding.UTF8.GetBytes(Reply);
        call.Response.ContentLength64 = payload.Length;
        await call.Response.OutputStream.WriteAsync(payload.AsMemory(0, 1));
        await call.Response.OutputStream.FlushAsync();
        await Task.Delay(80);
        await call.Response.OutputStream.WriteAsync(payload.AsMemory(1));
        call.Response.Close();
        Check((bool?)(await pending)["success"] == true, "The local provider's delayed body must still be usable.");
        JToken first = Counts(await Read(), model, completed: 1);
        Check((double)first["latency_ms"] >= 60, "Provider latency must include waiting for the full response body.");

        await ReplyTo(listener, Send(baseUrl, model), json: "{}", status: 503);
        Check((double)Counts(await Read(), model, completed: 1, errors: 1)["latency_ms"] == (double)first["latency_ms"],
            "A failed request must leave the successful-response mean untouched.");
        await ReplyTo(listener, Send(baseUrl, "requested/model"));
        Program.Sessions.GenericSharedUser.SaveGenericData("openrouter_api", "key", "loopback-test-key");
        await ReplyTo(listener, Send(baseUrl, model, provider: "openrouter"));
        JArray rows = await Read();
        Counts(rows, "requested/model", completed: 1);
        Counts(rows, model, completed: 1, provider: "openrouter");
        Check(rows.Count == 3 && rows.All(row => (string)row["model"] != "routed-alias"),
            "Attribution must use the exact request ID and provider, independent of the response's model alias.");

        string longId = " model " + new string('x', 1024) + " ";
        await ReplyTo(listener, Send(baseUrl, longId));
        Counts(await Read(), longId, completed: 1);
        Counts(await Reset(Local, longId), longId);
    }

    private static async Task CheckDirectActions(HttpListener listener, string baseUrl)
    {
        Program.Sessions.GenericSharedUser.SaveGenericData("magicprompt", "config", Settings(baseUrl)["settings"].ToString());
        foreach (string action in new[] { "prompt", "random-prompt", "generate-instruction" })
        {
            JObject request = Request("direct/model", text: action == "random-prompt" ? "" : "Test request secret");
            request["action"] = action;
            Check((bool?)(await ReplyTo(listener, LLMAPICalls.MagicPromptPhoneHome(request)))["success"] == true,
                $"The direct {action} action did not return a provider response.");
        }
        Counts(await Read(), "direct/model", completed: 3);
    }

    private static async Task CheckSharedTagRequests(HttpListener listener)
    {
        const string firstTag = "<mpprompt[|tag/first]:shared prompt>";
        T2IParamInput owner = TagInput(firstTag + " <mpprompt[|tag/second]:other prompt>", seed: 1);
        Task ownerDone = Task.Run(owner.ApplyLateSpecialLogic);
        HttpListenerContext first = await Receive(listener);
        Check(await RequestedModel(first) == "tag/first", "The first tag must dispatch its requested model.");

        Logs.LogTracker debug = Logs.Trackers[(int)Logs.LogLevel.Debug];
        long afterSequence;
        lock (debug.Lock) afterSequence = debug.LastSeq;
        T2IParamInput waiter = TagInput(firstTag, seed: 2);
        Task waiterDone = Task.Run(waiter.ApplyLateSpecialLogic);
        using (CancellationTokenSource deadline = new(Deadline))
        {
            // Keep the owner in flight until the other generation reaches the shared cache wait.
            while (true)
            {
                lock (debug.Lock)
                {
                    if (debug.Messages.Any(message => message.Sequence > afterSequence && message.Message.Contains("another thread is already fetching"))) break;
                }
                await Task.Delay(10, deadline.Token);
            }
        }
        await Respond(first, "{}", status: 502);
        HttpListenerContext retry = await Receive(listener);
        Check(await RequestedModel(retry) == "tag/first", "The failed in-flight call must retry the first tag, once.");
        await Respond(retry);
        HttpListenerContext second = await Receive(listener);
        Check(await RequestedModel(second) == "tag/second", "The generation's second tag must dispatch its own model.");
        await Respond(second);
        await Task.WhenAll(ownerDone, waiterDone).WaitAsync(Deadline);
        T2IParamInput cached = TagInput(firstTag, seed: 3);
        await Task.Run(cached.ApplyLateSpecialLogic).WaitAsync(Deadline);
        Check(new[] { owner, waiter, cached }.All(input => input.Get(T2IParamTypes.Prompt).Contains("Test provider prompt")),
            "The tag owner, waiter and later cache hit must all get the usable response.");
        JArray rows = await Read();
        Counts(rows, "tag/first", completed: 1, errors: 1);
        Counts(rows, "tag/second", completed: 1);
        Check(rows.All(row => (string)row["model"] != "generation/default"), "A tag model must take precedence over the generation model.");
    }

    private static async Task CheckCatalogExclusion(HttpListener listener, string baseUrl)
    {
        JArray before = await Read();
        Task<List<ModelData>> pending = LLMAPICalls.GetModelsForBackend(Local, (JObject)Settings(baseUrl)["settings"]);
        HttpListenerContext listing = await Receive(listener);
        Check(listing.Request.HttpMethod == "GET" && listing.Request.Url.AbsolutePath == "/v1/models", "Expected a catalog GET.");
        await Respond(listing, """{"data":[{"id":"catalog/only"}]}""");
        Check((await pending.WaitAsync(Deadline)).Single().Model == "catalog/only" && JToken.DeepEquals(before, await Read()),
            "A catalog request must return models without changing originating request counts.");
    }

    private static T2IParamInput TagInput(string prompt, long seed)
    {
        T2IParamInput input = new(null);
        input.Set(T2IParamTypes.Prompt, prompt);
        input.Set(T2IParamTypes.Seed, seed);
        T2IParamTypes.ApplyParameter("mpmodelid", "generation/default", input);
        T2IParamTypes.ApplyParameter("mpusecache", "true", input);
        input.PreparsePromptLikes();
        return input;
    }

    private static async Task CheckFailuresAndCancellation(HttpListener listener, string baseUrl)
    {
        const string model = "test/errors";
        foreach ((string json, int status) in new[] { ("{}", 500), ("", 200), ("{bad json", 200), ("""{"choices":[{"message":{"content":"AI:    "}}]}""", 200) })
        {
            Check((bool?)(await ReplyTo(listener, Send(baseUrl, model), json, status))["success"] == false,
                "An HTTP error or unusable response must fail the provider attempt.");
        }
        Task<JObject> timedOut = Send(baseUrl, model, timeout: 1);
        HttpListenerContext timeout = await Receive(listener);
        timeout.Response.ContentLength64 = 100;
        await timeout.Response.OutputStream.WriteAsync(new byte[] { (byte)'{' });
        await timeout.Response.OutputStream.FlushAsync();
        Check((bool?)(await timedOut.WaitAsync(Deadline))["success"] == false, "The timeout must cover an incomplete response body.");
        timeout.Response.Abort();

        Check((bool?)(await Send(UnusedLoopbackUrl(), model))["success"] == false, "A refused connection must fail the provider attempt.");
        Check(Counts(await Read(), model, errors: 6)["latency_ms"].Type == JTokenType.Null,
            "Failed originating attempts must not contribute successful latency.");

        using CancellationTokenSource interrupt = new();
        Task<JObject> pending = Send(baseUrl, "test/cancelled", interrupt.Token);
        HttpListenerContext waiting = await Receive(listener);
        interrupt.Cancel();
        await ExpectCancelled(pending);
        waiting.Response.Abort();
        await ExpectCancelled(Send(baseUrl, "test/pre-cancelled", interrupt.Token));
        Check((bool?)(await Send("relative-endpoint", "test/misconfigured"))["success"] == false, "Relative provider endpoints must be rejected.");
        Check((bool?)(await Send("ftp://localhost", "test/misconfigured"))["success"] == false, "Unsupported provider URL schemes must be rejected.");
        Check((bool?)(await Send(baseUrl, "test/misconfigured", timeout: int.MaxValue))["success"] == false,
            "An unsupported timeout must fail before dispatch.");
        Program.Sessions.GenericSharedUser.SaveGenericData("openrouter_api", "key", "");
        Check((bool?)(await Send(baseUrl, "test/misconfigured", provider: "openrouter"))["success"] == false, "A missing required provider key must fail before dispatch.");
        Check((await Read()).All(row => (string)row["model"] is not ("test/cancelled" or "test/pre-cancelled" or "test/misconfigured")),
            "User cancellation and preflight configuration errors must not create stats rows.");
    }

    private static async Task CheckDecisionOutcomes(HttpListener listener, string baseUrl)
    {
        const string model = "test/with-decisions";
        DecisionEvaluator.Selection selection = new("test/private-evaluator", [Question("plain", ""), Question("one", "yes"), Question("two", "no")]);
        async Task<JObject> Fetch()
        {
            JObject fetched = await ReplyTo(listener, Send(baseUrl, model, decisionSource: true));
            // Eligibility remains remote-only. Use the real originating receipt with fixture decision evidence.
            DecisionEvaluator.CaptureSource(fetched, Reply, "https://openrouter.ai", Local, model, 1);
            return fetched;
        }

        JObject completed = await Fetch();
        await Evaluate(completed, selection, (request, _) => (string)request["question"] == "plain"
            ? FailedCheck() : Answer((string)request["question"] == "two"));
        Counts(await Read(), model, completed: 1, skipCompleted: 1);
        await Evaluate(completed, selection, (_, _) => Answer(true));
        Counts(await Read(), model, completed: 1, skipCompleted: 1);

        JObject missing = await Fetch();
        await Evaluate(missing, selection, (request, _) => (string)request["question"] == "one"
            ? FailedCheck() : Answer(true));
        Counts(await Read(), model, completed: 2, skipCompleted: 1);

        JObject matches = await Fetch();
        JObject match = await Evaluate(matches, selection, (request, _) => (string)request["question"] == "one"
            ? FailedCheck() : Answer(false));
        Check((string)match?["decision"]?["id"] == "two", "A negative Skip If rule must still match after an earlier eligible error.");
        Counts(await Read(), model, completed: 3, skipCompleted: 2, skipped: 1);

        JObject cancelled = await Fetch();
        using CancellationTokenSource interrupt = new();
        int checks = 0;
        DecisionEvaluator.Selection cancellationOrder = new(selection.Evaluator,
            [Question("one", "yes"), Question("plain", ""), Question("two", "no")]);
        await Evaluate(cancelled, cancellationOrder, (_, token) =>
        {
            if (++checks == 2) interrupt.Cancel();
            token.ThrowIfCancellationRequested();
            return Answer(false);
        }, interrupt.Token);
        Check(checks == 2, "Cancellation must leave a later eligible question unattempted.");
        Counts(await Read(), model, completed: 4, skipCompleted: 2, skipped: 1);

        JObject noRule = await Fetch();
        await Evaluate(noRule, new(selection.Evaluator, [Question("plain", "")]), (_, _) => Answer(true));
        Counts(await Read(), model, completed: 5, skipCompleted: 2, skipped: 1);

        JObject invalidRules = await Fetch();
        JObject staleChoice = Question("choice", "Removed option");
        staleChoice["type"] = "choice";
        staleChoice["options"] = new JArray("Accepted", "Declined");
        await Evaluate(invalidRules, new(selection.Evaluator, [Question("invalid", "maybe"), staleChoice]), (request, _) => Task.FromResult(new JObject
        {
            ["evaluator_model"] = selection.Evaluator, ["answer"] = (string)request["question"] == "invalid" ? new JValue(false) : new JValue("Accepted")
        }));
        Counts(await Read(), model, completed: 6, skipCompleted: 2, skipped: 1);
        Recent(await Read(), model, completed: 2, skipped: 1);

        JObject preReset = await Fetch();
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JObject> evaluating = Evaluate(preReset, new(selection.Evaluator, [Question("one", "yes")]), async (_, _) =>
        {
            reached.SetResult();
            await release.Task;
            return await Answer(true);
        });
        await reached.Task.WaitAsync(Deadline);
        Counts(await Reset(Local, model), model);
        release.SetResult();
        Check(await evaluating.WaitAsync(Deadline) != null, "Reset must not affect the live evaluator outcome.");
        Counts(await Read(), model);
        Recent(await Read(), model, completed: 0, skipped: 0);
    }

    private static async Task CheckRecentOutcomes(HttpListener listener, string baseUrl)
    {
        const string model = "test/rolling-skips";
        DecisionEvaluator.Selection selection = new("test/private-evaluator", [Question("one", "yes")]);
        for (int i = 0; i < 105; i++)
        {
            JObject fetched = await ReplyTo(listener, Send(baseUrl, model, decisionSource: true));
            DecisionEvaluator.CaptureSource(fetched, Reply, "https://openrouter.ai", Local, model, 1);
            await Evaluate(fetched, selection, (_, _) => Answer(i < 5));
            if (i == 7) Recent(await Read(), model, completed: 8, skipped: 5);
        }
        JArray rows = await Read();
        Counts(rows, model, completed: 105, skipCompleted: 105, skipped: 5);
        Recent(rows, model, completed: 100, skipped: 0);
        JArray reset = await Reset(Local, model);
        Counts(reset, model);
        Recent(reset, model, completed: 0, skipped: 0);
    }

    private static Task<JObject> Evaluate(JObject fetched, DecisionEvaluator.Selection selection,
        Func<JObject, CancellationToken, Task<JObject>> evaluate, CancellationToken interrupt = default) =>
        DecisionEvaluator.EvaluateAsync(fetched, selection, 0, evaluate, _ => { }, interrupt);

    private static JObject Question(string id, string rule) => new()
    {
        ["id"] = id, ["title"] = id, ["question"] = id, ["type"] = "noul", ["options"] = new JArray(), ["skipIf"] = rule
    };

    private static Task<JObject> Answer(bool answer) => Task.FromResult(new JObject { ["answer"] = answer, ["evaluator_model"] = "test/private-evaluator" });

    private static Task<JObject> FailedCheck() => Task.FromException<JObject>(new InvalidOperationException("Fixture evaluator unavailable"));

    private static async Task CheckResetTransport(HttpListener listener, string baseUrl)
    {
        const string model = "test/first-request-reset";
        Task<JObject> oldSuccess = Send(baseUrl, model);
        HttpListenerContext successful = await Receive(listener);
        Task<JObject> oldError = Send(baseUrl, model);
        HttpListenerContext failing = await Receive(listener);
        JArray reset = await Reset(Local, model);
        Check(Counts(reset, model)["latency_ms"].Type == JTokenType.Null, "Reset must expose an empty row with null latency.");
        Counts(reset, "tag/first", completed: 1, errors: 1);
        await ReplyTo(successful, oldSuccess);
        await Respond(failing, "{}", status: 500);
        await oldError.WaitAsync(Deadline);
        Counts(await Read(), model);
        await ReplyTo(listener, Send(baseUrl, model));
        Counts(await Read(), model, completed: 1);
        Check((bool?)(await MagicPromptAPI.ResetMagicPromptStats("unrecognized", model))["success"] == false
            && (bool?)(await MagicPromptAPI.ResetMagicPromptStats(Local, "  "))["success"] == false,
            "Reset must reject an unknown provider or blank model ID.");
    }

    private static async Task CheckConcurrentWrites(HttpListener listener, string baseUrl)
    {
        const string model = "test/concurrent";
        Task<JObject>[] requests = Enumerable.Range(0, 20).Select(_ => Send(baseUrl, model, decisionSource: true)).ToArray();
        HttpListenerContext[] calls = new HttpListenerContext[requests.Length];
        for (int i = 0; i < calls.Length; i++) calls[i] = await Receive(listener);
        await Task.WhenAll(calls.Select(call => Respond(call))).WaitAsync(Deadline);
        JObject[] responses = await Task.WhenAll(requests).WaitAsync(Deadline);
        Check(responses.All(result => (bool?)result["success"] == true), "Concurrent provider requests failed.");
        DecisionEvaluator.Selection selection = new("test/private-evaluator", [Question("one", "yes")]);
        await Task.WhenAll(responses.Select((fetched, index) => Task.Run(async () =>
        {
            DecisionEvaluator.CaptureSource(fetched, Reply, "https://openrouter.ai", Local, model, 1);
            await Evaluate(fetched, selection, (_, _) => Answer(index < 7));
        }))).WaitAsync(Deadline);
        JArray rows = await Read();
        Counts(rows, model, completed: 20, skipCompleted: 20, skipped: 7);
        Recent(rows, model, completed: 20, skipped: 7);
    }

    private static async Task CheckStoreFailures(HttpListener listener, string baseUrl)
    {
        string original = Program.DataDir;
        Program.DataDir = Path.Combine(original, "broken-store");
        try
        {
            Directory.CreateDirectory(Program.DataDir);
            const string model = "test/uncommitted";
            string statsDir = Path.Combine(Program.DataDir, "MagicPrompt");
            Task<JObject> pending = Send(baseUrl, model, decisionSource: true);
            HttpListenerContext call = await Receive(listener);
            Directory.Move(statsDir, statsDir + "-unavailable");
            File.WriteAllText(statsDir, "blocks directory creation");
            JObject fetched = await ReplyTo(call, pending);
            Check((bool?)fetched["success"] == true, "A stats write failure must not break usable provider generation.");
            Check((bool?)(await MagicPromptAPI.GetMagicPromptStats())["success"] == false
                && (bool?)(await MagicPromptAPI.ResetMagicPromptStats(Local, model))["success"] == false,
                "Stats read and reset must report persistence failures.");
            File.Delete(statsDir);
            Directory.Move(statsDir + "-unavailable", statsDir);
            DecisionEvaluator.CaptureSource(fetched, Reply, "https://openrouter.ai", Local, model, 1);
            Check(await Evaluate(fetched, new("test/private-evaluator", [Question("one", "yes")]), (_, _) => Answer(true)) != null,
                "Decision matches must still work after an originating stats write failure.");
            Check((await Read()).Count == 0, "A matched decision must not create stats when its originating write failed.");
        }
        finally
        {
            Program.DataDir = original;
        }
    }

    private static void CheckPersistence(string oldHistory)
    {
        Check(File.ReadAllText(oldHistory) == "existing daily history\n" && Directory.GetFiles(Path.GetDirectoryName(oldHistory)).Length == 1,
            "The old decision log must stay untouched.");
        using LiteDatabase reopened = new(Path.Combine(Program.DataDir, "MagicPrompt", "Stats.db"));
        BsonDocument[] rows = reopened.GetCollection("models").FindAll().ToArray();
        BsonDocument concurrent = rows.Single(row => row["model"].AsString == "test/concurrent");
        BsonArray window = concurrent["recent_skip_outcomes"].AsArray;
        Check(concurrent["completed"].AsInt64 == 20 && window.Count == 20 && window.Count(outcome => outcome.AsBoolean) == 7,
            "Counters and the bounded window must be durable when the database is independently reopened.");
        Check(rows.All(row => row.Keys.ToHashSet().SetEquals(new[] { "_id", "provider", "model", "revision", "completed", "errors", "latency_total_ms", "skip_completed", "skipped", "recent_skip_outcomes" })
            && row["recent_skip_outcomes"].AsArray.All(outcome => outcome.IsBoolean)),
            "Only model aggregate counters, recent Boolean outcomes and reset revisions belong in the stats database.");
    }

    private static void CheckPermissions()
    {
        MagicPromptAPI.Register();
        PermInfo read = API.APIHandlers["getmagicpromptstats"].Permission;
        PermInfo reset = API.APIHandlers["resetmagicpromptstats"].Permission;
        Check(read.ID != reset.ID && reset.Default == PermissionDefault.ADMINS,
            "Resetting shared counters must use its own permission, defaulting to administrators.");
    }

    private static async Task CheckPromptModelPreferences()
    {
        const string Favorite = "favoritedModels";
        const string Blocked = "blockedModels";
        JObject initial = PreferenceSettings(await SessionSettings.SaveMagicPromptSettings(JObject.Parse("""
            {"settings":{"model":"persisted-model","instructions":{"prompt":"persisted instruction"},"favoritedModels":{"openrouter":["other-provider"]}}}
            """)));
        APICall route = API.APIHandlers["changemagicpromptmodelpreference"];
        Check(route.Permission == MagicPromptPermissions.PermSaveConfig, "Changing shared preferences must require the save-settings permission.");
        JObject Edit(string model, string preference, bool enabled, string provider = Local) => new()
        {
            ["provider"] = provider, ["model"] = model, ["preference"] = preference, ["enabled"] = enabled
        };
        Task<JObject> Call(JObject request) => route.Call(null, null, null, request);
        Task<JObject> Change(string model, string preference, bool enabled, string provider = Local) => Call(Edit(model, preference, enabled, provider));

        JObject firstTab = Edit("model/X", Favorite, true);
        JObject secondTab = Edit("model/Y", Blocked, true);
        PreferenceSettings(await Call(firstTab));
        JObject afterSecondTab = PreferenceSettings(await Call(secondTab));
        Check(afterSecondTab[Favorite][Local].Values<string>().SequenceEqual(new[] { "model/X" })
            && afterSecondTab[Blocked][Local].Values<string>().SequenceEqual(new[] { "model/Y" }),
            "A stale tab blocking one model must preserve another tab's newly saved favorite.");
        Check(JToken.DeepEquals(initial["decisions"], afterSecondTab["decisions"])
            && JToken.DeepEquals(initial[Favorite]["openrouter"], afterSecondTab[Favorite]["openrouter"])
            && (string)afterSecondTab["model"] == "persisted-model" && (string)afterSecondTab["instructions"]["prompt"] == "persisted instruction",
            "A prompt model preference may not change other providers, decision settings, or defaults.");

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JObject>[] edits = Enumerable.Range(0, 12).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return await Change($"concurrent/{index}", index % 2 == 0 ? Favorite : Blocked, true);
        })).Append(Task.Run(async () =>
        {
            await start.Task;
            return await SessionSettings.SaveMagicPromptSettings(JObject.Parse("""{"settings":{"instructions":{"prompt":"concurrent instruction"}}}"""));
        })).ToArray();
        start.SetResult();
        foreach (JObject result in await Task.WhenAll(edits).WaitAsync(Deadline)) PreferenceSettings(result);
        JObject concurrent = PreferenceSettings(await SessionSettings.GetMagicPromptSettings());
        string[] expectedFavs = new[] { "model/X" }.Concat(Enumerable.Range(0, 6).Select(index => $"concurrent/{index * 2}")).ToArray();
        string[] expectedBlocks = new[] { "model/Y" }.Concat(Enumerable.Range(0, 6).Select(index => $"concurrent/{index * 2 + 1}")).ToArray();
        Check(concurrent[Favorite][Local].Values<string>().ToHashSet().SetEquals(expectedFavs)
            && concurrent[Blocked][Local].Values<string>().ToHashSet().SetEquals(expectedBlocks)
            && (string)concurrent["instructions"]["prompt"] == "concurrent instruction",
            "Overlapping model actions and a general settings save must keep all changes.");

        JObject starred = PreferenceSettings(await Change("MODEL/y", Favorite, true));
        Check(starred[Favorite][Local].Values<string>().Contains("MODEL/y") && !starred[Blocked][Local].Values<string>().Contains("model/Y"),
            "Starring a blocked model must remove the block case-insensitively.");
        JObject duplicate = PreferenceSettings(await Change("model/Y", Favorite, true));
        Check(JToken.DeepEquals(starred[Favorite][Local], duplicate[Favorite][Local]), "Repeated preference actions must preserve the saved ID and order.");
        JObject blocked = PreferenceSettings(await Change("model/Y", Blocked, true));
        Check(blocked[Blocked][Local].Values<string>().Contains("model/Y") && !blocked[Favorite][Local].Values<string>().Contains("MODEL/y"),
            "Blocking a favorite must remove the star case-insensitively.");
        JObject removed = PreferenceSettings(await Change("Model/Y", Blocked, false));
        Check(!removed[Blocked][Local].Values<string>().Contains("model/Y") && removed[Favorite][Local].Count() == 7,
            "Removing one preference must leave unrelated preferences intact.");

        Check((bool?)(await Change(" ", Favorite, true))["success"] == false
            && (bool?)(await Change("model/invalid", Favorite, true, "decisions"))["success"] == false
            && (bool?)(await Change("model/invalid", "instructions", true))["success"] == false,
            "Preference mutations must reject a blank model, unsupported provider or unsupported preference.");
        bool noPersist = Program.NoPersist;
        try
        {
            Program.NoPersist = true;
            Check((bool?)(await Change("model/unsaved", Favorite, true))["success"] == false,
                "A refused persistence write must not claim the new preference was saved.");
        }
        finally
        {
            Program.NoPersist = noPersist;
        }
        JObject persisted = PreferenceSettings(await SessionSettings.GetMagicPromptSettings());
        Check(JToken.DeepEquals(removed, persisted), "Validation and failed writes must preserve the persisted settings.");
    }

    private static JObject PreferenceSettings(JObject result)
    {
        Check((bool?)result["success"] == true && result["settings"] is JObject, $"Expected saved settings, got {result}.");
        return (JObject)result["settings"];
    }

    private static HttpListener StartProvider(out string baseUrl)
    {
        baseUrl = UnusedLoopbackUrl();
        HttpListener listener = new();
        listener.Prefixes.Add(baseUrl + "/");
        listener.Start();
        return listener;
    }

    private static string UnusedLoopbackUrl()
    {
        TcpListener port = new(IPAddress.Loopback, 0);
        port.Start();
        string baseUrl = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}";
        port.Stop();
        return baseUrl;
    }

    private static Task<HttpListenerContext> Receive(HttpListener provider) =>
        provider.GetContextAsync().WaitAsync(Deadline);

    private static async Task<string> RequestedModel(HttpListenerContext call)
    {
        Check(call.Request.HttpMethod == "POST" && call.Request.Url.AbsolutePath == "/v1/chat/completions", "Expected a provider chat POST.");
        using StreamReader reader = new(call.Request.InputStream, call.Request.ContentEncoding);
        return (string)JObject.Parse(await reader.ReadToEndAsync())["model"];
    }

    private static async Task<JObject> ReplyTo(HttpListener listener, Task<JObject> pending, string json = Reply, int status = 200) =>
        await ReplyTo(await Receive(listener), pending, json, status);

    private static async Task<JObject> ReplyTo(HttpListenerContext call, Task<JObject> pending, string json = Reply, int status = 200)
    {
        await Respond(call, json, status);
        return await pending.WaitAsync(Deadline);
    }

    private static async Task Respond(HttpListenerContext call, string json = Reply, int status = 200)
    {
        call.Response.StatusCode = status;
        call.Response.ContentType = "application/json";
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);
        call.Response.ContentLength64 = payload.Length;
        await call.Response.OutputStream.WriteAsync(payload);
        call.Response.Close();
    }

    private static async Task ExpectCancelled(Task<JObject> pending)
    {
        try
        {
            await pending.WaitAsync(Deadline);
            throw new Exception("Expected the provider request to respect Swarm interruption.");
        }
        catch (OperationCanceledException) { }
    }

    private static Task<JObject> Send(string baseUrl, string model, CancellationToken token = default, int timeout = 5,
        bool decisionSource = false, string provider = Local) => LLMAPICalls.MagicPromptPhoneHomeForGeneration(
            Request(model), null, token, decisionSource, Settings(baseUrl, provider, timeout));

    private static JObject Request(string model, string text = "Test request secret") => new()
    {
        ["modelId"] = model, ["messageContent"] = new JObject { ["text"] = text }
    };

    private static JObject Settings(string baseUrl, string provider = Local, int timeout = 5) => new()
    {
        ["success"] = true,
        ["settings"] = new JObject
        {
            ["backend"] = provider, ["backends"] = new JObject { [provider] = new JObject
            {
                ["baseurl"] = baseUrl, ["timeout"] = timeout,
                ["endpoints"] = new JObject { ["chat"] = "/v1/chat/completions", ["models"] = "/v1/models" }
            } }
        }
    };

    private static async Task<JArray> Read() => Rows(await MagicPromptAPI.GetMagicPromptStats());

    private static async Task<JArray> Reset(string provider, string model) => Rows(await MagicPromptAPI.ResetMagicPromptStats(provider, model));

    private static JArray Rows(JObject data)
    {
        Check((bool?)data["success"] == true && data["models"] is JArray, $"Expected a successful stats response. Got {data}.");
        return (JArray)data["models"];
    }

    private static JToken Counts(JArray rows, string model, long completed = 0, long errors = 0, long skipCompleted = 0, long skipped = 0, string provider = Local)
    {
        JToken row = rows.SingleOrDefault(row => (string)row["provider"] == provider && (string)row["model"] == model);
        Check(row != null && (long)row["completed"] == completed && (long)row["errors"] == errors
            && (long)row["skip_completed"] == skipCompleted && (long)row["skipped"] == skipped,
            $"Expected {provider}/{model} to have {completed} completed, {errors} errors, {skipCompleted} completed Skip If checks and {skipped} skips. Got {row}.");
        return row;
    }

    private static void Recent(JArray rows, string model, int completed, int skipped)
    {
        JToken row = rows.Single(entry => (string)entry["provider"] == Local && (string)entry["model"] == model);
        Check((int?)row["recent_skip_completed"] == completed && (int?)row["recent_skipped"] == skipped,
            $"Expected {model} to have {completed} recent eligible outcomes and {skipped} recent skips. Got {row}.");
    }

    private static void Check(bool condition, string failure)
    {
        if (!condition) throw new Exception(failure);
    }
}
