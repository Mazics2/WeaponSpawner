using System.Globalization;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Players;
using WeaponSpawner.Config;
using WeaponSpawner.Models;

namespace WeaponSpawner.Services;

/// <summary>
/// WeaponSpawner eklentisinin SwiftlyS2 Core.Menu tabanli yonetim arayuzu.
/// CustomHUD, CenterHTML ve Chat rendererlariyla tam uyumludur.
/// </summary>
public sealed class WeaponSpawnerMenu
{
    private const string MenuTag = "weaponspawner.menu";
    private const float NearestSearchRadius = 512f;

    private sealed record CategoryDef(string Id, string Title, string[] Weapons);

    private static readonly CategoryDef[] Categories =
    [
        new("rifles", "Tüfekler",
        [
            "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer",
            "weapon_famas", "weapon_galilar", "weapon_aug", "weapon_sg556"
        ]),
        new("snipers", "Keskin Nişancı",
        [
            "weapon_awp", "weapon_ssg08", "weapon_scar20", "weapon_g3sg1"
        ]),
        new("smg", "SMG",
        [
            "weapon_p90", "weapon_mp9", "weapon_mac10",
            "weapon_ump45", "weapon_mp7", "weapon_mp5sd", "weapon_bizon"
        ]),
        new("heavy", "Pompalı / Makineli",
        [
            "weapon_nova", "weapon_xm1014", "weapon_mag7",
            "weapon_sawedoff", "weapon_negev", "weapon_m249"
        ]),
        new("pistols", "Tabancalar",
        [
            "weapon_deagle", "weapon_revolver", "weapon_glock",
            "weapon_usp_silencer", "weapon_hkp2000", "weapon_p250",
            "weapon_cz75a", "weapon_fiveseven", "weapon_tec9", "weapon_elite"
        ]),
        new("other", "Diğer",
        [
            "weapon_taser"
        ])
    ];

    private sealed class DangerSubmenuComponent : SubmenuComponent
    {
        public DangerSubmenuComponent(string text, Func<IMenuSession, IMenu?> factory, string? id = null)
            : base(text, factory, id)
        {
        }

        public override MenuNode Render(IMenuComponentRenderContext context)
        {
            var node = base.Render(context);
            var raw = new MenuRawNode("customhud.panorama", "danger");
            return node is MenuLineNode line
                ? line with { Children = [.. line.Children, raw] }
                : MenuLineNode.Of(node, raw);
        }
    }

    private sealed class PageState
    {
        public int? Focus { get; set; }
        public object? FocusKey { get; set; }
        public int RowsAt { get; set; }
        public List<IMenuComponent> Rows { get; } = [];
    }

    private readonly ISwiftlyCore _core;
    private readonly SpawnerStore _store;
    private readonly SpawnerManager _manager;
    private readonly AimTracer _tracer;
    private readonly PluginConfig _config;
    private readonly Action _reloadMapCallback;

    private readonly Dictionary<int, List<IMenu>> _ownedMenus = new();

    public WeaponSpawnerMenu(
        ISwiftlyCore core,
        SpawnerStore store,
        SpawnerManager manager,
        AimTracer tracer,
        PluginConfig config,
        Action reloadMapCallback)
    {
        _core = core;
        _store = store;
        _manager = manager;
        _tracer = tracer;
        _config = config;
        _reloadMapCallback = reloadMapCallback;
    }

    public void Toggle(IPlayer player)
    {
        if (player == null || !player.IsValid)
            return;

        if (_core.Menu.GetSession(player) is { IsOpen: true, Menu.Tag: string tag } && tag == MenuTag)
        {
            Close(player);
            return;
        }

        OpenMain(player);
    }

    public void OpenMain(IPlayer player)
    {
        if (player == null || !player.IsValid)
            return;

        Close(player);
        var menu = BuildMainMenu(player.Slot);
        menu.Open(player);
    }

