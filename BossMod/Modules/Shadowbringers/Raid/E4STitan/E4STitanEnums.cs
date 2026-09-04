namespace BossMod.Shadowbringers.Raid.E4STitan;

// NOTE (WIP): Boss/BossMaximum/GraniteGaolHelper/BombBoulder all confirmed from user's actual pulls
// via BossMod's Debug > Actors panel (2026-09-01). Everything else (all AIDs, SIDs, IconIDs,
// TetherIDs) comes directly from cactbot's data/05-shb/raid/e4s.ts, which is name/ID-based and
// doesn't depend on client language, so those are reliable.
public enum OID : uint
{
    Boss = 0x298F, // "泰坦" (Titan) in phases 1/2 - confirmed via Debug > Actors
    BossMaximum = 0x2990, // "極大泰坦" (Titan Maximum) in phase 3 - confirmed via Debug > Actors
    BombBoulder = 0x2991, // "爆破岩石" (Bomb Boulder) adds - confirmed via Debug > Actors,
                           // positions matched cactbot's documented 86/100/114 grid exactly
    GiantRock = 0x2992, // "巨大岩石" - spotted alongside Bomb Boulder in the same pull, purpose not
                         // yet identified/used by any component (possibly a telegraph/prep actor
                         // that appears before the bombs proper) - noted for future investigation
    GraniteGaolHelper = 0x2A4F, // "花崗石牢" (Granite Gaol) - confirmed via Debug > Actors
    Helper = 0x233C, // NOTE: this turned out to be a bunch of decorative/environment actors also
                      // literally named "泰坦" (statues around the arena?), NOT a generic invisible
                      // cast-helper OID as originally guessed - currently unused by any component,
                      // harmless either way, but don't rely on it if you add something that needs a
                      // real helper OID later.
}

public enum AID : uint
{
    AutoAttack = 870, // Boss->player, no cast, single-target (generic ARR/SHB Titan autoattack id, verify)

    // ---- Warmup / repeated phase 1&2 mechanics ----
    Stonecrusher = 0x4116, // Boss->self, 5.0s cast, tankbuster (generally invulned by MT, low priority)
    StonecrusherFollowup = 0x4143, // Boss->self, no cast, tankbuster followups x2

    WeightOfTheLand = 0x4108, // Boss->self, cast, visual - precedes WeightOfTheLandAOE puddles
    PulseOfTheLand = 0x4106, // Boss->self, no cast, headmarker spread resolve (icon 00B9, Yellow Spread)
    EvilEarth = 0x410B, // Boss->self, cast, spread pattern markers on ground
    ForceOfTheLand = 0x4107, // Boss->self, no cast, headmarker stack resolve (icon 00BA, Orange Stack)
    VoiceOfTheLand = 0x4114, // Boss->self, cast, raidwide
    Geocrush = 0x4113, // Boss->self, cast, raidwide + knockback (source of "doesn't dodge knockback" complaints)

    // ---- Wheels / Gauntlets path split ----
    EarthenWheels = 0x40E8, // Boss->self, cast, starts "Wheels" (fault line) path - massive landslide on sides
    EarthenGauntlets = 0x40E6, // Boss->self, cast, starts "Gauntlets" (landslide) path - massive landslide in front
    EarthenArmorA = 0x40E7, // Boss->self, cast, armor phase marker A
    EarthenArmorB = 0x40E9, // Boss->self, cast, armor phase marker B

    // Wheels path specific
    FaultLineSides = 0x40E8, // duplicate of EarthenWheels ability - massive AOE on both sides, safe middle/front-back
    FaultLineFront = 0x411F, // Boss->tank, cast, tank charge (line AOE at current MT position)
    MagnitudeFive = 0x4121, // Boss->self, cast, raidwide - "get under boss" (donut, safe near hitbox)

    // Gauntlets path specific
    MassiveLandslideFront = 0x40E6, // duplicate id of EarthenGauntlets - big frontal cleave, safe behind boss
    MassiveLandslideSides = 0x4117, // Boss->self, cast, big cleave on both sides, safe front/back
    LandslideBackCorners = 0x411A, // Boss->self, cast, safe in back corners
    LandslideLeftRight = 0x411C, // Boss->self, cast, directional rect knockback (paired w/ 0x411D)
    LandslideRightLeft = 0x411D, // Boss->self, cast, directional rect knockback (paired w/ 0x411C)

    CrumblingDown = 0x410E, // Boss->self, cast, headmarker (icon 0017) "bomb on you" setup
    BombBoulders = 0x4109, // Boss->self, cast, spawns Bomb Boulder adds
    BuryDirections = 0x4142, // BombBoulder->location, no cast, bomb explosion (positions fixed per phase pattern)
    SeismicWave = 0x4110, // Boss->self, cast, raidwide-ish follow-up after bombs

    Orogenesis = 0x4371, // Boss->self, cast, phase transition into Titan Maximum (very long cast/untargetable window)

    // ---- Titan Maximum phase (Transition) ----
    EarthenFury = 0x4124, // BossMaximum->self, cast, raidwide
    EarthenFuryBleed = 0x413A, // BossMaximum->self, cast, raidwide + bleed DoT
    EarthenFuryEnrage = 0x4140, // BossMaximum->self, cast, enrage wipe

    EarthenAnguish = 0x4137, // Boss->tank, cast, tankbuster (usually invulned)
    EarthenFistLeftRight = 0x412F, // BossMaximum->self, cast, sequential cleave left then right
    EarthenFistRightLeft = 0x4130, // BossMaximum->self, cast, sequential cleave right then left
    EarthenFistDoubleLeft = 0x4131, // BossMaximum->self, cast, cleave left twice (stay left)
    EarthenFistDoubleRight = 0x4132, // BossMaximum->self, cast, cleave right twice (stay right)
    DualEarthenFists = 0x4135, // BossMaximum->self, cast, raidwide + knockback (source of "doesn't dodge knockback" complaints)

    Megalith = 0x4138, // BossMaximum->player, cast, shared tankbuster (stack on tank)
    TectonicUplift = 0x4122, // BossMaximum->self, cast, raidwide-ish, arena-wide telegraphed damage
    RockThrow = 0x412D, // BossMaximum->player, no cast, gaol tether setup (icon 00BF headmarker on 2 players)
    WeightOfTheWorld = 0x442B, // BossMaximum->player, no cast, headmarker (icon 00BB) single-target heavy damage
    Tumult = 0x412A, // BossMaximum->self, no cast, raidwide, repeats x5

    PlateFractureFrontRight = 0x4125, // Boss->self, cast, quadrant AOE - danger front-right
    PlateFractureBackRight = 0x4126, // Boss->self, cast, quadrant AOE - danger back-right
    PlateFractureBackLeft = 0x4127, // Boss->self, cast, quadrant AOE - danger back-left
    PlateFractureFrontLeft = 0x4128, // Boss->self, cast, quadrant AOE - danger front-left
}

public enum SID : uint
{
    Filthy = 0x5C2, // BossMaximum->player, bleed DoT from EarthenFuryBleed
}

public enum IconID : uint
{
    PulseOfTheLandSpread = 0xB9, // Yellow Spread
    ForceOfTheLandStack = 0xBA, // Orange Stack
    CrumblingDownBomb = 0x17, // Bomb on you
    WeightOfTheWorldSingle = 0xBB, // Blue single-target weight
    MegalithStack = 0x5D, // shared tankbuster stack marker
    GraniteGaolTether = 0xBF, // gaol pairing marker (2 players share)
}
