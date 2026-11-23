using System.Globalization;
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
/// Spawner'larin dunyadaki <c>prop_physics</c> modellerini yonetir (olusturma, yeniden olusturma,
/// konum/aci/olcek guncelleme, donme, glow) ve oyuncu yaklasinca silah degisimini yapar.
/// Spawner, silah alindiktan sonra da haritada kalir.
/// </summary>
public class SpawnerManager
{
    private const float TickInterval = 1f / 64f;
    private static readonly Color DefaultGlowColor = new(255, 176, 0, 255);

    private readonly ISwiftlyCore _core;
    private readonly SpawnerStore _store;
    private readonly PluginConfig _config;
    private readonly SkinIntegrationService? _skinIntegration;

    /// <summary>Spawner Id -> dunyadaki model entity'si.</summary>
    private readonly Dictionary<int, CHandle<CBaseModelEntity>> _props = new();

    /// <summary>Spawner Id -> su an alanin icinde olan oyuncu slotlari (kenar tetikleme icin).</summary>
    private readonly Dictionary<int, HashSet<int>> _inside = new();

    /// <summary>(spawnerId, slot) -> son alma zamani.</summary>
    private readonly Dictionary<(int, int), float> _lastPickup = new();

    /// <summary>Katalogda olmayan silahlar icin calisma zamaninda bulunan modeller.</summary>
    private readonly Dictionary<string, string> _modelCache = new(StringComparer.OrdinalIgnoreCase);

    private int _tick;
    private string? _invalidGlowColorWarned;

    public SpawnerManager(ISwiftlyCore core, SpawnerStore store, PluginConfig config, SkinIntegrationService? skinIntegration = null)
    {
        _core = core;
        _store = store;
        _config = config;
        _skinIntegration = skinIntegration;
    }

    // ------------------------------------------------------------------
    // Model / entity yonetimi
    // ------------------------------------------------------------------

    public string? ResolveModel(string weapon)
    {
        if (string.IsNullOrWhiteSpace(weapon)) return null;

        if (_config.ModelOverrides.TryGetValue(weapon, out var ov) && !string.IsNullOrWhiteSpace(ov)) return ov;

        var known = WeaponCatalog.GetModel(weapon);
        if (known != null) return known;

        if (_modelCache.TryGetValue(weapon, out var cached)) return cached;

        // Son care: gecici bir silah entity'si olusturup gercek model yolunu oku ve onbellege al.
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
        foreach (var m in _modelCache.Values)
        {
            if (!string.IsNullOrWhiteSpace(m)) set.Add(m);
        }
        return set;
    }

    public void SpawnAll()
    {
        foreach (var s in _store.Spawners) Spawn(s);
    }

