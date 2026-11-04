using SwiftlyS2.Shared.SchemaDefinitions;

namespace WeaponSpawner.Services;

/// <summary>
/// Desteklenen silahlar, dunya modelleri ve hangi slota girdikleri.
/// </summary>
public static class WeaponCatalog
{
    private record Entry(string Model, gear_slot_t Slot);

    private static readonly Dictionary<string, Entry> Weapons = new(StringComparer.OrdinalIgnoreCase)
    {
        // Tufekler
        ["weapon_ak47"]          = new("weapons/models/ak47/weapon_rif_ak47.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_m4a1"]          = new("weapons/models/m4a4/weapon_rif_m4a4.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_m4a1_silencer"] = new("weapons/models/m4a1s/weapon_rif_m4a1_s.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_famas"]         = new("weapons/models/famas/weapon_rif_famas.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_galilar"]       = new("weapons/models/galilar/weapon_rif_galilar.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_aug"]           = new("weapons/models/aug/weapon_rif_aug.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_sg556"]         = new("weapons/models/sg556/weapon_rif_sg556.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        // Keskin nisanci
        ["weapon_awp"]           = new("weapons/models/awp/weapon_snip_awp.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_ssg08"]         = new("weapons/models/ssg08/weapon_snip_ssg08.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_scar20"]        = new("weapons/models/scar20/weapon_snip_scar20.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_g3sg1"]         = new("weapons/models/g3sg1/weapon_snip_g3sg1.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        // SMG
        ["weapon_p90"]           = new("weapons/models/p90/weapon_smg_p90.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_mp9"]           = new("weapons/models/mp9/weapon_smg_mp9.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_mac10"]         = new("weapons/models/mac10/weapon_smg_mac10.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_ump45"]         = new("weapons/models/ump45/weapon_smg_ump45.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_mp7"]           = new("weapons/models/mp7/weapon_smg_mp7.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_mp5sd"]         = new("weapons/models/mp5sd/weapon_smg_mp5sd.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_bizon"]         = new("weapons/models/bizon/weapon_smg_bizon.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        // Pompali / makineli
        ["weapon_nova"]          = new("weapons/models/nova/weapon_shot_nova.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_xm1014"]        = new("weapons/models/xm1014/weapon_shot_xm1014.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_mag7"]          = new("weapons/models/mag7/weapon_shot_mag7.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_sawedoff"]      = new("weapons/models/sawedoff/weapon_shot_sawedoff.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_negev"]         = new("weapons/models/negev/weapon_mach_negev.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        ["weapon_m249"]          = new("weapons/models/m249/weapon_mach_m249para.vmdl", gear_slot_t.GEAR_SLOT_RIFLE),
        // Tabancalar
        ["weapon_deagle"]        = new("weapons/models/deagle/weapon_pist_deagle.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        ["weapon_revolver"]      = new("weapons/models/revolver/weapon_pist_revolver.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        ["weapon_glock"]         = new("weapons/models/glock18/weapon_pist_glock18.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        ["weapon_usp_silencer"]  = new("weapons/models/usp_silencer/weapon_pist_usp_silencer.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        ["weapon_hkp2000"]       = new("weapons/models/hkp2000/weapon_pist_hkp2000.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        ["weapon_p250"]          = new("weapons/models/p250/weapon_pist_p250.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        ["weapon_cz75a"]         = new("weapons/models/cz75a/weapon_pist_cz75a.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        ["weapon_fiveseven"]     = new("weapons/models/fiveseven/weapon_pist_fiveseven.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        ["weapon_tec9"]          = new("weapons/models/tec9/weapon_pist_tec9.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        ["weapon_elite"]         = new("weapons/models/elite/weapon_pist_elite.vmdl", gear_slot_t.GEAR_SLOT_PISTOL),
        // Diger
        ["weapon_taser"]         = new("weapons/models/taser/weapon_taser.vmdl", gear_slot_t.GEAR_SLOT_KNIFE),
    };

    /// <summary>Kisa adlari (orn. "m4a4", "usp") tam silah adina cevirir.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m4a4"] = "weapon_m4a1",
        ["m4a1s"] = "weapon_m4a1_silencer",
        ["m4"] = "weapon_m4a1",
        ["usp"] = "weapon_usp_silencer",
        ["usps"] = "weapon_usp_silencer",
        ["p2000"] = "weapon_hkp2000",
        ["glock18"] = "weapon_glock",
        ["galil"] = "weapon_galilar",
        ["sg553"] = "weapon_sg556",
        ["krieg"] = "weapon_sg556",
        ["scout"] = "weapon_ssg08",
        ["cz"] = "weapon_cz75a",
        ["r8"] = "weapon_revolver",
        ["dualies"] = "weapon_elite",
        ["zeus"] = "weapon_taser",
        ["57"] = "weapon_fiveseven",
        ["mp5"] = "weapon_mp5sd",
        ["deag"] = "weapon_deagle",
    };

    public static IEnumerable<string> AllWeapons => Weapons.Keys;

    /// <summary>Kullanici girdisini (ak47, weapon_ak47, m4a4...) tam silah adina cevirir.</summary>
    public static string Normalize(string input)
    {
        var s = input.Trim().ToLowerInvariant();
        if (Aliases.TryGetValue(s, out var alias)) return alias;
        if (!s.StartsWith("weapon_")) s = "weapon_" + s;
        return s;
    }

    public static bool IsKnown(string weapon) => Weapons.ContainsKey(weapon);

    public static string? GetModel(string weapon) =>
        Weapons.TryGetValue(weapon, out var e) ? e.Model : null;

    public static gear_slot_t GetSlot(string weapon) =>
        Weapons.TryGetValue(weapon, out var e) ? e.Slot : gear_slot_t.GEAR_SLOT_INVALID;
}

