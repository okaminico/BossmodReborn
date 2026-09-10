namespace BossMod.Shadowbringers.Raid.E4STitan;

// Small helper: show a one-line "what to do" note in the module hint bar while the given action is
// being cast by a specific actor. Loc.T keys fall back to English if there is no translation.
static class E4SNote
{
    public static void WhileCasting(BossModule module, BossComponent.TextHints hints, Actor? caster, AID aid, string text)
    {
        if (caster?.CastInfo?.Action.ID == (uint)aid)
            hints.Add(Loc.T(text), false);
    }
}

// ---- simple raidwides (no positioning requirement beyond "take the damage") ----
class VoiceOfTheLand(BossModule module) : Components.CastCounter(module, (uint)AID.VoiceOfTheLand)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
        => E4SNote.WhileCasting(Module, hints, Module.PrimaryActor, AID.VoiceOfTheLand, "Voice of the Land: raidwide - use mitigation / heal through");

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
    public override void AddHints(int slot, Actor actor, TextHints hints)
        => E4SNote.WhileCasting(Module, hints, ((E4STitan)Module).BossMaximum(), AID.Tumult, "Tumult: repeated raidwide x5 - mitigate");

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        if (_nextExpected > WorldState.CurrentTime)
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), _nextExpected);
    }
}

class EarthenFury(BossModule module) : Components.CastCounter(module, (uint)AID.EarthenFury)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        var c = ((E4STitan)Module).BossMaximum()?.CastInfo;
        if (c != null && (AID)c.Action.ID is AID.EarthenFury or AID.EarthenFuryBleed or AID.EarthenFuryEnrage)
            hints.Add(Loc.T((AID)c.Action.ID == AID.EarthenFuryEnrage
                ? "Earthen Fury: ENRAGE - kill or die"
                : "Earthen Fury: big raidwide - mitigation + shields"), (AID)c.Action.ID == AID.EarthenFuryEnrage);
    }

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        var c = ((E4STitan)Module).BossMaximum()?.CastInfo;
        if (c != null && (AID)c.Action.ID is AID.EarthenFury or AID.EarthenFuryBleed or AID.EarthenFuryEnrage)
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), Module.CastFinishAt(c));
    }
}

class TectonicUplift(BossModule module) : Components.CastCounter(module, (uint)AID.TectonicUplift)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
        => E4SNote.WhileCasting(Module, hints, ((E4STitan)Module).BossMaximum(), AID.TectonicUplift, "Tectonic Uplift: terrain rises - jump from high ground to low, boulder blocks the centre");

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        var c = ((E4STitan)Module).BossMaximum()?.CastInfo;
        if (c != null && (AID)c.Action.ID == AID.TectonicUplift)
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), Module.CastFinishAt(c));
    }
}

class SeismicWave(BossModule module) : Components.CastCounter(module, (uint)AID.SeismicWave)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
        => E4SNote.WhileCasting(Module, hints, Module.PrimaryActor, AID.SeismicWave, "Seismic Wave: raidwide - line-of-sight it behind a Giant Rock if one is up");

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        if (Module.PrimaryActor.CastInfo != null)
            hints.AddPredictedDamage(Raid.WithSlot().Mask(), Module.CastFinishAt(Module.PrimaryActor.CastInfo));
    }
}

// ---- headmarker spread / stack. Radii are best-effort defaults for this era of savage content.
//
// BUG FIX: the "resolve" abilities (Pulse 0x4106 / Force 0x4107) are cast SELF-TARGETED by the helper
// (it drops the AOE where the marked player was), NOT on the marked player. Components.IconStackSpread
// clears a spread/stack only when the resolve cast's MainTargetID matches the marked player - which
// never happens here - so after the first Pulse of the Land the spread lingers forever and the AI
// keeps forcing players apart for the rest of the fight (reported: two co-tanks permanently drift
// apart). Both components therefore time-expire their entries ~1s past the icon's activation. ----
class PulseOfTheLand(BossModule module) : Components.SpreadFromIcon(module, (uint)IconID.PulseOfTheLandSpread, (uint)AID.PulseOfTheLand, 6, 5)
{
    public override void Update()
    {
        base.Update();
        Spreads.RemoveAll(s => s.Activation.AddSeconds(1) < WorldState.CurrentTime);
    }

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        base.AddHints(slot, actor, hints);
        if (Spreads.Count != 0)
            hints.Add(Loc.T(IsSpreadTarget(actor)
                ? "Pulse of the Land (yellow marker on YOU): spread - get >6y from everyone"
                : "Pulse of the Land: yellow spread markers out - stay clear of marked players"), false);
    }
}

