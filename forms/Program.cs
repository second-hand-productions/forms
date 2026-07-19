using Anthropic;
using forms.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

// POC persistence: singleton so saved forms survive across requests.
// Replace with a database-backed IFormStore when the POC graduates.
builder.Services.AddSingleton<IFormStore, InMemoryFormStore>();

// AI generation is optional — the app runs fully without a key, and the
// /api/forms/generate endpoint reports 503 rather than the app failing to start.
// Supply the key out-of-band, never in source control:
//   dotnet user-secrets set "Anthropic:ApiKey" "<key>"      (local dev)
//   ANTHROPIC_API_KEY=<key>                                  (container/CI)
var anthropicApiKey = builder.Configuration["Anthropic:ApiKey"]
    ?? builder.Configuration["ANTHROPIC_API_KEY"];

if (!string.IsNullOrWhiteSpace(anthropicApiKey))
{
    builder.Services.AddSingleton(new AnthropicClient { ApiKey = anthropicApiKey });
    builder.Services.AddSingleton<IFormSchemaGenerator, ClaudeFormSchemaGenerator>();
}
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// The app is mounted under /forms/ on the shared nginx root, which forwards the
// prefix rather than stripping it. Must match `base` in clientapp/vite.config.js
// — that one is baked in at build time, so the two are changed together.
// Requests without the prefix (e.g. the container's own /healthz probe) pass
// through untouched.
app.UsePathBase("/forms");

// UseRouting is called explicitly so it lands *after* UsePathBase. WebApplication
// otherwise auto-inserts it at the very start of the pipeline, which would match
// routes against the unstripped path and send /forms/api/... to the SPA fallback.
app.UseRouting();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Only meaningful outside the container: the image listens on plain HTTP
    // and TLS is terminated upstream, so redirecting there would loop.
    app.UseHttpsRedirection();
}

// The published image drops the built Vue app in wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthorization();

app.MapControllers();

app.MapGet("/healthz", () => Results.Ok("ok"));

// Client-side routes fall through to the SPA shell; /api/* is matched above.
app.MapFallbackToFile("index.html");

app.Run();
