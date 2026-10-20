using System.IO;
using System.Text.Json;

namespace MailCleaner.Services;

public sealed record UserSettings(bool? AllowChanges = null);
public interface IUserSettingsStore
{
    UserSettings Load();
    void Save(UserSettings settings);
}
public sealed class SettingsService : IUserSettingsStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APS.OwaMailCleaner.Wpf");
    readonly string path = Path.Combine(DataDirectory, "settings.json");
    public UserSettings Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(false); }
    }
    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, path, true);
    }
}
