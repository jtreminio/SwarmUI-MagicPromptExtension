using Hartsy.Extensions.MagicPromptExtension;
using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

internal static class OpenRouterDecisionChecks
{
    private const string RemoteEndpoint = "https://openrouter.ai/api/v1/chat/completions";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal static void Run()
    {
        Check(T2IParamTypes.Types.TryGetValue("mpdecisionmodelid", out T2IParamType decisionModel),
            "Generate is missing the MP Decision Model ID parameter.");
        T2IParamType decisionToggle = T2IParamTypes.Types["mpdecisionchecks"];
        T2IParamType decisionQuestion = T2IParamTypes.Types["mpdecisionquestion"];
        Check(decisionModel.Type == T2IParamDataType.DROPDOWN && decisionModel.Default == ""
            && decisionModel.Group == decisionToggle.Group && decisionModel.Group == decisionQuestion.Group
            && decisionToggle.OrderPriority < decisionModel.OrderPriority && decisionModel.OrderPriority < decisionQuestion.OrderPriority,
            "Generate should list the decision model dropdown between the decision toggle and question, with Settings default selected.");

        JObject legacy = JObject.Parse("""{"evaluator":"jev","questions":[{"id":"keep","title":"Keep","question":"Refused?","type":"noul","options":[],"selected":true,"skipIf":"yes"}]}""");
        JObject migrated = DecisionEvaluator.NormalizeSettings(legacy);
        Check((string)migrated["model"] == "" && migrated["evaluator"] == null, "Local evaluator must migrate to an unset remote model.");
        Check(JToken.DeepEquals(migrated["questions"], legacy["questions"]), "Migration lost saved rules.");
        migrated["model"] = "typesafe/blocked";
        migrated["blockedModels"] = new JArray(" typesafe/blocked ", "typesafe/blocked");
        migrated["favoritedModels"] = new JArray("typesafe/blocked", "typesafe/keep", "typesafe/keep");
        migrated = DecisionEvaluator.NormalizeSettings(migrated);
        Check((string)migrated["model"] == "" && migrated["blockedModels"].Count() == 1 && migrated["favoritedModels"].Count() == 1,
            "Blocked default must clear and decision preferences must be distinct and exclusive.");
        CheckGenerationDecisionModel(migrated);

        JObject request = JObject.Parse("""{"evaluator":"typesafe/test","evidence":"raw 9007199254740993\nAI: text","question":"Did it refuse?","type":"noul","options":["Complied","Refused"]}""");
        JObject payload = OpenRouterDecisions.BuildRequest(request);
        Check((string)payload["model"] == "typesafe/test" && (string)payload["state"] == (string)request["evidence"], "Decision request changed original evidence or model.");
        Check((string)payload["questions"]["decision"]["criteria"]["false"] == "Complied", "False/true criteria changed order.");
        JObject Reply(JObject answer) => new() { ["model"] = "typesafe/test-version", ["id"] = "dec-1", ["provider"] = "TypeSafe", ["usage"] = new JObject { ["input_tokens"] = 12, ["output_tokens"] = 0, ["cost"] = 0.01 }, ["answers"] = new JObject { ["decision"] = answer } };
        JObject parsed = OpenRouterDecisions.ParseResponse(request, Reply(JObject.Parse("""{"type":"noul","noul":0.8}""")));
        Check((bool)parsed["answer"] && (double)parsed["probabilities"][1] == 0.8 && (string)parsed["evaluator_model"] == "typesafe/test-version", "Native probability/model mapping failed.");
        Check((string)parsed["evaluator_source"]["requested_model"] == "typesafe/test" && (int)parsed["metrics"]["generated_tokens"] == 0, "Decision provenance/zero output usage lost.");
        Check(!(bool)OpenRouterDecisions.ParseResponse(request, Reply(JObject.Parse("""{"type":"noul","noul":0.5}""")))["answer"], "Yes/no tie behavior changed.");
        foreach (JObject invalid in new[] {
            JObject.Parse("""{"type":"refusal"}"""), JObject.Parse("""{"type":"noul","noul":1.1}"""),
            JObject.Parse("""{"type":"noul","noul":"0.8"}"""), JObject.Parse("""{"type":"choice","choice":"yes"}"""),
            JObject.Parse("""{"type":"noul","noul":0.8,"confidence":-1}""") })
        {
            Reject(() => OpenRouterDecisions.ParseResponse(request, Reply(invalid)));
        }
        request["type"] = "choice";
        request["options"] = new JArray("3", "1", "__proto__");
        payload = OpenRouterDecisions.BuildRequest(request);
        Check((string)payload["questions"]["decision"]["criteria"]["option_2"] == "__proto__", "Choice labels must use stable API keys.");
        parsed = OpenRouterDecisions.ParseResponse(request, Reply(JObject.Parse("""{"type":"choice","choice":"option_2","probabilities":{"option_0":0.1,"option_1":0.1,"option_2":0.8}}""")));
        Check((string)parsed["answer"] == "__proto__" && (double)parsed["probabilities"][2] == 0.8, "Choice label/probability mapping failed.");
        CheckInvalidDistributionCannotSkip(request, Reply(JObject.Parse("""{"type":"choice","choice":"option_0","probabilities":{"option_0":0,"option_1":0,"option_2":0}}""")));
        CheckInvalidDistributionCannotSkip(request, Reply(JObject.Parse("""{"type":"choice","choice":"option_0","probabilities":{"option_0":0,"option_1":0.5,"option_2":0.5}}""")));
        Reject(() => OpenRouterDecisions.ParseResponse(request, Reply(JObject.Parse("""{"type":"choice","choice":"unknown"}"""))));
        Reject(() => OpenRouterDecisions.ParseResponse(request, Reply(JObject.Parse("""{"type":"choice","choice":"option_0","probabilities":{"option_0":2}}"""))));
        Check((string)OpenRouterDecisions.ParseResponse(request, Reply(JObject.Parse("""{"type":"choice","choice":"option_0"}""")))["answer"] == "3", "Optional probabilities must stay optional.");
        request["type"] = "score";
        payload = OpenRouterDecisions.BuildRequest(request);
        Check(JToken.DeepEquals(payload["questions"]["decision"]["criteria"], request["options"]), "Rating scale order changed.");
        parsed = OpenRouterDecisions.ParseResponse(request, Reply(JObject.Parse("""{"type":"score","score":1.75,"probabilities":{"0":0,"1":0.25,"2":0.75}}""")));
        Check((double)parsed["answer"] == 1.75 && (double)parsed["probabilities"][2] == 0.75, "Fractional rating must retain zero-based scale.");
        Reject(() => OpenRouterDecisions.ParseResponse(request, Reply(JObject.Parse("""{"type":"score","score":3}"""))));

        JObject catalog = JObject.Parse("""
        {"data":[
          {"id":"decision","name":"Decision","created":3,"endpoints":["/api/alpha/decisions"],"architecture":{"input_modalities":["text"],"output_modalities":["decisions"]},"pricing":{"prompt":"0.00000014","completion":"0","request":"0.001"}},
          {"id":"extract","endpoints":["/api/alpha/extractions"],"architecture":{"input_modalities":["text"],"output_modalities":["decisions"]}},
          {"id":"image-only","endpoints":["/api/alpha/decisions"],"architecture":{"input_modalities":["image"],"output_modalities":["decisions"]}},
          {"id":"chat","endpoints":["/api/v1/chat/completions"],"architecture":{"input_modalities":["text"],"output_modalities":["text"]}}]}
        """);
        JArray models = OpenRouterDecisions.ParseModels(catalog);
        Check(models.Count == 1 && (string)models[0]["model"] == "decision", "Catalog admitted non-decision/text models.");
        Check((string)models[0]["costIn"] == "$0.14" && (string)models[0]["costRequest"] == "$0.001", "Catalog lost input/request pricing.");
        CheckDecisionModelOptions();
        if (Environment.GetEnvironmentVariable("MP_TEST_DECISION_CATALOG") == "1")
        {
            JObject live = OpenRouterDecisions.GetMagicPromptDecisionModels(null).GetAwaiter().GetResult();
            Check((bool?)live["success"] == true && live["models"] is JArray rows && rows.Count > 0, $"Live public decision catalog failed: {live["error"]}");
            Console.WriteLine($"Verified public OpenRouter catalog: {live["models"].Count()} decision models (no inference).");
        }
        Console.WriteLine("OpenRouter decision settings, payloads, typed answers, and catalog checks passed.");
    }

