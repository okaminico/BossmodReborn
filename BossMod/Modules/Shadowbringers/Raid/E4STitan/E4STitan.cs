namespace BossMod.Shadowbringers.Raid.E4STitan;

// NOTE (WIP): this module was hand-built from cactbot's e4s.ts/e4s.txt data rather than an actual
// replay/log analysis, so timings and a few shapes/radii are best-effort estimates - see individual
// component files for details. BossMaximum/BombBoulder OIDs still need to be filled in from a real
// pull before phase 3 (Titan Maximum) mechanics will activate (see E4STitanEnums.cs).
[ModuleInfo(BossModuleInfo.Maturity.WIP,
    Contributors = "Community (cactbot data) + Claude",
    GroupType = BossModuleInfo.GroupType.CFC,
    // 台服 ContentFinderCondition row 690 =「伊甸零式希望樂園 覺醒之章4」(內部名 n4g4_2)。
    // NameID(BNpcName) 刻意留空:猜錯比不填更糟,等有實機錄影再補。
    GroupID = 690u,
    PlanLevel = 80)]
public class E4STitan : BossModule
{
    public readonly List<Actor> Bombs;
    public Actor? BossMaximum() => Enemies((uint)OID.BossMaximum).Count != 0 ? Enemies((uint)OID.BossMaximum)[0] : null;

    // arena is a ~40x40 yalm square platform; center taken from cactbot's documented bomb-marker
    // grid (x/y = 86/100/114, i.e. centered on 100,100) - verify against an actual replay.
    public E4STitan(WorldState ws, Actor primary) : base(ws, primary, new(100, 100), new ArenaBoundsSquare(20))
    {
        Bombs = Enemies((uint)OID.BombBoulder);
    }

    protected override void DrawEnemies(int pcSlot, Actor pc)
    {
        Arena.Actor(PrimaryActor, allowDeadAndUntargetable: true);
        var max = BossMaximum();
        if (max != null)
            Arena.Actor(max, allowDeadAndUntargetable: true);
        Arena.Actors(Bombs, Colors.Object);
    }
}
