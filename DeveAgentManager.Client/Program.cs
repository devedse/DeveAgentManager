using DeveAgentManager.Client.Services;
using DeveAgentManager.Contracts;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// One of each for the whole app (the browser tab): the live connection is shared by all pages.
builder.Services.AddSingleton(_ =>
{
    // Same origin as the Manager: the login cookie goes along with every request.
    var http = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) };
    http.DefaultRequestHeaders.Add(ApiHeaders.Request, "1");
    return http;
});
builder.Services.AddSingleton<ManagerApi>();
builder.Services.AddSingleton<LiveUpdates>();
builder.Services.AddSingleton<SignedInUser>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.PreventDuplicates = true;
    config.SnackbarConfiguration.VisibleStateDuration = 5000;
});

var host = builder.Build();
_ = host.Services.GetRequiredService<LiveUpdates>().StartAsync();
await host.RunAsync();
