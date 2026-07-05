using Quince.Service;
using Quince.Service.Configuration;
using Quince.Service.Services;

var builder = WebApplication.CreateBuilder(args);

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

var configDir = PathResolver.Resolve(builder.Configuration["ConfigDir"], "config");
var logDir = PathResolver.Resolve(builder.Configuration["LogDir"], "log");
var appConfig = new YamlConfigLoader().LoadApp(configDir);
builder.Logging.AddProvider(new FileLoggerProvider(logDir, appConfig));

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
