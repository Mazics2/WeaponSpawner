using System.Text.Json.Serialization;

namespace WeaponSpawner.Models;

/// <summary>
/// Haritaya yerlestirilmis tek bir silah spawner'inin kalici verisi.
/// </summary>
public class SpawnerData
{
    public int Id { get; set; }

    /// <summary>Verilecek silah (orn. weapon_ak47).</summary>
    public string Weapon { get; set; } = "weapon_ak47";

    /// <summary>Konum [x, y, z].</summary>
    public float[] Origin { get; set; } = new float[3];

    /// <summary>Aci [pitch, yaw, roll] (derece).</summary>
    public float[] Angles { get; set; } = new float[3];

    /// <summary>Alma yaricapi (birim).</summary>
    public float Radius { get; set; } = 32f;

    /// <summary>Model olcegi.</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Yaw ekseninde donme hizi (derece/sn). 0 = sabit.</summary>
    public float SpinSpeed { get; set; } = 0f;

    // ---- Calisma zamani (kaydedilmez) ----

    /// <summary>Donme animasyonu icin anlik yaw.</summary>
    [JsonIgnore] public float RuntimeYaw { get; set; }
}

