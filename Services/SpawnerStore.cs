using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using WeaponSpawner.Config;
using WeaponSpawner.Models;

namespace WeaponSpawner.Services;

/// <summary>
/// Harita bazli JSON kayit. Dosyalar: &lt;veri klasoru&gt;/maps/&lt;harita&gt;.json
/// </summary>
public class SpawnerStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ISwiftlyCore _core;
    private readonly string _basePath;
    private readonly List<SpawnerData> _spawners = new();

    public string CurrentMap { get; private set; } = "unknown";
    public IReadOnlyList<SpawnerData> Spawners => _spawners;

    public SpawnerStore(ISwiftlyCore core)
    {
        _core = core;
        _basePath = ResolveBasePath(core);
    }

    private string MapsFolder => Path.Combine(_basePath, "maps");
    private string MapFile => Path.Combine(MapsFolder, CurrentMap + ".json");

    public static string ResolveBasePath(ISwiftlyCore core)
    {
        try
        {
            var dir = core.PluginDataDirectory;
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        catch { }

        try
        {
            var dir = core.Configuration.BasePath;
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        catch { }

        return Path.Combine(AppContext.BaseDirectory, "WeaponSpawner");
    }

    public static string Sanitize(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName)) return "unknown";
        var cleaned = mapName.Trim();
        var slash = cleaned.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0 && slash < cleaned.Length - 1) cleaned = cleaned[(slash + 1)..];
        foreach (var c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }

    public void LoadMap(string? mapName)
    {
        var sanitized = Sanitize(mapName);
        if (sanitized == "unknown") return;

        CurrentMap = sanitized;
        _spawners.Clear();
        try
        {
            Directory.CreateDirectory(MapsFolder);
            if (File.Exists(MapFile))
            {
                var list = JsonSerializer.Deserialize<List<SpawnerData>>(File.ReadAllText(MapFile), Json);
                if (list != null)
                {
                    foreach (var s in list.Where(s => s.Origin?.Length == 3 && s.Angles?.Length == 3))
                    {
                        s.RuntimeYaw = s.Angles[1];
                        _spawners.Add(s);
                    }
                }
            }
            _core.Logger.LogInformation($"[WeaponSpawner] {CurrentMap}: {_spawners.Count} spawner yuklendi.");
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[WeaponSpawner] {CurrentMap} yuklenemedi: {ex.Message}");
        }
    }

    public void Save()
    {
        if (CurrentMap == "unknown") return;
        try
        {
            Directory.CreateDirectory(MapsFolder);
            File.WriteAllText(MapFile, JsonSerializer.Serialize(_spawners, Json));
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[WeaponSpawner] {CurrentMap} kaydedilemedi: {ex.Message}");
        }
    }

    public SpawnerData? Get(int id) => _spawners.FirstOrDefault(s => s.Id == id);

    public SpawnerData Add(SpawnerData data)
    {
        data.Id = _spawners.Count == 0 ? 1 : _spawners.Max(s => s.Id) + 1;
        data.RuntimeYaw = data.Angles[1];
        _spawners.Add(data);
        Save();
        return data;
    }

    public bool Remove(int id)
    {
        var removed = _spawners.RemoveAll(s => s.Id == id) > 0;
        if (removed) Save();
        return removed;
    }

    // ------------------------------------------------------------------
    // Config
    // ------------------------------------------------------------------

    public static PluginConfig LoadConfig(ISwiftlyCore core)
    {
        var baseDir = ResolveBasePath(core);
        var path = Path.Combine(baseDir, "config.json");
        try
        {
            Directory.CreateDirectory(baseDir);
            var cfg = File.Exists(path)
                ? JsonSerializer.Deserialize<PluginConfig>(File.ReadAllText(path), Json) ?? new PluginConfig()
                : new PluginConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, Json));
            return cfg;
        }
        catch (Exception ex)
        {
            core.Logger.LogError($"[WeaponSpawner] config.json okunamadi, varsayilanlar kullaniliyor: {ex.Message}");
            return new PluginConfig();
        }
    }
}

