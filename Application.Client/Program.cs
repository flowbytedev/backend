using Application.Client;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Authorization;
using Application.Shared.Services;
using Application.Shared.Authorization;
using Application.Client.Authorization;


var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.Services.AddFluentUIComponents();

builder.Services.AddAuthorizationCore(options => options.AddFlowbytePolicies());
builder.Services.AddScoped<ICurrentCompanyAccessor, QueryStringCompanyAccessor>();
builder.Services.AddScoped<IAuthorizationHandler, ModuleAccessHandler>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<AuthenticationStateProvider, PersistentAuthenticationStateProvider>();


builder.Services.AddScoped(http => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddScoped<ClientAuthenticationDetail>();
builder.Services.AddScoped<StateContainer>();
builder.Services.AddScoped<Application.Client.Services.ActivityLogClient>();
builder.Services.AddScoped<Application.Client.Services.DebugLogClientService>();

// Status Module Client Services
builder.Services.AddScoped<Application.Client.Services.MonitoredAssetClientService>();
builder.Services.AddScoped<Application.Client.Services.IncidentClientService>();
builder.Services.AddScoped<Application.Client.Services.AssetStatusHistoryClientService>();
builder.Services.AddScoped<Application.Client.Services.ServerManagementClientService>();
builder.Services.AddScoped<Application.Client.Services.EntityAudienceClientService>();
builder.Services.AddScoped<Application.Client.Services.PowerBiClientService>();
builder.Services.AddScoped<Application.Client.Services.StatusOverviewClientService>();
builder.Services.AddScoped<Application.Client.Services.DatabaseTableClientService>();

await builder.Build().RunAsync();
