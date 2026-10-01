namespace Resenz.Data;

public static class ResenzAppData
{
    private const string OverrideVariable = "RENZENZ_DATA_DIR";

    public static string ResolveDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return Path.GetFullPath(overridden);

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "Resenz");
    }
}
