namespace BossMod.Shadowbringers.Raid.E4STitan;

// ---- simple raidwides (no positioning requirement beyond "take the damage") ----
class VoiceOfTheLand(BossModule module) : Components.CastCounter(module, (uint)AID.VoiceOfTheLand)
{
    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        if (Module.PrimaryActor.CastInfo != null)
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), Module.CastFinishAt(Module.PrimaryActor.CastInfo));
    }
}

class Tumult(BossModule module) : Components.CastCounter(module, (uint)AID.Tumult)
{
    private DateTime _nextExpected;
    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == WatchedAction)
            _nextExpected = Module.CastFinishAt(spell);
    }
    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        if (_nextExpected > WorldState.CurrentTime)
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), _nextExpected);
    }
}

class EarthenFury(BossModule module) : Components.CastCounter(module, (uint)AID.EarthenFury)
{
    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        var c = ((E4STitan)Module).BossMaximum()?.CastInfo;
        if (c != null && (AID)c.Action.ID is AID.EarthenFury or AID.EarthenFuryBleed or AID.EarthenFuryEnrage)
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), Module.CastFinishAt(c));
    }
}

class TectonicUplift(BossModule module) : Components.CastCounter(module, (uint)AID.TectonicUplift)
{
    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        var c = ((E4STitan)Module).BossMaximum()?.CastInfo;
        if (c != null && (AID)c.Action.ID == AID.TectonicUplift)
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), Module.CastFinishAt(c));
    }
}

class SeismicWave(BossModule module) : Components.CastCounter(module, (uint)AID.SeismicWave)
{
    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        if (Module.PrimaryActor.CastInfo != null)
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), Module.CastFinishAt(Module.PrimaryActor.CastInfo));
    }
}

// ---- headmarker spread / stack (icon-driven, resolved by the follow-up cast) ----
// radii are best-effort defaults (typical for this era of savage content) - tune if they feel wrong in practice
class PulseOfTheLand(BossModule module) : Components.SpreadFromIcon(module, (uint)IconID.PulseOfTheLandSpread, (uint)AID.PulseOfTheLand, 6, 5);
class ForceOfTheLand(BossModule module) : Components.StackWithIcon(module, (uint)IconID.ForceOfTheLandStack, (uint)AID.ForceOfTheLand, 6, 5);

// ---- Weight of the Land: 1.8s boss telegraph (WeightOfTheLandVisual), then the helper drops ~6y
// circle puddles on a 4x4 grid that go off after a 4.7s cast. Radius is a best-effort estimate
// (matches Ex3Titan's WeightOfTheLandAOE). This is the single highest-frequency mechanic in the
// fight (160 casts in the analysed replay) and was previously completely untelegraphed. ----
class WeightOfTheLand(BossModule module) : Components.SimpleAOEs(module, (uint)AID.WeightOfTheLand, 6);

// ---- Evil Earth (邪土): 3.8s boss telegraph, then a 3-STAGE EXPANDING-RING AOE from the marked
// square(s) - huijiwiki: "每次扩大为上一轮的外圈" (each wave is the outer ring of the previous), i.e.
// circle -> donut -> larger donut, so the intended dodge is to move inward toward the marked square
// as it expands. This component only draws the FIRST stage (helper cast 0x410C, ~6y circle); the
// expansion waves come through as Aftershock1/2 (0x410D / 0x41B5) which are not yet modelled. So the
// AI will dodge the initial hit but may re-path into an expansion ring - a proper multi-stage
// donut-sequence component (see A11Prishe KnuckleSandwich for the pattern) is the real fix. ----
class EvilEarth(BossModule module) : Components.SimpleAOEs(module, (uint)AID.EvilEarthAOE, 6)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        base.AddHints(slot, actor, hints);
        if (Casters.Count == 0 && Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.EvilEarth)
            hints.Add(Loc.T("Evil Earth - dodge the ground markers!"), false);
    }
}

