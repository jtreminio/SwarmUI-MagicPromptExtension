using System.Reflection;
using Hartsy.Extensions.MagicPromptExtension;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;

internal static class PromptTagChecks
{
    private static void Main()
    {
        T2IParamTypes.RegisterDefaults();
        typeof(MagicPromptExtension).GetMethod("RegisterT2IParameters", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

        T2IRegisteredParam<T> Parameter<T>(string name) => new(T2IParamTypes.Types[name]);
        var useCache = Parameter<bool>("mpusecache");
        var model = Parameter<string>("mpmodelid");
        var instructions = Parameter<string>("mpinstructions");
        var postFilter = Parameter<string>("mppostfilter");
        var thinking = Parameter<string>("mpthinking");
        var onError = Parameter<string>("mponerror");
        var disableLlm = Parameter<bool>("disablellmrequest");
        var handler = new PromptHandler(new PromptCache(), useCache, model, instructions, postFilter, thinking, onError, disableLlm);
        DecisionChecks.Run().GetAwaiter().GetResult();
        ModelStatsChecks.Run().GetAwaiter().GetResult();
        int passed = 0;

        void Equal(string expected, string actual, string name)
        {
            if (expected != actual)
            {
                throw new Exception($"{name}: expected '{expected}', got '{actual}'");
            }
            passed++;
        }

        T2IParamInput Check(string prompt, string expected, bool disabled = true, string filter = "", string negative = "", string expectedNegative = "")
        {
            var input = new T2IParamInput(null);
            input.Set(T2IParamTypes.Prompt, prompt);
            input.Set(T2IParamTypes.NegativePrompt, negative);
            input.Set(T2IParamTypes.Seed, 1L);
            input.Set(T2IParamTypes.WildcardSeed, 0L);
            input.Set(T2IParamTypes.WildcardSeedBehavior, "Index");
            input.Set(disableLlm, disabled);
            input.Set(postFilter, filter);
            PromptHandler.NormalizeInlinePostFilters(input);
            input.PreparsePromptLikes();
            handler.ProcessPrompt(input);
            Equal(expected, input.Get(T2IParamTypes.Prompt), prompt);
            Equal(expectedNegative, input.Get(T2IParamTypes.NegativePrompt, ""), "negative prompt");
            Equal(prompt, (string)input.ExtraMeta["original_prompt"], "original prompt metadata");
            if (input.ExtraMeta.ContainsKey("mp_literal_escape") || input.ExtraMeta.ContainsKey("mp_parsed_prompts") || input.ExtraMeta.ContainsKey("mp_tag_variables"))
            {
                throw new Exception("Internal escape marker leaked into metadata.");
            }
            return input;
        }

        Check("<mpprompt:angry >:( face>", "angry >:( face");
        Check("<mpprompt:hat <8) face>", "hat <8) face");
        Check("before <mpprompt:>:( <8)> after", "before >:( <8) after");
        Check("<mpprompt:a < b, c > d, e >= f></mpprompt>", "a < b, c > d, e >= f");
        Check("<mpprompt:angry >:(, wearing hat <8), 5 > 3></mpprompt>", "angry >:(, wearing hat <8), 5 > 3");
        Check("<mpprompt[false]:first response> <mpprompt[Tag-Based]:<mpresponse:0>></mpprompt>", "first response");
        Check("<mpprompt:<div>hello</div>></mpprompt>", "<div>hello</div>");
        Check("before <mpprompt:literal > and < and >:(></mpprompt> after > stays outside", "before literal > and < and >:( after > stays outside");
        Check("<mpprompt:literal </mpprompt> in content></mpprompt>", "literal </mpprompt> in content");
        Check("<mpprompt:literal trailing >></mpprompt>", "literal trailing >");
        Check("<mpprompt:></mpprompt>", "");
        Check("<mpprompt:cat> outside > stays outside", "cat outside > stays outside");
        Check("<mpprompt|\">:(=happy\":>:( <8)></mpprompt> <mpprompt:second>", "happy <8) second");
        Check("<MPPROMPT:>:( <8)></MPPROMPT>", ">:( <8)");
        Check("<mpprompt:cat> outside >:(", "cat outside >:(");
        Check("<mpprompt:<random:>:(|<8)>>", ">:(");
        Check("<mpprompt:<random:<8)|>:(>>", "<8)");
        Check("<mpprompt:<repeat[2]:<random:<8)|>:(>>>", "<8) <8)");
        Check("<mpprompt:<setvar[count,false]:2><repeat[<var:count>]:<8)>>", "<8) <8)");
        Check("<repeat[2]:<mpprompt:>:( <8)>>", ">:( <8) >:( <8)");
        Check("<random:<mpprompt:<8)>|unused>", "<8)");
        Check("<mpprompt:__mp_parsed_prompt__literal>", "__mp_parsed_prompt__literal");
        Check("<mpprompt:cat> <mpprompt:__mp_parsed_prompt__Y2F0>", "cat __mp_parsed_prompt__Y2F0");
        Check("<repeat[2]:<mpprompt:literal > and <8)></mpprompt>>", "literal > and <8) literal > and <8)");
        Check("<mpprompt:a > b <random:red|blue>></mpprompt>", "a > b red");
        Check("<mpprompt[false]:>:( <8)> <mpprompt:copy <mpresponse:0>> / <mpresponse:0>", "copy >:( <8) / >:( <8)");
        Check("<mpprompt|\">:(=happy\",\"<8)=hat\":>:( <8)>", "happy hat");
        Check("<mpprompt[false]:>:( <8)> <mpresponse|\">:(=happy\":0>", "happy <8)");
        Check("<mpprompt:>:( <8)> / <mporiginal>", ">:( <8) / >:( <8)");
        Check("<mpprompt:>:( <8)>", ">:( <8)", disabled: false);
        Check("<mpprompt:>:( <8)>", "happy hat", filter: "\">:(=happy\"\n\"<8)=hat\"");
        Check("<mpprompt:>", "");
        Check(@"<mpprompt:literal \<random:cat\> and \>>", "literal <random:cat> and >");

        var variableInput = Check("<mpprompt:<setvar[face,false]:<8)> <var:face>>", "<8)");
        Equal("<8)", ((Dictionary<string, string>)variableInput.ExtraMeta["mp_variables"])["face"], "variable metadata");
        Check("<mpprompt:<setvar[face,false]:<8)> <var:face>>", "<8)", negative: "<mpprompt:negative tag> <var:face>", expectedNegative: "<mpprompt:negative tag> <8)");
        T2IPromptHandling.PromptTagProcessors["testface"] = (data, context) => ">:( <8)";
        Check("<setvar[face,false]:<testface>> <mpprompt:<var:face>>", ">:( <8)");

        Check("<setvar[mplength,false]:50><mpprompt[; mplength=100]:<var:mplength>> / <mpprompt[; mplength=300]:<var:mplength>> / <mpprompt:<var:mplength>> / <var:mplength>", "100 / 300 / 50 / 50");
        Check("<mpprompt[; mplength=100]:<repeat[2]:<var:mplength>>>", "100 100");
        Check("<repeat[2]:<mpprompt[; mplength=100]:<var:mplength>>>", "100 100");
        Check("<mpprompt[Describe, random, false; mplength=100]:<var:mplength>><mpprompt[; mplength=300]:<mpresponse:0> / <var:mplength>>", "100 / 300");
        Check("<mpprompt[; tone=\"dark, cinematic; [forest] > <8)\"]:<var:tone>>", "dark, cinematic; [forest] > <8)");
        Check("<mpprompt[; tone=\"\"]:start<var:tone>end>", "startend");
        Check("<mpprompt[; tone=\"say \\\"hello\\\" C:\\\\temp\"]:<var:tone>>", "say \"hello\" C:\\temp");
        Check("<mpprompt[; tone=forest]|\"forest=woodland\":<var:tone>>", "woodland");
        Check("<mpprompt[; tone=forest]:<var:tone>>", "forest", disabled: false);
        var scopedMetadata = Check("<setvar[mplength,false]:50><mpprompt[; mplength=100]:<setvar[mplength,false]:200><var:mplength>><mpprompt:<var:mplength>><var:mplength>", "2005050");
        Equal("50", ((Dictionary<string, string>)scopedMetadata.ExtraMeta["mp_variables"])["mplength"], "local variables do not leak into metadata");
        Check("<mpprompt[; mplength=100]:<var:mplength>><mpprompt:<var:mplength>>", "100");

        var batchInput = new T2IParamInput(null);
        batchInput.Set(T2IParamTypes.Prompt, "<mpprompt[; tone=forest]:<var:tone>>");
        batchInput.Set(disableLlm, true);
        PromptHandler.NormalizeInlinePostFilters(batchInput);
        var batchMetadata = batchInput.BuildExtraDataJObject();
        var batchImages = new[] { batchInput.Clone(), batchInput.Clone() };
        foreach (var image in batchImages)
        {
            image.PreparsePromptLikes();
            handler.ProcessPrompt(image);
            Equal("forest", image.Get(T2IParamTypes.Prompt), "scoped batch image");
        }
        Equal("True", JToken.DeepEquals(batchMetadata, batchInput.BuildExtraDataJObject()).ToString(), "parsing batch images leaves the batch input metadata untouched");

        // Inspect the exact snapshots consumed by instruction resolution before finalization clears them.
        var scopedInput = new T2IParamInput(null);
        scopedInput.Set(T2IParamTypes.Prompt, "<setvar[mplength,false]:50><setvar[tone,false]:global><mpprompt[; mplength=100]:<var:mplength>><mpprompt[; mplength=300]:<var:mplength>><mpprompt:<var:mplength>>");
        PromptHandler.NormalizeInlinePostFilters(scopedInput);
        scopedInput.PreparsePromptLikes();
        var tokens = System.Text.RegularExpressions.Regex.Matches(scopedInput.Get(T2IParamTypes.Prompt), @":(__mp_parsed_prompt_\w+__)>" );
        Equal("3", tokens.Count.ToString(), "three tag snapshots");
        string[] lengths = ["100", "300", "50"];
        var instructionFixture = JObject.Parse("""
            {"prompt":"Write <var:mplength> words, <var:tone> tone.",
             "custom":{"custom-description":{"title":"Describe","content":"Write <var:mplength> words, <var:tone> tone."}}}
            """);
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i].Groups[1].Value;
            var variables = PromptTagSyntax.GetTagVariables(token, scopedInput);
            Equal(lengths[i], PromptTagSyntax.DecodeParsedPrompt(token, scopedInput), "scoped body");
            Equal($"Write {lengths[i]} words, global tone.", InstructionResolver.SubstituteVariables("Write <var:mplength> words, <var:tone> tone.", scopedInput, variables), "scoped instructions inherit globals");
            foreach (string key in new[] { "prompt", "custom-description", "Describe", "describe", "missing", "" })
            {
                Equal($"Write {lengths[i]} words, global tone.", InstructionResolver.ResolveContent(instructionFixture, key, scopedInput, variables), $"scoped instruction lookup: {key}");
            }
        }
        Equal("50", InstructionResolver.SubstituteVariables("<var:mplength>", scopedInput), "global instruction variables remain unchanged");

