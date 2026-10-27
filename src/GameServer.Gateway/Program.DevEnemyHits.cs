using GameServer.Core.World;

partial class Program
{
    sealed record DevEnemyHit(int Damage, string Mitigation, uint Life, uint Mana, uint ManaBefore,
        bool Blocked, int BlockChance, bool Suppressed, (int Chance, int Effect, bool Lucky, bool Unlucky) Suppression);

    // /hit and /spellhit are synthetic incoming enemy contacts. /blockhit can additionally block.
    // Admission, prior-history decision, pool mutation and recording are a single combat commit.
    static bool TryDevEnemyHit(ClientState conn, DamageType type, int amount, bool block, bool spell, bool suppress,
        out DevEnemyHit hit)
    {
        lock (conn.Monsters)
        {
            hit = null!;
            if (conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return false;
            double now = conn.Clock.Elapsed.TotalSeconds;
            int priorContacts = conn.RecentEnemyHits.Count(now);
            using var defenceTime = FortificationOf(conn).Capture(now);
            // Refresh lazy aura/Fortification pools before reading ANY final defence at the accepted contact.
            var stats = CharacterPlayerStats(conn);
            int resistance = Resistance(conn, type), maximum = MaxResistance(conn, type);
            int blockChance = spell ? conn.Pools.SpellBlockChance : conn.Pools.BlockChance;
            bool luckyBlock = spell ? conn.Pools.SpellBlockLucky : conn.Pools.AttackBlockLucky;
            bool blocked = block && DamageModel.Blocks(blockChance, conn.IncomingHitRandom.NextDouble() * 100, luckyBlock);
            bool immune = PlayerImmunities(conn).Contains(type);
            bool armourRelevant = type == DamageType.Physical ? amount > 0
                : type == DamageType.Chaos && conn.Pools.ArmourAppliesToChaosDamage
                    && DamageModel.Mitigate(amount, resistance, maximum) > 0;
            var armour = IncomingArmourLocked(conn, !blocked && !immune && armourRelevant, priorContacts, stats);
            int dealt = blocked || immune ? 0
                : MitigateTakenWithArmour(conn, type, amount, resistance, maximum, armour.EffectiveArmour, stats);
            string mitigation = immune ? "immunity"
                : MitigationLabel(conn, type, amount, resistance, maximum, armour.EffectiveArmour, stats);
            var suppression = suppress ? Suppression(conn) : default;
            bool suppressed = suppress && DamageModel.Suppresses(suppression.Chance,
                conn.IncomingHitRandom.Next(100), suppression.Lucky, suppression.Unlucky);
            if (suppressed) dealt = DamageModel.Suppress(dealt, suppression.Effect);
            uint manaBefore = conn.PlayerMana;
            int gainedLife = blocked ? conn.Pools.LifeGainedOnBlock : 0;
            int gainedMana = blocked ? conn.Pools.ManaGainedOnBlock : 0;
            conn.PlayerLife = (uint)Math.Min(conn.PlayerMaxLife, Math.Max(0, (long)conn.PlayerLife
                - conn.EnergyShield.Damage(dealt, type == DamageType.Chaos, now) + gainedLife));
            if (block) conn.PlayerMana = (uint)Math.Min((uint)UnreservedMana(conn), (long)conn.PlayerMana + gainedMana);
            conn.RecentEnemyHits.Record(now);
            if (block) ChargesWhenHit(conn, blocked, spell);
            if (conn.PlayerLife == 0) conn.InvalidateIncomingHits();
            RecordHardcoreDeath(conn);
            hit = new(dealt, mitigation, conn.PlayerLife, conn.PlayerMana, manaBefore,
                blocked, blockChance, suppressed, suppression);
            return true;
        }
    }
}
