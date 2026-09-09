namespace BossMod.Shadowbringers.Raid.E4STitan;

// NOTE: IDs are now cross-checked against a real E4S replay
// (BossModReborn log `4_WAR100_..._2026_09_10_01_27_54.log`, zone 856 / CFC 690, game 2026.07.22).
// "verified (replay)" = seen firing in that log with the stated caster / cast time.
// "cactbot, unverified" = did not occur in that pull (RNG path or the group never pushed far enough).
//
// Helper note: OID.Helper (0x233C) is BOTH the decorative arena statues AND the real invisible
// cast-helper - the grid AOEs (Weight of the Land / Evil Earth puddles, Million-Ton Landslide,
// Earthen Fury/Fist helper hits, aftershocks) are all cast from a 0x233C actor named "泰坦".
// Components match those by action id regardless of caster, so this is fine.
public enum OID : uint
{
    Boss = 0x298F, // "泰坦" (Titan) in phases 1/2 - verified (replay), BNpcName 8350
    BossMaximum = 0x2990, // "極大泰坦" (Titan Maximum) - verified (replay), BNpcName 8349.
                          // IMPORTANT: 0x2990 spawns *together with* 0x298F at pull start and both are
                          // destroyed+recreated as a pair at every phase transition (see E4STitanStates).
    BombBoulder = 0x2991, // "爆破岩石" (Bomb Boulder) adds - verified (replay), 3x3 grid at X/Z in {86,100,114}
    GiantRock = 0x2992, // "巨大岩石" - verified (replay): casts GiantRockLandslide (0x410F), i.e. extra
                        // scattered landslide circles alongside the boss's own Crumbling Down.
    GraniteGaolHelper = 0x2A4F, // "花崗石牢" (Granite Gaol) - spawns at pull start; did NOT cast in the
                                // analysed replay (gaol never triggered), tether/icon path unverified.
    Helper = 0x233C, // invisible cast-helper + decorative statues, see file header note
}

public enum AID : uint
{
    AutoAttack = 0x413D, // Boss->player, no cast - verified (replay), id 16701
    AutoAttackMaximum = 0x413E, // BossMaximum->player, no cast - verified (replay), id 16702

    // ---- Titan (small) phase ----
    Stonecrusher = 0x4116, // Boss->player, 4.7s cast, tankbuster ("崩岩") - verified (replay)
    StonecrusherFollowup = 0x4143, // Boss->player, no cast, tankbuster followups - verified (replay)

    WeightOfTheLandVisual = 0x4105, // Boss->self, 1.8s cast, telegraph before the puddles - verified (replay), id 16645
    WeightOfTheLand = 0x4108, // Helper->self, 4.7s cast, ~6y circle puddle on a 4x4 grid (X/Z in {85,95,105,115}) - verified (replay), id 16648
    Aftershock1 = 0x410D, // Helper->self, no cast, high-frequency small aftershock - verified (replay), id 16653
    Aftershock2 = 0x41B5, // Helper->self, no cast, aftershock rings, bursts of ~16 - verified (replay), id 16821

    PulseOfTheLand = 0x4106, // Helper->self, no cast, spread resolve (icon 00B9) - verified (replay), id 16646
    ForceOfTheLand = 0x4107, // Helper->self, no cast, stack resolve (icon 00BA) - verified (replay), id 16647

    EvilEarth = 0x410B, // Boss->self, 3.8s cast, telegraph - verified (replay), id 16651
    EvilEarthAOE = 0x410C, // Helper->self, 4.7s cast, ~6y circle puddle on the grid - verified (replay), id 16652

    VoiceOfTheLand = 0x4114, // Boss->self, 3.7s cast, raidwide - verified (replay), id 16660
    Geocrush = 0x4113, // Boss->self, 4.7s cast, raidwide + knockback from a fixed edge point (LocXZ carries it) - verified (replay), id 16659

