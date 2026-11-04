namespace WeaponSpawner.Config;

/// <summary>
/// configs/plugins/WeaponSpawner/config.json (veya eklenti veri klasoru) icindeki ayarlar.
/// Dosya ilk yuklemede otomatik olusturulur; eksik alanlar her yuklemede eklenir.
/// </summary>
public class PluginConfig
{
    /// <summary>Ana komut adi. Sohbette !wsp, konsolda sw_wsp.</summary>
    public string Command { get; set; } = "wsp";

    /// <summary>Ek komut adlari.</summary>
    public List<string> Aliases { get; set; } = new() { "spawner", "silah", "wspawner", "silahspawner" };

    /// <summary>Spawner yonetimi icin gereken yetkiler (herhangi biri yeterli). Bos liste = herkes.</summary>
    public List<string> Permissions { get; set; } = new() { "weaponspawner.admin", "admin.root" };

    public string ChatPrefix { get; set; } = "[gold][Silah Spawner][default]";

    /// <summary>Yeni spawner'in varsayilan alma yaricapi (birim).</summary>
    public float DefaultRadius { get; set; } = 32f;

    /// <summary>Yeni spawner nisan noktasinin ne kadar ustune konulacak (birim).</summary>
    public float DefaultHeightOffset { get; set; } = 16f;

    /// <summary>Oyuncunun ayak hizasindan itibaren algilama yuksekligi (birim).</summary>
    public float PickupHeight { get; set; } = 72f;

    /// <summary>Ayni oyuncunun ayni spawner'dan tekrar alabilmesi icin gereken sure (sn).
    /// Ayrica oyuncunun alandan cikip tekrar girmesi gerekir.</summary>
    public float PickupCooldownSeconds { get; set; } = 1.0f;

    /// <summary>Elde bicak varken bicagi silme (sadece yeni silahi ver). false = bicak da silinir.</summary>
    public bool KeepKnife { get; set; } = true;

    /// <summary>Elde C4 varken C4'u silme.</summary>
    public bool KeepC4 { get; set; } = true;

    /// <summary>Yeni silahin girecegi slottaki (birincil/ikincil) mevcut silahi da sil.
    /// Boylece oyuncunun eline cakisan iki silah gelmez ve yere silah dusmez.</summary>
    public bool ClearTargetSlot { get; set; } = true;

    /// <summary>Spawner modellerine parlama (glow) uygula. (Varsayilan: false)</summary>
    public bool Glow { get; set; } = false;

    /// <summary>Glow rengi (hex).</summary>
    public string GlowColor { get; set; } = "#FFB000";

    /// <summary>Glow gorunme mesafesi (birim).</summary>
    public int GlowRange { get; set; } = 1500;

    /// <summary>Silah alininca oyuncuya sohbetten mesaj gonder (false = sessiz).</summary>
    public bool NotifyOnPickup { get; set; } = false;

    /// <summary>
    /// Silah -> dunya modeli eslemesi. Yerlesik tabloda olmayan veya yanlis olan silahlar
    /// icin buradan override yapabilirsiniz. Ornek: "weapon_ak47": "weapons/models/ak47/weapon_rif_ak47.vmdl"
    /// </summary>
    public Dictionary<string, string> ModelOverrides { get; set; } = new();
}

