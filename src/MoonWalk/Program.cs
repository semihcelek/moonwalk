using MoonWalk.Core;
using MoonWalk.Web;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://localhost:5870");
}

var runsRoot = Path.GetFullPath(builder.Configuration["MoonWalk:DataDir"] ?? "runs");

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Options.PropertyNamingPolicy;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddSingleton(new EventHub(runsRoot));
builder.Services.AddSingleton(new RunStore(runsRoot));
builder.Services.AddSingleton(sp => new RunEngine(sp.GetRequiredService<RunStore>(), sp.GetRequiredService<EventHub>(), runsRoot));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.MapApi();

app.Run();
