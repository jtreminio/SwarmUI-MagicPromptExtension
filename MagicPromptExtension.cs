using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using SwarmUI.Core;
using SwarmUI.Utils;
using SwarmUI.Text2Image;

namespace Hartsy.Extensions.MagicPromptExtension;

/// <summary>
/// MagicPrompt - an LLM-powered prompt enhancement tool.
/// </summary>
public class MagicPromptExtension : Extension
{
    private static readonly PromptCache _promptCache = new();
    private static T2IRegisteredParam<bool> _paramUseCache;
    private static T2IRegisteredParam<string> _paramModelId;
    private static T2IRegisteredParam<string> _paramInstructions;
    private static T2IRegisteredParam<string> _paramPostFilter;
    private static T2IRegisteredParam<string> _paramThinking;
    private static T2IRegisteredParam<string> _paramOnError;
    private static T2IRegisteredParam<bool> _paramDisableLlmRequest;
    private static T2IRegisteredParam<bool> _paramDecisionChecks;
    private static T2IRegisteredParam<string> _paramDecisionQuestion;
    private System.Diagnostics.Process _decisionRuntime;

    public override void OnPreInit()
    {
        Logs.Info("MagicPrompt prompt tools started.");
        RegisterAssets();
    }

    public override void OnInit()
    {
        MagicPromptAPI.Register();
        RegisterT2IParameters();
        _decisionRuntime = DecisionRuntimeHost.Start(FilePath, Program.ServerSettings.Paths.ModelRoot);
    }

    public override void OnShutdown()
    {
        DecisionRuntimeHost.Stop(_decisionRuntime);
        _decisionRuntime?.Dispose();
        _decisionRuntime = null;
    }

    private void RegisterAssets()
    {
        ScriptFiles.Add("Assets/magicprompt.js");
        ScriptFiles.Add("Assets/settings.js");
        ScriptFiles.Add("Assets/modeldropdown.js");
        ScriptFiles.Add("Assets/prompthistory.js");
        ScriptFiles.Add("Assets/decisionhistory.js");
        StyleSheetFiles.Add("Assets/settings.css");
        StyleSheetFiles.Add("Assets/modeldropdown.css");
        StyleSheetFiles.Add("Assets/prompthistory.css");
    }

