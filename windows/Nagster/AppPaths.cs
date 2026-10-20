namespace Nagster;

/// <summary>
/// Where the app keeps its database, settings and sign-in tokens. The app was called DG Nagging Alarms
/// before it became Nagster, so on first use the old data folder is moved across. If the move fails
/// (e.g. another process still has a file open) the old folder is used for now and the move is retried
/// on the next launch, so nothing is ever split between the two.
/// </summary>
public static class AppPaths
{
    private const string LegacyFolderName = "DGNaggingAlarms";

    public static string DataFolder { get; } =
        ResolveDataFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Returns the data folder under <paramref name="root"/>, moving the legacy folder there if needed.</summary>
    public static string ResolveDataFolder(string root)
    {
        var folder = Path.Combine(root, "Nagster");
        var legacy = Path.Combine(root, LegacyFolderName);
        if (Directory.Exists(folder) || !Directory.Exists(legacy)) return folder;
        try
        {
            Directory.Move(legacy, folder);
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return legacy; }
    }
}
