namespace Sentinel.Api.Configuration;

/// <summary>
/// Loads KEY=VALUE pairs from the nearest ".env" file (current directory or up to three parents)
/// into process environment variables so ASP.NET configuration picks them up. Variables that are
/// already set in the real environment always win. Keys use the ASP.NET "Section__Key" format.
/// </summary>
public static class DotEnv
{
    public static string? Load()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var depth = 0; dir is not null && depth < 4; depth++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(path)) continue;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim().Trim('"');
                if (Environment.GetEnvironmentVariable(key) is null)
                    Environment.SetEnvironmentVariable(key, value);
            }
            return path;
        }
        return null;
    }
}