    /// <summary>Spawner 3D prop modelini (yeniden) olusturur.</summary>
    public bool Spawn(SpawnerData data)
    {
        Despawn(data.Id);

        var model = ResolveModel(data.Weapon);
        if (string.IsNullOrWhiteSpace(model))
        {
            _core.Logger.LogWarning($"[WeaponSpawner] #{data.Id} ({data.Weapon}) icin model bulunamadi, olusturulamadi.");
            return false;
        }

        try
        {
            // CS2 silah modelleri 'propdata' icerdiginden prop_dynamic yerine prop_physics kullanilir
            // ve spawnflags=8 (Motion Disabled) ile yercekimsiz/sabit olarak havada tutulur.
            CBaseModelEntity? prop = _core.EntitySystem.CreateEntityByDesignerName<CPhysicsProp>("prop_physics")
                                     ?? _core.EntitySystem.CreateEntityByDesignerName<CPhysicsProp>("prop_physics_override")
                                     ?? _core.EntitySystem.CreateEntityByDesignerName<CBaseModelEntity>("prop_physics")
                                     ?? _core.EntitySystem.CreateEntityByDesignerName<CBaseModelEntity>("prop_physics_override");

            if (prop == null || !prop.IsValid)
            {
                _core.Logger.LogWarning($"[WeaponSpawner] #{data.Id} ({data.Weapon}) icin prop_physics entity'si olusturulamadi.");
                return false;
            }

            var origin = ToVector(data.Origin);
            var angles = new QAngle(data.Angles[0], data.RuntimeYaw, data.Angles[2]);
            bool glowEnabled = IsGlowEnabled();
            var glowColor = glowEnabled ? ResolveGlowColor() : new Color(0, 0, 0, 0);

            // DispatchSpawn oncesi ASLA SetModel() cagirmayin (EF_IN_STAGING_LIST assertion'ini tetikler).
            // Model ve baslangic fizik/glow alanlari CEntityKeyValues uzerinden verilir.
            using (var kv = new CEntityKeyValues())
            {
                kv.SetString("model", model);
                kv.SetString("targetname", $"weaponspawner_{data.Id}");
                kv.SetInt32("spawnflags", 8);          // 8 = Motion Disabled
                kv.SetBool("motiondisabled", true);
                kv.SetBool("forcemotiondisabled", true);
                kv.SetBool("phys_start_asleep", true);
                kv.SetInt32("solid", 0);               // Carpisma yok; oyuncu icinden gecer
                kv.SetInt32("CollisionGroup", (int)CollisionGroup.Debris);
                kv.SetBool("disableshadows", true);
                kv.SetVector("origin", origin);
                kv.SetQAngle("angles", angles);
                kv.SetInt32("glowstate", glowEnabled ? 3 : 0);
                kv.SetInt32("glowrange", glowEnabled ? Math.Max(0, _config.GlowRange) : 0);
                kv.SetInt32("glowrangemin", 0);
                kv.SetColor("glowcolor", glowColor);
                prop.DispatchSpawn(kv);
            }

            try
            {
                if (string.IsNullOrWhiteSpace(prop.GetModel()))
                {
                    prop.SetModel(model);
                }
            }
            catch { }

            prop.Teleport(origin, angles, new Vector(0f, 0f, 0f));
            try { prop.AcceptInput("DisableMotion", "", null, null, 0); } catch { }
            try { prop.AcceptInput("DisableCollision", "", null, null, 0); } catch { }

            ApplyNonSolid(prop);
            ApplyScale(prop, data.Scale);
            ApplyGlow(prop, glowEnabled);

            // CS2 fizik/glow alt sistemi spawn tick'inde bazi bayraklari sifirlayabildigi icin
            // bir sonraki tick'te non-solid ve guncel glow durumunu tekrar saglamlastir.
            var propHandle = _core.EntitySystem.GetRefEHandle(prop);
            _core.Scheduler.NextTick(() =>
            {
                try
                {
                    if (propHandle.IsValid && propHandle.Value is { } p && p.IsValid)
                    {
                        try { p.AcceptInput("DisableMotion", "", null, null, 0); } catch { }
                        try { p.AcceptInput("DisableCollision", "", null, null, 0); } catch { }
                        ApplyNonSolid(p);
                        ApplyGlow(p, IsGlowEnabled());
                    }
                }
                catch { }
            });

            _props[data.Id] = propHandle;
            return true;
        }
        catch (Exception ex)
        {
            _core.Logger.LogError(ex, $"[WeaponSpawner] #{data.Id} ({data.Weapon}) olusturulamadi: {ex.Message}");
            return false;
        }
    }

