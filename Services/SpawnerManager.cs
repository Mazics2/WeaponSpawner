using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.EntitySystem;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using WeaponSpawner.Config;
using WeaponSpawner.Models;

namespace WeaponSpawner.Services;

/// <summary>
/// Spawner'larin dunyadaki prop_dynamic modellerini yonetir (olusturma, yeniden olusturma,
/// konum/aci guncelleme, donme) ve oyuncu yaklasinca silah degisimini yapar.
/// Spawner, silah alindiktan sonra da haritada kalir.
/// </summary>
public class SpawnerManager
{
    private const float TickInterval = 1f / 64f;

    private readonly ISwiftlyCore _core;
    private readonly SpawnerStore _store;
    private readonly PluginConfig _config;

    /// <summary>Spawner Id -> dunyadaki model entity'si.</summary>
    private readonly Dictionary<int, CHandle<CBaseModelEntity>> _props = new();

    /// <summary>Spawner Id -> dunyadaki 3D yazi entity'si.</summary>
    private readonly Dictionary<int, CHandle<CPointWorldText>> _texts = new();

    /// <summary>Spawner Id -> su an alanin icinde olan oyuncu slotlari (kenar tetikleme icin).</summary>
    private readonly Dictionary<int, HashSet<int>> _inside = new();

    /// <summary>(spawnerId, slot) -> son alma zamani.</summary>
    private readonly Dictionary<(int, int), float> _lastPickup = new();

    private int _tick;

    public SpawnerManager(ISwiftlyCore core, SpawnerStore store, PluginConfig config)
    {
        _core = core;
        _store = store;
        _config = config;
    }

    // ------------------------------------------------------------------
    // Model / entity yonetimi
    // ------------------------------------------------------------------

    public string? ResolveModel(string weapon)
    {
        if (_config.ModelOverrides.TryGetValue(weapon, out var ov) && !string.IsNullOrWhiteSpace(ov)) return ov;
        return WeaponCatalog.GetModel(weapon);
    }

