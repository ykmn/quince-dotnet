using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;

namespace Quince.Service.Services;

/// <summary>Holds the live app.yaml settings so the Settings dialog can edit them without an app restart.</summary>
public class AppSettingsService
{
    private readonly YamlConfigLoader _loader;
    private readonly FileLoggerProvider _fileLoggerProvider;
    private readonly string _configDir;
    private readonly ILogger<AppSettingsService> _logger;

    public event Action? Changed;

    public AppConfig Current { get; private set; }

    public AppSettingsService(YamlConfigLoader loader, FileLoggerProvider fileLoggerProvider, IConfiguration configuration,
        ILogger<AppSettingsService> logger)
    {
        _loader = loader;
        _fileLoggerProvider = fileLoggerProvider;
        _configDir = PathResolver.Resolve(configuration["ConfigDir"], "config");
        _logger = logger;
        Current = _loader.LoadApp(_configDir);
    }

    /// <summary>
    /// A settings.yaml write failure (permissions, disk full, a locked file, ...) must not crash the
    /// caller's Blazor circuit — every call site here is a UI event handler (the burger menu's
    /// quick-toggles, the full Settings dialog form) with no try/catch of its own, so an unhandled
    /// exception here would freeze that browser tab exactly the same way the analogous unguarded
    /// file write in <see cref="FileLoggerProvider.WriteLine"/> crashed the whole process (found and
    /// fixed the same session — same root cause, this is its sibling in the settings-save path). The
    /// setting still takes effect for the rest of this run (just won't survive a restart) rather than
    /// the whole toggle silently doing nothing.
    /// </summary>
    public void Save(AppConfig updated)
    {
        try
        {
            _loader.SaveApp(_configDir, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // "The logged-in user has permission on that folder" is not the same question as "does
            // THIS PROCESS'S identity have permission" — a Windows Service commonly runs as a
            // different account (LocalSystem/NetworkService/a dedicated service account) than whoever
            // is looking at the folder in Explorer, and Windows' own Controlled Folder Access
            // (ransomware protection) can block an unrecognized .exe from writing under a user's
            // Desktop/Documents regardless of NTFS ACLs entirely. Logging the actual running identity
            // and target path turns "почему не сохраняется, у меня же есть права" into something
            // answerable from the log alone instead of guessing back and forth.
            _logger.LogError(ex,
                "Не удалось сохранить settings.yaml ({Path}) под учётной записью {Identity} — изменение применено только в памяти на время работы, не переживёт перезапуск. " +
                "Если это Windows-служба — проверьте, под какой учётной записью она запущена (LocalSystem/NetworkService видят не то же самое, что интерактивный пользователь); " +
                "также проверьте Windows Defender → Защита от программ-вымогателей → Контролируемый доступ к папкам — он может блокировать запись в Desktop/Documents в обход обычных прав NTFS.",
                Path.Combine(_configDir, "settings.yaml"), WindowsIdentity.GetCurrent().Name);
        }
        Current = updated;
        _fileLoggerProvider.UpdateSettings(updated);
        Changed?.Invoke();
    }
}