class ForceOfTheLand(BossModule module) : Components.StackWithIcon(module, (uint)IconID.ForceOfTheLandStack, (uint)AID.ForceOfTheLand, 6, 5)
{
    public override void Update()
    {
        base.Update();
        Stacks.RemoveAll(s => s.Activation.AddSeconds(1) < WorldState.CurrentTime);
    }

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        base.AddHints(slot, actor, hints);
        if (Stacks.Count != 0)
            hints.Add(Loc.T("Force of the Land (orange marker): stack - everyone group on the marked player"), false);
    }
}

// ---- Weight of the Land: 1.8s boss telegraph (WeightOfTheLandVisual), then the helper drops ~6y
// circle puddles on a 4x4 grid that go off after a 4.7s cast. Radius is a best-effort estimate
// (matches Ex3Titan's WeightOfTheLandAOE). This is the single highest-frequency mechanic in the
// fight (160 casts in the analysed replay) and was previously completely untelegraphed. ----
class WeightOfTheLand(BossModule module) : Components.SimpleAOEs(module, (uint)AID.WeightOfTheLand, 6)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        base.AddHints(slot, actor, hints);
        if (Casters.Count != 0 || Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.WeightOfTheLandVisual)
            hints.Add(Loc.T("Weight of the Land: dodge the ground circles"), false);
    }
}

// ---- Evil Earth (邪土): 3.8s boss telegraph, then a multi-stage EXPANDING aftershock from the marked
// squares (Aftershock1/2, 0x410D / 0x41B5, instant, cast from helpers sitting on the grid). Replay
// analysis of every Evil Earth aftershock: the stages spread outward and ALWAYS finish on the arena
// perimeter (~t+7s after the telegraph resolves). The intermediate broad stages hit ~14/16 grid cells
// and can't be dodged without the per-square mark data, but the final perimeter stage always leaves
// the centre 2x2 safe - so this schedules a donut forbidden zone to pull the AI to centre for that.
// This component still draws the first helper circle (0x410C) too. ----
class EvilEarth(BossModule module) : Components.SimpleAOEs(module, (uint)AID.EvilEarthAOE, 6)
{
    private static readonly AOEShapeDonut _perimeter = new(9f, 26f);
    private readonly List<(WPos, DateTime)> _rings = [];

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor)
    {
        var span = base.ActiveAOEs(slot, actor);
        if (_rings.Count == 0)
            return span;
        var all = new List<AOEInstance>(span.Length + _rings.Count);
        all.AddRange(span);
        foreach (var (c, act) in _rings)
            all.Add(new(_perimeter, c, default, act));
        return CollectionsMarshal.AsSpan(all);
    }

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        base.OnCastStarted(caster, spell);
        if (spell.Action.ID == (uint)AID.EvilEarth) // 0x410B boss telegraph
            _rings.Add((Module.Center, Module.CastFinishAt(spell).AddSeconds(6d)));
    }

    public override void Update()
    {
        base.Update();
        _rings.RemoveAll(r => r.Item2.AddSeconds(2.5d) < WorldState.CurrentTime);
    }

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        base.AddHints(slot, actor, hints);
        if (Casters.Count == 0 && Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.EvilEarth)
            hints.Add(Loc.T("Evil Earth - dodge markers, then centre for the shockwave!"), false);
    }
}

