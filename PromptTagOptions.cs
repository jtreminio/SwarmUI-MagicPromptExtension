using System.Text.RegularExpressions;
using SwarmUI.Utils;

namespace Hartsy.Extensions.MagicPromptExtension;

/// <summary>Separates legacy positional options from tag-local variable assignments, preserving nested Swarm tags.</summary>
internal sealed class PromptTagOptions
{
    internal string Positional;
    internal Dictionary<string, string> Variables = new();

    internal static PromptTagOptions Parse(string header)
    {
        header ??= "";
        List<string> parts = new();
        bool quoted = false;
        int start = 0;
        for (int i = 0; i < header.Length; i++)
        {
            if (header[i] == '\\' && i + 1 < header.Length && (header[i + 1] is '<' or '>' || quoted && header[i + 1] is '"' or '\\'))
            {
                i++;
            }
            else if (!quoted && header[i] == '<')
            {
                int tagEnd = PromptTagSyntax.FindNestedTagEnd(header, i);
                if (tagEnd >= 0)
                {
                    i = tagEnd;
                }
            }
            else if (header[i] == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && (header[i] == ';' || parts.Count > 0 && header[i] == ','
                && Regex.IsMatch(header[(i + 1)..], @"^\s*[A-Za-z_][A-Za-z0-9_.-]*\s*=")))
            {
                parts.Add(header[start..i]);
                start = i + 1;
            }
        }
        if (quoted)
        {
            throw Invalid("Unclosed quoted value.");
        }
        parts.Add(header[start..]);
        PromptTagOptions options = new() { Positional = parts[0].Trim() };
        foreach (string part in parts.Skip(1))
        {
            int equals = part.IndexOf('=');
            if (equals < 1)
            {
                throw Invalid("Expected name=value after each semicolon.");
            }
            string name = part[..equals].Trim();
            string value = part[(equals + 1)..].Trim();
            if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.-]*$"))
            {
                throw Invalid($"Invalid variable name '{name}'.");
            }
            if (value.StartsWith('"'))
            {
                if (!Regex.IsMatch(value, "^\"(?:\\\\.|[^\"\\\\])*\"$"))
                {
                    throw Invalid($"Invalid quoted value for '{name}'.");
                }
                value = Regex.Replace(value[1..^1], @"\\([""\\])", "$1");
            }
            else if (value.Length == 0)
            {
                throw Invalid($"Use name=\"\" for an empty value, or quote the entire value for '{name}'.");
            }
            if (!options.Variables.TryAdd(name, value))
            {
                throw Invalid($"Duplicate variable '{name}'.");
            }
        }
        return options;
    }

    private static SwarmReadableErrorException Invalid(string reason) => new($"MagicPrompt: invalid tag options. {reason}");
}