    // Bomb Boulders
    BombBoulders = 0x4109, // Boss->self, 4.7s cast, spawns the Bomb Boulder adds - verified (replay), id 16649
    BombBoulderAOE = 0x410A, // BombBoulder->self, 4.7s cast, ~6y circle explosion (3x3 grid) - verified (replay), id 16650
    BuryDirections = 0x4142, // BombBoulder->self, no cast, cosmetic "sink" - verified (replay), id 16706

    // Crumbling Down / Landslide setup
    CrumblingDown = 0x410E, // Boss->self, 4.7s cast, preceded by icon 0017 on players - verified (replay), id 16654
    GiantRockLandslide = 0x410F, // GiantRock->self, 4.7s cast, extra scattered ~6y landslide circles - verified (replay), id 16655
    SeismicWave = 0x4110, // Boss->self, 3.7s cast, raidwide follow-up - verified (replay), id 16656

    // Fault Line
    FaultLineFront = 0x411E, // Boss->player (tank), 2.7s cast, line at the marked tank - verified (replay), id 16670
    FaultLineHelperA = 0x411F, // Boss->self, no cast - verified (replay), id 16671 (paired with 0x4120)
    FaultLineHelperB = 0x4120, // Helper->self, no cast - verified (replay), id 16672
    MagnitudeFive = 0x4121, // Boss->self, 2.7s cast, "get under boss" donut - verified (replay), id 16673

    // Wheels / Gauntlets / Armor selectors - ALL INSTANT (no cast bar). Components must react to the
    // follow-up casts below, not to these. verified (replay).
    EarthenGauntlets = 0x40E6, // Boss->self, no cast, "Gauntlets" path selector - id 16614
    EarthenArmorA = 0x40E7, // Boss->self, no cast - id 16615
    EarthenWheels = 0x40E8, // Boss->self, no cast, "Wheels" path selector - id 16616
    EarthenArmorB = 0x40E9, // Boss->self, no cast - id 16617

    // Landslide family follow-ups
    MassiveLandslideFront = 0x40E6, // alias of EarthenGauntlets - INSTANT, cannot be reacted to via CastInfo
    FaultLineSides = 0x40E8, // alias of EarthenWheels - INSTANT, cannot be reacted to via CastInfo
    MassiveLandslideSides = 0x4117, // Boss->self, no cast (+ helpers 0x4118/0x4119) - verified (replay), id 16663
    MassiveLandslideSidesHelperA = 0x4118, // Helper->self, no cast - id 16664
    MassiveLandslideSidesHelperB = 0x4119, // Helper->self, no cast - id 16665
    LandslideBackCorners = 0x411A, // Boss->self, 4.1s cast, central line (paired with helper 0x411B) - verified (replay), id 16666
    LandslideBackCornersHelper = 0x411B, // Helper->self, 4.7s cast - verified (replay), id 16667
    LandslideLeftRight = 0x411C, // Boss->self, 2.7s cast, "left" directional landslide - verified (replay), id 16668
    LandslideRightLeft = 0x411D, // Boss->self, cast, "right" variant - cactbot, unverified, id 16669

    Orogenesis = 0x4371, // Boss->self, no cast, phase-transition trigger; boss goes untargetable ~5s
                         // earlier, then Titan/Maximum despawn+respawn ~45s later - verified (replay), id 17265

    // ---- Titan Maximum phase ----
    EarthenFury = 0x4124, // BossMaximum->self, 5.7s cast, raidwide - verified (replay), id 16676
    EarthenFuryHelper = 0x43E8, // Helper->self, 7.2s cast, actual raidwide hit - verified (replay), id 17384
    EarthenFuryBleed = 0x413A, // BossMaximum->self, cast, raidwide + Filthy bleed - used all through the FINAL
                               // phase (after OrogenesisFinal) - cactbot e4s.ts, unverified, id 16698
    EarthenFuryEnrage = 0x4140, // BossMaximum->self, cast, hard enrage (~t+1581 on cactbot's timeline) - id 16704
    ContinentalOverlayEffect = 0x4129, // Helper->self, no cast, x6 burst seen in replay - cactbot's -ii list marks
                                       // this an ignored effect; the real repeating raidwide is Tumult (0x412A). id 16681