// ---- Shared knockback helper. REPLAY-MEASURED (2026-09-10, player hugging the origin so the sample
// is the pure shove): Geocrush moves you ~13-16y, EXACTLY away from the cast point (spell.LocXZ) -
// dot(shove, awayFromOrigin) = +1.00 across 18 samples. The earlier "30y" figure was a measurement
// artefact (sampled 2.5s post-fire, i.e. shove + the AI then running). All three E4S knockbacks
// (Geocrush / Landslide L-R / Dual Earthen Fists) share this "shove straight away from the point".
//
// With a real ~15y shove in an r21 arena the only way to fall off is to already be near the edge
// opposite the origin, so the AI hint is deliberately light: forbid the cells whose shove-landing
// would be out of bounds, plus a modest goal to prefer hugging the origin. (Over-modelling this with
// distance=30 + a hard brace circle is what made the AI panic-position onto the edge and get flung -
// six iterations of that; don't.)
static class E4SKnockback
{
    public const float Distance = 15f;
    public const float SafeRadius = 12f; // stay within this of the origin (the shove then lands in bounds)

    public static void AddHint(BossModule module, AIHints hints, WPos origin, DateTime resolveAt)
    {
        hints.AddPredictedDamage(module.Raid.WithSlot().Mask(), resolveAt);
        if (resolveAt <= module.WorldState.CurrentTime)
            return;
        var bounds = module.Bounds;
        var center = module.Center;
        // near-centre origin (Dual Earthen Fists): no stand point beats the shove, leave it to
        // predicted damage + the player's own anti-knockback.
        if ((origin - center).Length() < bounds.Radius * 0.35f)
            return;

        // Forbidden = (further than SafeRadius from the origin) OR (a 15y shove away from the origin
        // would land you off the arena). The safe set is a small lens hugging the origin on its
        // centre-facing side - one clear target, so the AI commits and holds instead of drifting.
        var r2 = SafeRadius * SafeRadius;
        hints.AddForbiddenZone(p =>
        {
            var delta = p - origin;
            if (delta.LengthSq() > r2)
                return -1f;
            var landing = delta.LengthSq() > 0.01f ? p + Distance * delta.Normalized() : p;
            var off = landing - center;
            return (off - bounds.ClampToBounds(off)).LengthSq() > 0.01f ? -1f : 1f;
        }, resolveAt.AddSeconds(-1.5d) is var a && a > module.WorldState.CurrentTime ? a : module.WorldState.CurrentTime);

        // extra pull toward the origin so within the lens the AI hugs it rather than skimming the edge.
        hints.GoalZones.Add(hints.GoalProximity(origin, 8f, 15f));
    }
}

// ---- Geocrush: raidwide + knockback straight away from the point the boss jumps to. That point is
// carried up front in ActorCastInfo.LocXZ (replay-confirmed populated on every cast), so the AI knows
// the true origin for the whole cast rather than only once the boss visually arrives. ----
class Geocrush(BossModule module) : Components.GenericKnockback(module, (uint)AID.Geocrush)
{
    private WPos _origin;
    private DateTime _resolveAt;
    public override ReadOnlySpan<Knockback> ActiveKnockbacks(int slot, Actor actor)
        => _resolveAt > WorldState.CurrentTime ? new Knockback[] { new(_origin, E4SKnockback.Distance, _resolveAt) } : [];

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (_resolveAt > WorldState.CurrentTime)
            hints.Add(Loc.T("Geocrush: raidwide + knockback (~15y away from where the boss jumped) - stack on that spot, mitigate"), false);
    }

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
        => E4SKnockback.AddHint(Module, hints, _origin, _resolveAt);

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == WatchedAction)
        {
            _origin = spell.LocXZ != default ? spell.LocXZ : caster.Position;
            _resolveAt = Module.CastFinishAt(spell);
        }
    }
}

// ---- Dual Earthen Fists: raidwide + knockback, same "shove away from LocXZ" pattern as Geocrush. ----
class DualEarthenFists(BossModule module) : Components.GenericKnockback(module, (uint)AID.DualEarthenFists)
{
    private WPos _origin;
    private DateTime _resolveAt;
    public override ReadOnlySpan<Knockback> ActiveKnockbacks(int slot, Actor actor)
        => _resolveAt > WorldState.CurrentTime ? new Knockback[] { new(_origin, E4SKnockback.Distance, _resolveAt) } : [];

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (_resolveAt > WorldState.CurrentTime)
            hints.Add(Loc.T("Dual Earthen Fists: raidwide + knockback from the arena centre - use anti-knockback, mitigate"), false);
    }

    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
        => E4SKnockback.AddHint(Module, hints, _origin, _resolveAt);

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
