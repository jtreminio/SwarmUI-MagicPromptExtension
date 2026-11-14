using System.Collections.Immutable;
using System.Text.RegularExpressions;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension;

/// <summary>Protects literal angle brackets while Swarm expands tags inside MagicPrompt content.</summary>
internal static class PromptTagSyntax
{
    internal static readonly Regex MppromptHeaderRegex = new(@"<mpprompt(?:\[((?:[^""\[\]]|""(?:\\.|[^""\\])*""|(?<optionBracket>\[)|(?<-optionBracket>\]))*(?(optionBracket)(?!)))\])?(?:\|\s*((?:""[^""]+""\s*)(?:,\s*""[^""]+""\s*)*))?:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Pre-data may itself contain tags, for example <repeat[<var:count>]:text>.
    private static readonly Regex TagHeaderRegex = new(@"\G<([a-zA-Z][a-zA-Z0-9_-]*)(?:\[(?:[^\[\]]|(?<bracket>\[)|(?<-bracket>\]))*(?(bracket)(?!))\])?(?::|(?=>))", RegexOptions.Compiled);
    private static readonly Regex GreaterThanEmoteRegex = new(@"\G>[:;=8xX][-^']?[()\[\]DPp/\\|oO]", RegexOptions.Compiled);
    private const string LiteralEscapeKey = "mp_literal_escape";
    private const string ParsedPromptsKey = "mp_parsed_prompts";
    private const string TagVariablesKey = "mp_tag_variables";
    private const string ExplicitMppromptClose = "></mpprompt>";

    internal static void ValidateHeaders(string prompt)
    {
        int end = 0;
        foreach (Match opening in Regex.Matches(prompt, @"<mpprompt\[", RegexOptions.IgnoreCase))
        {
            if (opening.Index < end)
            {
                continue;
            }
            Match header = MppromptHeaderRegex.Match(prompt, opening.Index);
            if (!header.Success || header.Index != opening.Index)
            {
                throw new SwarmReadableErrorException("MagicPrompt: invalid tag options. Close the bracket and any quoted value, then add ':' before the prompt.");
            }
            end = header.Index + header.Length;
        }
    }

    /// <summary>
    /// Accepts &gt;&lt;/mpprompt&gt; for ambiguous content, otherwise keeps the first closing bracket
    /// that is not part of an emote. Registered nested tags retain their delimiters.
    /// </summary>
    internal static string ProtectLiteralBrackets(string prompt, T2IParamInput input)
    {
        string escape = input.ExtraMeta.TryGetValue(LiteralEscapeKey, out object existing) ? (string)existing : $"__mp_literal_{Guid.NewGuid():N}_";
        StringBuilder result = new();
        int start = 0;
        foreach (Match header in MppromptHeaderRegex.Matches(prompt))
        {
            if (header.Index < start)
            {
                continue;
            }
            int contentStart = header.Index + header.Length;
            int end = FindMppromptEnd(prompt, contentStart, out int closingLength);
            if (end < 0)
            {
                continue;
            }
            result.Append(prompt, start, contentStart - start);
            ProtectContent(prompt, contentStart, end, escape, result);
            result.Append('>');
            start = end + closingLength;
        }
        result.Append(prompt, start, prompt.Length - start);
        string protectedPrompt = result.ToString();
        if (protectedPrompt != prompt)
        {
            input.ExtraMeta[LiteralEscapeKey] = escape;
        }
        return protectedPrompt;
    }

    private static int FindMppromptEnd(string prompt, int start, out int closingLength)
    {
        int end = -1;
        closingLength = 1;
        for (int i = start; i < prompt.Length; i++)
        {
            if (prompt[i] == '\\' && i + 1 < prompt.Length && prompt[i + 1] is '<' or '>')
            {
                i++;
            }
            else if (prompt[i] == '<')
            {
                if (prompt.AsSpan(i).StartsWith("<mpprompt", StringComparison.OrdinalIgnoreCase)
                    && MppromptHeaderRegex.Match(prompt, i) is { Success: true } nextPrompt && nextPrompt.Index == i)
                {
                    break;
                }
                int nestedEnd = FindNestedTagEnd(prompt, i);
                if (nestedEnd >= 0)
                {
                    i = nestedEnd;
                }
            }
            else if (prompt[i] == '>')
            {
                if (prompt.AsSpan(i).StartsWith(ExplicitMppromptClose, StringComparison.OrdinalIgnoreCase))
                {
                    closingLength = ExplicitMppromptClose.Length;
                    return i;
                }
                if (end < 0 && !IsGreaterThanEmote(prompt, i))
                {
                    end = i;
                }
            }
        }
        return end;
    }

    private static bool IsGreaterThanEmote(string prompt, int index)
    {
        Match emote = GreaterThanEmoteRegex.Match(prompt, index);
        return emote.Success && emote.Index == index;
    }

    private static bool IsRegisteredPrefix(string prefix)
    {
        prefix = prefix.ToLowerInvariant();
        return T2IPromptHandling.PromptTagBasicProcessors.ContainsKey(prefix)
            || T2IPromptHandling.PromptTagProcessors.ContainsKey(prefix)
            || T2IPromptHandling.PromptTagPostProcessors.ContainsKey(prefix)
            || PromptRegion.CustomPartPrefixes.Contains(prefix)
            || prefix == "mporiginal";
    }

    internal static int FindNestedTagEnd(string prompt, int start, int depth = 0)
    {
        if (depth > 1000)
        {
            return -1;
        }
        Match header = TagHeaderRegex.Match(prompt, start);
        if (!header.Success || !IsRegisteredPrefix(header.Groups[1].Value))
        {
            return -1;
        }
        for (int i = start + header.Length; i < prompt.Length; i++)
        {
            if (prompt[i] == '\\' && i + 1 < prompt.Length && prompt[i + 1] is '<' or '>')
            {
                i++;
            }
            else if (prompt[i] == '<')
            {
                int nestedEnd = FindNestedTagEnd(prompt, i, depth + 1);
                if (nestedEnd >= 0)
                {
                    i = nestedEnd;
                }
            }
            else if (prompt[i] == '>' && !IsGreaterThanEmote(prompt, i))
            {
                return i;
            }
        }
        return -1;
    }

    private static void ProtectContent(string prompt, int start, int end, string escape, StringBuilder result)
    {
        for (int i = start; i < end; i++)
        {
            if (prompt[i] == '\\' && i + 1 < end && prompt[i + 1] is '<' or '>')
            {
                result.Append(escape).Append(prompt[++i] == '<' ? "lt__" : "gt__");
            }
            else if (prompt[i] == '<')
            {
                int nestedEnd = FindNestedTagEnd(prompt, i);
                if (nestedEnd >= 0 && nestedEnd < end)
                {
                    Match header = TagHeaderRegex.Match(prompt, i);
                    result.Append(header.Value);
                    ProtectContent(prompt, i + header.Length, nestedEnd, escape, result);
                    result.Append('>');
                    i = nestedEnd;
                }
                else
                {
                    result.Append(escape).Append("lt__");
                }
            }
            else if (prompt[i] == '>')
            {
                result.Append(escape).Append("gt__");
            }
            else
            {
                result.Append(prompt[i]);
            }
        }
    }

    internal static string RestoreLiteralBrackets(string text, T2IParamInput input)
    {
        if (input.ExtraMeta.TryGetValue(LiteralEscapeKey, out object value) && value is string escape)
        {
            return text.Replace(escape + "lt__", "<").Replace(escape + "gt__", ">");
        }
        return text;
    }

    /// <summary>Expands a local assignment once, using the same literal-bracket protection as prompt content.</summary>
    internal static string ParseVariableValue(string text, T2IPromptHandling.PromptTagContext context)
    {
        string escape = context.Input.ExtraMeta.TryGetValue(LiteralEscapeKey, out object value)
            ? (string)value : $"__mp_literal_{Guid.NewGuid():N}_";
        StringBuilder protectedValue = new();
        ProtectContent(text, 0, text.Length, escape, protectedValue);
        string protectedText = protectedValue.ToString();
        if (protectedText != text)
        {
            context.Input.ExtraMeta[LiteralEscapeKey] = escape;
        }
        return context.Parse(protectedText);
    }

    internal static void ClearParsingState(T2IParamInput input)
    {
        input.ExtraMeta.Remove(LiteralEscapeKey);
        input.ExtraMeta.Remove(ParsedPromptsKey);
        input.ExtraMeta.Remove(TagVariablesKey);
    }

    internal static void SaveTagVariables(string token, Dictionary<string, string> variables, T2IParamInput input)
    {
        if (!input.ExtraMeta.TryGetValue(TagVariablesKey, out object value)
            || value is not Dictionary<string, Dictionary<string, string>> snapshots)
        {
            snapshots = new();
            input.ExtraMeta[TagVariablesKey] = snapshots;
        }
        snapshots[token] = variables.ToDictionary(pair => pair.Key, pair => RestoreLiteralBrackets(pair.Value, input));
    }

    internal static IReadOnlyDictionary<string, string> GetTagVariables(string token, T2IParamInput input)
    {
        return input.ExtraMeta.TryGetValue(TagVariablesKey, out object value)
            && value is Dictionary<string, Dictionary<string, string>> snapshots
            && snapshots.TryGetValue(token, out var variables) ? variables : null;
    }

    /// <summary>Keeps expanded wildcard/variable text opaque until the late LLM handler reads it.</summary>
    internal static string EncodeParsedPrompt(string text, T2IParamInput input)
    {
        var encodedPrompts = input.ExtraMeta.GetValueOrDefault(ParsedPromptsKey) as ImmutableDictionary<string, string>
            ?? ImmutableDictionary<string, string>.Empty;
        if (encodedPrompts.ContainsKey(text))
        {
            return text;
        }
        string encoded = $"__mp_parsed_prompt_{Guid.NewGuid():N}__";
        // Batch inputs share ExtraMeta values through a shallow copy. Replace the map on this input only.
        input.ExtraMeta[ParsedPromptsKey] = encodedPrompts.Add(encoded, text);
        return encoded;
    }

    internal static string DecodeParsedPrompt(string text, T2IParamInput input)
    {
        if (input.ExtraMeta.TryGetValue(ParsedPromptsKey, out object value)
            && value is ImmutableDictionary<string, string> encodedPrompts && encodedPrompts.TryGetValue(text, out string decoded))
        {
            text = decoded;
        }
        return RestoreLiteralBrackets(text, input);
    }

    /// <summary>Restores tokens in prompt fields that the late LLM handler does not process.</summary>
    internal static string RestoreParsedPrompts(string text, T2IParamInput input)
    {
        if (input.ExtraMeta.TryGetValue(ParsedPromptsKey, out object value)
            && value is ImmutableDictionary<string, string> encodedPrompts)
        {
            foreach (var pair in encodedPrompts)
            {
                text = text.Replace(pair.Key, pair.Value);
            }
        }
        return RestoreLiteralBrackets(text, input);
    }
}
