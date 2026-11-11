using System.Text;
using System.Text.RegularExpressions;

namespace MoonWalk.Core;

public sealed record BriefTemplate(string Name, string Text);

/// <summary>
/// Reusable briefs: one markdown file per template in the briefs folder. A template is plain text that fills the brief box, so it can
/// say anything a brief can, including "read docs/plans/impact-juice.md first".
/// </summary>
public sealed partial class BriefStore(string dir)
{
    private const int MaxChars = 40_000;
    private readonly object _lock = new();

    public IReadOnlyList<BriefTemplate> List()
    {
        lock (_lock)
        {
            if (!Directory.Exists(dir))
            {
                return [];
            }
            return Directory.EnumerateFiles(dir, "*.md")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new BriefTemplate(Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)))
                .ToList();
        }
    }

    /// <summary>Saves (or replaces) a template. Returns an error message, or null when saved.</summary>
    public string? Save(string name, string text)
    {
        if (!ValidName(name))
        {
            return "A template name uses letters, digits, spaces, dashes and underscores (1 to 60 characters).";
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return "The template is empty.";
        }
        if (text.Length > MaxChars)
        {
            return $"A template can hold at most {MaxChars:N0} characters.";
        }

        lock (_lock)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name.Trim() + ".md"), text, new UTF8Encoding(false));
        }
        return null;
    }

    public bool Delete(string name)
    {
        if (!ValidName(name))
        {
            return false;
        }
        lock (_lock)
        {
            var path = Path.Combine(dir, name.Trim() + ".md");
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
    }

    // The name becomes a file name, so it must not be able to name another folder.
    private static bool ValidName(string name) => NamePattern().IsMatch(name.Trim());

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 _\-]{0,59}$")]
    private static partial Regex NamePattern();
}
