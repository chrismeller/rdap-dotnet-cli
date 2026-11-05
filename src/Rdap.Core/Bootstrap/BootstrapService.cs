namespace Rdap.Bootstrap;

/// <summary>
/// Loads IANA bootstrap files, in order of preference: a fresh on-disk cache, the network,
/// a stale on-disk cache, and finally the snapshot embedded at build time.
/// </summary>
public sealed class BootstrapService
{
    public static readonly Uri DefaultBaseUri = new("https://data.iana.org/rdap/");
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly string? _cacheDirectory;
    private readonly Uri _baseUri;
    private readonly TimeSpan _maxAge;
    private readonly TimeProvider _time;
    private readonly Dictionary<BootstrapKind, BootstrapFile?> _loaded = [];

    /// <param name="http">Client used to fetch the files.</param>
    /// <param name="cacheDirectory">Where to cache downloaded files; null disables the disk cache.</param>
    /// <param name="baseUri">Override for the IANA base URL (used by tests).</param>
    /// <param name="maxAge">How long a cached file is considered fresh.</param>
    /// <param name="timeProvider">Clock (used by tests).</param>
    public BootstrapService(HttpClient http, string? cacheDirectory, Uri? baseUri = null, TimeSpan? maxAge = null, TimeProvider? timeProvider = null)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _baseUri = baseUri ?? DefaultBaseUri;
        _maxAge = maxAge ?? DefaultMaxAge;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>When true, the disk cache is ignored for reads and always refreshed from the network.</summary>
    public bool ForceRefresh { get; set; }

    /// <summary>Diagnostic messages (e.g. falling back to the embedded snapshot).</summary>
    public event Action<string>? Warning;

    /// <summary>The platform-appropriate cache directory: <c>%LOCALAPPDATA%\rdap\cache</c> or <c>$XDG_CACHE_HOME/rdap</c>.</summary>
    public static string DefaultCacheDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rdap", "cache");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var root = !string.IsNullOrEmpty(xdg) && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return Path.Combine(root, "rdap");
    }

    public static string FileName(BootstrapKind kind) => kind switch
    {
        BootstrapKind.Dns => "dns.json",
        BootstrapKind.Ipv4 => "ipv4.json",
        BootstrapKind.Ipv6 => "ipv6.json",
        BootstrapKind.Asn => "asn.json",
        BootstrapKind.ObjectTags => "object-tags.json",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Builds a registry containing only the requested bootstrap files.</summary>
    public async Task<BootstrapRegistry> GetRegistryAsync(IEnumerable<BootstrapKind> kinds, CancellationToken cancellationToken = default)
    {
        foreach (var kind in kinds)
        {
            await GetFileAsync(kind, cancellationToken);
        }

        return new BootstrapRegistry(
            _loaded.GetValueOrDefault(BootstrapKind.Dns),
            _loaded.GetValueOrDefault(BootstrapKind.Ipv4),
            _loaded.GetValueOrDefault(BootstrapKind.Ipv6),
            _loaded.GetValueOrDefault(BootstrapKind.Asn),
            _loaded.GetValueOrDefault(BootstrapKind.ObjectTags));
    }

    /// <exception cref="RdapException">The file couldn't be loaded from any source.</exception>
    public async Task<BootstrapFile> GetFileAsync(BootstrapKind kind, CancellationToken cancellationToken = default)
    {
        if (_loaded.TryGetValue(kind, out var cached) && cached is not null)
        {
            return cached;
        }

        var file = await LoadAsync(kind, cancellationToken);
        _loaded[kind] = file;
        return file;
    }

    private async Task<BootstrapFile> LoadAsync(BootstrapKind kind, CancellationToken cancellationToken)
    {
        var name = FileName(kind);
        var cachePath = _cacheDirectory is null ? null : Path.Combine(_cacheDirectory, name);

        // 1. Fresh cache.
        if (!ForceRefresh && cachePath is not null && File.Exists(cachePath) &&
            _time.GetUtcNow() - File.GetLastWriteTimeUtc(cachePath) < _maxAge &&
            TryParse(await TryReadAsync(cachePath, cancellationToken), out var fresh))
        {
            return fresh;
        }

        // 2. Network.
        string? networkError = null;
        try
        {
            var json = await _http.GetStringAsync(new Uri(_baseUri, name), cancellationToken);
            var file = BootstrapFile.Parse(json);
            if (cachePath is not null)
            {
                await TryWriteCacheAsync(cachePath, json, cancellationToken);
            }

            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or RdapParseException && !cancellationToken.IsCancellationRequested)
        {
            networkError = ex.Message;
        }

        // 3. Stale cache.
        if (cachePath is not null && File.Exists(cachePath) &&
            TryParse(await TryReadAsync(cachePath, cancellationToken), out var stale))
        {
            Warning?.Invoke($"Could not refresh IANA {name} ({networkError}); using cached copy from {File.GetLastWriteTimeUtc(cachePath):u}.");
            return stale;
        }

        // 4. Embedded snapshot.
        if (TryParse(ReadEmbedded(name), out var embedded))
        {
            Warning?.Invoke($"Could not download IANA {name} ({networkError}); using built-in snapshot.");
            return embedded;
        }

        throw new RdapException($"Could not load the IANA bootstrap registry {name}: {networkError}");
    }

    internal static string? ReadEmbedded(string name)
    {
        using var stream = typeof(BootstrapService).Assembly.GetManifestResourceStream("Rdap.Bootstrap." + name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool TryParse(string? json, out BootstrapFile file)
    {
        file = null!;
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            file = BootstrapFile.Parse(json);
            return true;
        }
        catch (RdapParseException)
        {
            return false;
        }
    }

    private static async Task<string?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task TryWriteCacheAsync(string path, string json, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Environment.ProcessId + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warning?.Invoke($"Could not write bootstrap cache {path}: {ex.Message}");
        }
    }
}
