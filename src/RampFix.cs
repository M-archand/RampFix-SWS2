using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameHooks;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Trace;
using Microsoft.Extensions.Logging;
using Tomlyn;

namespace RampFix;

[PluginMetadata(Id = "RampFix", Version = "1.0.0", Name = "RampFix", Author = "Marchand")]
public sealed class FallenRampFix : BasePlugin
{
    // tpm = TryPlayerMove
    private const int MaxPlayerCount = 64;
    private static readonly Vector[] LastValidPlaneNormal = new Vector[MaxPlayerCount];
    private static readonly Vector[] TpmOrigin = new Vector[MaxPlayerCount];
    private static readonly Vector[] TpmVelocity = new Vector[MaxPlayerCount];
    private static readonly bool[] OverridenTpm = new bool[MaxPlayerCount];
    // True if TryPlayerMove ran this ProcessMovement frame; if not, lastValidPlane is stale and cleared (CS2Fixes Detour_ProcessMovement).
    private static readonly bool[] DidTpm = new bool[MaxPlayerCount];

    // CS2Fixes vec3_invalid sentinel (FLT_MAX). Used as the "not computed this frame" marker for tpm state.
    private static readonly Vector Vec3Invalid = new(float.MaxValue, float.MaxValue, float.MaxValue);

    // Player-movement trace filter (CS2Fixes CTraceFilterPlayerMovementCS); MUST exclude own pawn or every trace StartInSolid -> no fix. ShouldHitEntity reads _activePawnAddress.
    private TraceParams _playerTraceParams;
    private nint _activePawnAddress;

    // InteractsAs=PLAYER for CS2Fixes contents-mask parity; SwiftlyS2 default (InteractsAs=0) misses PLAYER_CLIP/non-solid ramps.
    private const MaskTrace PlayerInteractsAsLayer = MaskTrace.Player;

    // Fallback InteractsWith when pawn collision attr unreadable: SOLID world, WORLD_GEOMETRY, PLAYER_CLIP, CSGO movables
    private const MaskTrace FallbackInteractsWith =
        MaskTrace.Solid | MaskTrace.WorldGeometry | MaskTrace.PlayerClip | MaskTrace.CsgoMoveable;
    
    // The pawn-collision mask currently programmed into _playerTraceParams.InteractWith, to skip redundant resyncs.
    private ulong _activeInteractsWith;

    private static bool _loggedNullTpmMoveData;
    private static bool _loggedNullCategorizeMoveData;

    private class Config
    {
        public float RampBugThreshold         { get; set; } = 0.98f;
        public float RampBugVelocityThreshold { get; set; } = 0.95f;
        public float RampPierceDistance       { get; set; } = 0.0625f;
        public float NewRampThreshold         { get; set; } = 0.95f;
    }

    private static readonly TomlModelOptions s_tomlOptions = new() { ConvertPropertyName = name => name };

    private Config _config = new();
    private readonly ISwiftlyCore _core;
    private const float FLT_EPSILON = 1.19209e-07f;

    public FallenRampFix(ISwiftlyCore core) : base(core)
    {
        _core = core;
        _playerTraceParams = TraceParams.Builder()
            .WithIterateEntities(true)
            .WithCollisionGroup(CollisionGroup.Player)
            .WithShouldHitEntity(ShouldHitTraceEntity)
            .Build();
        // CS2Fixes sets m_bHitSolidRequiresGenerateContacts = true on its movement filter.
        _playerTraceParams.HitSolidRequiresGenerateContacts = true;
        // Declare trace as PLAYER layer (CS2Fixes EnableInteractsAsLayer) so player-clip ramps are hit, not passed through.
        _playerTraceParams.InteractAs = PlayerInteractsAsLayer;
        // Seed with fallback mask; replaced per-pawn by ConfigureFilterForPawn to match the engine's movement collision set.
        _playerTraceParams.InteractWith = FallbackInteractsWith;
        _activeInteractsWith = (ulong)FallbackInteractsWith;

        Array.Fill(TpmOrigin, Vec3Invalid);
        Array.Fill(TpmVelocity, Vec3Invalid);
    }

    // Exclude moving player's own pawn from traces; compare by address since the wrapper is re-resolved per callback.
    private bool ShouldHitTraceEntity(CEntityInstance entity) => entity.Address != _activePawnAddress;

