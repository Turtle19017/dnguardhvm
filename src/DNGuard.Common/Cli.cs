namespace DNGuard.Common;

public sealed class Cli
{
    private readonly string[] _args;
    public Cli(string[] args) => _args = args;

    public bool Has(string key) => _args.Any(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));

    public string? Value(string key, string? fallback = null)
    {
        for (var i = 0; i + 1 < _args.Length; i++)
            if (string.Equals(_args[i], key, StringComparison.OrdinalIgnoreCase))
                return _args[i + 1];
        return fallback;
    }

    public int Int(string key, int fallback)
        => int.TryParse(Value(key), out var value) ? value : fallback;

    public string? FirstFile()
        => _args.FirstOrDefault(x => !x.StartsWith('-') && File.Exists(x));
}