    public void Close(IPlayer player)
    {
        if (player == null || !player.IsValid)
            return;

        if (_core.Menu.GetSession(player) is { IsOpen: true, Menu.Tag: string tag } && tag == MenuTag)
        {
            _core.Menu.CloseFor(player);
        }

        DisposeSlotMenus(player.Slot);
    }

    public void CloseAll()
    {
        try { _core.Menu.CloseAll(); } catch { }

        foreach (int slot in _ownedMenus.Keys.ToList())
        {
            DisposeSlotMenus(slot);
        }
        _ownedMenus.Clear();
    }

    public void OnPlayerDisconnected(int slot)
    {
        DisposeSlotMenus(slot);
    }

    private IMenu Track(int slot, IMenu menu)
    {
        menu.Tag = MenuTag;
        if (!_ownedMenus.TryGetValue(slot, out var list))
        {
            list = new List<IMenu>();
            _ownedMenus[slot] = list;
        }
        list.Add(menu);
        return menu;
    }

    private void DisposeSlotMenus(int slot)
    {
        if (!_ownedMenus.TryGetValue(slot, out var list))
            return;

        foreach (var menu in list)
        {
            try { menu.Dispose(); } catch { }
        }
        list.Clear();
        _ownedMenus.Remove(slot);
    }

    // ==============================================================================
    // 1. ANA MENÜ
    // ==============================================================================

