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
            if (input.ExtraMeta.ContainsKey("mp_literal_escape") || input.ExtraMeta.ContainsKey("mp_parsed_prompts"))
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
        T2IPromptHandling.PromptTagProcessors.Remove("testface");

        WildcardsHelper.WildcardFiles["bracket-test"] = new() { Name = "bracket-test", Raw = ">:( <8)", Options = [">:( <8)"] };
        Check("<mpprompt:<wildcard:bracket-test>>", ">:( <8)");
        Check("<mpprompt[false]:<wildcard:bracket-test>> <mpprompt:<mpresponse:0>>", ">:( <8)");
        WildcardsHelper.WildcardFiles.TryRemove("bracket-test", out _);

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

        Console.WriteLine($"Prompt tag checks passed: {passed}");
    }
}
