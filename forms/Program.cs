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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
