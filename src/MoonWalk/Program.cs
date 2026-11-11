using MoonWalk.Agents;
using MoonWalk.Core;
using MoonWalk.Projects;
using MoonWalk.Web;
using MoonWalk.Workspace;

// Dollar amounts and token counts in messages must read the same on every machine (no decimal commas).
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://localhost:5870");
}

var paths = AppPaths.From(builder.Configuration, builder.Environment.ContentRootPath);
var runsRoot = paths.RunsRoot;
var projectsDir = paths.ProjectsDir;
var agentsFile = paths.AgentsFile;

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var agents = new AgentRegistry(agentsFile);
agents.Reload();
var registry = new ProjectRegistry(projectsDir, agents);
registry.Reload();

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(new PresetStore(paths.PresetsFile));
builder.Services.AddSingleton(new BriefStore(paths.BriefsDir));
builder.Services.AddSingleton(agents);
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(new EventHub(runsRoot));
builder.Services.AddSingleton(new RunStore(runsRoot));
builder.Services.AddSingleton(sp => new ReviewService(
    sp.GetRequiredService<ProjectRegistry>(),
    sp.GetRequiredService<RunStore>(),
    sp.GetRequiredService<EventHub>()));
builder.Services.AddSingleton(new FeatureStore(Path.Combine(paths.RunsRoot, "_features")));
builder.Services.AddSingleton(sp => new RunEngine(
    sp.GetRequiredService<RunStore>(),
    sp.GetRequiredService<EventHub>(),
    sp.GetRequiredService<ProjectRegistry>(),
    sp.GetRequiredService<AgentRegistry>(),
    sp.GetRequiredService<ReviewService>(),
    sp.GetRequiredService<FeatureStore>(),
    runsRoot));

builder.Services.AddSingleton(sp => new BatchService(
    sp.GetRequiredService<RunStore>(),
    sp.GetRequiredService<RunEngine>(),
    sp.GetRequiredService<ProjectRegistry>(),
    Path.Combine(paths.RunsRoot, "_batches")));

var app = builder.Build();
app.Services.GetRequiredService<BatchService>(); // load saved batches now, so interrupted ones are marked at startup

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapApi();

app.Run();
