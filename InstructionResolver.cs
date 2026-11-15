using FreneticUtilities.FreneticExtensions;
using Hartsy.Extensions.MagicPromptExtension.WebAPI;
using Newtonsoft.Json.Linq;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension;

public static class InstructionResolver
{
    public static string Resolve(T2IParamInput userInput, string instructionId, T2IRegisteredParam<string> paramInstructions, IReadOnlyDictionary<string, string> variables = null)
    {
        return Resolve(GetInstructionsObject(), userInput, instructionId, paramInstructions, variables);
    }

    internal static string Resolve(JToken instructionsObj, T2IParamInput userInput, string instructionId, T2IRegisteredParam<string> paramInstructions, IReadOnlyDictionary<string, string> variables)
    {
        var instructionKey = GetInstructionKey(userInput, instructionId, paramInstructions);

        Logs.Debug($"MagicPromptExtension.InstructionResolver: instructionId='{instructionId ?? "(null)"}', using='{instructionKey}'");

        return ResolveContent(instructionsObj, instructionKey, userInput, variables);
    }

    internal static string ResolveContent(JToken instructionsObj, string instructionKey, T2IParamInput userInput, IReadOnlyDictionary<string, string> variables = null)
    {
        if (instructionsObj == null)
        {
            return SubstituteVariables(instructionKey, userInput, variables);
        }

        if (string.IsNullOrWhiteSpace(instructionKey))
        {
            return SubstituteVariables(GetDefaultInstruction(instructionsObj), userInput, variables);
        }

        var content = TryGetCustomInstructionByKey(instructionsObj, instructionKey)
            ?? TryGetCustomInstructionByTitle(instructionsObj, instructionKey)
            ?? TryGetBaseInstruction(instructionsObj, instructionKey)
            ?? GetDefaultInstruction(instructionsObj);
        return SubstituteVariables(content, userInput, variables);
    }

    /// <summary>
    /// Substitutes &lt;var:name&gt; tags in instructions with variable values from the prompt.
    /// Variables are set in the prompt using &lt;setvar[name]:value&gt; syntax.
    /// </summary>
    public static string SubstituteVariables(string instructions, T2IParamInput userInput, IReadOnlyDictionary<string, string> variables = null)
    {
        if (string.IsNullOrWhiteSpace(instructions) || !instructions.Contains("<var:"))
        {
            return instructions;
        }

        if (variables == null && userInput.ExtraMeta.TryGetValue("mp_variables", out object variablesObj))
        {
            variables = variablesObj as IReadOnlyDictionary<string, string>;
        }
        if (variables == null || variables.Count == 0)
        {
            return instructions;
        }

        return FreneticUtilities.FreneticToolkit.StringConversionHelper.QuickSimpleTagFiller(
            instructions, "<", ">", tag =>
            {
                string prefix = tag.BeforeAndAfter(':', out string varName);
                if (prefix.ToLowerInvariant() != "var")
                {
                    return $"<{tag}>";
                }

                if (variables.TryGetValue(varName, out string value))
                {
                    Logs.Debug($"MagicPromptExtension.InstructionResolver: substituted <var:{varName}> ({value?.Length ?? 0} chars)");
                    return value;
                }

                Logs.Warning($"MagicPromptExtension.InstructionResolver: variable '{varName}' not found for substitution");
                return $"<var:{varName}>";
            }, false, 0);
    }

    private static string GetInstructionKey(T2IParamInput userInput, string instructionId, T2IRegisteredParam<string> paramInstructions)
    {
        return !string.IsNullOrWhiteSpace(instructionId) ? instructionId : userInput.Get(paramInstructions);
    }

    private static JToken GetInstructionsObject()
    {
        var sessionSettings = SessionSettings.GetMagicPromptSettings()
            .GetAwaiter()
            .GetResult();

        if (sessionSettings?["success"]?.Value<bool>() != true)
        {
            return null;
        }

        return sessionSettings["settings"]?["instructions"];
    }

    private static string GetDefaultInstruction(JToken instructionsObj)
    {
        Logs.Debug("MagicPromptExtension.InstructionResolver: using default \"prompt\" instruction");
        return instructionsObj["prompt"]?.ToString();
    }

    private static string TryGetCustomInstructionByKey(JToken instructionsObj, string key)
    {
        var customContent = instructionsObj["custom"]?[key]?["content"]?.ToString();
        if (string.IsNullOrWhiteSpace(customContent))
        {
            return null;
        }

        var title = instructionsObj["custom"]?[key]?["title"]?.ToString();
        Logs.Debug($"MagicPromptExtension.InstructionResolver: using custom instruction \"{title}\" (matched by key)");
        return customContent;
    }

    private static string TryGetCustomInstructionByTitle(JToken instructionsObj, string titleToMatch)
    {
        if (instructionsObj["custom"] is not JObject customObj)
        {
            return null;
        }

        foreach (var prop in customObj.Properties())
        {
            var title = prop.Value?["title"]?.ToString();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            if (!string.Equals(title, titleToMatch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = prop.Value?["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            Logs.Debug($"MagicPromptExtension.InstructionResolver: using custom instruction \"{title}\" (matched by title)");
            return content;
        }

        return null;
    }

    private static string TryGetBaseInstruction(JToken instructionsObj, string key)
    {
        var baseContent = instructionsObj[key]?.ToString();
        if (string.IsNullOrWhiteSpace(baseContent))
        {
            return null;
        }

        Logs.Debug($"MagicPromptExtension.InstructionResolver: using base instruction \"{key}\"");
        return baseContent;
    }
}
