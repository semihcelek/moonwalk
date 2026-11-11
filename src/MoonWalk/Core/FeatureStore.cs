using System.Text.Json;
using System.Text.RegularExpressions;

namespace MoonWalk.Core;

/// <summary>
/// A named line of work that several runs build on, one after another, on the same git branch (<c>moonwalk/feature/&lt;name&gt;</c>).
/// It stays Open until a person completes it. Stored as a file (<c>runs/_features/&lt;project&gt;--&lt;name&gt;.json</c>).
/// </summary>
public sealed class Feature
{
    public string Name { get; set; } = "";
    public string Project { get; set; } = "";
    public string Branch { get; set; } = "";

    /// <summary>Open or Complete.</summary>
    public string Status { get; set; } = "Open";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public List<string> Runs { get; set; } = new();

    public bool IsOpen => Status == "Open";
}

public sealed partial class FeatureStore
{
    private static readonly JsonSerializerOptions Options = new(Json.Options) { WriteIndented = true };

    private readonly string _dir;
    private readonly object _lock = new();
    private readonly Dictionary<string, Feature> _features = new(StringComparer.OrdinalIgnoreCase);

    public FeatureStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Feature>(File.ReadAllText(file), Options) is { Name.Length: > 0 } feature)
                {
                    _features[Key(feature.Project, feature.Name)] = feature;
                }
            }
            catch (JsonException)
            {
                // A damaged feature file is ignored rather than stopping MoonWalk.
            }
        }
    }

    /// <summary>Lower-case letters, digits and dashes, at most 40 characters. Empty when nothing usable is left.</summary>
    public static string Slug(string name)
    {
        var slug = NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 40)
        {
            slug = slug[..40].Trim('-');
        }
        return slug;
    }

    public static string BranchFor(string slug) => $"moonwalk/feature/{slug}";

    public IReadOnlyList<Feature> List(string? project = null)
    {
        lock (_lock)
        {
            return _features.Values
                .Where(f => project is null || string.Equals(f.Project, project, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.CreatedAt)
                .ToList();
        }
    }

    public Feature? Get(string project, string name)
    {
        lock (_lock)
        {
            return _features.GetValueOrDefault(Key(project, Slug(name)));
        }
    }

    /// <summary>The open feature with this name, created when it does not exist yet. Throws <see cref="ArgumentException"/> for an unusable name or a completed feature.</summary>
    public Feature Open(string project, string name)
    {
        var slug = Slug(name);
        if (slug.Length == 0)
        {
            throw new ArgumentException($"'{name}' is not a usable feature name (use letters and digits).");
        }
        lock (_lock)
        {
            if (_features.TryGetValue(Key(project, slug), out var existing))
            {
                return existing.IsOpen
                    ? existing
                    : throw new ArgumentException($"Feature '{slug}' is already complete. Pick another name, or reopen it first.");
            }
            var feature = new Feature { Name = slug, Project = project, Branch = BranchFor(slug) };
            _features[Key(project, slug)] = feature;
            Save(feature);
            return feature;
        }
    }

    public void AddRun(string project, string name, string runId)
    {
        lock (_lock)
        {
            if (_features.TryGetValue(Key(project, Slug(name)), out var feature) && !feature.Runs.Contains(runId))
            {
                feature.Runs.Add(runId);
                Save(feature);
            }
        }
    }

    public Feature? SetStatus(string project, string name, bool complete)
    {
        lock (_lock)
        {
            if (!_features.TryGetValue(Key(project, Slug(name)), out var feature))
            {
                return null;
            }
            feature.Status = complete ? "Complete" : "Open";
            feature.CompletedAt = complete ? DateTimeOffset.Now : null;
            Save(feature);
            return feature;
        }
    }

    /// <summary>True when this branch belongs to a feature that is still open.</summary>
    public bool IsOpenBranch(string branch)
    {
        lock (_lock)
        {
            return _features.Values.Any(f => f.IsOpen && f.Branch == branch);
        }
    }

    private static string Key(string project, string slug) => $"{project}--{slug}";

    private void Save(Feature feature)
    {
        var path = Path.Combine(_dir, Key(feature.Project, feature.Name) + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(feature, Options));
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
