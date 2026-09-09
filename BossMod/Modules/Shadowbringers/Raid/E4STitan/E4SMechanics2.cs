namespace BossMod.Shadowbringers.Raid.E4STitan;

// ---- Landslide family: big frontal/side cleaves + directional knockback variants.
// Shapes are estimated from cactbot's callouts + Ex3Titan's near-identical attacks (same boss
// "personality", same names) - verify radii/angles once actually tested in-game.
//
// REPLAY CAVEAT: the "Gauntlets"/"Wheels"/"Armor" selectors (0x40E6/E7/E8/E9) and MassiveLandslideSides
// (0x4117) are all INSTANT in the replay - no cast bar - so the boss.CastInfo / OnCastStarted logic in
// MassiveLandslideFront / MassiveLandslideSides / FaultLineSides never fires. Treat those three as
// non-functional until a cast-bar variant is confirmed. The real telegraphed damage comes from the
// follow-ups that DO have cast bars: FaultLineFront (0x411E), LandslideBackCorners (0x411A),
// LandslideLeftRight (0x411C), MagnitudeFive (0x4121), CrumblingDown (0x410E), GiantRockLandslide (0x410F).

// "Landslide: In Front" (大地之手甲/Earthen Gauntlets, AID 40E6) - user reported via combat log this
// knocked them off the platform; see the instant caveat above - the actual knockback likely resolves
// through one of the cast-bar follow-ups, not 0x40E6 itself.
class MassiveLandslideFront(BossModule module) : Components.GenericKnockback(module, (uint)AID.MassiveLandslideFront)
{
    private static readonly AOEShapeCone _shape = new(24, 60.Degrees());
    private WPos _origin;
    private Angle _rotation;
    private DateTime _resolveAt;

    public override ReadOnlySpan<Knockback> ActiveKnockbacks(int slot, Actor actor)
        => _resolveAt > WorldState.CurrentTime ? new Knockback[] { new(_origin, 15, _resolveAt, _shape, _rotation) } : [];

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        hints.AddPredictedDamage(Raid.WithSlot().Mask(), _resolveAt);
        if (_resolveAt > WorldState.CurrentTime)
            hints.AddForbiddenZone(ShapeDistance.InvertedCircle(_origin, 2), _resolveAt);
    }

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            _origin = spell.LocXZ != default ? spell.LocXZ : caster.Position;
            _rotation = spell.Rotation;
            _resolveAt = Module.CastFinishAt(spell);
        }
    }
}

// "地裂 / Landslide" (Gauntlets combo, part 1): huijiwiki says Titan jumps to a random cardinal and
// does a CROSS-shaped AOE there, then immediately follows with Left/Right half-arena landslide.
// Replay: boss casts 0x411A (4.1s) + helper 0x411B (4.7s) centered on a cardinal (e.g. 100/110).
// Cross length/half-width are still estimates (arena is 40 wide, cast point ~10 off centre).
// Safe = the quadrant diagonals, away from both cross arms.
class LandslideBackCorners(BossModule module) : Components.SimpleAOEs(module, (uint)AID.LandslideBackCorners, new AOEShapeCross(30f, 5f));

// ---- Giant Rock landslide: the Giant Rock adds (OID 0x2992) each cast a ~6y circle landslide
// (0x410F, 4.7s) scattered across the arena, alongside the boss's own Crumbling Down. Radius is a
// best-effort estimate. verified (replay). ----
class GiantRockLandslide(BossModule module) : Components.SimpleAOEs(module, (uint)AID.GiantRockLandslide, 6);

// "Massive Landslide - Sides" (right/left simultaneous) - safe in front/back
class MassiveLandslideSides(BossModule module) : Components.GenericAOEs(module, (uint)AID.MassiveLandslideSides)
{
    private static readonly AOEShapeRect _shapeRight = new(24, 12, DirectionOffset: 90.Degrees());
    private static readonly AOEShapeRect _shapeLeft = new(24, 12, DirectionOffset: -90.Degrees());

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor)
    {
        var boss = Module.PrimaryActor;
        if (boss.CastInfo?.Action.ID != (uint)AID.MassiveLandslideSides)
            return [];
        var act = Module.CastFinishAt(boss.CastInfo);
        return new AOEInstance[]
        {
            new(_shapeRight, boss.Position, boss.Rotation, act),
            new(_shapeLeft, boss.Position, boss.Rotation, act),
        };
    }
}

// "左側/右側地裂 (Left/Right Landslide)" - the Gauntlets-combo half-arena landslide that follows the
// 地裂 cross. huijiwiki: half-arena AOE + a big knockback + aftershock. REPLAY: this was killing the AI
// by shoving it ~25-28y off the platform edge (twice a fall death), same as Geocrush. Modelling the
// knockback as "shove straight away from the cast point (LocXZ), 30y" + the shared landing-in-bounds
// AI hint. The half-arena damage side (dodge left vs right) is not modelled yet - the fall deaths were
// the knockback, not the AOE.
class LandslideDirectional(BossModule module) : Components.GenericKnockback(module, (uint)AID.LandslideLeftRight)
{
    private WPos _origin;
    private DateTime _resolveAt;

