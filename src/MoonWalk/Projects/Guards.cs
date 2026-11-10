using System.Text;
using System.Text.RegularExpressions;

namespace MoonWalk.Projects;

/// <summary>Pure checks over the set of paths an agent changed. Paths use forward slashes and are relative to the repo root.</summary>
public static class Guards
{
    public static List<string> Check(IReadOnlyList<string> changed, int changedLines, GuardRules rules)
    {
        var violations = new List<string>();

        if (rules.MaxChangedFiles > 0 && changed.Count > rules.MaxChangedFiles)
        {
            violations.Add($"{changed.Count} files changed, limit is {rules.MaxChangedFiles}.");
        }

        if (rules.MaxChangedLines > 0 && changedLines > rules.MaxChangedLines)
        {
            violations.Add($"{changedLines} lines changed, limit is {rules.MaxChangedLines}.");
        }

        var deny = rules.DenyPaths.Select(ToRegex).ToList();
        var allow = rules.AllowPaths.Select(ToRegex).ToList();

        foreach (var path in changed)
        {
            if (deny.Any(r => r.IsMatch(path)))
            {
                violations.Add($"'{path}' matches a denied path.");
            }
            else if (allow.Count > 0 && !allow.Any(r => r.IsMatch(path)))
            {
                violations.Add($"'{path}' is outside the allowed paths.");
            }
        }

        return violations;
    }

    /// <summary>Glob to regex: <c>**</c> crosses folders, <c>*</c> and <c>?</c> stay inside one segment, a trailing <c>/</c> means "everything below".</summary>
    public static Regex ToRegex(string glob)
    {
        var g = glob.Replace('\\', '/');
        if (g.EndsWith('/'))
        {
            g += "**";
        }

        var sb = new StringBuilder("^");
        for (var i = 0; i < g.Length; i++)
        {
            var c = g[i];
            if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
            {
                var followedBySlash = i + 2 < g.Length && g[i + 2] == '/';
                if (followedBySlash)
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i += 1;
                }
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