    private static void RegisterT2IParameters()
    {
        var paramGroup = new T2IParamGroup(
            Name: "Magic Prompt Auto Enable",
            Description: "Automatically use Magic Prompt to rewrite your prompt before generation.",
            Toggles: true,
            Open: false,
            OrderPriority: 9
        );

        _paramUseCache = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "MP Use Cache",
            Description: "Reuse cached LLM results for matching prompts across image seeds. When disabled, results are still cached, but only reused when the prompt, image seed, and LLM model all match.",
            Default: "true",
            Group: paramGroup,
            OrderPriority: 6
        ));

        T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "MP Generate Wildcard Seed",
            Description: "Every time you press Generate, a new Wildcard Seed is generated. " +
                         "This is extremely useful for batching images, so they can reuse cached LLM responses.",
            Default: "false",
            Group: paramGroup,
            OrderPriority: 7
        ));

        _paramModelId = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "MP Model ID",
            Description: "Select an LLM to use for this batch",
            Default: "loading",
            IgnoreIf: "loading",
            Group: paramGroup,
            OrderPriority: 1,
            ValidateValues: false,
            GetValues: ModelListProvider.GetModelList
        ));

        _paramInstructions = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "MP Instructions",
            Description: "Select a prompt to use for this batch",
            Default: "loading",
            IgnoreIf: "loading",
            Group: paramGroup,
            OrderPriority: 2,
            ValidateValues: false,
            GetValues: ModelListProvider.GetInstructionList
        ));

        _paramPostFilter = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "MP Post-Filter",
            Description: "Strings to strip from LLM responses, one per line. Each line is removed as a literal match from every response. A line in the form \"FOO=BAR\" (double quotes required) instead replaces FOO with BAR.",
            Default: "",
            IgnoreIf: "",
            Group: paramGroup,
            OrderPriority: 5,
            ViewType: ParamViewType.BIG,
            HideFromMetadata: true,
            Toggleable: true
        ));

        _paramThinking = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "MP Thinking",
            Description: "How much thinking/reasoning effort the LLM uses before responding. 'None' disables thinking where the backend supports an off switch. Mapping per backend: OpenRouter reasoning effort, local API reasoning_effort. Levels only work on reasoning-capable models; some backends reject unsupported levels.",
            Default: "none",
            IgnoreIf: "none",
            GetValues: _ => ["none///None", "low///Low", "medium///Medium", "high///High"],
            Group: paramGroup,
            OrderPriority: 3
        ));

        _paramOnError = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "MP On Error",
            Description: "What to do when the LLM request fails (error response, timeout, empty reply). " +
                         "'Skip Generation' cancels just that image without touching the backend - enable Swarm's " +
                         "'Continue After Errors' parameter to let the rest of the batch keep going, otherwise Swarm " +
                         "cancels the whole queue on the first failure. " +
                         "'Use Original Prompt' generates anyway using the raw text inside the mpprompt tag.",
            Default: PromptHandler.OnErrorSkip,
            IgnoreIf: PromptHandler.OnErrorSkip,
            GetValues: _ => [$"{PromptHandler.OnErrorSkip}///Skip Generation", $"{PromptHandler.OnErrorFallback}///Use Original Prompt"],
            Group: paramGroup,
            OrderPriority: 8
        ));

        _paramDisableLlmRequest = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "Disable LLM Request",
            Description: "Skip LLM requests and cached responses, applying MP Post-Filter to the original mpprompt tag text instead. If there are no mpprompt tags, filter the whole prompt. No MP Model ID is required.",
            Default: "false",
            IgnoreIf: "false",
            Group: paramGroup,
            OrderPriority: 4
        ));

        _paramDecisionChecks = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "MP Decision Checks",
            Description: "Run the selected decision questions on newly fetched remote LLM responses using local CPU inference. Results are logged for comparison.",
            Default: "false",
            IgnoreIf: "false",
            Group: paramGroup,
            OrderPriority: 4.1
        ));
        _paramDecisionQuestion = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "MP Decision Question",
            Description: "Choose one saved question to run with MP Decision Checks, or use the default questions selected in MagicPrompt Settings.",
            Default: "",
            IgnoreIf: "",
            Group: paramGroup,
            ValidateValues: false,
            GetValues: ModelListProvider.GetDecisionQuestions,
            OrderPriority: 4.2
        ));

        PromptRegion.RegisterCustomPrefix("mpprompt");
        PromptRegion.RegisterCustomPrefix("mpresponse");
        T2IPromptHandling.PromptTagPostProcessors["mpprompt"] = ProcessMppromptTag;
        T2IPromptHandling.PromptTagPostProcessors["mpresponse"] = ProcessMpresponseTag;
        T2IParamInput.SpecialParameterHandlers.Add(PromptHandler.NormalizeInlinePostFilters);
        RegisterLateParameterHandler();
    }

    private static string ProcessMppromptTag(string data, T2IPromptHandling.PromptTagContext context)
    {
        var instructionPart = !string.IsNullOrEmpty(context.PreData) ? $"[{context.PreData}]" : "";
        // Enclosing repeat/random tags can cause Swarm to visit an already expanded tag again.
        if (PromptTagSyntax.GetTagVariables(data, context.Input) != null)
        {
            return $"<mpprompt{instructionPart}:{data}>";
        }
        var options = PromptTagOptions.Parse(PromptTagSyntax.DecodeParsedPrompt(context.PreData ?? "", context.Input));
        var outerVariables = context.Variables;
        string token;
        try
        {
            if (options.Variables.Count > 0)
            {
                context.Variables = new(outerVariables);
                foreach (var pair in options.Variables)
                {
                    // Resolve left to right, so later assignments can reference globals or earlier local values.
                    context.Variables[pair.Key] = PromptTagSyntax.ParseVariableValue(pair.Value, context);
                }
            }
            var parsedData = context.Parse(data);
            token = PromptTagSyntax.EncodeParsedPrompt(parsedData, context.Input);
            PromptTagSyntax.SaveTagVariables(token, context.Variables, context.Input);
        }
        finally
        {
            context.Variables = outerVariables;
        }
        if (context.Variables.Count > 0 && context.Input != null)
        {
            context.Input.ExtraMeta["mp_variables"] = context.Variables.ToDictionary(
                pair => pair.Key, pair => PromptTagSyntax.RestoreLiteralBrackets(pair.Value, context.Input));
        }

        return $"<mpprompt{instructionPart}:{token}>";
    }

    private static string ProcessMpresponseTag(string data, T2IPromptHandling.PromptTagContext context)
    {
        var filterPart = !string.IsNullOrEmpty(context.PreData) ? $"[{context.PreData}]" : "";
        return $"<mpresponse{filterPart}:{data}>";
    }

    private static void RegisterLateParameterHandler()
    {
        T2IParamInput.LateSpecialParameterHandlers.Add(userInput =>
        {
            var handler = new PromptHandler(_promptCache, _paramUseCache, _paramModelId, _paramInstructions, _paramPostFilter, _paramThinking, _paramOnError, _paramDisableLlmRequest, _paramDecisionChecks, _paramDecisionQuestion);
            handler.ProcessPrompt(userInput);
        });
    }
}