    private IMenu BuildMainMenu(int ownerSlot)
    {
        var createCatMenu = BuildWeaponCategoryMenu(ownerSlot, targetSpawnerId: null, editMenuToJumpBack: null);
        var listMenu = BuildListMenu(ownerSlot);
        var settingsMenu = BuildSettingsMenu(ownerSlot);

        var menu = _core.Menu.CreateMenu("weaponspawner.main")
            .WithTitle("SİLAH SPAWNER")
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"Harita: {_store.CurrentMap} • {_store.Spawners.Count} Spawner"
            })
            .Add(new SubmenuComponent("Nişan Noktasına Spawner Ekle", createCatMenu, "create"))
            .Add(new SubmenuComponent("Harita Spawnerları", listMenu, "list")
            {
                TextProvider = _ => $"Harita Spawnerları\t{_store.Spawners.Count}"
            })
            .Add(new SubmenuComponent("En Yakın Spawner'ı Düzenle", session =>
            {
                var nearest = FindNearest(session.Player);
                if (nearest == null)
                {
                    Reply(session.Player, $"[red]Yakınınızda ({NearestSearchRadius:0} birim içinde) spawner yok.[default]");
                    return null;
                }
                return BuildEditMenu(ownerSlot, nearest.Id, parentForDelete: session.Menu);
            }, "nearest"))
            .Add(new SubmenuComponent("Eklenti Ayarları", settingsMenu, "settings"))
            .Add(new ButtonComponent("Harita Spawnerlarını Yenile", ctx =>
            {
                _reloadMapCallback();
                Reply(ctx.Player, $"Spawner'lar yeniden yüklendi: [lime]{_store.Spawners.Count}[default]");
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Build();

        return Track(ownerSlot, menu);
    }

    // ==============================================================================
    // 2. SİLAH KATEGORİ VE SİLAH SEÇİMİ (EKLE / DEĞİŞTİR)
    // ==============================================================================

    private IMenu BuildWeaponCategoryMenu(int ownerSlot, int? targetSpawnerId, IMenu? editMenuToJumpBack)
    {
        string title = targetSpawnerId.HasValue ? $"#{targetSpawnerId.Value} SİLAH DEĞİŞTİR" : "SPAWNER EKLE";
        string menuId = targetSpawnerId.HasValue ? "weaponspawner.change_weapon_cat" : "weaponspawner.create_cat";

        var builder = _core.Menu.CreateMenu(menuId)
            .WithTitle(title)
            .AddHeader(new TextComponent("Silah Kategorisi Seçin"));

        var knownSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in Categories)
        {
            foreach (var w in cat.Weapons)
                knownSet.Add(w);

            var list = cat.Weapons.ToList();
            if (cat.Id == "other")
            {
                foreach (var ov in _config.ModelOverrides.Keys)
                {
                    if (!knownSet.Contains(ov) && _manager.ResolveModel(ov) != null)
                        list.Add(ov);
                }
            }

            var capturedList = list;
            string catTitle = cat.Title;
            builder.Add(new SubmenuComponent($"{catTitle}\t{capturedList.Count}", _ =>
                BuildWeaponListMenu(ownerSlot, catTitle, capturedList, targetSpawnerId, editMenuToJumpBack)));
        }

        return Track(ownerSlot, builder.Build());
    }

    private IMenu BuildWeaponListMenu(
        int ownerSlot,
        string categoryTitle,
        IReadOnlyList<string> weapons,
        int? targetSpawnerId,
        IMenu? editMenuToJumpBack)
    {
        string menuId = targetSpawnerId.HasValue ? "weaponspawner.change_weapon_list" : "weaponspawner.create_list";
        var builder = _core.Menu.CreateMenu(menuId)
            .WithTitle(categoryTitle.ToUpperInvariant())
            .AddHeader(new TextComponent(targetSpawnerId.HasValue
                ? $"#{targetSpawnerId.Value} için yeni silah seçin"
                : "Nişan aldığınız noktaya yerleştirilecek silahı seçin"));

        foreach (var weapon in weapons)
        {
            string weaponId = weapon;
            string display = SpawnerManager.Pretty(weaponId);

            if (targetSpawnerId.HasValue)
            {
                builder.Add(new ButtonComponent(display, ctx =>
                {
                    var spawner = _store.Get(targetSpawnerId.Value);
                    if (spawner == null)
                    {
                        Reply(ctx.Player, $"[red]#{targetSpawnerId.Value} spawner artık mevcut değil.[default]");
                        ctx.Session.Close();
                        return ValueTask.CompletedTask;
                    }

                    spawner.Weapon = weaponId;
                    _store.Save();
                    _manager.Spawn(spawner);
                    Reply(ctx.Player, $"[lime]#{spawner.Id}[default] artık {SpawnerManager.Pretty(weaponId)} veriyor.");

                    if (editMenuToJumpBack != null)
                    {
                        var cur = ctx.Session.Menu;
                        var prevParent = cur.Parent;
                        cur.Parent = editMenuToJumpBack;
                        ctx.Session.Close();
                        cur.Parent = prevParent;
                    }
                    else
                    {
                        ctx.Session.Close();
                    }

                    return ValueTask.CompletedTask;
                }));
            }
            else
            {
                builder.Add(new SubmenuComponent(display, session =>
                {
                    var created = CreateSpawnerAtAim(session.Player, weaponId);
                    return created != null ? BuildEditMenu(ownerSlot, created.Id, parentForDelete: null) : null;
                }));
            }
        }

        return Track(ownerSlot, builder.Build());
    }

    private SpawnerData? CreateSpawnerAtAim(IPlayer player, string weapon)
    {
        if (_manager.ResolveModel(weapon) == null)
        {
            Reply(player, $"[red]'{weapon}' için model bulunamadı.[default]");
            return null;
        }

        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid || !pawn.AbsOrigin.HasValue)
        {
            Reply(player, "[red]Spawner koymak için hayatta olmalısın.[default]");
            return null;
        }

        if (!_tracer.TryGetAimPoint(pawn, out var point))
        {
            point = pawn.AbsOrigin.Value;
        }

        var data = new SpawnerData
        {
            Weapon = weapon,
            Origin = [point.X, point.Y, point.Z + _config.DefaultHeightOffset],
            Angles = [0f, NormalizeAngle(pawn.EyeAngles.Y), 0f],
            Radius = _config.DefaultRadius
        };

        _store.Add(data);
        _manager.Spawn(data);
        Reply(player, $"[lime]#{data.Id}[default] {SpawnerManager.Pretty(weapon)} spawner'ı oluşturuldu. ({Fmt(data.Origin)})");
        return data;
    }

    // ==============================================================================
    // 3. SPAWNER LİSTESİ
    // ==============================================================================

    private IMenu BuildListMenu(int ownerSlot)
    {
        var page = new PageState { RowsAt = 0 };
        var menu = _core.Menu.CreateMenu("weaponspawner.list")
            .WithTitle("HARİTA SPAWNERLARI")
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"{_store.CurrentMap} • Toplam {_store.Spawners.Count} Spawner"
            })
            .Build();

        menu.Opened += session =>
        {
            ReplaceRows(menu, page, BuildListRows(ownerSlot, session.Player, menu));
            RestoreFocus(session, page);
        };

        menu.FocusChanged += session =>
        {
            page.Focus = session.FocusedIndex;
            page.FocusKey = (session.FocusedComponent as MenuComponentBase)?.Tag;
        };

        return Track(ownerSlot, menu);
    }

    private IReadOnlyList<IMenuComponent> BuildListRows(int ownerSlot, IPlayer viewer, IMenu listMenu)
    {
        if (_store.Spawners.Count == 0)
            return [new TextComponent("Bu haritada henüz spawner yok.")];

        Vector? viewerPos = viewer.PlayerPawn is { IsValid: true, AbsOrigin: { } org } ? org : null;
        var rows = new List<IMenuComponent>(_store.Spawners.Count);

        foreach (var s in _store.Spawners)
        {
            int spawnerId = s.Id;
            string weaponName = SpawnerManager.Pretty(s.Weapon);
            bool alive = _manager.IsSpawned(spawnerId);

            string rightNote = alive ? $"r={s.Radius:0}" : "MODEL YOK";
            if (viewerPos.HasValue)
            {
                float dx = s.Origin[0] - viewerPos.Value.X;
                float dy = s.Origin[1] - viewerPos.Value.Y;
                float dz = s.Origin[2] - viewerPos.Value.Z;
                float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
                rightNote = alive ? $"{dist:0}u" : $"{dist:0}u (!)";
            }

            rows.Add(new SubmenuComponent($"#{spawnerId} {weaponName}\t{rightNote}", _ =>
                BuildEditMenu(ownerSlot, spawnerId, parentForDelete: listMenu))
            {
                Tag = spawnerId
            });
        }

        return rows;
    }

    // ==============================================================================
    // 4. SPAWNER DÜZENLEME & TRANSFORM
    // ==============================================================================

    private IMenu BuildEditMenu(int ownerSlot, int spawnerId, IMenu? parentForDelete)
    {
        var initial = _store.Get(spawnerId);
        if (initial == null)
        {
            return Track(ownerSlot, _core.Menu.CreateMenu("weaponspawner.edit")
                .WithTitle($"SPAWNER #{spawnerId}")
                .Add(new TextComponent("Bu spawner artık mevcut değil."))
                .Build());
        }

        var builder = _core.Menu.CreateMenu("weaponspawner.edit")
            .WithTitle($"SPAWNER #{spawnerId}")
            .AddHeader(new TextComponent
            {
                TextProvider = _ =>
                {
                    var cur = _store.Get(spawnerId);
                    if (cur == null) return "Silinmiş spawner";
                    return $"{SpawnerManager.Pretty(cur.Weapon)} • ({Fmt(cur.Origin)})";
                }
            });

        IMenu editMenu = null!;

        builder.Add(new SubmenuComponent("Silahı Değiştir", _ =>
            BuildWeaponCategoryMenu(ownerSlot, spawnerId, editMenu))
        {
            TextProvider = _ =>
            {
                var cur = _store.Get(spawnerId);
                return cur != null ? $"Silahı Değiştir\t{SpawnerManager.Pretty(cur.Weapon)}" : "Silahı Değiştir";
            }
        });

        builder.Add(new SliderComponent("Alma Yarıçapı", 16f, 160f, Math.Clamp(initial.Radius, 16f, 160f), 4f, change =>
        {
            var cur = _store.Get(spawnerId);
            if (cur == null) return;
            cur.Radius = change.NewValue;
            _store.Save();
        })
        {
            TotalBars = 10,
            ValueFormat = "0",
            DefaultValueProvider = _ =>
            {
                var cur = _store.Get(spawnerId);
                return cur != null ? Math.Clamp(cur.Radius, 16f, 160f) : _config.DefaultRadius;
            }
        });

        builder.Add(new SliderComponent("Model Ölçeği", 0.5f, 3.0f, Math.Clamp(initial.Scale, 0.5f, 3.0f), 0.1f, change =>
        {
            var cur = _store.Get(spawnerId);
            if (cur == null) return;
            cur.Scale = MathF.Round(change.NewValue, 2);
            Commit(cur);
        })
        {
            TotalBars = 10,
            ValueFormat = "0.0",
            DefaultValueProvider = _ =>
            {
                var cur = _store.Get(spawnerId);
                return cur != null ? Math.Clamp(cur.Scale, 0.5f, 3.0f) : 1.0f;
            }
        });

        builder.Add(new SliderComponent("Dönme Hızı (°/sn)", -180f, 180f, Math.Clamp(initial.SpinSpeed, -180f, 180f), 15f, change =>
        {
            var cur = _store.Get(spawnerId);
            if (cur == null) return;
            cur.SpinSpeed = change.NewValue;
            Commit(cur);
        })
        {
            TotalBars = 10,
            ValueFormat = "0",
            DefaultValueProvider = _ =>
            {
                var cur = _store.Get(spawnerId);
                return cur != null ? Math.Clamp(cur.SpinSpeed, -180f, 180f) : 0f;
            }
        });

        builder.Add(new SubmenuComponent("Konum & Açı Ayarları", _ => BuildTransformMenu(ownerSlot, spawnerId)));

        builder.Add(new ButtonComponent("Yanına Işınlan", ctx =>
        {
            var cur = _store.Get(spawnerId);
            if (cur == null)
            {
                Reply(ctx.Player, $"[red]#{spawnerId} artık mevcut değil.[default]");
                return ValueTask.CompletedTask;
            }

            var pos = new Vector(
                cur.Origin[0] + cur.Radius + 24f,
                cur.Origin[1],
                cur.Origin[2] - _config.DefaultHeightOffset + 4f);
            ctx.Player.Teleport(pos, null, Vector.Zero);
            Reply(ctx.Player, $"[lime]#{cur.Id}[default] yanına ışınlandın.");
            CloseTree(ctx.Session);
            return ValueTask.CompletedTask;
        }));

        builder.Add(new ButtonComponent("Modeli Yeniden Oluştur", ctx =>
        {
            var cur = _store.Get(spawnerId);
            if (cur == null) return ValueTask.CompletedTask;
            _manager.Spawn(cur);
            Reply(ctx.Player, $"[lime]#{cur.Id}[default] modeli yeniden oluşturuldu.");
            ctx.Session.Invalidate();
            return ValueTask.CompletedTask;
        }));

        builder.Add(new DangerSubmenuComponent("Spawner'ı Sil", _ =>
            BuildConfirmDeleteMenu(ownerSlot, spawnerId, editMenu, parentForDelete)));

        editMenu = Track(ownerSlot, builder.Build());
        return editMenu;
    }

    private IMenu BuildTransformMenu(int ownerSlot, int spawnerId)
    {
        var menu = _core.Menu.CreateMenu("weaponspawner.transform")
            .WithTitle($"#{spawnerId} KONUM & AÇI")
            .AddHeader(new TextComponent
            {
                TextProvider = _ =>
                {
                    var cur = _store.Get(spawnerId);
                    return cur != null
                        ? $"Konum: ({Fmt(cur.Origin)}) | Açı: ({Fmt(cur.Angles)})"
                        : "Spawner bulunamadı";
                }
            })
            .Add(new ButtonComponent("Nişan Aldığım Yere Taşı", ctx =>
            {
                var cur = _store.Get(spawnerId);
                var pawn = ctx.Player.PlayerPawn;
                if (cur == null || pawn == null || !pawn.IsValid) return ValueTask.CompletedTask;
                if (!_tracer.TryGetAimPoint(pawn, out var point))
                {
                    Reply(ctx.Player, "[red]Nişan alınan nokta bulunamadı.[default]");
                    return ValueTask.CompletedTask;
                }

                cur.Origin = [point.X, point.Y, point.Z + _config.DefaultHeightOffset];
                Commit(cur);
                Reply(ctx.Player, $"[lime]#{cur.Id}[default] taşındı: ({Fmt(cur.Origin)})");
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent("Baktığım Yöne Çevir (Face Me)", ctx =>
            {
                var cur = _store.Get(spawnerId);
                var pawn = ctx.Player.PlayerPawn;
                if (cur == null || pawn == null || !pawn.IsValid) return ValueTask.CompletedTask;

                cur.Angles[1] = NormalizeAngle(pawn.EyeAngles.Y);
                Commit(cur);
                Reply(ctx.Player, $"[lime]#{cur.Id}[default] baktığın yöne çevrildi ({cur.Angles[1]:0.#}°).");
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent("Yaw +90° Döndür", ctx => RotateAxis(ctx.Session, spawnerId, 1, 90f)))
            .Add(new ButtonComponent("Yaw -90° Döndür", ctx => RotateAxis(ctx.Session, spawnerId, 1, -90f)))
            .Add(new ButtonComponent("Yukarı +8 Birim (Z+)", ctx => NudgeOrigin(ctx.Session, spawnerId, 0f, 0f, 8f)))
            .Add(new ButtonComponent("Aşağı -8 Birim (Z-)", ctx => NudgeOrigin(ctx.Session, spawnerId, 0f, 0f, -8f)))
            .Add(new ButtonComponent("X +8 Birim", ctx => NudgeOrigin(ctx.Session, spawnerId, 8f, 0f, 0f)))
            .Add(new ButtonComponent("X -8 Birim", ctx => NudgeOrigin(ctx.Session, spawnerId, -8f, 0f, 0f)))
            .Add(new ButtonComponent("Y +8 Birim", ctx => NudgeOrigin(ctx.Session, spawnerId, 0f, 8f, 0f)))
            .Add(new ButtonComponent("Y -8 Birim", ctx => NudgeOrigin(ctx.Session, spawnerId, 0f, -8f, 0f)))
            .Add(new ButtonComponent("Pitch & Roll Sıfırla", ctx =>
            {
                var cur = _store.Get(spawnerId);
                if (cur == null) return ValueTask.CompletedTask;
                cur.Angles[0] = 0f;
                cur.Angles[2] = 0f;
                Commit(cur);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Build();

        return Track(ownerSlot, menu);
    }

    private ValueTask RotateAxis(IMenuSession session, int spawnerId, int axis, float delta)
    {
        var cur = _store.Get(spawnerId);
        if (cur == null) return ValueTask.CompletedTask;
        cur.Angles[axis] = NormalizeAngle(cur.Angles[axis] + delta);
        Commit(cur);
        session.Invalidate();
        return ValueTask.CompletedTask;
    }

    private ValueTask NudgeOrigin(IMenuSession session, int spawnerId, float dx, float dy, float dz)
    {
        var cur = _store.Get(spawnerId);
        if (cur == null) return ValueTask.CompletedTask;
        cur.Origin = [cur.Origin[0] + dx, cur.Origin[1] + dy, cur.Origin[2] + dz];
        Commit(cur);
        session.Invalidate();
        return ValueTask.CompletedTask;
    }

    private IMenu BuildConfirmDeleteMenu(int ownerSlot, int spawnerId, IMenu editMenu, IMenu? parentForDelete)
    {
        var s = _store.Get(spawnerId);
        string weapon = s != null ? SpawnerManager.Pretty(s.Weapon) : "Bilinmeyen";

        IMenu confirmMenu = null!;
        confirmMenu = _core.Menu.CreateMenu("weaponspawner.confirm_delete")
            .WithTitle($"#{spawnerId} SİLİNSİN Mİ?")
            .AddHeader(new TextComponent($"{weapon} spawner'ı haritadan kalıcı olarak kaldırılacak"))
            .Add(new ButtonComponent("Evet, Kalıcı Olarak Sil", ctx =>
            {
                var cur = _store.Get(spawnerId);
                if (cur != null)
                {
                    _manager.Delete(cur.Id);
                    _store.Remove(cur.Id);
                    Reply(ctx.Player, $"[red]#{cur.Id}[default] {SpawnerManager.Pretty(cur.Weapon)} spawner'ı silindi.");
                }

                var jumpTarget = parentForDelete ?? editMenu.Parent;
                confirmMenu.Parent = jumpTarget;
                ctx.Session.Close();
                return ValueTask.CompletedTask;
            }))
            .Add(new ButtonComponent("İptal / Geri Dön", ctx =>
            {
                ctx.Session.Close();
                return ValueTask.CompletedTask;
            }))
            .Build();

        return Track(ownerSlot, confirmMenu);
    }

    // ==============================================================================
    // 5. EKLENTİ AYARLARI
    // ==============================================================================

    private IMenu BuildSettingsMenu(int ownerSlot)
    {
        var menu = _core.Menu.CreateMenu("weaponspawner.settings")
            .WithTitle("SPAWNER AYARLARI")
            .AddHeader(new TextComponent("Genel Davranış ve Varsayılanlar"))
            .Add(new ToggleComponent("Model Parlaması (Glow)", _config.Glow, change =>
            {
                _config.Glow = change.NewValue;
                SpawnerStore.SaveConfig(_core, _config);
                _manager.SpawnAll();
                Reply(change.Player, $"Model glow: {(change.NewValue ? "[lime]Açık" : "[red]Kapalı")}");
            })
            {
                DefaultValueProvider = _ => _config.Glow
            })
            .Add(new ToggleComponent("Bıçağı Koru (Silme)", _config.KeepKnife, change =>
            {
                _config.KeepKnife = change.NewValue;
                SpawnerStore.SaveConfig(_core, _config);
            })
            {
                DefaultValueProvider = _ => _config.KeepKnife
            })
            .Add(new ToggleComponent("C4'ü Koru (Silme)", _config.KeepC4, change =>
            {
                _config.KeepC4 = change.NewValue;
                SpawnerStore.SaveConfig(_core, _config);
            })
            {
                DefaultValueProvider = _ => _config.KeepC4
            })
            .Add(new ToggleComponent("Hedef Slottaki Silahı Sil", _config.ClearTargetSlot, change =>
            {
                _config.ClearTargetSlot = change.NewValue;
                SpawnerStore.SaveConfig(_core, _config);
            })
            {
                DefaultValueProvider = _ => _config.ClearTargetSlot
            })
            .Add(new ToggleComponent("Silah Alınca Mesaj Gönder", _config.NotifyOnPickup, change =>
            {
                _config.NotifyOnPickup = change.NewValue;
                SpawnerStore.SaveConfig(_core, _config);
            })
            {
                DefaultValueProvider = _ => _config.NotifyOnPickup
            })
            .Add(new SliderComponent("Varsayılan Yarıçap", 16f, 128f, Math.Clamp(_config.DefaultRadius, 16f, 128f), 4f, change =>
            {
                _config.DefaultRadius = change.NewValue;
                SpawnerStore.SaveConfig(_core, _config);
            })
            {
                TotalBars = 10,
                ValueFormat = "0",
                DefaultValueProvider = _ => Math.Clamp(_config.DefaultRadius, 16f, 128f)
            })
            .Add(new SliderComponent("Varsayılan Yükseklik Ofseti", 0f, 64f, Math.Clamp(_config.DefaultHeightOffset, 0f, 64f), 2f, change =>
            {
                _config.DefaultHeightOffset = change.NewValue;
                SpawnerStore.SaveConfig(_core, _config);
            })
            {
                TotalBars = 10,
                ValueFormat = "0",
                DefaultValueProvider = _ => Math.Clamp(_config.DefaultHeightOffset, 0f, 64f)
            })
            .Build();

        return Track(ownerSlot, menu);
    }

    // ==============================================================================
    // YARDIMCILAR
    // ==============================================================================

    private SpawnerData? FindNearest(IPlayer player)
    {
        var pawn = player.PlayerPawn;
        if (pawn == null || !pawn.IsValid || !pawn.AbsOrigin.HasValue)
            return null;

        var pos = pawn.AbsOrigin.Value;
        SpawnerData? best = null;
        float bestDist = NearestSearchRadius * NearestSearchRadius;
        foreach (var s in _store.Spawners)
        {
            float dx = s.Origin[0] - pos.X;
            float dy = s.Origin[1] - pos.Y;
            float dz = s.Origin[2] - pos.Z;
            float d = dx * dx + dy * dy + dz * dz;
            if (d < bestDist)
            {
                bestDist = d;
                best = s;
            }
        }
        return best;
    }

    private void Commit(SpawnerData s)
    {
        _store.Save();
        _manager.ApplyTransform(s);
    }

    private void Reply(IPlayer player, string msg)
    {
        if (player == null || !player.IsValid || player.IsFakeClient) return;
        player.SendChat($"{_config.ChatPrefix} {msg}");
    }

    private static void CloseTree(IMenuSession session)
    {
        var menu = session.Menu;
        var parent = menu.Parent;
        menu.Parent = null;
        session.Close();
        menu.Parent = parent;
    }

    private static float NormalizeAngle(float a)
    {
        a %= 360f;
        if (a > 180f) a -= 360f;
        if (a <= -180f) a += 360f;
        return MathF.Round(a, 2);
    }

    private static string Fmt(float[] v) =>
        string.Join(", ", v.Select(x => x.ToString("0.#", CultureInfo.InvariantCulture)));

    private static void ReplaceRows(IMenu menu, PageState page, IReadOnlyList<IMenuComponent> rows)
    {
        foreach (var row in page.Rows)
            menu.Remove(row);
        page.Rows.Clear();

        for (int i = 0; i < rows.Count; i++)
        {
            menu.Insert(MenuRegion.Body, page.RowsAt + i, rows[i]);
            page.Rows.Add(rows[i]);
        }
    }

    private static void RestoreFocus(IMenuSession session, PageState page)
    {
        if (page.FocusKey is { } key && FocusWhere(session, c => c is MenuComponentBase { Tag: { } tag } && tag.Equals(key)))
            return;

        if (page.Focus is { } focus)
        {
            for (int i = focus; i > 0; i--)
            {
                if (session.FocusedIndex == i || session.SetFocus(i))
                    return;
            }
        }
    }

    private static bool FocusWhere(IMenuSession session, Func<IMenuComponent, bool> match)
    {
        int index = 0;
        foreach (var component in session.Menu.GetComponents(MenuRegion.Body))
        {
            if (!component.IsFocusable || !component.IsVisible(session))
                continue;

            if (match(component))
            {
                if (session.FocusedIndex != index)
                    session.SetFocus(index);
                return true;
            }

            index++;
        }

        return false;
    }
}