    EarthenAnguish = 0x4137, // Titan->player, tankbuster, always paired with Dual Earthen Fists - cactbot e4s.ts, id 16695

    EarthenFistLeftRight = 0x412F, // BossMaximum->self, 6.7s cast, left then right - verified (replay), id 16687
    EarthenFistRightLeft = 0x4130, // BossMaximum->self, cast - cactbot, unverified, id 16688
    EarthenFistDoubleLeft = 0x4131, // BossMaximum->self, cast - cactbot, unverified, id 16689
    EarthenFistDoubleRight = 0x4132, // BossMaximum->self, 6.7s cast - verified (replay), id 16690
    EarthenFistExtra = 0x4134, // BossMaximum->self, no cast, x3 - verified (replay), id 16692
    EarthenFistHelperLeft = 0x43CA, // Helper->self (x~89), 0.7s cast, actual left line hit - verified (replay), id 17354
    EarthenFistHelperRight = 0x43C9, // Helper->self (x~111), 0.7s cast, actual right line hit - verified (replay), id 17353

    DualEarthenFists = 0x4135, // BossMaximum->self, 4.0s cast, raidwide + knockback (LocXZ carries origin, y~5) - verified (replay), id 16693
    DualEarthenFistsHelperA = 0x4136, // Helper->self, 4.7s cast - verified (replay), id 16694
    DualEarthenFistsHelperB = 0x4687, // Helper->self, 5.1s cast - verified (replay), id 18055

    Megalith = 0x4138, // BossMaximum->player, shared tankbuster stack (headmarker 005D) - cactbot e4s.ts, unverified, id 16696
    TectonicUplift = 0x4122, // BossMaximum->self, cast, terrain-raise / arena-shrink - cactbot e4s.ts, unverified, id 16674
    RockThrow = 0x412D, // BossMaximum->player, no cast, gaol tether setup (icon 00BF) - cactbot e4s.ts, unverified, id 16685
    WeightOfTheWorld = 0x442B, // BossMaximum->player, no cast, single-target heavy (icon 00BB) - cactbot e4s.ts, unverified, id 17451
    Tumult = 0x412A, // BossMaximum->self, cast, repeating raidwide x5 over ~6s - cactbot e4s.ts confirms this ID, unverified in replay, id 16682

    // Plate Fracture: cactbot e4s.ts confirms these 4 IDs, StartsUsing, source Titan Maximum (NOT Titan).
    // 3-then-2 CW/CCW quadrant sequence. Timeline id 0x43EA is the separate floor-break effect.
    PlateFractureFrontRight = 0x4125, // BossMaximum->self, cast - cactbot e4s.ts, unverified, id 16677
    PlateFractureBackRight = 0x4126, // BossMaximum->self, cast - cactbot e4s.ts, unverified, id 16678
    PlateFractureBackLeft = 0x4127, // BossMaximum->self, cast - cactbot e4s.ts, unverified, id 16679
    PlateFractureFrontLeft = 0x4128, // BossMaximum->self, cast - cactbot e4s.ts, unverified, id 16680
    PlateFractureEffect = 0x43EA, // Titan->self, the floor-break ("Plate Fracture 1/2/3" on cactbot's timeline) - id 17386

    OrogenesisFinal = 0x4372, // BossMaximum->self, cast, transition into the final (bleed + enrage) phase - cactbot e4s.ts, id 17266
}

public enum SID : uint
{
    Filthy = 0x5C2, // BossMaximum->player, bleed DoT from EarthenFuryBleed - cactbot, unverified
}

public enum IconID : uint
{
    PulseOfTheLandSpread = 0xB9, // Yellow Spread - verified (replay)
    ForceOfTheLandStack = 0xBA, // Orange Stack - verified (replay)
    CrumblingDownBomb = 0x17, // Bomb on you (precedes Crumbling Down) - verified (replay)
    WeightOfTheWorldSingle = 0xBB, // Blue single-target weight - verified (replay, 1 occurrence)
    MegalithStack = 0x5D, // shared tankbuster stack marker - cactbot, unverified
    GraniteGaolTether = 0xBF, // gaol pairing marker - cactbot, unverified
}