    private static void ApplyScale(CBaseModelEntity prop, float scale)
    {
        var clamped = float.IsFinite(scale) ? Math.Clamp(scale, 0.05f, 10f) : 1f;
        try { prop.SetScale(clamped); } catch { }
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

    private bool IsGlowEnabled() => _config.Glow && _config.GlowRange >= 0;

    private Color ResolveGlowColor()
    {
        if (TryParseHexColor(_config.GlowColor, out var parsed))
        {
            return parsed.A == 0 ? new Color(parsed.R, parsed.G, parsed.B, 255) : parsed;
        }

        if (!string.Equals(_invalidGlowColorWarned, _config.GlowColor, StringComparison.Ordinal))
        {
            _invalidGlowColorWarned = _config.GlowColor;
            _core.Logger.LogWarning($"[WeaponSpawner] Gecersiz GlowColor '{_config.GlowColor}', varsayilan #FFB000 kullaniliyor.");
        }

        return DefaultGlowColor;
    }

    /// <summary>
    /// Hex renk kodunu (#RGB, #RGBA, #RRGGBB, #RRGGBBAA veya # isaretsiz) guvenle ayristirir.
    /// </summary>
    public static bool TryParseHexColor(string? hex, out Color color)
    {
        color = DefaultGlowColor;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var span = hex.AsSpan().Trim();
        if (span.StartsWith("#")) span = span[1..];

        if (span.Length is not (3 or 4 or 6 or 8)) return false;

        static bool TryHexByte(ReadOnlySpan<char> chars, out byte value) =>
            byte.TryParse(chars, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

        if (span.Length is 3 or 4)
        {
            Span<char> expanded = stackalloc char[2];
            byte Expand(char ch)
            {
                expanded[0] = ch;
                expanded[1] = ch;
                return byte.TryParse(expanded, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b) ? b : (byte)0;
            }

            foreach (var c in span)
            {
                if (!Uri.IsHexDigit(c)) return false;
            }

            byte r = Expand(span[0]);
            byte g = Expand(span[1]);
            byte b = Expand(span[2]);
            byte a = span.Length == 4 ? Expand(span[3]) : (byte)255;
            color = new Color(r, g, b, a);
            return true;
        }

        if (!TryHexByte(span[..2], out var r8) ||
            !TryHexByte(span[2..4], out var g8) ||
            !TryHexByte(span[4..6], out var b8))
        {
            return false;
        }

        byte a8 = 255;
        if (span.Length == 8 && !TryHexByte(span[6..8], out a8))
        {
            return false;
        }

        color = new Color(r8, g8, b8, a8);
        return true;
    }

    private void ApplyGlow(CBaseModelEntity prop, bool enabled)
    {
        try
        {
            var glow = prop.Glow;
            if (glow == null) return;

            if (enabled)
            {
                var color = ResolveGlowColor();
                glow.GlowColorOverride = color;
                glow.GlowRange = Math.Max(0, _config.GlowRange);
                glow.GlowRangeMin = 0;
                glow.GlowTeam = -1;
                glow.GlowType = 3;
                glow.Flashing = false;
                glow.EligibleForScreenHighlight = true;
                glow.Glowing = true;

                glow.GlowColorOverrideUpdated();
                glow.GlowRangeUpdated();
                glow.GlowRangeMinUpdated();
                glow.GlowTeamUpdated();
                glow.GlowTypeUpdated();
                glow.FlashingUpdated();
                glow.EligibleForScreenHighlightUpdated();
                prop.GlowUpdated();
            }
            else
            {
                glow.GlowColorOverride = new Color(0, 0, 0, 0);
                glow.GlowType = 0;
                glow.GlowRange = 0;
                glow.GlowRangeMin = 0;
                glow.GlowTeam = -1;
                glow.Flashing = false;
                glow.EligibleForScreenHighlight = false;
                glow.Glowing = false;

                glow.GlowColorOverrideUpdated();
                glow.GlowTypeUpdated();
                glow.GlowRangeUpdated();
                glow.GlowRangeMinUpdated();
                glow.GlowTeamUpdated();
                glow.FlashingUpdated();
                glow.EligibleForScreenHighlightUpdated();
                prop.GlowUpdated();
            }
        }
        catch (Exception ex)
        {
            _core.Logger.LogWarning($"[WeaponSpawner] Glow uygulanamadi: {ex.Message}");
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

    /// <summary>Konum/aci/olcek/glow degisikligini dunyaya uygular.</summary>
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
            ApplyScale(prop, data.Scale);
            ApplyGlow(prop, IsGlowEnabled());
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
        var spawners = _store.Spawners;
        int spawnerCount = spawners.Count;
        if (spawnerCount == 0) return;

        // Haritada silinmis veya temizlenmis prop'lari saniyede bir kontrol et ve yeniden olustur
        if (_tick % 64 == 0)
        {
            for (int i = 0; i < spawnerCount; i++)
            {
                var s = spawners[i];
                if (!IsPropAlive(s.Id, out _)) Spawn(s);
            }
        }

        // Donen spawner'lar
        for (int i = 0; i < spawnerCount; i++)
        {
            var s = spawners[i];
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

            for (int i = 0; i < spawnerCount; i++)
            {
                var s = spawners[i];
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
    /// Spawner alanina giren oyuncuya yeni silahi verir; ayar aktifse yalnizca hedef slottaki eski silahi temizler.
    /// WeaponSkins mevcutsa SelectWeapon oncesinde oyuncunun skin/sticker/keychain bilgilerini anlik uygular.
    /// </summary>
    private void GiveWeapon(IPlayer player, SpawnerData s)
    {
        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid) return;

        var weaponServices = pawn.WeaponServices;
        var itemServices = pawn.ItemServices;
        if (weaponServices == null || itemServices == null) return;

        var targetSlot = ResolveTargetSlot(s.Weapon);
        var targetDefIndex = TryGetDefinitionIndex(s.Weapon);

        // 1) Oyuncu zaten bu silaha sahip mi? Sahipse hicbir islem yapma (yerde silah birikmesini onler)
        bool alreadyHasThisWeapon = false;
        try
        {
            foreach (var w in weaponServices.MyValidWeapons)
            {
                if (w == null || !w.IsValid) continue;
                if (string.Equals(w.DesignerName, s.Weapon, StringComparison.OrdinalIgnoreCase) ||
                    (targetDefIndex > 0 && w.AttributeManager.Item.ItemDefinitionIndex == targetDefIndex))
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

        // 2) ClearTargetSlot aciksa SADECE yeni silahin girecegi slottaki eski silahi yok et
        if (_config.ClearTargetSlot && targetSlot != gear_slot_t.GEAR_SLOT_INVALID)
        {
            var weaponsToRemove = new List<CBasePlayerWeapon>();
            try
            {
                foreach (var w in weaponServices.MyValidWeapons.ToList())
                {
                    if (w == null || !w.IsValid) continue;
                    if (IsProtected(w)) continue;

                    if (GetSlot(w) == targetSlot)
                    {
                        weaponsToRemove.Add(w);
                    }
                }
            }
            catch (Exception ex)
            {
                _core.Logger.LogWarning($"[WeaponSpawner] Hedef slot taranamadi: {ex.Message}");
            }

            foreach (var w in weaponsToRemove)
            {
                try
                {
                    weaponServices.RemoveWeapon(w);
                    if (w.IsValid) w.Despawn();
                }
                catch { }
            }
        }

        // 3) Bir sonraki tick'te yeni silahi ver, SelectWeapon oncesinde skin uygula ve sec
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
                if (given != null && given.IsValid)
                {
                    // Istemci silahi eline aldigi ilk anda skinli gorebilsin diye SelectWeapon oncesinde boya.
                    _skinIntegration?.ApplySkinToWeapon(p, given);

                    pp.WeaponServices?.SelectWeapon(given);

                    if (_config.NotifyOnPickup)
                    {
                        p.SendChat($"{_config.ChatPrefix} [lime]{Pretty(weaponName)}[default] verildi.");
                    }
                }
            }
            catch (Exception ex)
            {
                _core.Logger.LogError(ex, $"[WeaponSpawner] {weaponName} verilemedi: {ex.Message}");
            }
        });
    }

    private gear_slot_t ResolveTargetSlot(string weapon)
    {
        var slot = WeaponCatalog.GetSlot(weapon);
        if (slot != gear_slot_t.GEAR_SLOT_INVALID) return slot;

        try
        {
            var vdata = _core.Helpers.GetWeaponCSDataFromKey(-1, weapon);
            if (vdata != null) return vdata.GearSlot;
        }
        catch { }

        return gear_slot_t.GEAR_SLOT_INVALID;
    }

    private ushort TryGetDefinitionIndex(string weapon)
    {
        try
        {
            return (ushort)(_core.Helpers.GetDefinitionIndexByClassname(weapon) ?? 0);
        }
        catch
        {
            return 0;
        }
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
        if (!_props.TryGetValue(id, out var handle)) return false;
        try
        {
            if (handle.IsValid && handle.Value is { } p && p.IsValid)
            {
                prop = p;
                return true;
            }
        }
        catch { }

        return false;
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
