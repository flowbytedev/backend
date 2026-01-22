using Application.Client.Pages;
using Application.Components;
using Application.Components.Account;
using Application.Helpers;
using Application.Hubs;
using Application.Services;
using Application.Services.Data;
using Application.Shared.Data;
using Application.Shared.Models;
using Application.Shared.Models.Data;
using Application.Shared.Models.User;
using Application.Shared.Services;
using Application.Shared.Services.Data;
using Application.Shared.Services.Org;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;



var builder = WebApplication.CreateBuilder(args);

AppContext.SetSwitch("DuckDB.NET.Native.DisableLibraryLoad", false);


builder.Services.AddControllers();


// Add services to the container.
builder.Services.AddRazorComponents()
    .AddAuthenticationStateSerialization()
    .AddInteractiveWebAssemblyComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy
            .AllowAnyOrigin()     // or .WithOrigins("https://your-frontend.com")
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});


builder.Services.AddFluentUIComponents();

builder.Services.AddCascadingAuthenticationState();
//builder.Services.AddAuthorizationCore();

builder.Services.AddScoped<IdentityUserAccessor>();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, PersistingServerAuthenticationStateProvider>();

builder.Services.AddApiAuthorization();

const string MS_OIDC_SCHEME = "MicrosoftOidc";

builder.Services.AddAuthorization();
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;


    }).AddCookie("Identity.Application")
    .AddCookie("Identity.External")
    //.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme)

    .AddOpenIdConnect(MS_OIDC_SCHEME, displayName: "Continue with Microsoft" , options =>
    {

        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.ClientId = builder.Configuration["AzureAd:ClientId"];
        options.ClientSecret = builder.Configuration["AzureAd:ClientSecret"];
        options.Authority = builder.Configuration["AzureAd:Authority"];
        options.MetadataAddress = builder.Configuration["AzureAd:MetadataAddress"];
        options.CallbackPath = builder.Configuration["AzureAd:CallbackPath"];
        options.RequireHttpsMetadata = false;

        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;

        options.SignedOutRedirectUri = builder.Configuration["AzureAd:SignedOutRedirectUri"];
        options.SignedOutCallbackPath = builder.Configuration["AzureAd:SignedOutCallbackPath"];
        options.ResponseType = OpenIdConnectResponseType.Code;


        // .NET 9 feature
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.TokenValidationParameters.NameClaimType = JwtRegisteredClaimNames.Name;
        options.TokenValidationParameters.RoleClaimType = "role";

        // CRITICAL: Use Object ID (oid) as NameIdentifier instead of sub
        options.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = context =>
            {
                var identity = context.Principal.Identity as ClaimsIdentity;
                
                if (identity != null)
                {
                    // Get the Object ID claim
                    var oidClaim = context.Principal.FindFirst(
                        "http://schemas.microsoft.com/identity/claims/objectidentifier");
                    
                    if (oidClaim != null)
                    {
                        // Remove existing NameIdentifier (sub claim)
                        var existingNameId = identity.FindFirst(ClaimTypes.NameIdentifier);
                        if (existingNameId != null)
                        {
                            identity.RemoveClaim(existingNameId);
                        }
                        
                        // Add Object ID as NameIdentifier
                        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, oidClaim.Value));
                    }
                }
                
                return Task.CompletedTask;
            }
        };

    });



