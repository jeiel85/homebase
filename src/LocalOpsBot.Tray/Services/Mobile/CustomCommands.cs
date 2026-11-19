using System.IO;
using System.Text.Json;

namespace LocalOpsBot.Tray.Services.Mobile;

/// <summary>
/// Custom remote commands (DECISIONS M-69): programs this Windows user registered here, by name, for phones to run — like
/// KDE Connect's "Run command". The phone only picks one by id; what runs (program, fixed arguments, folder) stays in this
/// user's <c>%LOCALAPPDATA%\Homebase\commands.json</c> and runs with this user's rights. The Agent only ever sees ids and names.
/// </summary>
internal static class CustomCommands
{
    public const int Max = 20;

    public sealed record Command(string Id, string Name, string Program, string? Arguments = null, string? WorkingDirectory = null);

    private static readonly object Gate = new();
    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Homebase", "commands.json");

    /// <summary>The list changed (added or removed here).</summary>
    public static event Action? Changed;

    public static IReadOnlyList<Command> List()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(PathName)
                    ? (JsonSerializer.Deserialize<List<Command>>(File.ReadAllText(PathName)) ?? new List<Command>())
                        .Where(c => !string.IsNullOrWhiteSpace(c.Id) && !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Program))
                        .Take(Max).ToList()
                    : Array.Empty<Command>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return Array.Empty<Command>();
            }
        }
    }

    /// <summary>Adds one; false when the list is full.</summary>
    public static bool Add(string name, string program, string? arguments, string? workingDirectory)
    {
        lock (Gate)
        {
            var list = List().ToList();
            if (list.Count >= Max) return false;
            list.Add(new Command(Guid.NewGuid().ToString("N")[..12], name.Trim(), program.Trim(),
                string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim(),
                string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim()));
            Save(list);
        }
        Changed?.Invoke();
        return true;
    }

    public static void Remove(string id)
    {
        lock (Gate) Save(List().Where(c => c.Id != id).ToList());
        Changed?.Invoke();
    }

    public static Command? Find(string id) => List().FirstOrDefault(c => c.Id == id);

    private static void Save(List<Command> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TrayLog.Warn("Commands", $"Could not save the custom commands: {ex.Message}");
        }
    }
}
