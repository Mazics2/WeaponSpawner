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

    /// <summary>Spawner Id -> su an alanin icinde olan oyuncu slotlari (kenar tetikleme icin).</summary>
    private readonly Dictionary<int, HashSet<int>> _inside = new();

    /// <summary>(spawnerId, slot) -> son alma zamani.</summary>
    private readonly Dictionary<(int, int), float> _lastPickup = new();

    /// <summary>Tabloda olmayan silahlar icin calisma zamaninda bulunan modeller.</summary>
    private readonly Dictionary<string, string> _modelCache = new(StringComparer.OrdinalIgnoreCase);

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
        var known = WeaponCatalog.GetModel(weapon);
        if (known != null) return known;
        if (_modelCache.TryGetValue(weapon, out var cached)) return cached;

        // Son care: gecici bir silah entity'si olusturup modelini oku.
        try
        {
            var temp = _core.EntitySystem.CreateEntityByDesignerName<CBasePlayerWeapon>(weapon);
            if (temp == null || !temp.IsValid) return null;
            temp.DispatchSpawn();
            var model = temp.GetModel();
            temp.Despawn();
            if (!string.IsNullOrWhiteSpace(model))
            {
                _modelCache[weapon] = model;
                return model;
            }
        }
        catch (Exception ex)
        {
            _core.Logger.LogWarning($"[WeaponSpawner] '{weapon}' modeli bulunamadi: {ex.Message}");
        }
        return null;
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

    private bool IsPropAlive(int id, out CBaseModelEntity? prop)
    {
        prop = null;
        if (!_props.TryGetValue(id, out var handle)) return false;
        try
        {
            if (!handle.IsValid) return false;
            prop = handle.Value;
            return prop != null && prop.IsValid;
        }
        catch
        {
            return false;
        }
    }

    public void SpawnAll()
    {
        foreach (var s in _store.Spawners) Spawn(s);
    }

    /// <summary>Spawner modelini (yeniden) olusturur.</summary>
    public bool Spawn(SpawnerData data)
    {
        Despawn(data.Id);

        var model = ResolveModel(data.Weapon);
        if (string.IsNullOrWhiteSpace(model))
        {
            _core.Logger.LogWarning($"[WeaponSpawner] #{data.Id} ({data.Weapon}) icin model yok, olusturulamadi.");
            return false;
        }

        try
        {
            // CS2'de silah modelleri 'propdata' icerir; Source 2 motoru bu modellerin prop_dynamic veya
            // prop_dynamic_override ile olusturulmasini kesinlikle yasaklar ve entity'yi SetupModel() sirasinda siler
            // ("has propdata which means that it be used on a prop_physics. DELETED").
            // Bu yuzden dogrudan 'prop_physics' entity'si olusturulur ve spawnflags=8 (Motion Disabled)
            // ile havada asili, hareketsiz ve yercekimsiz olarak dondurulur.
            CBaseModelEntity? prop = _core.EntitySystem.CreateEntityByDesignerName<CPhysicsProp>("prop_physics");
            if (prop == null || !prop.IsValid)
            {
                prop = _core.EntitySystem.CreateEntityByDesignerName<CBaseModelEntity>("prop_physics");
            }
            if (prop == null || !prop.IsValid) return false;

            var origin = ToVector(data.Origin);
            var angles = new QAngle(data.Angles[0], data.RuntimeYaw, data.Angles[2]);

            // DispatchSpawn oncesi modeli ve konumu hazirla
            try { prop.SetModel(model); } catch { }
            try { prop.Teleport(origin, angles, new Vector(0f, 0f, 0f)); } catch { }

            using (var kv = new CEntityKeyValues())
            {
                kv.SetString("model", model);
                kv.SetString("targetname", $"weaponspawner_{data.Id}");
                kv.SetInt32("spawnflags", 8);          // 8 = Motion Disabled (yercekimsiz / havada dondurulmus)
                kv.SetBool("motiondisabled", true);
                kv.SetInt32("solid", 0);               // carpisma yok; oyuncu icinden gecer
                kv.SetInt32("CollisionGroup", (int)CollisionGroup.Debris);
                kv.SetBool("disableshadows", true);
                kv.SetVector("origin", origin);
                kv.SetQAngle("angles", angles);
                prop.DispatchSpawn(kv);
            }

            // DispatchSpawn sonrasi modeli, yerlesimi ve carpismayi saglamlastir
            try { prop.SetModel(model); } catch { }
            try { prop.Teleport(origin, angles, new Vector(0f, 0f, 0f)); } catch { }
            try { prop.AcceptInput("DisableMotion", "", null, null, 0); } catch { }
            try { prop.AcceptInput("DisableCollision", "", null, null, 0); } catch { }

            // Kesinlikle non-solid olmasini sagla (oyuncu icinden hayalet gibi gecsin)
            ApplyNonSolid(prop);

            // CS2 fizik motoru spawn tick'inde carpisma kutusunu tekrar aktif edebildigi icin
            // bir sonraki tick'te de carpismayi kesin olarak kapat
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

            if (MathF.Abs(data.Scale - 1f) > 0.001f)
            {
                try { prop.SetScale(data.Scale); } catch { }
            }

            if (_config.Glow) ApplyGlow(prop);

            _props[data.Id] = propHandle;
            return true;
        }
        catch (Exception ex)
        {
            _core.Logger.LogError($"[WeaponSpawner] #{data.Id} olusturulamadi: {ex.Message}");
            return false;
        }
    }

    private static void ApplyNonSolid(CBaseModelEntity prop)
    {
        try
        {
            prop.Collision.SolidType = SolidType_t.SOLID_NONE;
            prop.Collision.SolidFlags = 4; // FSOLID_NOT_SOLID
            prop.Collision.CollisionGroup = (byte)CollisionGroup.Debris;
            prop.Collision.EnablePhysics = 0;
            prop.Collision.SolidTypeUpdated();
            prop.Collision.SolidFlagsUpdated();
            prop.Collision.CollisionGroupUpdated();
            prop.Collision.EnablePhysicsUpdated();
            prop.CollisionRulesChanged();
        }
        catch { }
    }

    private void ApplyGlow(CBaseModelEntity prop)
    {
        try
        {
            var color = Color.FromHex(_config.GlowColor);
            prop.Glow.GlowColorOverride = color;
            prop.Glow.GlowColorOverrideUpdated();
            prop.Glow.GlowRange = _config.GlowRange;
            prop.Glow.GlowRangeUpdated();
            prop.Glow.GlowRangeMin = 0;
            prop.Glow.GlowRangeMinUpdated();
            prop.Glow.GlowTeam = -1;
            prop.Glow.GlowTeamUpdated();
            prop.Glow.GlowType = 3;
            prop.Glow.GlowTypeUpdated();
        }
        catch (Exception ex)
        {
            _core.Logger.LogDebug($"[WeaponSpawner] Glow uygulanamadi: {ex.Message}");
        }
    }

    /// <summary>Sadece dunyadaki modeli kaldirir (oyuncu durum bilgisi korunur).</summary>
    public void Despawn(int id)
    {
        if (IsPropAlive(id, out var prop))
        {
            try { prop!.Despawn(); } catch { }
        }
        _props.Remove(id);
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
        foreach (var id in _props.Keys.ToList()) Despawn(id);
        _props.Clear();
        _inside.Clear();
    }

    /// <summary>Round basinda CS2 eklenti entity'lerini temizler; referanslari unut ve yeniden olustur.</summary>
    public void ForgetAndRespawn()
    {
        _props.Clear();
        _inside.Clear();
        SpawnAll();
    }

    /// <summary>Konum/aci/olcek degisikligini dunyaya uygular.</summary>
    public void ApplyTransform(SpawnerData data)
    {
        data.RuntimeYaw = data.Angles[1];
        if (!IsPropAlive(data.Id, out var prop))
        {
            Spawn(data);
            return;
        }
        try
        {
            prop!.Teleport(ToVector(data.Origin), new QAngle(data.Angles[0], data.RuntimeYaw, data.Angles[2]), new Vector(0f, 0f, 0f));
            prop.SetScale(data.Scale);
        }
        catch
        {
            Spawn(data);
        }
    }

    // ------------------------------------------------------------------
    // Tick: donme, gecerlilik kontrolu, silah alma
    // ------------------------------------------------------------------

    public void OnTick()
    {
        _tick++;
        if (_store.Spawners.Count == 0) return;

        // Silinmis/temizlenmis modelleri yarim saniyede bir yeniden olustur
        if (_tick % 32 == 0)
        {
            foreach (var s in _store.Spawners)
            {
                if (!IsPropAlive(s.Id, out _)) Spawn(s);
            }
        }

        // Donen spawner'lar
        foreach (var s in _store.Spawners)
        {
            if (MathF.Abs(s.SpinSpeed) < 0.01f) continue;
            s.RuntimeYaw = (s.RuntimeYaw + s.SpinSpeed * TickInterval) % 360f;
            if (IsPropAlive(s.Id, out var prop))
            {
                try { prop!.Teleport(ToVector(s.Origin), new QAngle(s.Angles[0], s.RuntimeYaw, s.Angles[2]), new Vector(0f, 0f, 0f)); } catch { }
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

    public bool IsSpawned(int id) => IsPropAlive(id, out _);

    public static string Pretty(string weapon) =>
        weapon.StartsWith("weapon_") ? weapon["weapon_".Length..].ToUpperInvariant() : weapon.ToUpperInvariant();

    public static Vector ToVector(float[] a) => new(a[0], a[1], a[2]);

    private float CurrentTime()
    {
        try { return _core.Engine.GlobalVars.CurrentTime; }
        catch { return (float)(Environment.TickCount64 / 1000.0); }
    }
}
