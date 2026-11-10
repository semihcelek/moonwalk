using MoonWalk.Core;
using MoonWalk.Projects;
using MoonWalk.Web;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://localhost:5870");
}

var runsRoot = Path.GetFullPath(builder.Configuration["MoonWalk:DataDir"] ?? "runs");
var projectsDir = Path.GetFullPath(builder.Configuration["MoonWalk:ProjectsDir"] ?? "projects");

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var registry = new ProjectRegistry(projectsDir);
registry.Reload();

builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(new EventHub(runsRoot));
builder.Services.AddSingleton(new RunStore(runsRoot));
builder.Services.AddSingleton(sp => new RunEngine(
    sp.GetRequiredService<RunStore>(),
    sp.GetRequiredService<EventHub>(),
    sp.GetRequiredService<ProjectRegistry>(),
    runsRoot));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapApi();

app.Run();
