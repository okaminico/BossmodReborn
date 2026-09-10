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
}

class ForceOfTheLand(BossModule module) : Components.StackWithIcon(module, (uint)IconID.ForceOfTheLandStack, (uint)AID.ForceOfTheLand, 6, 5)
{
    public override void Update()
    {
        base.Update();
        Stacks.RemoveAll(s => s.Activation.AddSeconds(1) < WorldState.CurrentTime);
    }
}

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

// ---- Shared knockback helper. REPLAY-MEASURED push distance: Geocrush launched the player 22-30y
// across 6 clean samples (the old 15y guess was ~half the real value - the AI braced for 15, got
// thrown 13y further, off the platform). All three E4S knockbacks (Geocrush / Landslide L-R / Dual
// Earthen Fists) are the same "boss jumps to a point, then shoves everyone straight away from it".
//
// AI hint: forbid cells from which a 30y shove away from the origin would land you off the arena,
// with a smooth gradient (yalms past the wall) so there is ALWAYS a "least bad" answer for the
// pathfinder. Earlier iterations used a hard InvertedCircle(origin, small) to force a tight brace -
// but when the origin sits near the edge (or dead centre, for Dual Fists) that made the entire
// reachable arena forbidden, the pathfinder returned no destination, and AI navigation locked up.
// Gradient-only can never fully lock: worst case the AI walks to the spot that overshoots the wall
// by the fewest yalms.
static class E4SKnockback
{
    public const float Distance = 30f;
    public const float HugRadius = 6f;

    public static void AddHint(BossModule module, AIHints hints, WPos origin, DateTime resolveAt)
    {
        hints.AddPredictedDamage(module.Raid.WithSlot().Mask(), resolveAt);
        if (resolveAt <= module.WorldState.CurrentTime)
            return;
        var bounds = module.Bounds;
        var center = module.Center;
        // Only position for the shove when the origin sits well away from the arena centre (Geocrush /
        // Landslide - the boss jumps to an edge and hugging that point is genuinely safe: the shove
        // then carries you ~30y clean across). For a near-centre origin (Dual Earthen Fists) no stand
        // point avoids the wall, so leave it to predicted damage + the player's own anti-knockback.
        if ((origin - center).Length() < bounds.Radius * 0.5f)
            return;

        // (1) forbidden: any cell a 30y shove away from the origin would launch off the arena.
        //     Binary (the pathfinder only checks sign) - value magnitude is irrelevant.
        hints.AddForbiddenZone(p =>
        {
            var landing = p != origin ? p + Distance * (p - origin).Normalized() : p;
            var off = landing - center;
            return (off - bounds.ClampToBounds(off)).LengthSq() > 0.01f ? -1f : 1f;
        }, resolveAt);

        // (2) goal: strongly pull the AI to HUG the origin. The forbidden zone alone just says "not
        //     there" and leaves a big flat safe region the AI won't commit to a spot within - the
        //     replays showed it drifting near centre and eating the full shove. Weight 40 dominates
        //     the ~1-2 uptime weight; goal zones only rasterize while the player's cell isn't already
        //     in imminent danger, so this pulls early in the cast and safety takes over at the end.
        hints.GoalZones.Add(hints.GoalProximity(origin, HugRadius, 40f));
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