    public override ReadOnlySpan<Knockback> ActiveKnockbacks(int slot, Actor actor)
        => _resolveAt > WorldState.CurrentTime ? new Knockback[] { new(_origin, E4SKnockback.Distance, _resolveAt) } : [];

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
        => E4SKnockback.AddHint(Module, hints, _origin, _resolveAt);

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if ((AID)spell.Action.ID is AID.LandslideLeftRight or AID.LandslideRightLeft)
        {
            _origin = spell.LocXZ != default ? spell.LocXZ : caster.Position;
            _resolveAt = Module.CastFinishAt(spell);
        }
    }
}

// "Wheels: On Sides" - matches EarthenWheels ability id, big AOE on both flanks, safe front/back
class FaultLineSides(BossModule module) : Components.GenericAOEs(module, (uint)AID.FaultLineSides)
{
    private static readonly AOEShapeRect _shapeRight = new(24, 12, DirectionOffset: 90.Degrees());
    private static readonly AOEShapeRect _shapeLeft = new(24, 12, DirectionOffset: -90.Degrees());

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor)
    {
        var boss = Module.PrimaryActor;
        if (boss.CastInfo?.Action.ID != (uint)AID.FaultLineSides)
            return [];
        var act = Module.CastFinishAt(boss.CastInfo);
        return new AOEInstance[]
        {
            new(_shapeRight, boss.Position, boss.Rotation, act),
            new(_shapeLeft, boss.Position, boss.Rotation, act),
        };
    }
}

// "Fault Line" tank line - 0x411E (2.7s), boss casts it targeting the marked tank. verified (replay).
class FaultLineFront(BossModule module) : Components.CastCounter(module, (uint)AID.FaultLineFront)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (NumCasts == 0 && Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.FaultLineFront)
            hints.Add(Loc.T("Tank line incoming - non-tanks stay clear of the tank's line!"), false);
    }
}

// ---- Bomb Boulders: adds spawn on a fixed 3x3 grid (X/Z in {86,100,114}), each explodes via a 4.7s
// BombBoulderAOE (0x410A) cast - verified (replay). Cactbot's own data says the safe-zone pattern
// depends on the current phase ("landslide" = corners-then-cardinals or reverse; "armor" = hide behind
// east/west half) - that phase-dependent branching isn't something a generic AOE component can express
// well, so this just telegraphs each bomb's actual blast radius from its cast, which is the reliable
// part. Radius 6 is a best-effort estimate. ----
class BombBoulders(BossModule module) : Components.GenericAOEs(module)
{
    private static readonly AOEShapeCircle _shape = new(6);
    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor)
    {
        var bombs = ((E4STitan)Module).Bombs;
        List<AOEInstance> aoes = [];
        foreach (var b in bombs)
            if (b.CastInfo?.Action.ID == (uint)AID.BombBoulderAOE)
                aoes.Add(new(_shape, b.CastInfo.LocXZ != default ? b.CastInfo.LocXZ : b.Position, default, Module.CastFinishAt(b.CastInfo)));
        return CollectionsMarshal.AsSpan(aoes);
    }
}

// ---- Plate Fracture (岩盤粉碎): cactbot e4s.ts confirms IDs 0x4125 (front-right) / 0x4126 (back-right)
// / 0x4127 (back-left) / 0x4128 (front-left), StartsUsing, source **Titan Maximum** - a 3-then-2 CW/CCW
// quadrant sequence (huijiwiki: the named 2x2 region of the arena breaks away). cactbot's callout is
// purely "which named quadrant is dangerous -> move to the surviving one", so a 90deg quadrant cone
// from the boss is a fair approximation of the danger. Still unverified in a real replay; the cast is
// on Titan Maximum, NOT the primary Titan actor (that was the earlier bug that made this dead code).
// The separate timeline id 0x43EA "Plate Fracture 1/2/3" is the floor-break effect, not the cast. ----
class PlateFracture(BossModule module) : Components.GenericAOEs(module)
{
    private static readonly (AID aid, Angle dir)[] _quadrants =
    [
        (AID.PlateFractureFrontRight, 45.Degrees()),
        (AID.PlateFractureBackRight, 135.Degrees()),
        (AID.PlateFractureBackLeft, -135.Degrees()),
        (AID.PlateFractureFrontLeft, -45.Degrees()),
    ];
    private static readonly AOEShapeCone _shape = new(30, 45.Degrees());

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor)
    {
        var boss = ((E4STitan)Module).BossMaximum();
        if (boss?.CastInfo == null)
            return [];
        foreach (var (aid, dir) in _quadrants)
        {
            if ((AID)boss.CastInfo.Action.ID == aid)
                return new AOEInstance[] { new(_shape, boss.Position, boss.Rotation + dir, Module.CastFinishAt(boss.CastInfo)) };
        }
        return [];
    }
}
