using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeGraphBuilder.Shared.Knowledge.Persistence;

public static class KnowledgeStorageConstants
{
    public const string KeyPrefix = "kgb:v1:";
    public const string IndexKey = KeyPrefix + "index";
    public const string PageKeyPrefix = KeyPrefix + "page:";
    public const string SettingsKey = KeyPrefix + "settings";
    public const long AssumedQuotaBytes = 5_000_000;
    public const double WarnAtFraction = 0.8;
    public const long MaxImportBytes = 10 * 1024 * 1024;

    public static string PageKey(string path) => PageKeyPrefix + path;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

public interface IKeyValueStore
{
    Task<string?> GetAsync(string key);

    /// <exception cref="StorageQuotaExceededException">When the browser quota is exceeded.</exception>
    Task SetAsync(string key, string value);
    Task RemoveAsync(string key);
    Task<IReadOnlyList<string>> KeysAsync(string prefix);
}

public sealed record StorageIndex(int Version, List<string> Folders, Dictionary<string, long> Pages)
{
    public static StorageIndex Empty() => new(1, [], new Dictionary<string, long>(StringComparer.Ordinal));

    public StorageIndex Copy() =>
        new(Version, [.. Folders], new Dictionary<string, long>(Pages, StringComparer.Ordinal));
}

public sealed record StorageUsage(long EstimatedBytes, long AssumedQuotaBytes, int Pages)
{
    public double Fraction => AssumedQuotaBytes <= 0 ? 0 : (double)EstimatedBytes / AssumedQuotaBytes;
    public bool IsNearlyFull => Fraction >= KnowledgeStorageConstants.WarnAtFraction;
}

/// <summary>An in-memory <see cref="IKeyValueStore"/> for tests; can simulate a quota.</summary>
public sealed class InMemoryKeyValueStore : IKeyValueStore
{
    readonly Dictionary<string, string> _items = new(StringComparer.Ordinal);

    /// <summary>Total UTF-16 bytes allowed, or null for unlimited.</summary>
    public long? QuotaBytes { get; set; }

    public List<string> WriteLog { get; } = [];

    public IReadOnlyDictionary<string, string> Items => _items;

    public Task<string?> GetAsync(string key) => Task.FromResult(_items.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value)
    {
        if (QuotaBytes is { } quota)
        {
            var current = _items.Where(kv => kv.Key != key).Sum(kv => (long)(kv.Key.Length + kv.Value.Length) * 2);
            var next = current + (long)(key.Length + value.Length) * 2;
            if (next > quota) throw new StorageQuotaExceededException(next);
        }
        _items[key] = value;
        WriteLog.Add("set " + key);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key)
    {
        _items.Remove(key);
        WriteLog.Add("remove " + key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> KeysAsync(string prefix) =>
        Task.FromResult<IReadOnlyList<string>>(_items.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList());
}
