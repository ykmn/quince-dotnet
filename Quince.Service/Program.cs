using Quince.Service;
using Quince.Service.Configuration;
using Quince.Service.Services;
using Quince.Service.Services.Auth;

if (args.Contains("--hash-password"))
{
    RunHashPasswordMode();
    return;
}

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

// Defaults (3 min circuit retention, 30s client timeout) are tuned for a page someone is actively
// looking at — a backgrounded/idle browser tab (throttled JS timers, laptop sleep, a brief network
// blip) routinely exceeds them, at which point the server has already discarded the circuit and no
// amount of client-side retrying (_Host.cshtml's Blazor.start) can recover it — only a manual page
// reload gets a fresh one. Raised here so a tab left open for a while reconnects on its own instead
// of getting stuck on "Reconnecting...".
builder.Services.AddServerSideBlazor(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(15);
}).AddHubOptions(options =>
{
    // SignalR requires ClientTimeoutInterval >= 2x KeepAliveInterval.
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

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
builder.Services.AddSingleton<LdapAuthenticator>();
builder.Services.AddSingleton<AuthService>();
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

// Gates the whole app behind config/ldap.yaml (see AuthService.AuthRequired) — /login and
// /api/auth/* always pass through so the login page itself is reachable and can call its own API.
// Everything else needs a valid session cookie: page navigation without one gets redirected to
// /login (preserving the original URL via ?next=), API calls get a plain 401. Static assets never
// reach this middleware — UseStaticFiles above already served/short-circuited them.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (path.Equals("/login", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var auth = context.RequestServices.GetRequiredService<AuthService>();
    if (!auth.AuthRequired)
    {
        await next();
        return;
    }

    var session = auth.GetSession(context.Request.Cookies[AuthService.CookieName]);
    if (session == null)
    {
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        var next2 = Uri.EscapeDataString(path + context.Request.QueryString);
        context.Response.Redirect($"/login?next={next2}");
        return;
    }

    context.Items["Username"] = session.Username;
    context.Items["IsAdmin"] = session.IsAdmin;
    await next();
});

app.MapPost("/api/auth/login", async (HttpContext context, AuthService auth, AppSettingsService appSettings) =>
{
    // JsonSerializer.DeserializeAsync's default options are case-sensitive — without this, the
    // lowercase JSON from Login.cshtml's fetch() ("username"/"password") never binds to the
    // PascalCase record properties below, and every login attempt fails as "empty username".
    var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var body = await System.Text.Json.JsonSerializer.DeserializeAsync<LoginRequest>(context.Request.Body, jsonOptions);
    var username = body?.Username?.Trim() ?? "";
    var password = body?.Password ?? "";
    if (string.IsNullOrEmpty(username))
        return Results.Json(new { detail = "Введите имя пользователя" }, statusCode: StatusCodes.Status400BadRequest);

    AuthResult? user;
    try
    {
        user = auth.Authenticate(username, password);
    }
    catch (AuthException ex)
    {
        app.Logger.LogWarning("Вход не удался: пользователь={User} причина={Reason}", username, ex.Message);
        return Results.Json(new { detail = ex.Message }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (user == null)
        return Results.Json(new { detail = "Авторизация не настроена" }, statusCode: StatusCodes.Status401Unauthorized);

    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "";
    var token = auth.CreateSession(user.Username, user.IsAdmin, user.AuthType, user.Domain, ip);
    context.Response.Cookies.Append(AuthService.CookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = false, // this app runs over plain HTTP on the LAN today, no TLS config to key off
        MaxAge = TimeSpan.FromSeconds(Math.Max(60, appSettings.Current.AuthSessionTtlSeconds)),
    });
    app.Logger.LogInformation("Вход: пользователь={User} тип={AuthType} домен={Domain} admin={IsAdmin} ip={Ip}",
        user.Username, user.AuthType, user.Domain, user.IsAdmin, ip);
    return Results.Ok(new { username = user.Username, isAdmin = user.IsAdmin, authType = user.AuthType, domain = user.Domain });
});

app.MapPost("/api/auth/logout", (HttpContext context, AuthService auth) =>
{
    var token = context.Request.Cookies[AuthService.CookieName];
    auth.DeleteSession(token);
    context.Response.Cookies.Delete(AuthService.CookieName);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/playback/stream/{channelName}", AudioStreamEndpoint.StreamAsync);

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

/// <summary>`Quince.Service.exe --hash-password` — prompts for a password (masked) and prints a
/// BCrypt hash ready to paste into config/users.yaml, mirroring apricot2's separate
/// tools/hash_password.py but built into the single exe instead of needing a second tool.</summary>
static void RunHashPasswordMode()
{
    Console.WriteLine();
    Console.WriteLine("Генерация BCrypt-хэша пароля для config/users.yaml.");
    Console.Write("Введите пароль: ");
    var password = ReadPasswordMasked();
    Console.WriteLine();
    Console.Write("Повторите пароль: ");
    var confirm = ReadPasswordMasked();
    Console.WriteLine();

    if (string.IsNullOrEmpty(password))
    {
        Console.Error.WriteLine("Пустой пароль — отменено.");
        return;
    }
    if (password != confirm)
    {
        Console.Error.WriteLine("Пароли не совпадают — отменено.");
        return;
    }

    var hash = PasswordHasher.Hash(password);
    Console.WriteLine();
    Console.WriteLine("BCrypt-хэш:");
    Console.WriteLine(hash);
    Console.WriteLine();
    Console.WriteLine("Фрагмент для config/users.yaml:");
    Console.WriteLine($"    password_hash: \"{hash}\"");
    Console.WriteLine();
}

static string ReadPasswordMasked()
{
    var sb = new System.Text.StringBuilder();
    ConsoleKeyInfo key;
    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
            {
                sb.Length--;
                Console.Write("\b \b");
            }
            continue;
        }
        if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write('*');
        }
    }
    return sb.ToString();
}

record LoginRequest(string? Username, string? Password);
