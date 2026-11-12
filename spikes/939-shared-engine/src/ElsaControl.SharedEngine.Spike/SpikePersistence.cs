namespace ElsaControl.SharedEngine.Spike;

internal static class SpikePersistence
{
    public static string WorkflowConnectionString(SpikeOptions options, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(options.SqlConnection))
        {
            Directory.CreateDirectory(options.DataDirectory);
            var path = Path.Combine(options.DataDirectory, $"{databaseName}.db");
            return $"Data Source={path};Cache=Shared";
        }

        var parts = options.SqlConnection
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part =>
                !part.StartsWith("Database=", StringComparison.OrdinalIgnoreCase) &&
                !part.StartsWith("Initial Catalog=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Add($"Database={databaseName}");
        return string.Join(';', parts);
    }

    public static bool UsesSqlServer(SpikeOptions options) => !string.IsNullOrWhiteSpace(options.SqlConnection);
}
