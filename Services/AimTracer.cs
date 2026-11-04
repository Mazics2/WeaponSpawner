using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Trace;

namespace WeaponSpawner.Services;

/// <summary>
/// Oyuncunun nisangahinin baktigi dunya noktasini bulur (spawner yerlestirme/tasima icin).
/// </summary>
public class AimTracer
{
    private readonly ISwiftlyCore _core;

    public AimTracer(ISwiftlyCore core) => _core = core;

    public bool TryGetAimPoint(CCSPlayerPawn pawn, out Vector point)
    {
        point = default;
        if (pawn == null || !pawn.IsValid || !pawn.AbsOrigin.HasValue) return false;

        var origin = pawn.AbsOrigin.Value;
        float viewZ = 64f;
        try
        {
            var z = pawn.ViewOffset.Z.Value;
            if (z > 16f && z < 96f) viewZ = z;
        }
        catch { }

        var start = new Vector(origin.X, origin.Y, origin.Z + viewZ);
        var angles = pawn.EyeAngles;

        TraceParams? traceParams = TraceParams.Builder()
            .WithLineRay()
            .WithObjectQuery(RnQueryObjectSet.All)
            .WithInteraction(
                MaskTrace.WorldGeometry | MaskTrace.StaticLevel | MaskTrace.CsgoOpaque |
                MaskTrace.PhysicsProp | MaskTrace.PlayerClip | MaskTrace.Window | MaskTrace.Solid,
                MaskTrace.Empty,
                MaskTrace.Empty)
            .WithCollisionGroup(CollisionGroup.Player)
            .HitSolid(true)
            .HitTrigger(false)
            .IgnoreEntity(pawn)
            .IgnoreOwner(pawn)
            .Build();

        var result = _core.Trace.TraceShapeAngle(in start, in angles, 4096f, in traceParams);
        if (!result.DidHit) return false;

        point = result.HitPoint;
        return true;
    }
}

