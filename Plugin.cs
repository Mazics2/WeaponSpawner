using System.Globalization;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using WeaponSpawner.Config;
using WeaponSpawner.Models;
using WeaponSpawner.Services;

namespace WeaponSpawner;

[PluginMetadata(
    Id = "WeaponSpawner",
    Version = "1.0.0",
    Name = "Silah Spawner",
    Author = "surfevo",
    Description = "Haritaya yönlendirilebilir silah spawner'ları yerleştirir. Üstüne gelen oyuncunun elindeki silah silinir ve spawner'daki silah verilir; spawner haritada kalır."
)]
public class WeaponSpawnerPlugin(ISwiftlyCore core) : BasePlugin(core)
{
    /// <summary>Hedef id verilmezse en yakin spawner bu mesafede aranir.</summary>
    private const float NearestSearchRadius = 512f;

    private PluginConfig _config = new();
    private SpawnerStore _store = null!;
    private SpawnerManager _manager = null!;
    private AimTracer _tracer = null!;

    private readonly List<Guid> _commandGuids = new();
    private Guid _chatHookGuid;
    private Guid _roundStartHook;
    private bool _mapReady;

    // ------------------------------------------------------------------
    // Yasam dongusu
    // ------------------------------------------------------------------

    public override void Load(bool hotReload)
    {
        _config = SpawnerStore.LoadConfig(Core);
        _store = new SpawnerStore(Core);
        _manager = new SpawnerManager(Core, _store, _config);
        _tracer = new AimTracer(Core);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _config.Command };
        if (_config.Aliases != null)
        {
            foreach (var a in _config.Aliases) names.Add(a);
        }

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            // 1. sw_ komutu ve standart chat ! / / komutu
            _commandGuids.Add(Core.Command.RegisterCommand(name, OnCommand, false, "", "Silah spawner yönetimi"));
            // 2. Raw komut (konsolda öneksiz çalıştırma)
            _commandGuids.Add(Core.Command.RegisterCommand(name, OnCommand, true, "", "Silah spawner yönetimi"));
        }

        _chatHookGuid = Core.Command.HookClientChat(OnChatFallback);

        Core.Event.OnTick += OnTick;
        Core.Event.OnMapLoad += OnMapLoad;
        Core.Event.OnMapUnload += OnMapUnload;
        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnPrecacheResource += OnPrecacheResource;
        _roundStartHook = Core.GameEvent.HookPost<EventRoundStart>(OnRoundStart);

        // Hot reload veya harita zaten yukluyken: hemen yukle
        var map = TryGetCurrentMap();
        if (map != null)
        {
            Core.Scheduler.NextTick(() => LoadMap(map));
        }

        Core.Logger.LogInformation($"[WeaponSpawner] Yüklendi. Komut: !{_config.Command}");
    }

    public override void Unload()
    {
        Core.Event.OnTick -= OnTick;
        Core.Event.OnMapLoad -= OnMapLoad;
        Core.Event.OnMapUnload -= OnMapUnload;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnPrecacheResource -= OnPrecacheResource;

        if (_roundStartHook != Guid.Empty)
        {
            try { Core.GameEvent.Unhook(_roundStartHook); } catch { }
            _roundStartHook = Guid.Empty;
        }

        if (_chatHookGuid != Guid.Empty)
        {
            try { Core.Command.UnhookClientChat(_chatHookGuid); } catch { }
            _chatHookGuid = Guid.Empty;
        }

        foreach (var g in _commandGuids)
        {
            try { Core.Command.UnregisterCommand(g); } catch { }
        }
        _commandGuids.Clear();

        _manager?.DespawnAll();
        _mapReady = false;
    }

    private string? TryGetCurrentMap()
    {
        try
        {
            var raw = Core.Engine.GlobalVars.MapName;
            var s = SpawnerStore.Sanitize(raw);
            return s == "unknown" ? null : s;
        }
        catch
        {
            return null;
        }
    }

    private void LoadMap(string map)
    {
        _manager.DespawnAll();
        _store.LoadMap(map);
        _manager.SpawnAll();
        _mapReady = true;
    }

    // ------------------------------------------------------------------
    // Eventler
    // ------------------------------------------------------------------

    private void OnPrecacheResource(IOnPrecacheResourceEvent e)
    {
        foreach (var model in _manager.AllModelPaths())
        {
            e.AddItem(model);
        }
    }

    private void OnMapLoad(IOnMapLoadEvent e)
    {
        _mapReady = false;
        var map = e.MapName;
        Core.Scheduler.NextTick(() =>
        {
            var s = SpawnerStore.Sanitize(map);
            if (s != "unknown") LoadMap(s);
        });
    }

    private void OnMapUnload(IOnMapUnloadEvent e)
    {
        _mapReady = false;
        _manager.DespawnAll();
    }

    private HookResult OnRoundStart(EventRoundStart e)
    {
        // CS2 her round basinda eklentinin olusturdugu prop'lari temizler -> yeniden olustur
        Core.Scheduler.NextTick(() =>
        {
            if (!_mapReady)
            {
                var map = TryGetCurrentMap();
                if (map != null) LoadMap(map);
                return;
            }
            _manager.ForgetAndRespawn();
        });
        return HookResult.Continue;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent e)
    {
        _manager.OnPlayerDisconnected(e.PlayerId);
    }

    private void OnTick()
    {
        if (!_mapReady)
        {
            var map = TryGetCurrentMap();
            if (map == null) return;
            LoadMap(map);
        }
        _manager.OnTick();
    }

    // ------------------------------------------------------------------
    // Komutlar
    // ------------------------------------------------------------------

    private void OnCommand(ICommandContext ctx)
    {
        var player = ctx.Sender;
        if (player == null || !player.IsValid)
        {
            ctx.Reply($"[WeaponSpawner] Harita: {_store.CurrentMap}, spawner sayısı: {_store.Spawners.Count}. Yönetim oyun içinden yapılır.");
            return;
        }

        ExecutePlayerCommand(player, ctx.Args);
    }

    private HookResult OnChatFallback(int playerId, string text, bool teamonly)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("\"") && trimmed.EndsWith("\"") && trimmed.Length >= 2)
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (trimmed.Length < 2 || (trimmed[0] != '!' && trimmed[0] != '/'))
        {
            return HookResult.Continue;
        }

        var parts = trimmed[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return HookResult.Continue;

        var cmd = parts[0];
        bool match = cmd.Equals(_config.Command, StringComparison.OrdinalIgnoreCase)
            || (_config.Aliases != null && _config.Aliases.Any(a => a.Equals(cmd, StringComparison.OrdinalIgnoreCase)));

        if (!match) return HookResult.Continue;

        var player = Core.PlayerManager.GetPlayer(playerId);
        if (player == null || !player.IsValid) return HookResult.Continue;

        var args = parts.Length > 1 ? parts[1..] : Array.Empty<string>();
        ExecutePlayerCommand(player, args);
        return trimmed[0] == '/' ? HookResult.Stop : HookResult.Continue;
    }

    private void ExecutePlayerCommand(IPlayer player, string[] args)
    {
        if (!HasPermission(player))
        {
            Reply(player, "[red]Bu komutu kullanmak için yetkiniz yok.[default]");
            return;
        }

        if (args.Length == 0)
        {
            SendHelp(player);
            return;
        }

        var sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            switch (sub)
            {
                case "ekle": case "olustur": case "add": case "create":
                    CmdCreate(player, rest); break;
                case "sil": case "kaldir": case "delete": case "remove":
                    CmdDelete(player, rest); break;
                case "liste": case "list":
                    CmdList(player); break;
                case "aci": case "angle":
                    CmdAngle(player, rest); break;
                case "dondur": case "rotate":
                    CmdRotate(player, rest); break;
                case "hizala": case "face":
                    CmdFace(player, rest); break;
                case "tasi": case "move":
                    CmdMove(player, rest); break;
                case "buraya": case "here":
                    CmdHere(player, rest); break;
                case "silah": case "weapon":
                    CmdWeapon(player, rest); break;
                case "donme": case "spin":
                    CmdSpin(player, rest); break;
                case "yaricap": case "radius":
                    CmdRadius(player, rest); break;
                case "olcek": case "scale":
                    CmdScale(player, rest); break;
                case "git": case "goto": case "tp":
                    CmdGoto(player, rest); break;
                case "yenile": case "reload":
                    CmdReload(player); break;
                case "silahlar": case "weapons":
                    Reply(player, "Silahlar: [lime]" + string.Join(", ", WeaponCatalog.AllWeapons.Select(SpawnerManager.Pretty)) + "[default]");
                    break;
                default:
                    SendHelp(player); break;
            }
        }
        catch (Exception ex)
        {
            Reply(player, $"[red]Hata: {ex.Message}[default]");
            Core.Logger.LogError($"[WeaponSpawner] Komut hatası: {ex}");
        }
    }

    private void SendHelp(IPlayer p)
    {
        var c = _config.Command;
        Reply(p, "[gold]Komutlar[default] ([grey]id verilmezse en yakın spawner seçilir[default]):");
        p.SendChat($" [lime]!{c} ekle <silah> [yarıçap][default] - Nişan aldığın yere spawner koy");
        p.SendChat($" [lime]!{c} sil [id][default] | [lime]!{c} liste[default] | [lime]!{c} git <id>[default]");
        p.SendChat($" [lime]!{c} aci [id] <pitch> <yaw> <roll>[default] - Açıyı ayarla");
        p.SendChat($" [lime]!{c} dondur [id] <p|y|r> <derece>[default] - Eksende döndür");
        p.SendChat($" [lime]!{c} hizala [id][default] - Baktığın yöne çevir");
        p.SendChat($" [lime]!{c} tasi [id] <x> <y> <z>[default] | [lime]!{c} buraya [id][default]");
        p.SendChat($" [lime]!{c} silah [id] <silah>[default] | [lime]!{c} donme [id] <derece/sn>[default]");
        p.SendChat($" [lime]!{c} yaricap [id] <birim>[default] | [lime]!{c} olcek [id] <değer>[default] | [lime]!{c} silahlar[default]");
    }

    private void CmdCreate(IPlayer player, string[] a)
    {
        if (a.Length < 1)
        {
            Reply(player, $"Kullanım: [lime]!{_config.Command} ekle <silah> [yarıçap][default]  (örn. ak47, awp, deagle)");
            return;
        }

        var weapon = WeaponCatalog.Normalize(a[0]);
        if (_manager.ResolveModel(weapon) == null)
        {
            Reply(player, $"[red]'{a[0]}' tanınmayan silah.[default] [lime]!{_config.Command} silahlar[default] ile listeyi gör.");
            return;
        }

        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid || !pawn.AbsOrigin.HasValue)
        {
            Reply(player, "[red]Spawner koymak için hayatta olmalısın.[default]");
            return;
        }

        if (!_tracer.TryGetAimPoint(pawn, out var point))
        {
            point = pawn.AbsOrigin.Value; // nisan bir yere carpmazsa ayaginin dibine
        }

        float radius = _config.DefaultRadius;
        if (a.Length > 1 && TryFloat(a[1], out var r) && r > 0) radius = r;

        var data = new SpawnerData
        {
            Weapon = weapon,
            Origin = new[] { point.X, point.Y, point.Z + _config.DefaultHeightOffset },
            Angles = new[] { 0f, NormalizeAngle(pawn.EyeAngles.Y), 0f },
            Radius = radius,
        };

        _store.Add(data);
        _manager.Spawn(data);
        Reply(player, $"[lime]#{data.Id}[default] {SpawnerManager.Pretty(weapon)} spawner'ı oluşturuldu. ({Fmt(data.Origin)})");
    }

    private void CmdDelete(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 0, out _);
        if (s == null) return;
        _manager.Delete(s.Id);
        _store.Remove(s.Id);
        Reply(player, $"[red]#{s.Id}[default] {SpawnerManager.Pretty(s.Weapon)} spawner'ı silindi.");
    }

    private void CmdList(IPlayer player)
    {
        if (_store.Spawners.Count == 0)
        {
            Reply(player, $"Bu haritada spawner yok. [lime]!{_config.Command} ekle <silah>[default]");
            return;
        }
        Reply(player, $"[gold]{_store.CurrentMap}[default] - {_store.Spawners.Count} spawner:");
        foreach (var s in _store.Spawners)
        {
            player.SendChat($" [lime]#{s.Id}[default] {SpawnerManager.Pretty(s.Weapon)} | konum ({Fmt(s.Origin)}) | açı ({Fmt(s.Angles)}) | r={s.Radius:0}" +
                            (s.SpinSpeed != 0 ? $" | dönme={s.SpinSpeed:0}" : "") +
                            (_manager.IsSpawned(s.Id) ? "" : " [red](model yok)[default]"));
        }
    }

    private void CmdAngle(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 3, out var v);
        if (s == null) return;
        if (v.Length < 3 || !TryFloat(v[0], out var pitch) || !TryFloat(v[1], out var yaw) || !TryFloat(v[2], out var roll))
        {
            Reply(player, $"Kullanım: [lime]!{_config.Command} aci [id] <pitch> <yaw> <roll>[default]");
            return;
        }
        s.Angles = new[] { NormalizeAngle(pitch), NormalizeAngle(yaw), NormalizeAngle(roll) };
        Commit(s);
        Reply(player, $"[lime]#{s.Id}[default] açısı: ({Fmt(s.Angles)})");
    }

    private void CmdRotate(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 2, out var v);
        if (s == null) return;
        if (v.Length < 2 || !TryFloat(v[1], out var deg))
        {
            Reply(player, $"Kullanım: [lime]!{_config.Command} dondur [id] <p|y|r> <derece>[default]  (p=pitch, y=yaw, r=roll)");
            return;
        }
        int axis = v[0].ToLowerInvariant() switch
        {
            "p" or "pitch" or "x" => 0,
            "y" or "yaw" or "z" => 1,
            "r" or "roll" => 2,
            _ => -1
        };
        if (axis < 0)
        {
            Reply(player, "[red]Eksen p (pitch), y (yaw) veya r (roll) olmalı.[default]");
            return;
        }
        s.Angles[axis] = NormalizeAngle(s.Angles[axis] + deg);
        Commit(s);
        Reply(player, $"[lime]#{s.Id}[default] açısı: ({Fmt(s.Angles)})");
    }

    private void CmdFace(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 0, out _);
        if (s == null) return;
        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid) return;
        s.Angles[1] = NormalizeAngle(pawn.EyeAngles.Y);
        Commit(s);
        Reply(player, $"[lime]#{s.Id}[default] baktığın yöne çevrildi. ({Fmt(s.Angles)})");
    }

    private void CmdMove(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 3, out var v);
        if (s == null) return;
        if (v.Length < 3 || !TryFloat(v[0], out var dx) || !TryFloat(v[1], out var dy) || !TryFloat(v[2], out var dz))
        {
            Reply(player, $"Kullanım: [lime]!{_config.Command} tasi [id] <x> <y> <z>[default]  (göreceli birim, örn. 0 0 10)");
            return;
        }
        s.Origin = new[] { s.Origin[0] + dx, s.Origin[1] + dy, s.Origin[2] + dz };
        Commit(s);
        Reply(player, $"[lime]#{s.Id}[default] taşındı: ({Fmt(s.Origin)})");
    }

    private void CmdHere(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 0, out _);
        if (s == null) return;
        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid) return;
        if (!_tracer.TryGetAimPoint(pawn, out var point))
        {
            Reply(player, "[red]Nişan alınan nokta bulunamadı.[default]");
            return;
        }
        s.Origin = new[] { point.X, point.Y, point.Z + _config.DefaultHeightOffset };
        Commit(s);
        Reply(player, $"[lime]#{s.Id}[default] nişan aldığın yere taşındı: ({Fmt(s.Origin)})");
    }

    private void CmdWeapon(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 1, out var v);
        if (s == null) return;
        if (v.Length < 1)
        {
            Reply(player, $"Kullanım: [lime]!{_config.Command} silah [id] <silah>[default]");
            return;
        }
        var weapon = WeaponCatalog.Normalize(v[0]);
        if (_manager.ResolveModel(weapon) == null)
        {
            Reply(player, $"[red]'{v[0]}' tanınmayan silah.[default]");
            return;
        }
        s.Weapon = weapon;
        _store.Save();
        _manager.Spawn(s);
        Reply(player, $"[lime]#{s.Id}[default] artık {SpawnerManager.Pretty(weapon)} veriyor.");
    }

    private void CmdSpin(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 1, out var v);
        if (s == null) return;
        if (v.Length < 1 || !TryFloat(v[0], out var speed))
        {
            Reply(player, $"Kullanım: [lime]!{_config.Command} donme [id] <derece/sn>[default]  (0 = kapalı, örn. 90)");
            return;
        }
        s.SpinSpeed = speed;
        Commit(s);
        Reply(player, $"[lime]#{s.Id}[default] dönme hızı: {speed:0} derece/sn");
    }

    private void CmdRadius(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 1, out var v);
        if (s == null) return;
        if (v.Length < 1 || !TryFloat(v[0], out var r) || r <= 0)
        {
            Reply(player, $"Kullanım: [lime]!{_config.Command} yaricap [id] <birim>[default]");
            return;
        }
        s.Radius = r;
        _store.Save();
        Reply(player, $"[lime]#{s.Id}[default] alma yarıçapı: {r:0}");
    }

    private void CmdScale(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 1, out var v);
        if (s == null) return;
        if (v.Length < 1 || !TryFloat(v[0], out var sc) || sc <= 0)
        {
            Reply(player, $"Kullanım: [lime]!{_config.Command} olcek [id] <değer>[default]  (örn. 1.5)");
            return;
        }
        s.Scale = sc;
        Commit(s);
        Reply(player, $"[lime]#{s.Id}[default] ölçeği: {sc:0.##}");
    }

    private void CmdGoto(IPlayer player, string[] a)
    {
        var s = ResolveTarget(player, a, 0, out _);
        if (s == null) return;
        // Spawner'in hemen yanina isinla (dogrudan ustune koyarsak silahi alir)
        var pos = new Vector(s.Origin[0] + s.Radius + 24f, s.Origin[1], s.Origin[2] - _config.DefaultHeightOffset + 4f);
        player.Teleport(pos, null, new Vector(0f, 0f, 0f));
        Reply(player, $"[lime]#{s.Id}[default] yanına ışınlandın.");
    }

    private void CmdReload(IPlayer player)
    {
        var map = TryGetCurrentMap() ?? _store.CurrentMap;
        LoadMap(map);
        Reply(player, $"Spawner'lar yeniden yüklendi: [lime]{_store.Spawners.Count}[default]");
    }

    // ------------------------------------------------------------------
    // Yardimcilar
    // ------------------------------------------------------------------

    /// <summary>
    /// Komut argumanlarindan hedef spawner'i cozer. Arguman sayisi gereken deger sayisindan
    /// fazlaysa ilk arguman id kabul edilir; degilse oyuncuya en yakin spawner secilir.
    /// </summary>
    private SpawnerData? ResolveTarget(IPlayer player, string[] args, int valueCount, out string[] values)
    {
        values = args;
        if (args.Length > valueCount)
        {
            var token = args[0].TrimStart('#');
            if (int.TryParse(token, out var id))
            {
                values = args.Skip(1).ToArray();
                var byId = _store.Get(id);
                if (byId == null) Reply(player, $"[red]#{id} numaralı spawner yok.[default]");
                return byId;
            }
        }

        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid || !pawn.AbsOrigin.HasValue)
        {
            Reply(player, "[red]Hedef spawner id'si belirtmelisin.[default]");
            return null;
        }

        var pos = pawn.AbsOrigin.Value;
        SpawnerData? best = null;
        float bestDist = NearestSearchRadius * NearestSearchRadius;
        foreach (var s in _store.Spawners)
        {
            float dx = s.Origin[0] - pos.X, dy = s.Origin[1] - pos.Y, dz = s.Origin[2] - pos.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < bestDist)
            {
                bestDist = d;
                best = s;
            }
        }

        if (best == null)
        {
            Reply(player, $"[red]Yakında spawner yok.[default] Id belirt veya [lime]!{_config.Command} liste[default] kullan.");
        }
        return best;
    }

    private void Commit(SpawnerData s)
    {
        _store.Save();
        _manager.ApplyTransform(s);
    }

    private bool HasPermission(IPlayer player)
    {
        if (_config.Permissions == null || _config.Permissions.Count == 0) return true;
        foreach (var node in _config.Permissions)
        {
            if (string.IsNullOrWhiteSpace(node)) return true;
            if (Core.Permission.PlayerHasPermission(player.SteamID, node)) return true;
        }
        return false;
    }

    private void Reply(IPlayer player, string msg) => player.SendChat($"{_config.ChatPrefix} {msg}");

    private static bool TryFloat(string s, out float v) =>
        float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static float NormalizeAngle(float a)
    {
        a %= 360f;
        if (a > 180f) a -= 360f;
        if (a <= -180f) a += 360f;
        return MathF.Round(a, 2);
    }

    private static string Fmt(float[] v) =>
        string.Join(", ", v.Select(x => x.ToString("0.#", CultureInfo.InvariantCulture)));
}