        var repeatedInput = new T2IParamInput(null);
        repeatedInput.Set(T2IParamTypes.Prompt, "<repeat[2]:<mpprompt[; mplength=100]:<setvar[mplength,false]:200><var:mplength>>>");
        PromptHandler.NormalizeInlinePostFilters(repeatedInput);
        repeatedInput.PreparsePromptLikes();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(repeatedInput.Get(T2IParamTypes.Prompt), @":(__mp_parsed_prompt_\w+__)>"))
        {
            var token = match.Groups[1].Value;
            Equal("200", PromptTagSyntax.DecodeParsedPrompt(token, repeatedInput), "repeated scoped body");
            Equal("200", PromptTagSyntax.GetTagVariables(token, repeatedInput)["mplength"], "revisited tag retains instruction scope");
        }

        // Cached responses exercise the complete late-handler path without a live backend or settings store.
        var scopedCache = new PromptCache();
        foreach (string length in lengths)
        {
            scopedCache.GetOrCreate("subject", $"Write {length} words.", "test-model", "none", () => $"response-{length}", 1000, out _);
        }
        var cachedInput = new T2IParamInput(null);
        cachedInput.Set(T2IParamTypes.Prompt, "<setvar[mplength,false]:50><mpprompt[; mplength=100]:subject> / <mpprompt[; mplength=300]:subject> / <mpprompt:subject>");
        cachedInput.Set(model, "test-model");
        cachedInput.Set(instructions, "Write <var:mplength> words.");
        cachedInput.Set(useCache, true);
        cachedInput.Set(thinking, "none");
        cachedInput.Set(T2IParamTypes.Seed, 1L);
        PromptHandler.NormalizeInlinePostFilters(cachedInput);
        cachedInput.PreparsePromptLikes();
        new PromptHandler(scopedCache, useCache, model, instructions, postFilter, thinking, onError, disableLlm).ProcessPrompt(cachedInput);
        Equal("response-100 / response-300 / response-50", cachedInput.Get(T2IParamTypes.Prompt), "scoped instructions select distinct cache entries");

        foreach (string malformed in new[] { "<mpprompt[; tone=\"unclosed]:text>", "<mpprompt[; mplength=100:text>", "<mpprompt[; mplength]:text>" })
        {
            var invalidInput = new T2IParamInput(null);
            invalidInput.Set(T2IParamTypes.Prompt, malformed);
            try
            {
                PromptHandler.NormalizeInlinePostFilters(invalidInput);
                throw new Exception($"Accepted malformed header: {malformed}");
            }
            catch (SwarmReadableErrorException)
            {
                passed++;
            }
        }

        foreach (string header in new[] { "; mplength", "; =100", "; bad name=100", "; mplength=", "; mplength=100; mplength=200", "; tone=\"unclosed", "; tone=\"valid\"suffix" })
        {
            try
            {
                PromptTagOptions.Parse(header);
                throw new Exception($"Accepted invalid options: {header}");
            }
            catch (SwarmReadableErrorException)
            {
                passed++;
            }
        }

        // A failing nested parser must still restore the outer variable dictionary.
        var outer = new Dictionary<string, string> { ["mplength"] = "50" };
        var failingContext = new T2IPromptHandling.PromptTagContext { Input = new T2IParamInput(null), Variables = outer, PreData = "; mplength=100" };
        T2IPromptHandling.PromptTagProcessors["testfail"] = (_, _) => throw new InvalidOperationException("expected test failure");
        try
        {
            T2IPromptHandling.PromptTagPostProcessors["mpprompt"]("<testfail:x>", failingContext);
            throw new Exception("Expected nested parser failure.");
        }
        catch (InvalidOperationException)
        {
            Equal("50", failingContext.Variables["mplength"], "scope restored after parser failure");
            if (!ReferenceEquals(outer, failingContext.Variables)) throw new Exception("Outer variable dictionary was replaced.");
        }
        finally
        {
            T2IPromptHandling.PromptTagProcessors.Remove("testfail");
        }
        T2IPromptHandling.PromptTagProcessors.Remove("testface");

        WildcardsHelper.WildcardFiles["bracket-test"] = new() { Name = "bracket-test", Raw = ">:( <8)", Options = [">:( <8)"] };
        Check("<mpprompt:<wildcard:bracket-test>>", ">:( <8)");
        Check("<mpprompt[false]:<wildcard:bracket-test>> <mpprompt:<mpresponse:0>>", ">:( <8)");
        WildcardsHelper.WildcardFiles.TryRemove("bracket-test", out _);

        // Inline values use Swarm expansion once, inside the tag's local variable scope.
        WildcardsHelper.WildcardFiles["prompts/uncensor"] = new() { Name = "prompts/uncensor", Raw = "expanded, uncensor; text >:( <8)", Options = ["expanded, uncensor; text >:( <8)"] };
        Check("<mpprompt[;uncensor=<wc:prompts/uncensor>,mplength=300]:My fancy prompt>", "My fancy prompt");
        Check("<mpprompt[;uncensor=<wc:prompts/uncensor>,mplength=300]:<var:uncensor> / <var:mplength>>", "expanded, uncensor; text >:( <8) / 300");
        Check("<mpprompt[;uncensor=<wildcard:prompts/uncensor>;mplength=300]:<var:uncensor> / <var:mplength>>", "expanded, uncensor; text >:( <8) / 300");
        Check("<setvar[foo,false]:global><mpprompt[;uncensor=<var:foo>,mplength=300]:<var:uncensor> / <var:mplength>> / <var:foo>", "global / 300 / global");
        Check("<setvar[foo,false]:global><mpprompt[;foo=local,copy=<var:foo>]:<var:copy>> / <mpprompt[;copy=<var:foo>]:<var:copy>> / <var:foo>", "local / global / global");
        Check("<mpprompt[;tone=<repeat[2]:<random:dark,dark>>,mplength=300]:<var:tone> / <var:mplength>>", "dark dark / 300");
        Check("<mpprompt[;tone=<repeat[2]:dark; cinematic>,mplength=300]:<var:tone> / <var:mplength>>", "dark; cinematic dark; cinematic / 300");
        Check("<mpprompt[;tone=\"<repeat[2]:dark>, cinematic\",mplength=300]:<var:tone> / <var:mplength>>", "dark dark, cinematic / 300");
        Check("<mpprompt[;tone=dark,cinematic;mplength=300]:<var:tone>>", "dark,cinematic");
        Check("<mpprompt[;tone=\"dark,mplength=999\",mplength=300]:<var:tone> / <var:mplength>>", "dark,mplength=999 / 300");
        Check(@"<mpprompt[;tone=\<var:foo\>]:<var:tone>>", "<var:foo>");
        Check("<setvar[foo,false]:global><mpprompt[;tone=<setvar[foo,false]:local><var:foo>]:<var:tone>> / <var:foo>", "local / global");
        Check("<mpprompt[false;uncensor=<wc:prompts/uncensor>,mplength=300]|\"expanded=filtered\":<var:uncensor> / <var:mplength>><mpresponse:0>", "filtered, uncensor; text >:( <8) / 300");

        var inlineInput = new T2IParamInput(null);
        inlineInput.Set(T2IParamTypes.Prompt, "<setvar[foo,false]:300><mpprompt[;uncensor=<wc:prompts/uncensor>,mplength=<var:foo>]:<var:uncensor> / <var:mplength>>");
        inlineInput.Set(T2IParamTypes.Seed, 1L);
        inlineInput.Set(T2IParamTypes.WildcardSeed, 0L);
        PromptHandler.NormalizeInlinePostFilters(inlineInput);
        inlineInput.PreparsePromptLikes();
        var inlineToken = System.Text.RegularExpressions.Regex.Match(inlineInput.Get(T2IParamTypes.Prompt), @":(__mp_parsed_prompt_\w+__)>").Groups[1].Value;
        Equal("expanded, uncensor; text >:( <8) / 300", PromptTagSyntax.DecodeParsedPrompt(inlineToken, inlineInput), "expanded inline body");
        Equal("expanded, uncensor; text >:( <8) / 300", InstructionResolver.SubstituteVariables("<var:uncensor> / <var:mplength>", inlineInput, PromptTagSyntax.GetTagVariables(inlineToken, inlineInput)), "expanded inline instructions");
        WildcardsHelper.WildcardFiles.TryRemove("prompts/uncensor", out _);

        int valueExpansions = 0;
        T2IPromptHandling.PromptTagProcessors["testvalue"] = (_, _) => (++valueExpansions).ToString();
        Check("<repeat[2]:<mpprompt[;value=<testvalue:>]:<var:value> / <var:value>>>", "1 / 1 2 / 2");
        Equal("2", valueExpansions.ToString(), "one expansion per repeated tag invocation");
        T2IPromptHandling.PromptTagProcessors.Remove("testvalue");

        var savedVariables = new Dictionary<string, string>
        {
            ["prompt"] = "Source MP Prompt",
            ["character"] = "A woman in a red coat",
            ["literal"] = "<random:red|blue> >:( <8)",
            ["unicode"] = "雪\n\"quoted\" \\ text",
            ["empty"] = ""
        };
        var finalizedPrompt = "A woman in a red coat";
        var sourcePrompt = "<mpprompt:<var:character>>";

        T2IParamInput Refine(Dictionary<string, string> variables)
        {
            // Exercise Swarm's actual request conversion, which accepts metadata as strings.
            var extra = new JObject
            {
                ["mp_is_refining"] = true,
                ["mp_refined_prompt"] = finalizedPrompt,
                ["original_prompt"] = sourcePrompt
            };
            if (variables != null)
            {
                extra["mp_refined_variables"] = JsonConvert.SerializeObject(variables);
            }
            var input = T2IAPI.RequestToParams(null, new JObject { ["extra_metadata"] = extra });
            input.Set(T2IParamTypes.Prompt, finalizedPrompt);
            input.Set(T2IParamTypes.NegativePrompt, "blurry");
            input.Set(T2IParamTypes.Seed, 123L);
            input.Set(T2IParamTypes.WildcardSeed, 0L);
            input.Set(disableLlm, false);
            input.Set(postFilter, "\"red=blue\"");
            PromptHandler.NormalizeInlinePostFilters(input);
            input.PreparsePromptLikes();
            handler.ProcessPrompt(input);
            Equal(finalizedPrompt, input.Get(T2IParamTypes.Prompt), "refine finalized prompt");
            Equal("blurry", input.Get(T2IParamTypes.NegativePrompt), "refine negative prompt");
            Equal("123", input.Get(T2IParamTypes.Seed).ToString(), "refine seed");
            Equal(sourcePrompt, (string)input.ExtraMeta["original_prompt"], "refine original prompt");
            if (input.ExtraMeta.Keys.Any(key => key.StartsWith("mp_refined_") || key == "mp_is_refining"))
            {
                throw new Exception("Internal refine marker leaked into metadata.");
            }
            return input;
        }

        var refinedInput = Refine(savedVariables);
        var refinedMetadata = JObject.Parse(refinedInput.GenRawMetadata());
        if (!JToken.DeepEquals(JObject.FromObject(savedVariables), refinedMetadata["sui_extra_data"]?["mp_variables"]))
        {
            throw new Exception("Refine Img did not preserve variable values as an object in image metadata.");
        }
        passed++;
        Equal(JsonConvert.SerializeObject(Refine(null).ToJSON()), JsonConvert.SerializeObject(refinedInput.ToJSON()), "vars do not change generation parameters");
        if (Refine(new Dictionary<string, string>()).BuildExtraDataJObject()["mp_variables"] is not JObject { Count: 0 })
        {
            throw new Exception("Refine Img did not preserve an empty variable map.");
        }
        passed++;

        // Retired providers must not survive migration, while custom content and supported URLs do.
        var migrate = typeof(Hartsy.Extensions.MagicPromptExtension.WebAPI.SessionSettings)
            .GetMethod("RemoveUnsupportedBackends", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (string retired in new[] { "ollama", "openai" })
        {
            var saved = JObject.Parse("""
                {"backend":"openrouter","model":"old-model","visionbackend":"ollama","visionmodel":"vision-model",
                 "linkChatAndVisionModels":true,"backends":{"ollama":{},"openai":{},"openrouter":{"timeout":90},"openaiapi":{"baseurl":"http://localhost:1234"}},
                 "instructions":{"custom":{"custom-existing":{"title":"Existing","content":"Exact content"}}}}
                """);
            saved["backend"] = retired;
            migrate.Invoke(null, [saved]);
            Equal("openrouter", saved["backend"]!.ToString(), $"{retired} fallback");
            Equal("", saved["model"]!.ToString(), $"{retired} model reset");
            Equal("Exact content", saved["instructions"]!["custom"]!["custom-existing"]!["content"]!.ToString(), "custom content survives");
            Equal("http://localhost:1234", saved["backends"]!["openaiapi"]!["baseurl"]!.ToString(), "local URL survives");
            Equal("False", ((JObject)saved["backends"]!).ContainsKey(retired).ToString(), "retired config removed");
            Equal("False", saved.ContainsKey("visionbackend").ToString(), "vision config removed");
        }
        var body = JObject.FromObject(BackendSchema.GetSchemaType("openrouter", new() { Text = "A scene", Instructions = "Expand" }, "test-model"));
        Equal("False", body["reasoning"]!["enabled"]!.ToString(), "OpenRouter thinking off");
        Equal("A scene", body["messages"]![1]!["content"]!.ToString(), "text request content");
        body = JObject.FromObject(BackendSchema.GetSchemaType("openaiapi", new() { Text = "A scene" }, "local", "high"));
        Equal("high", body["reasoning_effort"]!.ToString(), "local reasoning effort");
        Equal("8168", body["max_tokens"]!.ToString(), "local thinking budget");
        foreach (string retired in new[] { "ollama", "openai" })
        {
            bool rejected = false;
            try { BackendSchema.GetSchemaType(retired, new() { Text = "A scene" }, "test-model"); }
            catch (ArgumentException) { rejected = true; }
            Equal("True", rejected.ToString(), $"{retired} schema rejected");
        }

        Console.WriteLine($"Prompt tag checks passed: {passed}");
    }
}
