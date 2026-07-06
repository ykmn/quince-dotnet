using Quince.Service;
using Quince.Service.Configuration;
using Quince.Service.Services;

// WebApplication.CreateBuilder(args) defaults ContentRootPath (and therefore WebRootPath =
// ContentRootPath/wwwroot) to Directory.GetCurrentDirectory() — the process's *working* directory,
// not the folder the exe/dll actually lives in. That's fine for `dotnet run` (CWD == project folder)
// but breaks static file serving (app.css, icon.svg, goniometer.js — the whole UI look) the moment
// CWD differs, which is exactly what happens when this runs as an installed Windows Service (CWD
// defaults to System32). ConfigDir/LogDir already anchor to AppContext.BaseDirectory via
// PathResolver for the same reason — do the same here for the built-in content/web root.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "QuinceAudioLogger";
});

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

builder.Services.AddSingleton<YamlConfigLoader>();
builder.Services.AddSingleton<ChannelManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ChannelManager>());

builder.Services.AddSingleton<AudioEngineManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioEngineManager>());

builder.Services.AddScoped<ChannelUiState>();
builder.Services.AddSingleton<FolderBrowserService>();
builder.Services.AddSingleton<MetadataDetectionService>();

var configDir = PathResolver.Resolve(builder.Configuration["ConfigDir"], "config");
var logDir = PathResolver.Resolve(builder.Configuration["LogDir"], "log");
var appConfig = new YamlConfigLoader().LoadApp(configDir);
var fileLoggerProvider = new FileLoggerProvider(logDir, appConfig);
builder.Services.AddSingleton(fileLoggerProvider);
builder.Services.AddSingleton<AppSettingsService>();
builder.Logging.AddProvider(fileLoggerProvider);

var app = builder.Build();

app.Logger.LogInformation("Айва (Quince) запущена, версия {Version}", VersionInfo.Version);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