var connectionString = builder.Configuration.GetConnectionString("ApplicationDbContext") ?? throw new InvalidOperationException("Connection string 'ApplicationDbContext' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString, b => b.MigrationsAssembly("Application")));


var userManagementConnectionString = builder.Configuration.GetConnectionString("UserManagementDbContext") ?? throw new InvalidOperationException("Connection string 'ApplicationDbContext' not found.");
builder.Services.AddDbContext<UserManagementDbContext>(options =>
    options.UseSqlServer(userManagementConnectionString, b => b.MigrationsAssembly("Application")));


// Add Data Warehouse DbContext
var dataWarehouseConnectionString = builder.Configuration.GetConnectionString("DataWarehouseDbContext");
if (!string.IsNullOrEmpty(dataWarehouseConnectionString))
{
    builder.Services.AddDbContext<DataWarehouseDbContext>(options =>
        options.UseSqlServer(dataWarehouseConnectionString));
}

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<UserManagementDbContext>()
    .AddSignInManager()
    .AddRoles<IdentityRole>()
    .AddRoleManager<RoleManager<IdentityRole>>()
    .AddRoleStore<RoleStore<IdentityRole, UserManagementDbContext>>()
    .AddUserStore<UserStore<ApplicationUser, IdentityRole, UserManagementDbContext>>()
    .AddDefaultTokenProviders();


// Add services to the container.
builder.Services.AddMemoryCache();

// Bind DuckdbOptions
var duckdbOption = new DuckdbOption();
builder.Configuration.Bind("Duckdb", duckdbOption);
// Register with DI
builder.Services.AddSingleton(duckdbOption);


builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

builder.Services.AddScoped<StateContainer>();
builder.Services.AddScoped<ClientAuthenticationDetail>();

builder.Services.AddScoped<ICompanyService, CompanyService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IDatasetService, DatasetService>();
builder.Services.AddScoped<IDuckdbService, DuckdbService>();
builder.Services.AddScoped<ICommentService, CommentService>();
builder.Services.AddScoped<IUserPreferencesService, UserPreferencesService>();
builder.Services.AddScoped<IUserSearchService, UserSearchService>();

// Metrics Services
builder.Services.AddScoped<IMetricService, MetricService>();
builder.Services.AddScoped<IMetricTargetService, MetricTargetService>();
builder.Services.AddScoped<IMetricValueService, MetricValueService>();
builder.Services.AddScoped<IClickHouseService, ClickHouseService>();

// Add Data Warehouse Service
builder.Services.AddScoped<DataWarehouseService>();

// Add Dataset Sharing Services
builder.Services.AddScoped<IDatasetSharingService, DatasetSharingService>();
builder.Services.AddScoped<Application.Shared.Services.Data.IEmailNotificationService, Application.Services.Data.EmailNotificationService>();

// Add Real-Time Data Service
builder.Services.AddScoped<IRealTimeDataService, RealTimeDataService>();
builder.Services.AddScoped<ISalesDataSignalRService, SalesDataSignalRService>();
builder.Services.AddScoped<ISalesDashboardService, SalesDashboardService>();

// Add Email Helper
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));
builder.Services.AddScoped<EmailHelper>();

// Add Chat Service for AI functionality
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IChatMessageRepository, InMemoryChatMessageRepository>();

// Configure Azure OpenAI settings
builder.Services.Configure<AzureOpenAIConfiguration>(builder.Configuration.GetSection("AzureOpenAI"));

// Add EmailSettings configuration
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));

// Add HTTP client factory
builder.Services.AddHttpClient();

// Register EmailHelper as a singleton
builder.Services.AddSingleton<EmailHelper>();


builder.Services.Configure<IdentityOptions>(options =>
{
    options.ClaimsIdentity.UserIdClaimType = ClaimTypes.NameIdentifier;
    options.ClaimsIdentity.UserNameClaimType = ClaimTypes.Name;
    options.ClaimsIdentity.RoleClaimType = ClaimTypes.Role;
    //options.ClaimsIdentity.EmailClaimType = ClaimTypes.Email;
    //options.User.RequireUniqueEmail = true;

});

// get the uri from the appsettings.json
var uri = builder.Configuration["BaseUri"];
//// Configure the HttpClient to include the user's access token when calling the API
builder.Services.AddHttpClient("Application.ServerAPI", client => client.BaseAddress = new Uri(uri));

builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("Application.ServerAPI"));


builder.Services.AddSignalR();

builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/octet-stream"]);
});


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.Use((context, next) =>
{
    context.Request.Scheme = "https";
    return next();
});

app.UseHttpsRedirection();

app.MapControllers();

app.UseStaticFiles();
app.UseAntiforgery();

app.UseCors("AllowAll");

app.MapHub<NotificationHub<DataJob>>("/notification/datajob");
app.MapHub<SalesDataHub>("/realtime/salesdata");
app.UseResponseCompression();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(Application.Client._Imports).Assembly);

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();
