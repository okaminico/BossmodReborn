namespace BossMod.Shadowbringers.Raid.E4STitan;

// ---- Landslide family: big frontal/side cleaves + directional knockback variants.
// Shapes are estimated from cactbot's callouts + Ex3Titan's near-identical attacks (same boss
// "personality", same names) - verify radii/angles once actually tested in-game.

// "Landslide: In Front" (大地之手甲/Earthen Gauntlets, AID 40E6) - user confirmed via combat log
// this is what actually knocked them off the platform, NOT just a frontal damage cone like cactbot's
// plain "Landslide: In Front" callout implied - it's a frontal cone knockback (only players caught
// in the cone get pushed), same origin-capture/debug-print approach as Geocrush since this is the
// same "boss aligns with a ground telegraph, then knocks back from there" pattern.
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
            Service.ChatGui.Print($"[E4S debug] MassiveLandslideFront (40E6) origin captured: {_origin} (from {(spell.LocXZ != default ? "spell.LocXZ" : "caster.Position fallback")}), caster currently at {caster.Position}, resolves in {(_resolveAt - WorldState.CurrentTime).TotalSeconds:F1}s");
        }
    }
}

// "Back Corners" - safe in back corners of the arena (danger is a frontal + side cone from boss)
class LandslideBackCorners(BossModule module) : Components.SimpleAOEs(module, (uint)AID.LandslideBackCorners, new AOEShapeCone(24, 120.Degrees()));

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

// "Tank Charge" - line stare/charge at current MT's position
class FaultLineFront(BossModule module) : Components.CastCounter(module, (uint)AID.FaultLineFront)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (NumCasts == 0 && Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.FaultLineFront)
            hints.Add(Loc.T("Tank charge incoming - non-tanks stay clear of the tank's line!"), false);
    }
}

// ---- Bomb Boulders: adds spawn on a fixed 3x3 grid (west/mid/east on each axis), explode via
// BuryDirections. Cactbot's own data says the safe-zone pattern depends on the current phase
// ("landslide" = corners-then-cardinals or reverse; "armor" = hide behind east/west half) - that
// phase-dependent branching logic isn't something a generic AOE component can express well, so this
// just telegraphs each bomb's actual blast radius from its position/cast, which is the reliable part. ----
class BombBoulders(BossModule module) : Components.GenericAOEs(module)
{
    private static readonly AOEShapeCircle _shape = new(6);
    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor)
    {
        var bombs = ((E4STitan)Module).Bombs;
        List<AOEInstance> aoes = [];
        foreach (var b in bombs)
            if (b.CastInfo != null)
                aoes.Add(new(_shape, b.Position, default, Module.CastFinishAt(b.CastInfo)));
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
