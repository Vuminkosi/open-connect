namespace OpenConnect.Samples.Shared;

/// <summary>
/// Resolves sample connection configuration from the environment. Never logs secret values.
/// </summary>
public static class SampleEnvironment
{
    /// <summary>
    /// Resolves a SQLite database file path from an environment variable, falling back to a
    /// file next to the running sample. Ensures the parent directory exists.
    /// </summary>
    /// <param name="envVarName">Environment variable holding an absolute SQLite file path.</param>
    /// <param name="defaultFileName">File name used when the variable is unset.</param>
    /// <returns>The absolute SQLite database file path.</returns>
    public static string ResolveSqliteFile(string envVarName, string defaultFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envVarName);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultFileName);

        var configured = Environment.GetEnvironmentVariable(envVarName);
        var path = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, defaultFileName)
            : configured;

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return full;
    }

    /// <summary>
    /// Builds a SQLite connection string for a resolved file path.
    /// </summary>
    /// <param name="sqliteFilePath">Absolute SQLite database file path.</param>
    /// <returns>A SQLite connection string. Treat as sensitive: do not log.</returns>
    public static string ToSqliteConnectionString(string sqliteFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqliteFilePath);
        return $"Data Source={sqliteFilePath}";
    }

    /// <summary>
    /// Resolves the InMemory database name from an environment variable with a dev-only default.
    /// </summary>
    /// <param name="envVarName">Environment variable holding the InMemory database name.</param>
    /// <param name="defaultName">Default database name used when the variable is unset.</param>
    /// <returns>The InMemory database name.</returns>
    public static string ResolveInMemoryName(string envVarName, string defaultName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envVarName);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultName);

        var configured = Environment.GetEnvironmentVariable(envVarName);
        return string.IsNullOrWhiteSpace(configured) ? defaultName : configured;
    }
}
