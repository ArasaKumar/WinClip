using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Clippet.Models;
using Microsoft.Win32;

namespace Clippet.Services;

/// <summary>All on-disk persistence: history.json / settings.json (atomic, serialized writer),
/// media PNGs, orphan sweeping, prune, and the autostart Run key.</summary>
public sealed class Storage
{
    public const int HistoryCap = 200;
    public const int ThumbMaxEdge = 96;
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClippetNext"; // distinct from the original app's Run value

    public string RootDir { get; }
    public string MediaDir { get; }
    public string HistoryPath { get; }
    public string SettingsPath { get; }

    private readonly Channel<List<ClipItemDto>> _saveChannel =
        Channel.CreateBounded<List<ClipItemDto>>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    private readonly Task _writerLoop;

    /// <summary>Data folder. Deliberately distinct from the original app's "%APPDATA%\Clippet"
    /// so the WinUI build runs fully isolated and never touches the original app's history.</summary>
    public const string AppFolderName = "ClippetNext";

    public Storage()
    {
        RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);
        MediaDir = Path.Combine(RootDir, "media");
        HistoryPath = Path.Combine(RootDir, "history.json");
        SettingsPath = Path.Combine(RootDir, "settings.json");
        Directory.CreateDirectory(MediaDir);
        _writerLoop = Task.Run(WriterLoopAsync);
    }

    public string MediaFileName(ulong id) => $"{id}.png";
    public string ThumbFileName(ulong id) => $"{id}_thumb.png";
    public string MediaPath(ulong id) => Path.Combine(MediaDir, MediaFileName(id));
    public string ThumbPath(ulong id) => Path.Combine(MediaDir, ThumbFileName(id));

    // ---------------- History load ----------------
    public sealed record LoadResult(List<ClipItem> Items, ulong NextId);

    public LoadResult LoadHistory()
    {
        List<ClipItem> items = [];
        ulong nextId = 1;
        bool loadedKnownFile = false;
        try
        {
            if (File.Exists(HistoryPath))
            {
                string json = File.ReadAllText(HistoryPath);
                var dto = JsonSerializer.Deserialize(json, ClippetJsonContext.Default.HistoryFileDto);
                if (dto is not { Version: 1 } && json.Trim().Length > 0)
                    BackupUnsupported(json); // never silently clobber an unrecognized file
                if (dto is { Version: 1 })
                {
                    loadedKnownFile = true;
                    foreach (var it in dto.Items)
                    {
                        var m = it.ToModel();
                        if (m.IsImage)
                        {
                            // Drop image items whose media file vanished.
                            if (m.MediaFile == null || !File.Exists(Path.Combine(MediaDir, m.MediaFile)))
                                continue;
                        }
                        items.Add(m);
                    }
                    ulong maxId = 0;
                    foreach (var m in items) maxId = Math.Max(maxId, m.Id);
                    nextId = maxId + 1;
                }
            }
        }
        catch
        {
            items = [];
            nextId = 1;
        }

        // Only sweep media when we actually loaded a recognized history file; otherwise an
        // empty item list would wrongly mark all existing media as orphaned.
        if (loadedKnownFile) SweepOrphanMedia(items);
        return new LoadResult(items, nextId);
    }

    private void BackupUnsupported(string json)
    {
        try
        {
            string bak = Path.Combine(RootDir, "history.unsupported.bak");
            if (!File.Exists(bak)) File.WriteAllText(bak, json);
        }
        catch { /* best-effort safety net */ }
    }

    private void SweepOrphanMedia(List<ClipItem> items)
    {
        try
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in items)
            {
                if (m.MediaFile != null) referenced.Add(m.MediaFile);
                if (m.ThumbFile != null) referenced.Add(m.ThumbFile);
            }
            foreach (var path in Directory.EnumerateFiles(MediaDir))
            {
                string name = Path.GetFileName(path);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || !referenced.Contains(name))
                    TryDelete(path);
            }
        }
        catch { /* best-effort */ }
    }

    // ---------------- History save (async, coalesced) ----------------
    public void RequestSave(IReadOnlyList<ClipItem> snapshot)
    {
        var dtos = new List<ClipItemDto>(snapshot.Count);
        foreach (var m in snapshot) dtos.Add(ClipItemDto.FromModel(m));
        _saveChannel.Writer.TryWrite(dtos);
    }

    /// <summary>Synchronous final flush for app exit.</summary>
    public void FlushNow(IReadOnlyList<ClipItem> snapshot)
    {
        var file = new HistoryFileDto { Version = 1 };
        foreach (var m in snapshot) file.Items.Add(ClipItemDto.FromModel(m));
        string json = JsonSerializer.Serialize(file, ClippetJsonContext.Default.HistoryFileDto);
        AtomicWriteAllText(HistoryPath, json);
    }

    private async Task WriterLoopAsync()
    {
        var reader = _saveChannel.Reader;
        while (await reader.WaitToReadAsync())
        {
            List<ClipItemDto>? latest = null;
            while (reader.TryRead(out var dtos)) latest = dtos; // coalesce to newest
            if (latest == null) continue;
            try
            {
                var file = new HistoryFileDto { Version = 1, Items = latest };
                string json = JsonSerializer.Serialize(file, ClippetJsonContext.Default.HistoryFileDto);
                AtomicWriteAllText(HistoryPath, json);
            }
            catch { /* swallow; next save retries */ }
        }
    }

    // ---------------- Media ----------------
    public async Task SaveImageAsync(ulong id, byte[] png, byte[] thumb)
    {
        await AtomicWriteAllBytesAsync(MediaPath(id), png);
        await AtomicWriteAllBytesAsync(ThumbPath(id), thumb);
    }

    public void DeleteMediaFor(ClipItem item)
    {
        if (item.MediaFile != null) TryDelete(Path.Combine(MediaDir, item.MediaFile));
        if (item.ThumbFile != null) TryDelete(Path.Combine(MediaDir, item.ThumbFile));
    }

    // ---------------- Settings ----------------
    public SettingsDto LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var dto = JsonSerializer.Deserialize(json, ClippetJsonContext.Default.SettingsDto);
                if (dto != null) return dto;
            }
        }
        catch { /* fall through to defaults */ }
        return new SettingsDto();
    }

    public void SaveSettings(SettingsDto settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, ClippetJsonContext.Default.SettingsDto);
            AtomicWriteAllText(SettingsPath, json);
        }
        catch { /* best-effort */ }
    }

    // ---------------- Autostart ----------------
    public bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        catch { return false; }
    }

    public void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length > 0) key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best-effort */ }
    }

    // ---------------- Atomic IO ----------------
    private static void AtomicWriteAllText(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static async Task AtomicWriteAllBytesAsync(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
