namespace BossMod.Shadowbringers.Raid.E4STitan;

// NOTE (WIP): rather than hand-timing all ~140 individual casts from e4s.txt (Wheels/Gauntlets path
// choice is random anyway, so a fully deterministic timeline isn't possible regardless), this uses
// broad phase blocks that keep every relevant mechanic component active throughout, with a single
// placeholder state per phase. Each component reacts to real OnCastStarted/OnEventIcon/OnTethered
// events, so actual in-game avoidance is fully live/reactive - the only thing a finer per-cast
// timeline would add is earlier lead-time in BossMod's own "upcoming mechanic" UI, not the
// underlying hint accuracy.
class E4STitanStates : StateMachineBuilder
{
    public E4STitanStates(BossModule module) : base(module)
    {
        SimplePhase(0, Phase1, "Phase 1-2 (Titan)")
            .ActivateOnEnter<Stonecrusher>()
            .ActivateOnEnter<PulseOfTheLand>()
            .ActivateOnEnter<EvilEarth>()
            .ActivateOnEnter<ForceOfTheLand>()
            .ActivateOnEnter<VoiceOfTheLand>()
            .ActivateOnEnter<Geocrush>()
            .ActivateOnEnter<MassiveLandslideFront>()
            .ActivateOnEnter<MassiveLandslideSides>()
            .ActivateOnEnter<LandslideBackCorners>()
            .ActivateOnEnter<LandslideDirectional>()
            .ActivateOnEnter<FaultLineSides>()
            .ActivateOnEnter<FaultLineFront>()
            .ActivateOnEnter<MagnitudeFive>()
            .ActivateOnEnter<BombBoulders>()
            .ActivateOnEnter<SeismicWave>()
            .Raw.Update = () => Module.PrimaryActor.IsDestroyed || !Module.PrimaryActor.IsTargetable; // ends at Orogenesis untargetable

        SimplePhase(1, Transition, "Orogenesis transition")
            .Raw.Update = () => Module.PrimaryActor.IsDestroyed || Module.PrimaryActor.IsTargetable;

        DeathPhase(2, Phase3)
            .ActivateOnEnter<EarthenFury>()
            .ActivateOnEnter<Tumult>()
            .ActivateOnEnter<TectonicUplift>()
            .ActivateOnEnter<EarthenAnguish>()
            .ActivateOnEnter<EarthenFist>()
            .ActivateOnEnter<DualEarthenFists>()
            .ActivateOnEnter<Megalith>()
            .ActivateOnEnter<WeightOfTheWorld>()
            .ActivateOnEnter<GraniteGaol>()
            .ActivateOnEnter<PlateFracture>()
            .ActivateOnEnter<VoiceOfTheLand>()
            .ActivateOnEnter<PulseOfTheLand>()
            .ActivateOnEnter<ForceOfTheLand>();
    }

    private void Phase1(uint id) => SimpleState(id, 1000, "Titan mechanics (phase ends via untargetable check above)");
    private void Transition(uint id) => SimpleState(id, 1000, "Orogenesis (phase ends via targetable check above)");
    private void Phase3(uint id) => SimpleState(id, 1000, "Titan Maximum mechanics (enrage)");
}
