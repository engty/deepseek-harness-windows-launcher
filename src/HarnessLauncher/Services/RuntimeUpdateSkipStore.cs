namespace HarnessLauncher.Services;

public sealed class RuntimeUpdateSkipStore
{
    private const string FileName = "skipped-runtime-updates.txt";
    private readonly AppPaths _paths;

    public RuntimeUpdateSkipStore(AppPaths paths) => _paths = paths;

    public bool Contains(string key) => Read().Contains(key, StringComparer.Ordinal);

    public void Skip(string key)
    {
        var values = Read().ToHashSet(StringComparer.Ordinal);
        values.Add(key);
        Directory.CreateDirectory(_paths.State);
        File.WriteAllLines(Path.Combine(_paths.State, FileName), values.OrderBy(value => value));
    }

    private IReadOnlyList<string> Read()
    {
        try { return File.Exists(Path.Combine(_paths.State, FileName)) ? File.ReadAllLines(Path.Combine(_paths.State, FileName)) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }
}
