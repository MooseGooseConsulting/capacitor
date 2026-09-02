using Capacitor.Web.Components;
using Capacitor.Web.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.Configure<CapacitorApiOptions>(builder.Configuration.GetSection(CapacitorApiOptions.SectionName));
builder.Services.AddHttpClient<ICapacitorSessionsClient, CapacitorSessionsClient>((services, client) => {
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CapacitorApiOptions>>().Value;
    client.BaseAddress = options.GetBaseAddress();
    client.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
