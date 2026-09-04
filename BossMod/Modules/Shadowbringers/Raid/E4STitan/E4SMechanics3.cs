namespace BossMod.Shadowbringers.Raid.E4STitan;

// ---- Earthen Fist family: each of these 4 ability IDs represents a two-hit left/right sequence
// (cactbot only exposes the starting cast + a text description of the sequence, not separate
// sub-events for each half) - text hint is reliable, but this only draws the danger zone for the
// FIRST half; you need to reposition for the second half yourself once the first resolves. ----
class EarthenFist(BossModule module) : Components.GenericAOEs(module)
{
    private static readonly AOEShapeRect _halfLeft = new(30, 20, DirectionOffset: 90.Degrees());
    private static readonly AOEShapeRect _halfRight = new(30, 20, DirectionOffset: -90.Degrees());

    public override ReadOnlySpan<AOEInstance> ActiveAOEs(int slot, Actor actor)
    {
        var boss = ((E4STitan)Module).BossMaximum() ?? Module.PrimaryActor;
        if (boss.CastInfo == null)
            return [];
        var aid = (AID)boss.CastInfo.Action.ID;
        if (aid is not (AID.EarthenFistLeftRight or AID.EarthenFistRightLeft or AID.EarthenFistDoubleLeft or AID.EarthenFistDoubleRight))
            return [];
        var firstIsLeft = aid is AID.EarthenFistLeftRight or AID.EarthenFistDoubleLeft;
        return new AOEInstance[] { new(firstIsLeft ? _halfLeft : _halfRight, boss.Position, boss.Rotation, Module.CastFinishAt(boss.CastInfo)) };
    }

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        base.AddHints(slot, actor, hints);
        var boss = ((E4STitan)Module).BossMaximum() ?? Module.PrimaryActor;
        if (boss.CastInfo == null)
            return;
        string? text = (AID)boss.CastInfo.Action.ID switch
        {
            AID.EarthenFistLeftRight => Loc.T("Left => Right"),
            AID.EarthenFistRightLeft => Loc.T("Right => Left"),
            AID.EarthenFistDoubleLeft => Loc.T("Left => Stay Left"),
            AID.EarthenFistDoubleRight => Loc.T("Right => Stay Right"),
            _ => null
        };
        if (text != null)
            hints.Add(text, false);
    }
}

// ---- Megalith: shared tankbuster, stack on the targeted tank, everyone else avoid tank cleave. ----
class Megalith(BossModule module) : Components.CastCounter(module, (uint)AID.Megalith)
{
    public Actor? Target;
    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == WatchedAction)
            Target = WorldState.Actors.Find(spell.TargetID);
    }
    public override void OnCastFinished(Actor caster, ActorCastInfo spell)
    {
        if (spell.Action.ID == WatchedAction)
            Target = null;
    }
    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (Target == null)
            return;
        if (actor == Target)
            hints.Add(Loc.T("Stack tankbuster on you!"), false);
        else if (actor.Role != Role.Tank)
            hints.Add(Loc.T("Stack with tank (Megalith)!"), !actor.Position.InCircle(Target.Position, 5));
    }
    public override void AddAIHints(int slot, Actor actor, PartyRolesConfig.Assignment assignment, AIHints hints)
    {
        if (Target == null)
            return;
        var castInfo = ((E4STitan)Module).BossMaximum()?.CastInfo;
        var act = castInfo != null ? Module.CastFinishAt(castInfo) : default;
        if (actor != Target && actor.Role != Role.Tank)
            hints.AddForbiddenZone(ShapeDistance.InvertedCircle(Target.Position, 5), act);
    }
}

// ---- Weight of the World: single-target heavy damage marker - spread away from the marked player. ----
class WeightOfTheWorld(BossModule module) : Components.SpreadFromIcon(module, (uint)IconID.WeightOfTheWorldSingle, (uint)AID.WeightOfTheWorld, 6, 5);

// ---- Rock Throw / Granite Gaol: pairs 2 players via headmarker; if not resolved (killed?) within
// the cast window one of them gets imprisoned. Structurally identical to Ex3Titan's GraniteGaol. ----
class GraniteGaol(BossModule module) : BossComponent(module)
{
    public BitMask PendingGaol;
    public DateTime ResolveAt;

    public override void OnEventIcon(Actor actor, uint iconID, ulong targetID)
    {
        if (iconID == (uint)IconID.GraniteGaolTether)
        {
            PendingGaol.Set(Raid.FindSlot(actor.InstanceID));
            ResolveAt = WorldState.FutureTime(5);
        }
    }

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (PendingGaol[slot])
            hints.Add(Loc.T("Granite Gaol marker on you!"), false);
    }

    public override PlayerPriority CalcPriority(int pcSlot, Actor pc, int playerSlot, Actor player, ref uint customColor)
        => PendingGaol[playerSlot] ? PlayerPriority.Interesting : PlayerPriority.Irrelevant;
}

// ---- Tankbusters that cactbot itself flags as low-priority (usually invulned by the MT), kept as
// simple info-only hints rather than full AOE telegraphs. ----
class Stonecrusher(BossModule module) : Components.CastCounter(module, (uint)AID.Stonecrusher)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (actor.Role == Role.Tank && NumCasts == 0 && Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.Stonecrusher)
            hints.Add(Loc.T("Tankbuster (Stonecrusher) - usually invuln"), false);
    }
}

class EarthenAnguish(BossModule module) : Components.CastCounter(module, (uint)AID.EarthenAnguish)
{
    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        if (actor.Role == Role.Tank && NumCasts == 0 && Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.EarthenAnguish)
            hints.Add(Loc.T("Tankbuster (Earthen Anguish) - usually invuln"), false);
    }
}
