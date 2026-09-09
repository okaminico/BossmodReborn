namespace BossMod.Shadowbringers.Raid.E4STitan;

// PHASE STRUCTURE (huijiwiki + replay `4_WAR100_..._2026_09_10_01_27_54.log`):
//
//   Titan (0x298F) and Titan Maximum (0x2990) BOTH spawn at pull start.
//   P1  构想泰坦: small Titan - Weight of the Land, Evil Earth, Geocrush -> Wheels/Gauntlets/Armor
//       transform -> Landslide/FaultLine/Magnitude5 combo, Bomb Boulders, Crumbling Down + Seismic Wave
//   P2  极大泰坦 (Orogenesis): Titan sealed in the gaol, 0x2990 fights from the north.
//       Earthen Fist x2 rounds, Dual Earthen Fists, Megalith, Tectonic Uplift, Plate Fracture,
//       Granite Gaol, Tumult, Earthen Fury between blocks.
//   P3  combined: Orogenesis again - Titan becomes targetable, 0x2990 sits outside at 12 o'clock and
//       throws Earthen Fury "1 inner + 4 outer squares, resolve CW x5" (used 3 times), Dual Earthen
//       Fists, Voice of the Land, Tumult (+1 hit each cast). Ends in a 10s Earthen Fury hard enrage
//       if Titan is not dead. This is a soft-enrage loop - the analysed (undergeared, never-killed)
//       pull cycled here, with both actors despawning+respawning as a pair every ~290s.
//
// Because transitions despawn the primary actor (BossMod recreates the module for each new pair) and
// the Wheels/Gauntlets path + mechanic order carry heavy RNG, there is no useful deterministic
// per-cast timeline. So this uses a single reactive phase with EVERY component active for the whole
// module lifetime - P1/P2/P3 mechanics alike. Every component reacts to real OnCastStarted/OnEventIcon
// events, so avoidance is live regardless of which phase label would be "current".
//
// NOTE: while a boss module is active, the generic Lumina-shape AutoHints fallback in AIHintsBuilder
// is SUPPRESSED - the AI dodges ONLY what these components emit. Gaps here (Crumbling Down bait,
// Aftershock expansion rings, Tectonic Uplift geometry, the instant Landslide selectors) are simply
// not dodged; wrong shapes are dodged wrongly. Keep that in mind before widening MinMaturity.
class E4STitanStates : StateMachineBuilder
{
    public E4STitanStates(BossModule module) : base(module)
    {
        SimplePhase(0, SinglePhase, "Titan / Titan Maximum (reactive)")
            // Titan (small) phase
            .ActivateOnEnter<Stonecrusher>()
            .ActivateOnEnter<WeightOfTheLand>()
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
            .ActivateOnEnter<GiantRockLandslide>()
            .ActivateOnEnter<SeismicWave>()
            // Titan Maximum phase
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
            .Raw.Update = () => Module.PrimaryActor.IsDeadOrDestroyed;
    }

    private void SinglePhase(uint id) => SimpleState(id, 10000, "Titan mechanics (reactive, no fixed timeline)");
}