    // Mirror CS2Fixes CTraceFilterPlayerMovementCS: set self-exclusion address + copy pawn's real InteractsWith mask.
    private void ConfigureFilterForPawn(CCSPlayerPawn? pawn)
    {
        _activePawnAddress = pawn?.Address ?? nint.Zero;

        ulong interactsWith;
        try
        {
            var collision = pawn?.Collision;
            interactsWith = collision is not null ? collision.CollisionAttribute.InteractsWith : 0UL;
        }
        catch
        {
            interactsWith = 0UL;
        }

        if (interactsWith == 0UL)
        {
            interactsWith = (ulong)FallbackInteractsWith;
        }

        // Ensure the player layer is included in what we interact with, matching the engine
        // player-vs-player and player-vs-playerclip behaviour the C++ filter inherits from the pawn mask
        interactsWith |= (ulong)MaskTrace.Player;

        if (interactsWith != _activeInteractsWith)
        {
            _activeInteractsWith = interactsWith;
            _playerTraceParams.InteractWith = (MaskTrace)interactsWith;
        }
    }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager)
    {
        Core.Configuration.InitializeWithTemplate("config.toml", "template.toml");
    }

    public override void Load(bool hotReload)
    {
        if (hotReload)
        {
            ClearPerSlotState();
        }

        _config = LoadConfig(Core.Configuration.GetConfigPath("config.toml"));

        // The TryPlayerMove/CategorizePosition signatures now live in SwiftlyS2 core poggers
        try
        {
            Core.GameHooks.Movement.ProcessMovement.Pre    += OnProcessMovementPre;
            Core.GameHooks.Movement.ProcessMovement.Post   += OnProcessMovementPost;
            Core.GameHooks.Movement.TryPlayerMove.Pre      += OnTryPlayerMovePre;
            Core.GameHooks.Movement.TryPlayerMove.Post     += OnTryPlayerMovePost;
            Core.GameHooks.Movement.CategorizePosition.Pre += OnCategorizePositionPre;
            Core.Event.OnClientDisconnected                += OnClientDisconnected;
        }
        catch (Exception ex)
        {
            Core.GameHooks.Movement.ProcessMovement.Pre    -= OnProcessMovementPre;
            Core.GameHooks.Movement.ProcessMovement.Post   -= OnProcessMovementPost;
            Core.GameHooks.Movement.TryPlayerMove.Pre      -= OnTryPlayerMovePre;
            Core.GameHooks.Movement.TryPlayerMove.Post     -= OnTryPlayerMovePost;
            Core.GameHooks.Movement.CategorizePosition.Pre -= OnCategorizePositionPre;
            Core.Event.OnClientDisconnected                -= OnClientDisconnected;

            Core.Logger.LogError(ex,
                "[RampFix] Failed to install movement GameHooks (TryPlayerMove / CategorizePosition). " +
                "The SwiftlyS2 core movement signatures may be outdated after a CS2 update - the ramp fix is DISABLED until core is updated.");
        }
    }

    public override void Unload()
    {
        Core.GameHooks.Movement.ProcessMovement.Pre    -= OnProcessMovementPre;
        Core.GameHooks.Movement.ProcessMovement.Post   -= OnProcessMovementPost;
        Core.GameHooks.Movement.TryPlayerMove.Pre      -= OnTryPlayerMovePre;
        Core.GameHooks.Movement.TryPlayerMove.Post     -= OnTryPlayerMovePost;
        Core.GameHooks.Movement.CategorizePosition.Pre -= OnCategorizePositionPre;
        Core.Event.OnClientDisconnected                -= OnClientDisconnected;

        ClearPerSlotState();
    }

    private static void ClearPerSlotState()
    {
        Array.Clear(LastValidPlaneNormal);
        // Match CS2Fixes: tpmOrigin/tpmVelocity default to vec3_invalid
        Array.Fill(TpmOrigin, Vec3Invalid);
        Array.Fill(TpmVelocity, Vec3Invalid);
        Array.Clear(OverridenTpm);
        Array.Clear(DidTpm);
        _loggedNullTpmMoveData = false;
        _loggedNullCategorizeMoveData = false;
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        var slot = Core.PlayerManager.GetPlayer(@event.PlayerId)?.Slot;
        if (slot is null or < 0 or >= MaxPlayerCount) return;

        LastValidPlaneNormal[slot.Value] = default;
        TpmOrigin[slot.Value] = Vec3Invalid;
        TpmVelocity[slot.Value] = Vec3Invalid;
        OverridenTpm[slot.Value] = false;
        DidTpm[slot.Value] = false;
    }

    // CS2Fixes resets lastValidPlane after any ProcessMovement frame in which TryPlayerMove did not run,
    // so a stale plane normal can never leak into the next frame's pierce/CategorizePosition checks
    private void OnProcessMovementPre(ref ProcessMovementMovementPreContext ctx)
    {
        var slot = ctx.Params.Player.Slot;
        if (slot < 0 || slot >= MaxPlayerCount) return;

        DidTpm[slot] = false;
    }

    private void OnProcessMovementPost(ref ProcessMovementMovementPostContext ctx)
    {
        var slot = ctx.Params.Player.Slot;
        if (slot < 0 || slot >= MaxPlayerCount) return;

        if (!DidTpm[slot])
        {
            LastValidPlaneNormal[slot] = default;
        }
    }

    private unsafe void OnTryPlayerMovePre(ref TryPlayerMoveMovementPreContext ctx)
    {
        var config = _config;
        var mv = ctx.Params.MoveData.TypedAddress;
        if (mv == null)
        {
            if (!_loggedNullTpmMoveData)
            {
                _loggedNullTpmMoveData = true;
                _core.Logger.LogError("RampFix: TryPlayerMove hook received a null CMoveData pointer; skipping ramp-fix logic this tick. This usually indicates a signature change which needs to be updated in SwiftlyS2 core.");
            }
            // Skip the native move (and Post) entirely
            ctx.SetHookResult(HookResult.CancelOriginal);
            return;
        }

        var player = ctx.Params.Player;
        var slot = player.Slot;
        if (slot < 0 || slot >= MaxPlayerCount)
        {
            return;
        }

        OverridenTpm[slot] = false;
        DidTpm[slot] = true;

        if (mv->Base.Velocity.LengthSquared() == 0)
        {
            // Let the original run, Post no-ops because OverridenTpm is false
            return;
        }

        PreTryPlayerMove(player, slot, mv, ctx.Params.FirstDest, ctx.Params.FirstTrace, config);
    }

    private unsafe void OnTryPlayerMovePost(ref TryPlayerMoveMovementPostContext ctx)
    {
        var mv = ctx.Params.MoveData.TypedAddress;
        if (mv == null) return;

        var slot = ctx.Params.Player.Slot;
        if (slot < 0 || slot >= MaxPlayerCount) return;

        PostTryPlayerMove(mv, slot, _config);
    }

    private unsafe void OnCategorizePositionPre(ref CategorizePositionMovementPreContext ctx)
    {
        var config = _config;
        var mv = ctx.Params.MoveData.TypedAddress;
        if (mv == null)
        {
            if (!_loggedNullCategorizeMoveData)
            {
                _loggedNullCategorizeMoveData = true;
                _core.Logger.LogError("RampFix: CategorizePosition hook received a null CMoveData pointer; skipping ramp-fix logic this tick. This usually indicates a signature change which needs to be updated in SwiftlyS2 core.");
            }
            ctx.SetHookResult(HookResult.CancelOriginal);
            return;
        }

        var player = ctx.Params.Player;
        var slot = player.Slot;
        if (slot < 0 || slot >= MaxPlayerCount) return;

        var pawn = player.PlayerPawn;
        // Self exclusion + per-pawn contents mask (mirrors CS2Fixes CTraceFilterPlayerMovementCS construction)
        ConfigureFilterForPawn(pawn);

        var lastN = LastValidPlaneNormal[slot].Normalized();

        if (ctx.Params.StayOnGround || lastN.Length() < 0.000001f || lastN.Z > 0.7f) return;

        if (mv->Base.Velocity.Z > -64.0f) return;

        var bbox = new BBox_t
        {
            Mins = new Vector(-16, -16, 0),
            Maxs = new Vector(16, 16, IsDucked(player) ? 54.0f : 72.0f)
        };

        // Read origin from the pawn scene-node (start-of-frame), matching CS2Fixes GetOrigin
        var origin = pawn?.AbsOrigin ?? mv->Base.AbsOrigin;
        var groundOrigin = origin;
        groundOrigin.Z -= 2.0f;

        var trace = new CGameTrace();
        TracePlayerBBox(origin, groundOrigin, bbox, ref trace);

        if (MathF.Abs(trace.Fraction - 1.0f) < FLT_EPSILON) return;

        if (trace.Fraction < 0.95f
            && trace.HitNormal.Z > 0.7f
            && lastN.Dot(trace.HitNormal.Normalized()) < config.RampBugThreshold)
        {
            origin += lastN * 0.0625f;
            groundOrigin = origin;
            groundOrigin.Z -= 2.0f;

            TracePlayerBBox(origin, groundOrigin, bbox, ref trace);

            if (trace.StartInSolid) return;

            if (MathF.Abs(trace.Fraction - 1.0f) < FLT_EPSILON
                || lastN.Dot(trace.HitNormal.Normalized()) >= config.RampBugThreshold)
            {
                mv->Base.AbsOrigin = origin;
            }
        }
    }

    private unsafe void PreTryPlayerMove(IPlayer player, int slot, CMoveData* mv, Vector firstDest, TraceResult firstTrace, Config config)
    {
        var pawn = player.PlayerPawn;
        // Self exclusion + per-pawn contents mask (mirrors CS2Fixes CTraceFilterPlayerMovementCS construction)
        ConfigureFilterForPawn(pawn);

        var timeLeft = _core.Engine.GlobalVars.FrameTime;
        // CS2Fixes reads the origin from the pawn's scene-node abs origin (start-of-frame)
        // Velocity and all write backs stay on CMoveData, matching CS2Fixe's asymmetric Get/Set
        var start = pawn?.AbsOrigin ?? mv->Base.AbsOrigin;
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
            Maxs = new Vector(16, 16, IsDucked(player) ? 54.0f : 72.0f)
        };

        var numPlanes = 0;
        var planes = new Vector[5];

        ReadOnlySpan<float> offsets = [0.0f, -1.0f, 1.0f];
        var test = new CGameTrace();

        for (var bumpCount = 0u; bumpCount < 4; bumpCount++)
        {
            end = start + (velocity * timeLeft);

            // Core supplies FirstDest as a zero vector when the engine passed no
            // precomputed destination, guard against that to mirror the null check
            if (firstDest != Vector.Zero && firstDest == end)
            {
                CopyTrace(in firstTrace, ref pm);
            }
            else
            {
                TracePlayerBBox(start, end, bbox, ref pm);

                if (start == end)
                {
                    continue;
                }

                var isValidTrace = IsValidMovementTrace(ref pm, bbox);

                if (isValidTrace && MathF.Abs(pm.Fraction - 1.0f) < FLT_EPSILON)
                {
                    break;
                }

                var lastN = LastValidPlaneNormal[slot].Normalized();
                var pmN = pm.HitNormal.Normalized();

                if (lastN.Length() > FLT_EPSILON
                    && (!isValidTrace
                        || pmN.Dot(lastN) < config.RampBugThreshold
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
                                    // Raw offset vector (length up to sqrt(3)), CS2Fixes does not normalize here
                                    offsetDirection = new Vector(offsets[i], offsets[j], offsets[k]);

                                    if (lastN.Dot(offsetDirection) <= 0.0f)
                                    {
                                        continue;
                                    }

                                    var testStart = start + (offsetDirection * config.RampPierceDistance);
                                    TracePlayerBBox(testStart, start, bbox, ref test);

                                    if (!IsValidMovementTrace(ref test, bbox))
                                    {
                                        continue;
                                    }
                                }

                                var goodTrace = false;
                                var hitNewPlane = false;

                                for (var ratio = 0.25f; ratio <= 1.0f; ratio += 0.25f)
                                {
                                    var ratioStart = start + (offsetDirection * ratio * config.RampPierceDistance);
                                    var ratioEnd = end + (offsetDirection * ratio * config.RampPierceDistance);

                                    TracePlayerBBox(ratioStart, ratioEnd, bbox, ref pierce);

                                    if (!IsValidMovementTrace(ref pierce, bbox))
                                    {
                                        continue;
                                    }

                                    var pierceN = pierce.HitNormal.Normalized();

                                    var validPlane = pierce.Fraction < 1.0f
                                                     && pierce.Fraction > 0.1f
                                                     && pierceN.Dot(lastN) >= config.RampBugThreshold;

                                    hitNewPlane = pmN.Dot(pierceN) < config.NewRampThreshold
                                                  && lastN.Dot(pierceN) > config.NewRampThreshold;

                                    goodTrace = MathF.Abs(pierce.Fraction - 1.0f) < (FLT_EPSILON * 4.0f) || validPlane;

                                    if (goodTrace)
                                    {
                                        break;
                                    }
                                }

                                if (goodTrace || hitNewPlane)
                                {
                                    // Trace back to the original end point to find its normal
                                    // CS2Fixes does not appear to validate this trace, it just uses its endpos/normal
                                    TracePlayerBBox(pierce.EndPos, end, bbox, ref test);

                                    pm = pierce;
                                    pm.StartPos = start;

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
                                    }
                                    else
                                    {
                                        pm.HitNormal = test.HitNormal;
                                    }

                                    success = true;
                                    OverridenTpm[slot] = true;
                                }
                            }
                        }
                    }
                }

                // Store the raw hit normal, gated on it being a real plane (length > 0.99),
                // matching CS2Fixes. Normalizing first would let degenerate near-zero normals through
                if (pm.HitNormal.Length() > 0.99f)
                {
                    LastValidPlaneNormal[slot] = pm.HitNormal;
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

            // Store raw hit normal: ClipVelocity below relies on the un-normalized normal (matches CS2Fixes)
            planes[numPlanes] = pm.HitNormal;
            numPlanes++;

            var isOnGround = pawn?.GroundEntity.IsValid ?? false;

            if (numPlanes == 1 && pawn?.MoveType == MoveType_t.MOVETYPE_WALK && !isOnGround)
            {
                ClipVelocity(velocity, planes[0], out velocity);
            }
            else
            {
                int i;

                for (i = 0; i < numPlanes; i++)
                {
                    ClipVelocity(velocity, planes[i], out velocity);
                    int jj;

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
                    // Go along this plane
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

    private unsafe void PostTryPlayerMove(CMoveData* mv, int slot, Config config)
    {
        var dot = TpmVelocity[slot].Normalized().Dot(mv->Base.Velocity.Normalized());

        var velocityHeavilyModified =
            dot < config.RampBugThreshold
            || (TpmVelocity[slot].Length() > 50.0f
                && mv->Base.Velocity.Length() / TpmVelocity[slot].Length() < config.RampBugVelocityThreshold);

        if (OverridenTpm[slot]
            && velocityHeavilyModified
            && TpmOrigin[slot] != Vec3Invalid
            && TpmVelocity[slot] != Vec3Invalid)
        {
            mv->Base.AbsOrigin = TpmOrigin[slot];
            mv->Base.Velocity = TpmVelocity[slot];
        }
    }

    private bool IsValidMovementTrace(ref CGameTrace trace, BBox_t bbox)
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

        if (MathF.Abs(trace.HitNormal.X)    > 1.0f
            || MathF.Abs(trace.HitNormal.Y) > 1.0f
            || MathF.Abs(trace.HitNormal.Z) > 1.0f)
        {
            return false;
        }

        var stuck = new CGameTrace();
        TracePlayerBBox(trace.EndPos, trace.EndPos, bbox, ref stuck);

        if (stuck.StartInSolid || stuck.Fraction < 1.0f - FLT_EPSILON)
        {
            return false;
        }

        TracePlayerBBox(trace.EndPos, trace.StartPos, bbox, ref stuck);

        if (stuck.StartInSolid)
        {
            return false;
        }

        return true;
    }

    private void TracePlayerBBox(in Vector start, in Vector end, in BBox_t bbox, ref CGameTrace trace)
    {
        TraceParams? param = _playerTraceParams;
        var r = _core.Trace.TracePlayerBBox(in start, in end, in bbox, in param);
        CopyTrace(in r, ref trace);
    }

    private static void CopyTrace(in TraceResult r, ref CGameTrace trace)
    {
        trace.StartPos        = r.StartPos;
        trace.EndPos          = r.EndPos;
        trace.HitNormal       = r.HitNormal;
        trace.HitPoint        = r.HitPoint;
        trace.HitOffset       = r.HitOffset;
        trace.Fraction        = r.Fraction;
        trace.Triangle        = r.Triangle;
        trace.HitboxBoneIndex = r.HitboxBoneIndex;
        trace.RayType         = r.RayType;
        trace.StartInSolid    = r.StartInSolid;
        trace.ExactHitPoint   = r.ExactHitPoint;
        trace.Contents        = r.Contents;
        trace.BodyTransform   = r.BodyTransform;
    }

    private static void ClipVelocity(in Vector inVec, in Vector normal, out Vector outVec)
    {
        // Mirror CS2Fixes ClipVelocity exactly
        var backoff = ((inVec.X * normal.X) + (inVec.Y * normal.Y) + (inVec.Z * normal.Z)) * -1.0f;
        backoff = MathF.Max(backoff, 0.0f) + 0.03125f;
        outVec = inVec + (normal * backoff);
    }

    private static bool IsDucked(IPlayer player)
    {
        try
        {
            return player.PlayerPawn?.MovementServices?.Ducked ?? false;
        }
        catch
        {
            return false;
        }
    }

    private Config LoadConfig(string path)
    {
        if (!File.Exists(path)) return new Config();
        try
        {
            return Toml.ToModel<Config>(File.ReadAllText(path), null, s_tomlOptions);
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning("[RampFix] Failed to parse config.toml - using defaults: {Message}", ex.Message);
            return new Config();
        }
    }
}