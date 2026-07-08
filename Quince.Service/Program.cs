using Quince.Service;
using Quince.Service.Configuration;
using Quince.Service.Services;

// Every running channel keeps several long-lived background loops going at once (capture,
// AudioWriter, LevelMeter, SilenceDetector, MetadataReader, ChannelEngine.MonitorAsync polling
// every 500ms) — with several real-world channels running simultaneously, plus Blazor Server's own
// SignalR circuit dispatch, that's dozens of Tasks contending for the thread pool. The pool's
// default ramp-up (one new thread roughly every 500ms under sustained demand) can leave a burst of
// simultaneously-ready continuations queued for a moment — observed as UI indicators freezing and
// monitored playback audio glitching at the very same moments, even for a plain Icecast source with
// no HLS segment jitter to blame. Raise the floor so the pool starts warm instead of ramping up
// under load. 64 was sized for "several" channels; real-world deployments run 15-30 channels at
// once (~5-7 background Tasks each = 100-200+), so raised to 256 for headroom at the top of that
// range plus room to grow.
ThreadPool.SetMinThreads(256, 256);

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
builder.Services.AddSingleton<AudioPlaybackService>();

var configDir = PathResolver.Resolve(builder.Configuration["ConfigDir"], "config");
var logDir = PathResolver.Resolve(builder.Configuration["LogDir"], "log");
var appConfig = new YamlConfigLoader().LoadApp(configDir);
var fileLoggerProvider = new FileLoggerProvider(logDir, appConfig);
builder.Services.AddSingleton(fileLoggerProvider);
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton(sp => new LocalizationService(
    sp.GetRequiredService<AppSettingsService>(),
    Path.Combine(AppContext.BaseDirectory, "i18n")));
builder.Logging.AddProvider(fileLoggerProvider);

var app = builder.Build();

app.Logger.LogInformation("Айва (Quince) запущена, версия {Version}", VersionInfo.Version);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();

app.MapGet("/api/playback/stream/{channelName}", AudioStreamEndpoint.StreamAsync);

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
