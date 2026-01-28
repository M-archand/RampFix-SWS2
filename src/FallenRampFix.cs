using System.Runtime.InteropServices;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Memory;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.SchemaDefinitions;
namespace FallenRampFix;

[PluginMetadata(Id = "FallenRampFix", Version = "1.0.0", Name = "FallenRampFix", Author = "zer0.k, Interesting-exe, rcnoob, Nukoooo, ported by Slime to SwiftlyS2")]
public sealed class FallenRampFix : BasePlugin
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void TryPlayerMoveDelegate(nint servicePtr, CMoveData* mv, Vector* pFirstDest, CGameTrace* pFirstTrace, bool* pIsSurfing);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void CategorizePositionDelegate(nint servicePtr, CMoveData* mv, bool stayOnGround);

    private IUnmanagedFunction<TryPlayerMoveDelegate>? _tryPlayerMoveFunc;
    private IUnmanagedFunction<CategorizePositionDelegate>? _categorizePositionFunc;

    private Guid _tryPlayerMoveHookGuid;
    private Guid _categorizePositionHookGuid;

    private static ISwiftlyCore _core = null!;

    private const int MaxPlayerCount = 65;
    private static readonly Vector[] LastValidPlaneNormal = new Vector[MaxPlayerCount];
    private static readonly Vector[] TpmOrigin = new Vector[MaxPlayerCount];
    private static readonly Vector[] TpmVelocity = new Vector[MaxPlayerCount];
    private static readonly bool[] OverridenTpm = new bool[MaxPlayerCount];
    private static readonly bool[] DidTpm = new bool[MaxPlayerCount];

    private const float RAMP_BUG_THRESHOLD = 0.98f;
    private const float RAMP_BUG_VELOCITY_THRESHOLD = 0.95f;
    private const float RAMP_PIERCE_DISTANCE = 0.0625f;
    private const float NEW_RAMP_THRESHOLD = 0.95f;
    private const float FLT_EPSILON = 1.19209e-07f;

    public FallenRampFix(ISwiftlyCore core) : base(core)
    {
        _core = core;
    }

    public override void Load(bool hotReload)
    {
        if (!Core.GameData.TryGetSignature("CCSPlayer_MovementServices::TryPlayerMove", out var tryPlayerMoveAddr))
        {
            Console.WriteLine("[FallenRampFix] Failed to get address for CCSPlayer_MovementServices::TryPlayerMove");
            return;
        }

        if (!Core.GameData.TryGetSignature("CCSPlayer_MovementServices::CategorizePosition", out var categorizePositionAddr))
        {
            Console.WriteLine("[FallenRampFix] Failed to get address for CCSPlayer_MovementServices::CategorizePosition");
            return;
        }

        _tryPlayerMoveFunc = Core.Memory.GetUnmanagedFunctionByAddress<TryPlayerMoveDelegate>(tryPlayerMoveAddr);
        _categorizePositionFunc = Core.Memory.GetUnmanagedFunctionByAddress<CategorizePositionDelegate>(categorizePositionAddr);

        _tryPlayerMoveHookGuid = _tryPlayerMoveFunc.AddHook(HookTryPlayerMove);
        _categorizePositionHookGuid = _categorizePositionFunc.AddHook(HookCategorizePosition);

        Core.Event.OnMovementServicesRunCommandHook += OnMovementServicesRunCommand;

        Console.WriteLine("[FallenRampFix] Plugin loaded successfully");
    }

    public override void Unload()
    {
        Core.Event.OnMovementServicesRunCommandHook -= OnMovementServicesRunCommand;

        _tryPlayerMoveFunc?.RemoveHook(_tryPlayerMoveHookGuid);
        _categorizePositionFunc?.RemoveHook(_categorizePositionHookGuid);

        Console.WriteLine("[FallenRampFix] Plugin unloaded");
    }

    private void OnMovementServicesRunCommand(IOnMovementServicesRunCommandHookEvent @event)
    {
        var movementServices = @event.MovementServices;
        if (movementServices == null) return;

        var pawnHandle = movementServices.Pawn;
        if (!pawnHandle.IsValid) return;

        CBasePlayerPawn? pawn = pawnHandle;
        if (pawn == null) return;

        var controller = pawn.Controller;
        if (!controller.IsValid) return;

        var slot = (int)controller.EntityIndex;
        if (slot < 0 || slot >= MaxPlayerCount) return;

        DidTpm[slot] = false;
    }

    private unsafe TryPlayerMoveDelegate HookTryPlayerMove(Func<TryPlayerMoveDelegate> next)
    {
        return (nint servicePtr, CMoveData* mv, Vector* pFirstDest, CGameTrace* pFirstTrace, bool* pIsSurfing) =>
        {
            var slot = GetPlayerSlotFromService(servicePtr);
            if (slot < 0 || slot >= MaxPlayerCount)
            {
                next()(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);
                return;
            }

            DidTpm[slot] = true;
            OverridenTpm[slot] = false;

            if (mv->Base.Velocity.LengthSquared() == 0)
            {
                next()(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);
                return;
            }

            PreTryPlayerMove(servicePtr, slot, mv, pFirstDest, pFirstTrace);
            next()(servicePtr, mv, pFirstDest, pFirstTrace, pIsSurfing);
            PostTryPlayerMove(mv, slot);
        };
    }

    private unsafe CategorizePositionDelegate HookCategorizePosition(Func<CategorizePositionDelegate> next)
    {
        return (nint servicePtr, CMoveData* mv, bool stayOnGround) =>
        {
            var slot = GetPlayerSlotFromService(servicePtr);
            if (slot < 0 || slot >= MaxPlayerCount)
            {
                next()(servicePtr, mv, stayOnGround);
                return;
            }

            var lastN = LastValidPlaneNormal[slot].Normalized();

            if (stayOnGround || lastN.Length() < 0.000001f || lastN.Z > 0.7f)
            {
                next()(servicePtr, mv, stayOnGround);
                return;
            }

            if (mv->Base.Velocity.Z > -64.0f)
            {
                next()(servicePtr, mv, stayOnGround);
                return;
            }

            var bbox = new BBox_t
            {
                Mins = new Vector(-16, -16, 0),
                Maxs = new Vector(16, 16, IsDucked(servicePtr) ? 54.0f : 72.0f)
            };

            var filter = new CTraceFilter(true)
            {
                IterateEntities = true
            };

            var origin = mv->Base.AbsOrigin;
            var groundOrigin = origin;
            groundOrigin.Z -= 2.0f;

            var trace = new CGameTrace();
            _core.Trace.TracePlayerBBox(origin, groundOrigin, bbox, filter, ref trace);

            if (MathF.Abs(trace.Fraction - 1.0f) < FLT_EPSILON)
            {
                next()(servicePtr, mv, stayOnGround);
                return;
            }

            if (trace.Fraction < 0.95f
                && trace.HitNormal.Z > 0.7f
                && lastN.Dot(trace.HitNormal.Normalized()) < RAMP_BUG_THRESHOLD)
            {
                origin += lastN * 0.0625f;
                groundOrigin = origin;
                groundOrigin.Z -= 2.0f;

                _core.Trace.TracePlayerBBox(origin, groundOrigin, bbox, filter, ref trace);

                if (trace.StartInSolid)
                {
                    next()(servicePtr, mv, stayOnGround);
                    return;
                }

                if (MathF.Abs(trace.Fraction - 1.0f) < FLT_EPSILON
                    || lastN.Dot(trace.HitNormal.Normalized()) >= RAMP_BUG_THRESHOLD)
                {
                    mv->Base.AbsOrigin = origin;
                }
            }

            next()(servicePtr, mv, stayOnGround);
        };
    }

    private static unsafe void PreTryPlayerMove(nint servicePtr, int slot, CMoveData* mv, Vector* pFirstDest, CGameTrace* pFirstTrace)
    {
        var timeLeft = _core.Engine.GlobalVars.FrameTime;
        var start = mv->Base.AbsOrigin;
        var end = Vector.Zero;

        var allFraction = 0.0f;

        var velocity = mv->Base.Velocity;
        var primalVelocity = velocity;

        var potentiallyStuck = false;

        var pm = new CGameTrace();
        var pierce = new CGameTrace();

        var bbox = new BBox_t
        {
            Mins = new Vector(-16, -16, 0),
            Maxs = new Vector(16, 16, IsDucked(servicePtr) ? 54.0f : 72.0f)
        };

        var filter = new CTraceFilter(true)
        {
            IterateEntities = true
        };

        var numPlanes = 0;
        var planes = new Vector[5];

        ReadOnlySpan<float> offsets = [0.0f, -1.0f, 1.0f];
        var test = new CGameTrace();

        for (var bumpCount = 0u; bumpCount < 4; bumpCount++)
        {
            end = start + (velocity * timeLeft);

            if (pFirstDest != null && *pFirstDest == end)
            {
                pm = *pFirstTrace;
            }
            else
            {
                _core.Trace.TracePlayerBBox(start, end, bbox, filter, ref pm);

                if (start == end)
                {
                    continue;
                }

                var isValidTrace = IsValidMovementTrace(ref pm, bbox, filter);

                if (isValidTrace && MathF.Abs(pm.Fraction - 1.0f) < FLT_EPSILON)
                {
                    break;
                }

                var lastN = LastValidPlaneNormal[slot].Normalized();
                var pmN = pm.HitNormal.Normalized();

                if (lastN.Length() > FLT_EPSILON
                    && (!isValidTrace
                        || pmN.Dot(lastN) < RAMP_BUG_THRESHOLD
                        || (potentiallyStuck && pm.Fraction == 0.0f)))
                {
                    var success = false;
                    test = default;

                    for (var i = 0; i < 3 && !success; i++)
                    {
                        for (var j = 0; j < 3 && !success; j++)
                        {
                            for (var k = 0; k < 3 && !success; k++)
                            {
                                Vector offsetDirection;

                                if (i == 0 && j == 0 && k == 0)
                                {
                                    offsetDirection = lastN;
                                }
                                else
                                {
                                    offsetDirection = new Vector(offsets[i], offsets[j], offsets[k]).Normalized();

                                    if (lastN.Dot(offsetDirection) <= 0.0f)
                                    {
                                        continue;
                                    }

                                    var testStart = start + (offsetDirection * RAMP_PIERCE_DISTANCE);
                                    _core.Trace.TracePlayerBBox(testStart, start, bbox, filter, ref test);

                                    if (!IsValidMovementTrace(ref test, bbox, filter))
                                    {
                                        continue;
                                    }
                                }

                                var goodTrace = false;
                                var hitNewPlane = false;

                                for (var ratio = 0.1f; ratio <= 1.0f; ratio += 0.1f)
                                {
                                    var ratioStart = start + (offsetDirection * ratio * RAMP_PIERCE_DISTANCE);
                                    var ratioEnd = end + (offsetDirection * ratio * RAMP_PIERCE_DISTANCE);

                                    _core.Trace.TracePlayerBBox(ratioStart, ratioEnd, bbox, filter, ref pierce);

                                    if (!IsValidMovementTrace(ref pierce, bbox, filter))
                                    {
                                        continue;
                                    }

                                    var pierceN = pierce.HitNormal.Normalized();
                                    var pmN2 = pm.HitNormal.Normalized();
                                    var lastN2 = LastValidPlaneNormal[slot].Normalized();

                                    var validPlane = pierce.Fraction < 1.0f
                                                     && pierce.Fraction > 0.1f
                                                     && pierceN.Dot(lastN2) >= RAMP_BUG_THRESHOLD;

                                    hitNewPlane = pmN2.Dot(pierceN) < NEW_RAMP_THRESHOLD
                                                  && lastN2.Dot(pierceN) > NEW_RAMP_THRESHOLD;

                                    goodTrace = MathF.Abs(pierce.Fraction - 1.0f) < (FLT_EPSILON * 4.0f) || validPlane;

                                    if (goodTrace)
                                    {
                                        break;
                                    }
                                }

                                if (goodTrace || hitNewPlane)
                                {
                                    _core.Trace.TracePlayerBBox(pierce.EndPos, end, bbox, filter, ref test);

                                    if (!IsValidMovementTrace(ref test, bbox, filter))
                                    {
                                        continue;
                                    }

                                    pm = pierce;

                                    var denom = (end - start).Length();
                                    if (denom > 1e-6f)
                                    {
                                        pm.Fraction = Math.Clamp(
                                            (pierce.EndPos - pierce.StartPos).Length() / denom,
                                            0.0f,
                                            1.0f);
                                    }
                                    else
                                    {
                                        pm.Fraction = 0.0f;
                                    }

                                    pm.EndPos = test.EndPos;

                                    if (pierce.HitNormal.LengthSquared() > 0.0f)
                                    {
                                        pm.HitNormal = pierce.HitNormal;
                                        LastValidPlaneNormal[slot] = pierce.HitNormal.Normalized();
                                    }
                                    else
                                    {
                                        pm.HitNormal = test.HitNormal;
                                        LastValidPlaneNormal[slot] = test.HitNormal.Normalized();
                                    }

                                    success = true;
                                    OverridenTpm[slot] = true;
                                }
                            }
                        }
                    }
                }

                var n = pm.HitNormal.Normalized();
                if (n.Length() > FLT_EPSILON)
                {
                    LastValidPlaneNormal[slot] = n;
                }

                potentiallyStuck = pm.Fraction == 0.0f;
            }

            if (pm.Fraction * velocity.Length() > 0.03125f || pm.Fraction > 0.03125f)
            {
                allFraction += pm.Fraction;
                start = pm.EndPos;
                numPlanes = 0;
            }

            if (MathF.Abs(allFraction - 1.0f) < FLT_EPSILON)
            {
                break;
            }

            timeLeft -= _core.Engine.GlobalVars.FrameTime * pm.Fraction;

            if (numPlanes >= 5 || (pm.HitNormal.Z >= 0.7f && velocity.Length2D() < 1.0f))
            {
                velocity = Vector.Zero;
                break;
            }

            planes[numPlanes] = pm.HitNormal.Normalized();
            numPlanes++;

            var pawn = GetPawnFromService(servicePtr);
            var isOnGround = pawn?.GroundEntity.IsValid ?? false;

            if (numPlanes == 1 && pawn?.MoveType == MoveType_t.MOVETYPE_WALK && !isOnGround)
            {
                ClipVelocity(velocity, planes[0], out velocity);
            }
            else
            {
                uint i;

                for (i = 0; i < numPlanes; i++)
                {
                    ClipVelocity(velocity, planes[i], out velocity);
                    uint jj;

                    for (jj = 0; jj < numPlanes; jj++)
                    {
                        if (jj == i)
                        {
                            continue;
                        }

                        if (velocity.Dot(planes[jj]) < 0)
                        {
                            break;
                        }
                    }

                    if (jj == numPlanes)
                    {
                        break;
                    }
                }

                if (i != numPlanes)
                {
                    // go along this plane
                }
                else
                {
                    if (numPlanes != 2)
                    {
                        velocity = Vector.Zero;
                        break;
                    }

                    var dir = planes[0].Cross(planes[1]);
                    dir.Normalize();
                    var d = dir.Dot(velocity);
                    velocity = dir * d;

                    if (velocity.Dot(primalVelocity) <= 0)
                    {
                        velocity = Vector.Zero;
                        break;
                    }
                }
            }
        }

        TpmOrigin[slot] = pm.EndPos;
        TpmVelocity[slot] = velocity;
    }

    private static unsafe void PostTryPlayerMove(CMoveData* mv, int slot)
    {
        var dot = TpmVelocity[slot].Normalized().Dot(mv->Base.Velocity.Normalized());

        var velocityHeavilyModified =
            dot < RAMP_BUG_THRESHOLD
            || (TpmVelocity[slot].Length() > 50.0f
                && mv->Base.Velocity.Length() / TpmVelocity[slot].Length() < RAMP_BUG_VELOCITY_THRESHOLD);

        if (OverridenTpm[slot]
            && velocityHeavilyModified
            && TpmOrigin[slot] != Vector.Zero
            && TpmVelocity[slot] != Vector.Zero)
        {
            mv->Base.AbsOrigin = TpmOrigin[slot];
            mv->Base.Velocity = TpmVelocity[slot];
        }
    }

    private static bool IsValidMovementTrace(ref CGameTrace trace, BBox_t bbox, CTraceFilter filter)
    {
        if (trace.StartInSolid)
        {
            return false;
        }

        if (trace.Fraction < 1.0f
            && MathF.Abs(trace.HitNormal.X) < FLT_EPSILON
            && MathF.Abs(trace.HitNormal.Y) < FLT_EPSILON
            && MathF.Abs(trace.HitNormal.Z) < FLT_EPSILON)
        {
            return false;
        }

        if (MathF.Abs(trace.HitNormal.X) > 1.0f
            || MathF.Abs(trace.HitNormal.Y) > 1.0f
            || MathF.Abs(trace.HitNormal.Z) > 1.0f)
        {
            return false;
        }

        var stuck = new CGameTrace();
        _core.Trace.TracePlayerBBox(trace.EndPos, trace.EndPos, bbox, filter, ref stuck);

        if (stuck.StartInSolid || stuck.Fraction < 1.0f - FLT_EPSILON)
        {
            return false;
        }

        _core.Trace.TracePlayerBBox(trace.EndPos, trace.StartPos, bbox, filter, ref stuck);

        if (stuck.StartInSolid)
        {
            return false;
        }

        return true;
    }

    private static void ClipVelocity(in Vector inVec, in Vector normal, out Vector outVec, float overbounce = 1.0f)
    {
        var n = normal.Normalized();
        if (n.LengthSquared() < 1e-12f)
        {
            outVec = inVec;
            return;
        }

        var backoff = ((inVec.X * n.X) + (inVec.Y * n.Y) + (inVec.Z * n.Z)) * overbounce;

        outVec = inVec - (n * backoff);

        if (MathF.Abs(outVec.X) < 1e-6f) outVec.X = 0;
        if (MathF.Abs(outVec.Y) < 1e-6f) outVec.Y = 0;
        if (MathF.Abs(outVec.Z) < 1e-6f) outVec.Z = 0;
    }

    private static int GetPlayerSlotFromService(nint servicePtr)
    {
        var pawn = GetPawnFromService(servicePtr);
        if (pawn == null) return -1;

        var controller = pawn.Controller;
        if (!controller.IsValid) return -1;

        return (int)controller.EntityIndex;
    }

    private static CBasePlayerPawn? GetPawnFromService(nint servicePtr)
    {
        try
        {
            var movementServices = Helper.AsSchema<CCSPlayer_MovementServices>(servicePtr);
            if (movementServices == null) return null;

            var pawnHandle = movementServices.Pawn;
            if (!pawnHandle.IsValid) return null;

            return pawnHandle;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDucked(nint servicePtr)
    {
        try
        {
            var movementServices = Helper.AsSchema<CCSPlayer_MovementServices>(servicePtr);
            return movementServices?.Ducked ?? false;
        }
        catch
        {
            return false;
        }
    }
}