// ---- Geocrush: raidwide + knockback away from boss. Cactbot doesn't give an exact push distance;
// 15y is the typical Titan-family value (matches Ex3Titan's Upheaval/Landslide push), tune if wrong.
// This is one of the two mechanics reported as "AI doesn't dodge the knockback" - the fix here is
// AddAIHints bracing the player near a wall-adjacent safe spot rather than trying to outrun it.
//
// IMPORTANT: the boss visibly jumps to a fixed "Geocrush center" partway through the cast, and the
// knockback resolves from THAT point, not from wherever the boss happened to be standing when the
// cast started. The cast packet carries this destination up front (ActorCastInfo.Location/LocXZ,
// populated by the server at cast-start even for a "self-targeted" ability like this one) - using
// that instead of the live PrimaryActor.Position means the AI knows the true landing spot for the
// entire cast, instead of only reacting once the boss visually arrives there (which per user report
// happens late enough in the cast that there wasn't time left to react). Falls back to the boss's
// position at cast-start if LocXZ ever comes back as an obviously-invalid zero. ----
class Geocrush(BossModule module) : Components.GenericKnockback(module, (uint)AID.Geocrush)
{
    private WPos _origin;
    private DateTime _resolveAt;
    public override ReadOnlySpan<Knockback> ActiveKnockbacks(int slot, Actor actor)
        => _resolveAt > WorldState.CurrentTime ? new Knockback[] { new(_origin, 15, _resolveAt) } : [];

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        hints.AddPredictedDamage(Raid.WithSlot().Mask(), _resolveAt);
        if (_resolveAt > WorldState.CurrentTime)
        {
            // margin tightened (was 4, a died-to-this report showed that wasn't enough) since both
            // the 15y push distance and the 20y arena half-width are themselves estimates - erring
            // toward "stand very close" costs little and buys a buffer against those being off.
            hints.AddForbiddenZone(ShapeDistance.InvertedCircle(_origin, 2), _resolveAt);
        }
    }
    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            // replay-confirmed: spell.LocXZ carries a valid edge landing point for every Geocrush cast,
            // so the fallback is essentially never hit.
            _origin = spell.LocXZ != default ? spell.LocXZ : caster.Position;
            _resolveAt = Module.CastFinishAt(spell);
        }
    }
}

// ---- Dual Earthen Fists: raidwide + knockback, same treatment as Geocrush (push distance estimated,
// cast-location-not-live-position fix applied the same way; replay-confirmed LocXZ is populated). ----
class DualEarthenFists(BossModule module) : Components.GenericKnockback(module, (uint)AID.DualEarthenFists)
{
    private WPos _origin;
    private DateTime _resolveAt;
    public override ReadOnlySpan<Knockback> ActiveKnockbacks(int slot, Actor actor)
        => _resolveAt > WorldState.CurrentTime ? new Knockback[] { new(_origin, 15, _resolveAt) } : [];

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
            _resolveAt = Module.CastFinishAt(spell);
        }
    }
}

// ---- Magnitude 5.0: "get under the boss" - donut safe zone hugging the boss hitbox, raidwide outside it.
// Radius numbers are estimated (typical for this style of mechanic); verify in practice. ----
class MagnitudeFive(BossModule module) : Components.CastCounter(module, (uint)AID.MagnitudeFive)
{
    private const float _outerRadius = 15;
    private const float _innerRadius = 4;

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (!actor.Position.InCircle(Module.PrimaryActor.Position, _outerRadius))
            hints.Add(Loc.T("Move closer to the boss!"));
        else if (!actor.Position.InCircle(Module.PrimaryActor.Position, _innerRadius))
            hints.Add(Loc.T("Move closer to the boss!"), false);
    }

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        if (NumCasts == 0 && Module.PrimaryActor.CastInfo != null)
        {
            hints.AddForbiddenZone(ShapeDistance.InvertedCircle(Module.PrimaryActor.Position, _innerRadius), Module.CastFinishAt(Module.PrimaryActor.CastInfo));
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), Module.CastFinishAt(Module.PrimaryActor.CastInfo));
        }
    }

    public override void DrawArenaBackground(int pcSlot, Actor pc)
    {
        if (Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.MagnitudeFive)
        {
            Arena.ZoneCircle(Module.PrimaryActor.Position, _outerRadius, Colors.AOE);
            Arena.ZoneCircle(Module.PrimaryActor.Position, _innerRadius, Colors.SafeFromAOE);
        }
    }
}