    private static void CheckGenerationDecisionModel(JObject config)
    {
        RejectModel(() => DecisionEvaluator.ResolveSelection(config, "", RemoteEndpoint), "MP Decision Model ID", "Decision Models");
        Check(DecisionEvaluator.ResolveSelection(config, "keep", RemoteEndpoint, "typesafe/override").Evaluator == "typesafe/override",
            "A generation override should work before a Settings default is chosen.");

        config["model"] = "typesafe/default";
        Check(DecisionEvaluator.ResolveSelection(config, "", RemoteEndpoint).Evaluator == "typesafe/default",
            "Existing workflows should inherit the current Settings model.");

        DecisionEvaluator.Selection selection = DecisionEvaluator.ResolveSelection(config, "keep", RemoteEndpoint, " typesafe/override ");
        JObject source = new() { ["decision_source"] = new JObject { ["remote"] = true }, ["decision_evidence"] = "AI: original response" };
        List<JObject> requests = [];
        DecisionEvaluator.EvaluateAsync(source, selection, 0, (request, _) =>
        {
            requests.Add(OpenRouterDecisions.BuildRequest(request));
            return Task.FromResult(JObject.Parse("""{"answer":false,"evaluator_model":"typesafe/override-version"}"""));
        }, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        Check(requests.Count == 1 && (string)requests[0]["model"] == "typesafe/override"
            && (string)requests[0]["state"] == "AI: original response"
            && (string)requests[0]["questions"]["decision"]["instructions"] == "Refused?",
            "Generation override should reach the native request with the selected question and original evidence.");

        string dataDir = Program.DataDir;
        SessionHandler previousSessions = Program.Sessions;
        DirectoryInfo temporary = Directory.CreateTempSubdirectory("mp-decision-selection-test-");
        try
        {
            Program.DataDir = temporary.FullName;
            Program.Sessions = new SessionHandler();
            Program.Sessions.GenericSharedUser.SaveGenericData("magicprompt", "config", new JObject { ["decisions"] = config }.ToString());
            var input = new T2IParamInput(null);
            input.Set(T2IParamTypes.Prompt, "<mpprompt:hello>");
            input.Set(T2IParamTypes.Seed, 1L);
            T2IParamTypes.ApplyParameter("mpmodelid", "example/prompt-model", input);
            T2IParamTypes.ApplyParameter("mpdecisionchecks", "true", input);
            T2IParamTypes.ApplyParameter("mpdecisionmodelid", "TYPESAFE/BLOCKED", input);
            RejectModel(input.ApplyLateSpecialLogic, "blocked", "MP Decision Model ID");
            CheckDecisionCatalogCache(config);
        }
        finally
        {
            if (Program.Sessions != previousSessions) Program.Sessions.Shutdown();
            Program.Sessions = previousSessions;
            Program.DataDir = dataDir;
            temporary.Delete(recursive: true);
        }
    }

    private static void CheckDecisionCatalogCache(JObject config)
    {
        JObject ReadParameter() => T2IParamTypes.Types["mpdecisionmodelid"].ToNet(null);

        JObject saved = SessionSettings.SaveMagicPromptSettings(JObject.Parse("""
            {"settings":{"decisions":{"favoritedModels":["typesafe/favorite"],"blockedModels":["typesafe/blocked"]}}}
            """)).GetAwaiter().GetResult();
        JToken merged = saved["settings"]?["decisions"];
        Check((bool?)saved["success"] == true && (string)merged?["model"] == "typesafe/default"
            && JToken.DeepEquals(merged?["questions"], config["questions"]),
            "Saving only decision model preferences should preserve the existing Settings default and questions.");

        JObject cold = Task.Run(ReadParameter).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        Check(cold["values"].Values<string>().SequenceEqual(new[] { "" })
            && (string)cold["value_names"][0] == "Settings default",
            "Parameter listing should return Settings default on a cold decision catalog cache.");

        using HttpClient client = new(new DecisionCatalogFixture());
        JObject fetched = OpenRouterDecisions.FetchDecisionModels(client).GetAwaiter().GetResult();
        Check((bool?)fetched["success"] == true && fetched["models"].Count() == 3, "Catalog refresh failed to load decision models.");
        Check(ReadParameter()["values"].Values<string>().SequenceEqual(new[] { "", "typesafe/favorite", "typesafe/plain" }),
            "Parameter listing should use the refreshed catalog and persisted decision model preferences.");

        JObject updated = SessionSettings.SaveMagicPromptSettings(JObject.Parse("""
            {"settings":{"backend":"openaiapi","decisions":{"favoritedModels":["typesafe/plain"],"blockedModels":["typesafe/favorite"]}}}
            """)).GetAwaiter().GetResult();
        Check((bool?)updated["success"] == true && (string)updated["settings"]?["backend"] == "openaiapi",
            "Settings save should switch the prompt provider.");
        Check(ReadParameter()["values"].Values<string>().SequenceEqual(new[] { "", "typesafe/plain", "typesafe/blocked" }),
            "Parameter listing should retain the fetched catalog and apply new decision model preferences after a settings save.");
    }

    private sealed class DecisionCatalogFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"data":[
                    {"id":"typesafe/plain","created":20,"endpoints":["/api/alpha/decisions"],"architecture":{"input_modalities":["text"]}},
                    {"id":"typesafe/favorite","created":10,"endpoints":["/api/alpha/decisions"],"architecture":{"input_modalities":["text"]}},
                    {"id":"typesafe/blocked","created":30,"endpoints":["/api/alpha/decisions"],"architecture":{"input_modalities":["text"]}}
                    ]}
                    """)
            });
    }

    private static void CheckDecisionModelOptions()
    {
        JObject settings = JObject.Parse("""{"favoritedModels":["FAVORITE","new-favorite","blocked"],"blockedModels":["BLOCKED"]}""");
        JArray models = JArray.Parse("""
            [{"model":"old","created":1},{"model":"favorite","created":2},
             {"model":"new","created":5},{"model":"blocked","created":6},
             {"model":"new-favorite","name":"New favorite","created":3,"costIn":"$0.14","costOut":"$1","costRequest":"$0.001"},
             {"model":"no-date","created":"unknown"}]
            """);
        List<string> options = ModelListProvider.FormatDecisionModelOptions(settings, models);
        Check(options.SequenceEqual(new[] { "///Settings default", "new-favorite///New favorite ‧ $0.14",
            "favorite///favorite ‧ —", "new///new ‧ —", "old///old ‧ —", "no-date///no-date ‧ —" }),
            "Generate should offer Settings default and unblocked decision models ordered by favorite and release date with compact input pricing.");
    }

    private static void RejectModel(Action action, params string[] expectedMessage)
    {
        try { action(); }
        catch (SwarmReadableErrorException ex) when (expectedMessage.All(ex.Message.Contains)) { return; }
        throw new Exception("Generation should reject a missing or blocked decision model and explain how to select another.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Malformed/refused decision was accepted.");
    }

    private static void CheckInvalidDistributionCannotSkip(JObject request, JObject reply)
    {
        JObject question = new() { ["id"] = "invalid", ["title"] = "Invalid distribution", ["question"] = request["question"],
            ["type"] = "choice", ["options"] = request["options"], ["skipIf"] = "3" };
        JObject source = new() { ["decision_source"] = new JObject { ["remote"] = true }, ["decision_evidence"] = request["evidence"] };
        List<JObject> records = [];
        JObject match = DecisionEvaluator.EvaluateAsync(source, new("typesafe/test", [question]), 0,
            (check, _) => Task.FromResult(OpenRouterDecisions.ParseResponse(check, reply)), records.Add, CancellationToken.None).GetAwaiter().GetResult();
        Check(match == null && records.Count == 1 && (string)records[0]["status"] == "error" && records[0]["answer"] == null,
            "An invalid probability distribution triggered Skip If or counted as an answer.");
    }
}