    /// <summary>Precache icin bilinen tum model yollari.</summary>
    public IEnumerable<string> AllModelPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in WeaponCatalog.AllWeapons)
        {
            var m = WeaponCatalog.GetModel(w);
            if (m != null) set.Add(m);
        }
        foreach (var m in _config.ModelOverrides.Values)
        {
            if (!string.IsNullOrWhiteSpace(m)) set.Add(m);
        }
        return set;
    }

    public void SpawnAll()
    {
        foreach (var s in _store.Spawners) Spawn(s);
    }

    /// <summary>Spawner modelini ve 3D yazisini olusturur.</summary>
    public bool Spawn(SpawnerData data)
    {
        Despawn(data.Id);

        var origin = ToVector(data.Origin);
        var angles = new QAngle(data.Angles[0], data.RuntimeYaw, data.Angles[2]);
        var model = ResolveModel(data.Weapon);

        // 1. Model (prop_physics) olustur
        if (!string.IsNullOrWhiteSpace(model))
        {
            try
            {
                var prop = _core.EntitySystem.CreateEntityByDesignerName<CPhysicsProp>("prop_physics")
                           ?? _core.EntitySystem.CreateEntityByDesignerName<CBaseModelEntity>("prop_physics");

                if (prop != null && prop.IsValid)
                {
                    // ONEMLI: DispatchSpawn oncesi asla SetModel() cagrilmaz! Aksi halde Source 2 motoru
                    // EF_IN_STAGING_LIST assertion hatasi verir. Model keyvalues uzerinden verilir.
                    using (var kv = new CEntityKeyValues())
                    {
                        kv.SetString("model", model);
                        kv.SetString("targetname", $"weaponspawner_{data.Id}");
                        kv.SetInt32("spawnflags", 8);          // 8 = Motion Disabled (yercekimsiz / havada donuk)
                        kv.SetBool("motiondisabled", true);
                        kv.SetInt32("solid", 0);               // Carpisma yok; oyuncu icinden gecer
                        kv.SetInt32("CollisionGroup", (int)CollisionGroup.Debris);
                        kv.SetBool("disableshadows", true);
                        kv.SetVector("origin", origin);
                        kv.SetQAngle("angles", angles);
                        prop.DispatchSpawn(kv);
                    }

                    prop.Teleport(origin, angles, new Vector(0f, 0f, 0f));
                    prop.AcceptInput("DisableMotion", "", null, null, 0);
                    prop.AcceptInput("DisableCollision", "", null, null, 0);
                    ApplyNonSolid(prop);

                    if (MathF.Abs(data.Scale - 1f) > 0.001f)
                    {
                        try { prop.SetScale(data.Scale); } catch { }
                    }

                    var propHandle = _core.EntitySystem.GetRefEHandle(prop);
                    _core.Scheduler.NextTick(() =>
                    {
                        try
                        {
                            if (propHandle.IsValid && propHandle.Value is { } p && p.IsValid)
                            {
                                p.AcceptInput("DisableCollision", "", null, null, 0);
                                ApplyNonSolid(p);
                            }
                        }
                        catch { }
                    });

                    _props[data.Id] = propHandle;
                }
            }
            catch (Exception ex)
            {
                _core.Logger.LogDebug($"[WeaponSpawner] #{data.Id} model olusturulamadi: {ex.Message}");
            }
        }

        // 2. Havada 3D Yazi (point_worldtext) olustur: [ AWP ]
        try
        {
            var text = _core.EntitySystem.CreateEntityByDesignerName<CPointWorldText>("point_worldtext");
            if (text != null && text.IsValid)
            {
                text.MessageText = $"[ {Pretty(data.Weapon)} ]";
                text.FontSize = 26f;
                text.WorldUnitsPerPx = 0.22f;
                text.Color = Color.FromHex("#FFD700"); // Altin sarisi
                text.Enabled = true;
                text.Fullbright = true;
                text.DispatchSpawn();

                var textPos = new Vector(origin.X, origin.Y, origin.Z + 20f);
                text.Teleport(textPos, angles, new Vector(0f, 0f, 0f));

                _texts[data.Id] = _core.EntitySystem.GetRefEHandle(text);
            }
        }
        catch (Exception ex)
        {
            _core.Logger.LogDebug($"[WeaponSpawner] 3D yazi olusturulamadi: {ex.Message}");
        }

        return _props.ContainsKey(data.Id) || _texts.ContainsKey(data.Id);
    }

    private static void ApplyNonSolid(CBaseEntity prop)
    {
        try
        {
            if (prop.Collision is { } col)
            {
                col.SolidType = SolidType_t.SOLID_NONE;
                col.SolidFlags = 4; // FSOLID_NOT_SOLID
                col.CollisionGroup = (byte)CollisionGroup.Debris;
                col.EnablePhysics = 0;
                col.SolidTypeUpdated();
                col.SolidFlagsUpdated();
                col.CollisionGroupUpdated();
                col.EnablePhysicsUpdated();
            }
            prop.CollisionRulesChanged();
        }
        catch { }
    }

    private void ApplyGlow(CBaseEntity prop)
    {
        try
        {
            if (prop is CBaseModelEntity modelEntity)
            {
                var color = Color.FromHex(_config.GlowColor);
                modelEntity.Glow.GlowColorOverride = color;
                modelEntity.Glow.GlowColorOverrideUpdated();
                modelEntity.Glow.GlowRange = _config.GlowRange;
                modelEntity.Glow.GlowRangeUpdated();
                modelEntity.Glow.GlowRangeMin = 0;
                modelEntity.Glow.GlowRangeMinUpdated();
                modelEntity.Glow.GlowTeam = -1;
                modelEntity.Glow.GlowTeamUpdated();
                modelEntity.Glow.GlowType = 3;
                modelEntity.Glow.GlowTypeUpdated();
            }
        }
        catch (Exception ex)
        {
            _core.Logger.LogDebug($"[WeaponSpawner] Glow uygulanamadi: {ex.Message}");
        }
    }

    /// <summary>Sadece dunyadaki modeli ve yaziyi kaldirir (oyuncu durum bilgisi korunur).</summary>
    public void Despawn(int id)
    {
        if (IsPropAlive(id, out var prop))
        {
            try { prop!.Despawn(); } catch { }
        }
        _props.Remove(id);

        if (IsTextAlive(id, out var text))
        {
            try { text!.Despawn(); } catch { }
        }
        _texts.Remove(id);
    }

    /// <summary>Spawner tamamen silindiginde model + durum bilgisini temizler.</summary>
    public void Delete(int id)
    {
        Despawn(id);
        _inside.Remove(id);
        foreach (var key in _lastPickup.Keys.Where(k => k.Item1 == id).ToList()) _lastPickup.Remove(key);
    }

    public void DespawnAll()
    {
        var allIds = _props.Keys.Concat(_texts.Keys).Distinct().ToList();
        foreach (var id in allIds) Despawn(id);
        _props.Clear();
        _texts.Clear();
        _inside.Clear();
    }

    /// <summary>Round basinda CS2 eklenti entity'lerini temizler; referanslari unut ve yeniden olustur.</summary>
    public void ForgetAndRespawn()
    {
        _props.Clear();
        _texts.Clear();
        _inside.Clear();
        SpawnAll();
    }

    /// <summary>Konum/aci/olcek degisikligini dunyaya uygular.</summary>
    public void ApplyTransform(SpawnerData data)
    {
        data.RuntimeYaw = data.Angles[1];
        var origin = ToVector(data.Origin);
        var angles = new QAngle(data.Angles[0], data.RuntimeYaw, data.Angles[2]);
        var textPos = new Vector(origin.X, origin.Y, origin.Z + 20f);

        bool hasProp = IsPropAlive(data.Id, out var prop);
        bool hasText = IsTextAlive(data.Id, out var text);

        if (!hasProp && !hasText)
        {
            Spawn(data);
            return;
        }

        if (hasProp && prop != null)
        {
            try
            {
                prop.Teleport(origin, angles, new Vector(0f, 0f, 0f));
                if (MathF.Abs(data.Scale - 1f) > 0.001f)
                {
                    prop.SetScale(data.Scale);
                }
            }
            catch { }
        }

        if (hasText && text != null)
        {
            try
            {
                text.Teleport(textPos, angles, new Vector(0f, 0f, 0f));
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Tick: donme, gecerlilik kontrolu, silah alma
    // ------------------------------------------------------------------

    public void OnTick()
    {
        _tick++;
        if (_store.Spawners.Count == 0) return;

        // Haritada silinmis veya temizlenmis entity'leri kontrol et (her 4 saniyede bir)
        if (_tick % 256 == 0)
        {
            foreach (var s in _store.Spawners)
            {
                if (!IsSpawned(s.Id)) Spawn(s);
            }
        }

        // Donen spawner'lar
        foreach (var s in _store.Spawners)
        {
            if (MathF.Abs(s.SpinSpeed) < 0.01f) continue;
            s.RuntimeYaw = (s.RuntimeYaw + s.SpinSpeed * TickInterval) % 360f;
            var angles = new QAngle(s.Angles[0], s.RuntimeYaw, s.Angles[2]);
            var origin = ToVector(s.Origin);

            if (IsPropAlive(s.Id, out var prop))
            {
                try { prop!.Teleport(origin, angles, new Vector(0f, 0f, 0f)); } catch { }
            }
            if (IsTextAlive(s.Id, out var text))
            {
                var textPos = new Vector(origin.X, origin.Y, origin.Z + 20f);
                try { text!.Teleport(textPos, angles, new Vector(0f, 0f, 0f)); } catch { }
            }
        }

        // Oyuncu yakinlik kontrolu
        var now = CurrentTime();
        var players = _core.PlayerManager.GetAllPlayers();
        foreach (var player in players)
        {
            if (player == null || !player.IsValid || !player.IsAlive) continue;

            var pawn = player.PlayerPawn;
            if (pawn == null || !pawn.IsValid || !pawn.AbsOrigin.HasValue) continue;

            var feet = pawn.AbsOrigin.Value;

            foreach (var s in _store.Spawners)
            {
                if (!_inside.TryGetValue(s.Id, out var set))
                {
                    set = new HashSet<int>();
                    _inside[s.Id] = set;
                }

                bool isInside = IsInside(s, feet);
                if (!isInside)
                {
                    set.Remove(player.Slot);
                    continue;
                }

                // Zaten alanin icindeyse tekrar tekrar tetikleme
                if (!set.Add(player.Slot)) continue;

                if (_lastPickup.TryGetValue((s.Id, player.Slot), out var last) &&
                    now - last >= 0f && now - last < _config.PickupCooldownSeconds)
                {
                    continue;
                }

                _lastPickup[(s.Id, player.Slot)] = now;
                GiveWeapon(player, s);
            }
        }
    }

    private bool IsInside(SpawnerData s, Vector feet)
    {
        float dx = feet.X - s.Origin[0];
        float dy = feet.Y - s.Origin[1];
        if (dx * dx + dy * dy > s.Radius * s.Radius) return false;

        // Spawner, oyuncunun ayak hizasi ile bas hizasi arasinda olmali (biraz tolerans)
        float dz = s.Origin[2] - feet.Z;
        return dz >= -s.Radius && dz <= _config.PickupHeight;
    }

    /// <summary>
    /// Spawner alanina giren oyuncunun SADECE o slottaki (tufekse tufek, tabancaysa tabanca)
    /// eski silahini siler ve yeni silahi verir. Tabanca, bicak, c4 vb. kesinlikle silinmez.
    /// Oyuncu zaten o silaha sahipse hicbir islem yapmaz (yerde silah birikmez).
    /// </summary>
    private void GiveWeapon(IPlayer player, SpawnerData s)
    {
        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid) return;

        var weaponServices = pawn.WeaponServices;
        var itemServices = pawn.ItemServices;
        if (weaponServices == null || itemServices == null) return;

        var targetSlot = WeaponCatalog.GetSlot(s.Weapon);

        // 1) Oyuncu zaten bu silaha sahip mi? Sahipse hicbir islem yapma (yerde silah birikmesini onler)
        bool alreadyHasThisWeapon = false;
        try
        {
            foreach (var w in weaponServices.MyValidWeapons)
            {
                if (w != null && w.IsValid && string.Equals(w.DesignerName, s.Weapon, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyHasThisWeapon = true;
                    break;
                }
            }
        }
        catch { }

        if (alreadyHasThisWeapon)
        {
            return;
        }

        // 2) SADECE yeni silahin gelecegi slottaki eski silahi bul ve aninda yok et (diger slotlara ASLA dokunma!)
        var weaponsToRemove = new List<CBasePlayerWeapon>();
        try
        {
            foreach (var w in weaponServices.MyValidWeapons.ToList())
            {
                if (w == null || !w.IsValid) continue;
                if (IsProtected(w)) continue;

                // SADECE hedef slottaki silahi sil (tufek veriliyorsa sadece tufek, tabanca veriliyorsa sadece tabanca)
                if (GetSlot(w) == targetSlot)
                {
                    weaponsToRemove.Add(w);
                }
            }
        }
        catch (Exception ex)
        {
            _core.Logger.LogDebug($"[WeaponSpawner] Slot taranamadi: {ex.Message}");
        }

        // Eski silahlari yere dusurmeden dogrudan yok et
        foreach (var w in weaponsToRemove)
        {
            try
            {
                w.AcceptInput("Kill", "", null, null, 0);
                weaponServices.RemoveWeapon(w);
                w.Despawn();
            }
            catch { }
        }

        // 3) Bir sonraki tick'te yeni silahi ver ve ele sec
        var slot = player.Slot;
        var weaponName = s.Weapon;
        _core.Scheduler.NextTick(() =>
        {
            var p = _core.PlayerManager.GetPlayer(slot);
            if (p == null || !p.IsValid || !p.IsAlive) return;
            var pp = p.PlayerPawn;
            if (pp == null || !pp.IsValid || pp.ItemServices == null) return;

            try
            {
                var given = pp.ItemServices.GiveItem<CBasePlayerWeapon>(weaponName);
                if (given != null && given.IsValid && pp.WeaponServices != null)
                {
                    pp.WeaponServices.SelectWeapon(given);
                }
            }
            catch (Exception ex)
            {
                _core.Logger.LogError($"[WeaponSpawner] {weaponName} verilemedi: {ex.Message}");
            }
        });
    }

    private bool IsProtected(CBasePlayerWeapon weapon)
    {
        var slot = GetSlot(weapon);
        if (_config.KeepKnife && slot == gear_slot_t.GEAR_SLOT_KNIFE)
        {
            // Zeus (taser) de bicak slotunda; o korunmaz
            return !weapon.DesignerName.Contains("taser", StringComparison.OrdinalIgnoreCase);
        }
        if (_config.KeepC4 && slot == gear_slot_t.GEAR_SLOT_C4) return true;
        return false;
    }

    private static gear_slot_t GetSlot(CBasePlayerWeapon weapon)
    {
        try
        {
            var vdata = weapon.As<CCSWeaponBase>().WeaponBaseVData;
            if (vdata != null) return vdata.GearSlot;
        }
        catch { }

        // Yedek: isimden tahmin
        var name = weapon.DesignerName ?? string.Empty;
        if (name.Contains("knife", StringComparison.OrdinalIgnoreCase) || name.Contains("bayonet", StringComparison.OrdinalIgnoreCase))
            return gear_slot_t.GEAR_SLOT_KNIFE;
        if (name.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)) return gear_slot_t.GEAR_SLOT_C4;
        return WeaponCatalog.GetSlot(name);
    }

    // ------------------------------------------------------------------
    // Yardimcilar
    // ------------------------------------------------------------------

    public void OnPlayerDisconnected(int slot)
    {
        foreach (var set in _inside.Values) set.Remove(slot);
        foreach (var key in _lastPickup.Keys.Where(k => k.Item2 == slot).ToList()) _lastPickup.Remove(key);
    }

    public bool IsPropAlive(int id, out CBaseModelEntity? prop)
    {
        prop = null;
        if (_props.TryGetValue(id, out var handle) && handle.IsValid && handle.Value is { } p && p.IsValid)
        {
            prop = p;
            return true;
        }
        return false;
    }

    public bool IsTextAlive(int id, out CPointWorldText? text)
    {
        text = null;
        if (_texts.TryGetValue(id, out var handle) && handle.IsValid && handle.Value is { } t && t.IsValid)
        {
            text = t;
            return true;
        }
        return false;
    }

    public bool IsSpawned(int id) => IsPropAlive(id, out _) || IsTextAlive(id, out _);

    public static string Pretty(string weapon) =>
        weapon.StartsWith("weapon_") ? weapon["weapon_".Length..].ToUpperInvariant() : weapon.ToUpperInvariant();

    public static Vector ToVector(float[] a) => new(a[0], a[1], a[2]);

    private float CurrentTime()
    {
        try { return _core.Engine.GlobalVars.CurrentTime; }
        catch { return (float)(Environment.TickCount64 / 1000.0); }
    }
}
