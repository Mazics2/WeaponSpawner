# WeaponSpawner (CS2 Silah Spawner Eklentisi)

Counter-Strike 2 (CS2) ve **SwiftlyS2** (.NET 10) için geliştirilmiş, harita üzerinde kalıcı, yönlendirilebilir ve dönen silah alma noktaları (spawner) oluşturan sunucu eklentisidir.

---

## 🎯 Özellikler

- **Haritada İstediğin Noktaya Spawner Koyma:** Nişan aldığın noktaya anında silah modeli yerleştirilir.
- **Serbest Yön ve Açı Kontrolü:** Spawner'ın pitch, yaw ve roll açılarını komutlarla veya baktığın yöne tek tuşla hizalayabilirsin.
- **Sürekli Kendi Etrafında Dönme (Opsiyonel):** Spawner modelleri havada kendi ekseni etrafında dönebilir (`!ws donme 90`).
- **Silahı Değiştirme (Eldekini Silip Yenisini Verme):**
  - Oyuncu spawner'ın üzerine geldiğinde elindeki silah güvenli bir şekilde silinir.
  - Spawner'daki yeni silah anında oyuncuya verilir ve ele seçilir.
  - Yere gereksiz silah düşmesi veya silah çakışması engellenir.
  - İsteğe bağlı bıçak ve C4 korunur (`KeepKnife`, `KeepC4`).
- **Kalıcı Spawner:** Oyuncu silahı aldığında spawner **asla kaybolmaz**; haritada kalmaya ve sonraki oyunculara hizmet vermeye devam eder.
- **Round Başı Otomatik Canlanma:** CS2'nin round başlangıçlarında entity'leri temizleme motoru davranışına karşı modeller otomatik olarak yeniden oluşturulur.
- **Harita Bazlı JSON Kayıt:** Spawner konumları ve ayarları `maps/<harita_adi>.json` dosyasına kaydedilir. Harita değişimlerinde ve sunucu yeniden başlatıldığında otomatik yüklenir.
- **Glow (Parlama / Işıma) Desteği:** Silah modelleri uzaktan belirgin olması için ışıldar (renk ve mesafe yapılandırılabilir).
- **Zengin Silah ve Model Kataloğu:** AK-47, M4A4, M4A1-S, AWP, Scout, Deagle, USP-S, Glock ve diğer tüm CS2 silahları doğrudan desteklenir.

---

## 📥 Kurulum

1. `WeaponSpawner.dll` dosyasını derleyin veya hazır derlenmiş dosyaları alın.
2. CS2 sunucunuzda eklenti klasörünü oluşturun:
   ```text
   addons/swiftlys2/plugins/WeaponSpawner/
   ```
3. Aşağıdaki dosyaları bu klasöre kopyalayın:
   - `WeaponSpawner.dll`
   - `WeaponSpawner.pdb` (opsiyonel)
   - `WeaponSpawner.deps.json`
4. Sunucu konsolundan `sw_plugins reload WeaponSpawner` yazın veya sunucuyu başlatın.

---

## 🕹️ Komutlar

Tüm komutlar sohbette `!wsp` veya `/wsp` (sessiz), sunucu konsolunda `sw_wsp` veya `wsp` ile çalıştırılabilir. Ayrıca `!spawner`, `!silah` ve `!wspawner` alternatifleri de kullanılabilir.

> [!TIP]
> Bir komutta **[id]** belirtmezseniz, sistem otomatik olarak **en yakınınızdaki** spawner'ı hedef alır!

