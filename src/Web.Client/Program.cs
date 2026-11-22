using Web.Client.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpClient("VideoApi", client =>
{
    var apiBaseUrl = builder.Configuration["VideoApi:BaseUrl"]
        ?? throw new InvalidOperationException("VideoApi:BaseUrl is required.");
    client.BaseAddress = new Uri(apiBaseUrl);
});
builder.Services.AddScoped(serviceProvider =>
    serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("VideoApi"));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
