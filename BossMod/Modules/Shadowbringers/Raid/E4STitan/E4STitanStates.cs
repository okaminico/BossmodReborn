namespace BossMod.Shadowbringers.Raid.E4STitan;

// PHASE STRUCTURE (from replay `4_WAR100_..._2026_09_10_01_27_54.log`):
//
//   Titan (0x298F) and Titan Maximum (0x2990) BOTH spawn at pull start. The fight alternates:
//     Titan phase (small Titan targetable, ~4-5 min of mechanics)
//       -> Orogenesis: Titan goes untargetable, ~5s later 0x2990 becomes targetable
//       -> Titan Maximum phase (~45s: Earthen Fury + Earthen Fists + Dual Earthen Fists)
//       -> BOTH actors despawn (ACT-) and a fresh Titan+Maximum pair spawns
//       -> back to Titan phase
//   In the analysed pull (undergeared, never killed) this loop ran ~5 times. A real clear ends it by
//   killing Titan in the small phase, or in Titan Maximum.
//
// Because the transition despawns the primary actor, BossMod recreates the module for each new pair,
// and because the Wheels/Gauntlets path and mechanic order carry a lot of RNG, there is no useful
// deterministic per-cast timeline to build. So this uses a single reactive phase with EVERY component
// active for the whole module lifetime - Titan-phase and Titan-Maximum-phase mechanics alike, since
// which one is "current" flips back and forth. Every component reacts to real
// OnCastStarted/OnEventIcon events, so avoidance is fully live regardless of phase bookkeeping.
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