| Komut | Açıklama | Örnek |
|---|---|---|
| `!wsp ekle <silah> [yarıçap]` | Nişan aldığın noktaya yeni spawner koyar | `!wsp ekle ak47`<br>`!wsp ekle awp 45` |
| `!wsp sil [id]` | Spawner'ı haritadan ve kayıtlardan siler | `!wsp sil`<br>`!wsp sil 2` |
| `!wsp liste` | Bu haritadaki tüm spawner'ları ve bilgilerini listeler | `!wsp liste` |
| `!wsp aci [id] <p> <y> <r>` | Spawner'ın tam açısını (pitch, yaw, roll) ayarlar | `!wsp aci 0 90 0`<br>`!wsp aci 1 15 180 0` |
| `!wsp dondur [id] <p\|y\|r> <derece>` | Belirtilen eksende verilen derece kadar çevirir | `!wsp dondur y 45`<br>`!wsp dondur p -15` |
| `!wsp hizala [id]` | Spawner'ı şu an baktığın yönle aynı açıya çevirir | `!wsp hizala` |
| `!wsp donme [id] <hız>` | Havada sürekli dönme hızını belirler (0 = sabit) | `!wsp donme 90`<br>`!wsp donme 0` |
| `!wsp tasi [id] <x> <y> <z>` | Göreceli olarak belirtilen birim kadar kaydırır | `!wsp tasi 0 0 10` (10 birim yukarı) |
| `!wsp buraya [id]` | Spawner'ı mevcut nişan aldığın noktaya taşır | `!wsp buraya 1` |
| `!wsp silah [id] <silah>` | Spawner'ın verdiği silahı ve modelini değiştirir | `!wsp silah deagle` |
| `!wsp yaricap [id] <birim>` | Silahın alınma mesafesini ayarlar | `!wsp yaricap 40` |
| `!wsp olcek [id] <oran>` | Silah modelinin boyutunu ayarlar (varsayılan: 1.0) | `!wsp olcek 1.3` |
| `!wsp git <id>` | Seni doğrudan o spawner'ın yanına ışınlar | `!wsp git 3` |
| `!wsp yenile` | Spawner'ları diskten tekrar okur ve modelleri yeniler | `!wsp yenile` |
| `!wsp silahlar` | Desteklenen tüm silahların isimlerini listeler | `!wsp silahlar` |

---

## ⚙️ Yapılandırma (`config.json`)

Eklenti ilk çalıştığında eklenti klasöründe otomatik olarak `config.json` oluşturulur:

```json
{
  "Command": "wsp",
  "Aliases": [
    "spawner",
    "silah",
    "wspawner",
    "silahspawner"
  ],
  "Permissions": [
    "weaponspawner.admin",
    "admin.root"
  ],
  "ChatPrefix": "[gold][Silah Spawner][default]",
  "DefaultRadius": 32.0,
  "DefaultHeightOffset": 16.0,
  "PickupHeight": 72.0,
  "PickupCooldownSeconds": 1.0,
  "KeepKnife": true,
  "KeepC4": true,
  "ClearTargetSlot": true,
  "Glow": true,
  "GlowColor": "#FFB000",
  "GlowRange": 1500,
  "Show3DText": false,
  "NotifyOnPickup": true,
  "ModelOverrides": {}
}
```

### Ayarların Açıklaması:
- **`Permissions`:** Komutları kullanabilecek yetki düğümleri (Swiftly yetki sistemi). Boş liste bırakırsanız yetki kontrolü yapılmaz.
- **`DefaultRadius`:** Yeni oluşturulan spawner'ların varsayılan alma yarıçapı (birim).
- **`DefaultHeightOffset`:** Nişan alınan zemin noktasından ne kadar yukarıda duracağı (zemine gömülmemesi için).
- **`PickupHeight`:** Algılama yüksekliği toleransı.
- **`PickupCooldownSeconds`:** Aynı oyuncunun aynı spawner'dan tekrar alabilmesi için gereken asgari süre (ayrıca oyuncunun alandan çıkıp tekrar girmesi gerekir).
- **`KeepKnife` / `KeepC4`:** Oyuncunun elindeki bıçak ve C4'ün silinmesini engeller (`true` önerilir).
- **`ClearTargetSlot`:** Yeni silah verildiğinde çakışan eski tüfek/tabancanın slottan temizlenmesi (`true`).
- **`Glow` & `GlowColor`:** Modellerin uzaktan parlamasını sağlar (varsayılan: `true`).
- **`Show3DText`:** Silah modelinin üstünde 3D metin (`[ AWP ]`) yazısı gösterilip gösterilmeyeceği (varsayılan: `false`).
- **`ModelOverrides`:** Özel veya atölye modelleri tanımlamak isterseniz silah adına karşılık gelen `.vmdl` yolunu buraya yazabilirsiniz.

---

## 💾 Harita Kayıtları (`maps/<harita>.json`)

Her harita için oluşturulan spawner'lar otomatik olarak kaydedilir:

```json
[
  {
    "Id": 1,
    "Weapon": "weapon_ak47",
    "Origin": [ 120.5, -450.2, 64.0 ],
    "Angles": [ 0.0, 90.0, 0.0 ],
    "Radius": 32.0,
    "Scale": 1.0,
    "SpinSpeed": 60.0
  }
]
```
Elle müdahale edebilir veya `!ws yenile` komutuyla oyunu durdurmadan değişiklikleri yükleyebilirsiniz.

