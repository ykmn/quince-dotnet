namespace Quince.Service.Services;

/// <summary>
/// Shows a native Windows folder-picker dialog. Important caveat for a Blazor Server app: the
/// dialog opens on the SERVER's desktop (wherever this process runs), not on the browser client's
/// machine — fine when the server is administered locally/interactively, misleading if the web UI
/// is used remotely from a different machine while the server has no attached interactive desktop.
/// <see cref="IsAvailable"/> reports whether this process has an interactive window station at all
/// (false when running as a Windows Service) — callers should fall back to the in-app
/// <see cref="FolderBrowserService"/>/<c>FolderBrowserDialog</c> when it's false, or if showing the
/// dialog throws.
/// </summary>
public static class NativeFolderPicker
{
    public static bool IsAvailable => Environment.UserInteractive;

    public static Task<string?> PickAsync(string? initialPath)
    {
        var tcs = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    ShowNewFolderButton = true,
                };
                if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                    dialog.SelectedPath = initialPath;

                var result = dialog.ShowDialog();
                tcs.SetResult(result == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
