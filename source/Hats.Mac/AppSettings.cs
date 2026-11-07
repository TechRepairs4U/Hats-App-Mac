using System.Diagnostics;
using System.Text.Json;
using Hats.Core;

namespace Hats.Mac;

public sealed class AppSettings
{
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MagicMonkeiHATS");
    public static string LogFolder => Path.Combine(DataFolder, "Logs");
    public static string WorkFolder => Path.Combine(DataFolder, "Work");
    public static string ProtectionFile => Path.Combine(DataFolder, "preserve-paths.json");
    public static string LogPath { get; } = Path.Combine(LogFolder, "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId + ".log");
    static readonly object LogLock = new();
    public static string DefaultBackupFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "HATSBackups");
    public string BackupFolder { get; set; } = DefaultBackupFolder;
    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(DataFolder, "settings.json"))) ?? new();
            var oldDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Magic Monkei", "HATS Backups");
            if (string.IsNullOrWhiteSpace(settings.BackupFolder) || settings.BackupFolder == oldDefault) settings.BackupFolder = DefaultBackupFolder;
            return settings;
        }
        catch(Exception e) when(e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save() { Directory.CreateDirectory(DataFolder); File.WriteAllText(Path.Combine(DataFolder, "settings.json"), JsonSerializer.Serialize(this)); }
    public static void Log(string message)
    {
        lock(LogLock) { Directory.CreateDirectory(LogFolder); File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\n"); }
    }
    public static void Open(string path)
    {
        if(OperatingSystem.IsMacOS()) { var start=new ProcessStartInfo("/usr/bin/open"){UseShellExecute=false};start.ArgumentList.Add(path);Process.Start(start); }
        else Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
}
