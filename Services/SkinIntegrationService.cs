using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;
using WeaponSkins.Shared;

namespace WeaponSpawner.Services;

/// <summary>
/// WeaponSkins eklentisinin paylasimli API'si (<c>WeaponSkins.API</c>) uzerinden verilen
/// silahlara ve bicaklara oyuncunun secili kaplama, sticker, keychain ve StatTrak ayarlarini
/// SelectWeapon oncesinde anlik olarak uygular. WeaponSkins yuklu degilse sessizce devredisi kalir.
/// </summary>
public sealed class SkinIntegrationService
{
    public const string SharedApiKey = "WeaponSkins.API";

    private readonly ISwiftlyCore _core;
    private readonly Lock _gate = new();

    private IInterfaceManager? _interfaceManager;
    private object? _api;
    private bool _connectedLogged;
    private bool _missingLogged;
    private bool _mismatchLogged;

    public SkinIntegrationService(ISwiftlyCore core)
    {
        _core = core;
    }

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _api is not null;
            }
        }
    }

    /// <summary>
    /// SwiftlyS2 paylasimli arayuz yasam dongusunde (<c>UseSharedInterface</c> /
    /// <c>OnSharedInterfaceInjected</c>) WeaponSkins API baglantisini kurar veya yeniler.
    /// </summary>
    public void Bind(IInterfaceManager interfaceManager)
    {
        if (interfaceManager == null) return;

        lock (_gate)
        {
            _interfaceManager = interfaceManager;

            if (!interfaceManager.HasSharedInterface(SharedApiKey))
            {
                if (_api is not null)
                {
                    _api = null;
                    _connectedLogged = false;
                    _core.Logger.LogInformation("[WeaponSpawner] WeaponSkins baglantisi kesildi.");
                }
                return;
            }

            try
            {
                var resolved = TypedBridge.TryResolveApi(interfaceManager);
                _api = resolved;
                if (resolved is not null)
                {
                    _missingLogged = false;
                    _mismatchLogged = false;
                    if (!_connectedLogged)
                    {
                        _connectedLogged = true;
                        _core.Logger.LogInformation("[WeaponSpawner] WeaponSkins paylasimli API ('{Key}') baglandi.", SharedApiKey);
                    }
                }
                else if (!_mismatchLogged)
                {
                    _mismatchLogged = true;
                    _core.Logger.LogWarning(
                        "[WeaponSpawner] '{Key}' kayitli ancak IWeaponSkinAPI kontratiyla eslesmedi. WeaponSpawner klasorunde kopya WeaponSkins.Shared.dll varsa silip sunucuyu yeniden baslatin.",
                        SharedApiKey);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException
                                           or BadImageFormatException or MissingMemberException)
            {
                _api = null;
                if (!_mismatchLogged)
                {
                    _mismatchLogged = true;
                    _core.Logger.LogWarning(ex,
                        "[WeaponSpawner] WeaponSkins kontrat DLL'i ('WeaponSkins.Shared.dll') yuklenemedi; eklenti skin entegrasyonu olmadan devam ediyor.");
                }
            }
            catch (Exception ex)
            {
                _api = null;
                _core.Logger.LogWarning(ex, "[WeaponSpawner] WeaponSkins API baglanirken beklenmeyen hata olustu.");
            }
        }
    }

    /// <summary>Tum eklentiler yuklendikten sonra baglantiyi dogrular.</summary>
    public void OnAllPluginsLoaded()
    {
        IInterfaceManager? im;
        bool hasApi;
        bool shouldLogMissing = false;

        lock (_gate)
        {
            im = _interfaceManager;
            hasApi = _api is not null;
        }

        if (!hasApi && im is not null)
        {
            Bind(im);
        }

        lock (_gate)
        {
            if (_api is null && !_missingLogged && !_mismatchLogged)
            {
                _missingLogged = true;
                shouldLogMissing = true;
            }
        }

        if (shouldLogMissing)
        {
            _core.Logger.LogInformation("[WeaponSpawner] WeaponSkins bulunamadi; verilen silahlar varsayilan gorunumuyle olusturulacak.");
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _api = null;
            _interfaceManager = null;
            _connectedLogged = false;
        }
    }

    /// <summary>
    /// Verilen silaha oyuncunun WeaponSkins uzerindeki kaplamasini uygular.
    /// Istemcinin silahi ilk ciziste skinli gorebilmesi icin <c>SelectWeapon</c> oncesinde cagrilmalidir.
    /// </summary>
    public bool ApplySkinToWeapon(IPlayer player, CBasePlayerWeapon weapon)
    {
        if (player == null || !player.IsValid || player.IsFakeClient || player.SteamID == 0) return false;
        if (weapon == null || !weapon.IsValid) return false;

        var controller = player.Controller;
        if (controller == null || !controller.IsValid) return false;

        object? api;
        IInterfaceManager? im;
        lock (_gate)
        {
            api = _api;
            im = _interfaceManager;
        }

        if (api is null && im is not null && im.HasSharedInterface(SharedApiKey))
        {
            Bind(im);
            lock (_gate)
            {
                api = _api;
            }
        }

        if (api is null) return false;

        try
        {
            return TypedBridge.ApplySkin(_core, api, player, controller.Team, weapon);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or TypeLoadException
                                       or BadImageFormatException or MissingMemberException)
        {
            _core.Logger.LogWarning(ex, "[WeaponSpawner] WeaponSkins kontrat uyumsuzlugu nedeniyle skin uygulanamadi.");
            return false;
        }
        catch (Exception ex)
        {
            _core.Logger.LogWarning(ex, "[WeaponSpawner] '{Weapon}' silahina skin uygulanamadi.", weapon.DesignerName);
            return false;
        }
    }

    public static bool IsKnifeDefinitionIndex(int def) =>
        def is 42 or 59 or (>= 500 and < 600);

    public static bool IsGloveDefinitionIndex(int def) =>
        def > 5000;

    public static bool IsWeaponDefinitionIndex(int def) =>
        !IsKnifeDefinitionIndex(def) && !IsGloveDefinitionIndex(def) && def is > 0 and < 100;

    /// <summary>
    /// <see cref="WeaponSkins.Shared"/> tiplerine dogrudan temas eden tum kodlar burada izole edilir.
    /// Bu sayede WeaponSkins yuklu olmayan sunucularda JIT sirasinda TypeLoad/FileNotFound hatasi olusmaz.
    /// </summary>
    private static class TypedBridge
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object? TryResolveApi(IInterfaceManager interfaceManager)
        {
            return interfaceManager.TryGetSharedInterface<IWeaponSkinAPI>(SharedApiKey, out var api)
                ? api
                : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ApplySkin(ISwiftlyCore core, object rawApi, IPlayer player, Team team, CBasePlayerWeapon weapon)
        {
            if (rawApi is not IWeaponSkinAPI api) return false;

            var item = weapon.AttributeManager.Item;
            var def = item.ItemDefinitionIndex;
            if (def == 0 && !string.IsNullOrWhiteSpace(weapon.DesignerName))
            {
                def = (ushort)(core.Helpers.GetDefinitionIndexByClassname(weapon.DesignerName) ?? 0);
            }

            if (def == 0) return false;

            var steamId = player.SteamID;

            if (IsKnifeDefinitionIndex(def))
            {
                if (!api.TryGetKnifeSkin(steamId, team, out var knife) || knife is null)
                {
                    return false;
                }

                ApplyKnifeAttributes(weapon, knife);
                return true;
            }

            if (IsWeaponDefinitionIndex(def))
            {
                if (!api.TryGetWeaponSkin(steamId, team, def, out var skin) || skin is null)
                {
                    return false;
                }

                ApplyWeaponAttributes(core, api, weapon, skin);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyWeaponAttributes(ISwiftlyCore core, IWeaponSkinAPI api, CBasePlayerWeapon weapon, WeaponSkinData skin)
        {
            var item = weapon.AttributeManager.Item;
            var wear = skin.PaintkitWear;
            var itemId = (ulong)Random.Shared.NextInt64(1, long.MaxValue);

            item.ItemID = itemId;
            item.ItemIDLow = (uint)itemId;
            item.ItemIDHigh = (uint)(itemId >> 32);
            item.AccountID = (uint)skin.SteamID;
            item.Initialized = true;
            item.ItemDefinitionIndex = skin.DefinitionIndex;
            item.EntityQuality = (int)skin.Quality;
            item.CustomName = skin.Nametag ?? string.Empty;

            weapon.OriginalOwnerXuidLow = (uint)skin.SteamID;
            weapon.OriginalOwnerXuidHigh = (uint)(skin.SteamID >> 32);
            weapon.FallbackPaintKit = skin.Paintkit;
            weapon.FallbackSeed = skin.PaintkitSeed;
            weapon.FallbackWear = wear;
            weapon.FallbackStatTrak = skin.Quality == EconItemQuality.StatTrak ? skin.StattrakCount : -1;

            RemoveCosmeticAttributes(item.AttributeList);
            RemoveCosmeticAttributes(item.NetworkedDynamicAttributes);
            WriteWeaponAttributes(skin, wear, (name, value) =>
            {
                item.AttributeList.SetOrAddAttribute(name, value);
                item.NetworkedDynamicAttributes.SetOrAddAttribute(name, value);
            });

            item.ItemIDLowUpdated();
            item.ItemIDHighUpdated();
            item.AccountIDUpdated();
            item.InitializedUpdated();
            item.CustomNameUpdated();
            item.AttributeListUpdated();
            item.NetworkedDynamicAttributesUpdated();
            weapon.FallbackPaintKitUpdated();
            weapon.FallbackSeedUpdated();
            weapon.FallbackWearUpdated();
            weapon.FallbackStatTrakUpdated();

            var classname = core.Helpers.GetClassnameByDefinitionIndex(skin.DefinitionIndex);
            IReadOnlyList<PaintkitDefinition>? paintkits = null;
            if (string.IsNullOrWhiteSpace(classname) || !api.WeaponToPaintkits.TryGetValue(classname, out paintkits))
            {
                classname = api.Items.FirstOrDefault(i => i.Value.Index == skin.DefinitionIndex).Key;
                if (!string.IsNullOrWhiteSpace(classname))
                {
                    api.WeaponToPaintkits.TryGetValue(classname, out paintkits);
                }
            }

            if (paintkits is not null)
            {
                var useLegacy = paintkits.FirstOrDefault(p => p.Index == skin.Paintkit)?.UseLegacyModel;
                try
                {
                    weapon.AcceptInput("SetBodygroup", $"body,{(useLegacy == true ? 1 : 0)}", null, null, 0);
                }
                catch { }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyKnifeAttributes(CBasePlayerWeapon weapon, KnifeSkinData knife)
        {
            var item = weapon.AttributeManager.Item;
            if (item.ItemDefinitionIndex != knife.DefinitionIndex)
            {
                weapon.AcceptInput("ChangeSubclass", knife.DefinitionIndex.ToString(CultureInfo.InvariantCulture), null, null, 0);
            }

            var itemId = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
            item.ItemID = itemId;
            item.ItemIDLow = (uint)itemId;
            item.ItemIDHigh = (uint)(itemId >> 32);
            item.AccountID = (uint)knife.SteamID;
            item.Initialized = true;
            item.ItemDefinitionIndex = knife.DefinitionIndex;
            item.EntityQuality = (int)knife.Quality;
            item.CustomName = knife.Nametag ?? string.Empty;

            weapon.OriginalOwnerXuidLow = (uint)knife.SteamID;
            weapon.OriginalOwnerXuidHigh = (uint)(knife.SteamID >> 32);
            weapon.FallbackPaintKit = knife.Paintkit;
            weapon.FallbackSeed = knife.PaintkitSeed;
            weapon.FallbackWear = knife.PaintkitWear;
            weapon.FallbackStatTrak = knife.Quality == EconItemQuality.StatTrak ? knife.StattrakCount : -1;

            item.AttributeList.Attributes.RemoveAll();
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();

            item.NetworkedDynamicAttributes.SetOrAddAttribute("set item texture prefab", knife.Paintkit);
            item.NetworkedDynamicAttributes.SetOrAddAttribute("set item texture seed", knife.PaintkitSeed);
            item.NetworkedDynamicAttributes.SetOrAddAttribute("set item texture wear", knife.PaintkitWear);
            item.AttributeList.SetOrAddAttribute("set item texture prefab", knife.Paintkit);
            item.AttributeList.SetOrAddAttribute("set item texture seed", knife.PaintkitSeed);
            item.AttributeList.SetOrAddAttribute("set item texture wear", knife.PaintkitWear);

            if (knife.Quality == EconItemQuality.StatTrak)
            {
                var val = BitConverter.Int32BitsToSingle(knife.StattrakCount);
                item.AttributeList.SetOrAddAttribute("kill eater", val);
                item.AttributeList.SetOrAddAttribute("kill eater score type", 0);
                item.NetworkedDynamicAttributes.SetOrAddAttribute("kill eater", val);
                item.NetworkedDynamicAttributes.SetOrAddAttribute("kill eater score type", 0);
            }

            item.ItemIDLowUpdated();
            item.ItemIDHighUpdated();
            item.AccountIDUpdated();
            item.InitializedUpdated();
            item.CustomNameUpdated();
            item.AttributeListUpdated();
            item.NetworkedDynamicAttributesUpdated();
            weapon.FallbackPaintKitUpdated();
            weapon.FallbackSeedUpdated();
            weapon.FallbackWearUpdated();
            weapon.FallbackStatTrakUpdated();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RemoveCosmeticAttributes(CAttributeList list)
        {
            for (var index = list.Attributes.Count - 1; index >= 0; index--)
            {
                var definition = list.Attributes[index].AttributeDefinitionIndex;
                if (definition is
                    >= (ushort)AttributeDefinitionIndex.STICKER_SLOT_0_ID and <= (ushort)AttributeDefinitionIndex.STICKER_SLOT_5_ROTATION or
                    >= (ushort)AttributeDefinitionIndex.STICKER_SLOT_0_OFFSET_X and <= (ushort)AttributeDefinitionIndex.STICKER_SLOT_5_SCHEMA or
                    >= (ushort)AttributeDefinitionIndex.KEYCHAIN_SLOT_0_ID and <= (ushort)AttributeDefinitionIndex.KEYCHAIN_SLOT_0_OFFSET_Z or
                    (ushort)AttributeDefinitionIndex.KEYCHAIN_SLOT_0_SEED or
                    (ushort)AttributeDefinitionIndex.KILL_EATER or (ushort)AttributeDefinitionIndex.KILL_EATER_SCORE_TYPE)
                {
                    list.Attributes.Remove(index);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteWeaponAttributes(WeaponSkinData skin, float wear, Action<string, float> setAttribute)
        {
            setAttribute("set item texture prefab", skin.Paintkit);
            setAttribute("set item texture seed", skin.PaintkitSeed);
            setAttribute("set item texture wear", wear);

            if (skin.Quality == EconItemQuality.StatTrak)
            {
                setAttribute("kill eater", BitConverter.Int32BitsToSingle(skin.StattrakCount));
                setAttribute("kill eater score type", 0);
            }

            for (var index = 0; index < 6; index++)
            {
                var sticker = skin.GetSticker(index);
                if (sticker is not { Id: > 0 }) continue;
                var prefix = $"sticker slot {index}";
                setAttribute($"{prefix} id", BitConverter.Int32BitsToSingle(sticker.Id));
                // 1337 varsayilan bos schema degeridir; model anchor olarak gonderilmemelidir.
                if (sticker.Schema != 1337)
                {
                    setAttribute($"{prefix} schema", BitConverter.Int32BitsToSingle(sticker.Schema));
                    setAttribute($"{prefix} offset x", sticker.OffsetX);
                    setAttribute($"{prefix} offset y", sticker.OffsetY);
                }
                setAttribute($"{prefix} wear", sticker.Wear);
                setAttribute($"{prefix} scale", sticker.Scale);
                setAttribute($"{prefix} rotation", sticker.Rotation);
            }

            if (skin.Keychain0 is { Id: > 0 } keychain)
            {
                setAttribute("keychain slot 0 id", BitConverter.Int32BitsToSingle(keychain.Id));
                setAttribute("keychain slot 0 offset x", keychain.OffsetX);
                setAttribute("keychain slot 0 offset y", keychain.OffsetY);
                setAttribute("keychain slot 0 offset z", keychain.OffsetZ);
                setAttribute("keychain slot 0 seed", BitConverter.Int32BitsToSingle(keychain.Seed));
            }
        }
    }
}

