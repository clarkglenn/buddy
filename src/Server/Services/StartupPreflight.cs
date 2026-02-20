namespace Server.Services;

public static class StartupPreflight
{
    public static void LogCommandAvailability(ILogger logger, params string[] commands)
    {
        foreach (var command in commands)
        {
            if (IsCommandAvailable(command))
            {
                logger.LogInformation("Executable available on PATH: {Command}", command);
                continue;
            }

            logger.LogWarning("Executable missing on PATH: {Command}", command);
        }
    }

    private static bool IsCommandAvailable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return false;
        }

        var pathExtVariable = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = (pathExtVariable ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            foreach (var extension in new[] { string.Empty }.Concat(extensions))
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
