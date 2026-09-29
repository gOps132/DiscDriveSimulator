using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using DiscDriveSimulator;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<DiscDriveSimulator.Services.IDiscGenerator, DiscDriveSimulator.Services.DiscGenerator>();
builder.Services.AddScoped<DiscDriveSimulator.Services.ISimulationService, DiscDriveSimulator.Services.SimulationService>();
builder.Services.AddScoped<DiscDriveSimulator.Services.StorageService>();

await builder.Build().RunAsync();
