namespace Procurement.SyntheticData;

public static class DataFiles
{
    public static string Resolve(string? configured, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var full = Path.GetFullPath(configured);
            return File.Exists(full) ? full : throw new FileNotFoundException($"Data file not found: {full}", full);
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "data", fileName);
        if (File.Exists(bundled)) return bundled;

        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not find data/{fileName} next to the application or in a parent folder", fileName);
    }
}
