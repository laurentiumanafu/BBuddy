using System;

namespace Styx
{
    /// <summary>
    /// NPC flags. Read from UNIT_MIRROR_NPC_FLAGS.
    /// Same bits as 3.3.5a (12340) through Mailbox, plus the high bits.
    /// </summary>
    [Flags]
    public enum NpcFlags : uint
    {
        None                = 0x00000000,
        Gossip              = 0x00000001,
        QuestGiver          = 0x00000002,
        AccountBanker       = 0x00000004,   // was Unk1 in 12340
        Unk2                = 0x00000008,
        Trainer             = 0x00000010,
        TrainerClass        = 0x00000020,
        TrainerProfession   = 0x00000040,
        Vendor              = 0x00000080,
        VendorAmmo          = 0x00000100,
        VendorFood          = 0x00000200,
        VendorPoison        = 0x00000400,
        VendorReagent       = 0x00000800,
        Repair              = 0x00001000,
        FlightMaster        = 0x00002000,
        SpiritHealer        = 0x00004000,
        AreaSpiritHealer    = 0x00008000,   // was SpiritGuide in 12340 (same bit)
        Innkeeper           = 0x00010000,
        Banker              = 0x00020000,
        Petitioner          = 0x00040000,
        TabardDesigner      = 0x00080000,
        BattleMaster        = 0x00100000,
        Auctioneer          = 0x00200000,
        StableMaster        = 0x00400000,
        GuildBanker         = 0x00800000,
        SpellClick          = 0x01000000,
        PlayerVehicle       = 0x02000000,
        Mailbox             = 0x04000000,
        ArtifactPowerRespec = 0x08000000,   // MoP reforger bit; not ForgeMaster
        Transmogrifier      = 0x10000000,
        VaultKeeper         = 0x20000000,
        WildBattlePet       = 0x40000000,
        BlackMarket         = 0x80000000
    }

    /// <summary>
    /// Second NPC flag dword. Read from UNIT_MIRROR_NPC_FLAGS2.
    /// Unlisted bits are still unused/unknown (0x100, 0x800, 0x1000, 0x20000, 0x40000, 0x100000, and anything above 0x200000).
    /// </summary>
    [Flags]
    public enum NpcFlags2 : uint
    {
        None                         = 0x00000000,
        ItemUpgradeMaster            = 0x00000001,
        GarrisonArchitect            = 0x00000002,
        Steering                     = 0x00000004,
        AreaSpiritHealerIndividual   = 0x00000008,
        ShipmentCrafter              = 0x00000010,
        GarrisonMissionNpc           = 0x00000020,
        TradeskillNpc                = 0x00000040,
        BlackMarketView              = 0x00000080,
        GarrisonTalentNpc            = 0x00000200,
        ContributionCollector        = 0x00000400,
        FastSteeringAvoidsObstacles  = 0x00002000,
        AzeriteRespec                = 0x00004000,
        IslandsQueue                 = 0x00008000,
        SuppressNpcSounds            = 0x00010000,
        PerksVendor                  = 0x00080000,   // trading post
        PersonalTabardDesigner       = 0x00200000
    }
}
