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

// "Back Corners" - safe in back corners of the arena (danger is a frontal + side cone from boss).
// Replay: boss casts 0x411A (4.1s) with a helper 0x411B (4.7s), telegraphs centered on the arena
// mid-line (100, 90/110) - the cone shape/angle here is still a cactbot-derived estimate.
class LandslideBackCorners(BossModule module) : Components.SimpleAOEs(module, (uint)AID.LandslideBackCorners, new AOEShapeCone(24, 120.Degrees()));

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

// "Right/Left Landslide" - directional rect knockback along one side of the boss
class LandslideDirectional(BossModule module) : Components.GenericKnockback(module, (uint)AID.LandslideLeftRight)
{
    private static readonly AOEShapeRect _shape = new(40, 3);
    private DateTime _resolveAt;
    private Kind _kind;

    public override ReadOnlySpan<Knockback> ActiveKnockbacks(int slot, Actor actor)
        => _resolveAt > WorldState.CurrentTime
            ? new Knockback[] { new(Module.PrimaryActor.Position, 15, _resolveAt, _shape, Module.PrimaryActor.Rotation, _kind) }
            : [];

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        hints.AddPredictedDamage(Raid.WithSlot().Mask(), _resolveAt);
        if (_resolveAt > WorldState.CurrentTime && _kind is Kind.DirLeft or Kind.DirRight)
        {
            // this is a directional push (not away-from-center like Geocrush), so the safety margin
            // has to be measured along the actual push direction, from the boss's ACTUAL position
            // (not a guessed arena-center coordinate - same reasoning as the Geocrush fix). Margin
            // kept tight (2y) since the 15y push distance and 20y arena half-width are both
            // estimates - a knocked-off-platform death from Geocrush already showed those estimates
            // being slightly off is enough to matter, so err on the side of standing very close.
            var pushDir = (Module.PrimaryActor.Rotation + (_kind == Kind.DirLeft ? 90.Degrees() : -90.Degrees())).ToDirection();
            var boundary = Module.PrimaryActor.Position + 2 * pushDir;
            hints.AddForbiddenZone(ShapeDistance.HalfPlane(boundary, pushDir), _resolveAt);
        }
    }

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if ((AID)spell.Action.ID == AID.LandslideLeftRight)
        {
            _kind = Kind.DirLeft;
            _resolveAt = Module.CastFinishAt(spell);
        }
        else if ((AID)spell.Action.ID == AID.LandslideRightLeft)
        {
            _kind = Kind.DirRight;
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

// ---- Plate Fracture: 4-cast rotating quadrant sequence (front-right/back-right/back-left/front-left),
// each cast is dangerous in its named 90-degree quadrant relative to boss facing - move to the opposite
// side. This directly covers the "falls off the platform" complaint since the safe direction is always
// toward arena center along the opposite quadrant, never toward an edge. ----
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
        var boss = Module.PrimaryActor;
        if (boss.CastInfo == null)
            return [];
        foreach (var (aid, dir) in _quadrants)
        {
            if ((AID)boss.CastInfo.Action.ID == aid)
                return new AOEInstance[] { new(_shape, boss.Position, boss.Rotation + dir, Module.CastFinishAt(boss.CastInfo)) };
        }
        return [];
    }
}
