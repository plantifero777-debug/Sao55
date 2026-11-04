using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace SaoPlugin
{
    public partial class SaoPlugin
    {
        readonly HashSet<int> eliteNpcs = new HashSet<int>();
        readonly Dictionary<int, int> mobLevel = new Dictionary<int, int>();
        readonly Dictionary<int, int> mobXp = new Dictionary<int, int>();

        // Enemigos que no reciben nivel: NPC amigables, bichos, enemigos de estatua y partes sueltas de jefes
        static bool IsLevelable(NPC npc)
        {
            if (npc.friendly || npc.townNPC || npc.lifeMax <= 5 || npc.SpawnedFromStatue) return false;
            if (npc.realLife != -1 && npc.realLife != npc.whoAmI) return false;
            return true;
        }

        // Nivel del jugador más cercano (con variación de -2 a +3 se aplica al mob)
        int NearestLevel(NPC npc)
        {
            PlayerData best = null;
            float bestDist = float.MaxValue;
            foreach (var p in TShock.Players)
            {
                if (p == null || !p.Active) continue;
                var d = GetData(p);
                if (d == null) continue;
                float dist = Vector2.Distance(npc.Center, p.TPlayer.Center);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }
            return best != null ? best.Level : 1;
        }

        // Media del nivel de todos los jugadores conectados (para los jefes)
        int AverageLevel()
        {
            var levels = new List<int>();
            foreach (var p in TShock.Players)
            {
                if (p == null || !p.Active) continue;
                var d = GetData(p);
                if (d != null) levels.Add(d.Level);
            }
            return levels.Count == 0 ? 1 : (int)Math.Round(levels.Average());
        }

        // ================= Spawn: asignar nivel, vida, daño y élite =================

        void OnNpcSpawn(NpcSpawnEventArgs args)
        {
            if (args.NpcId < 0 || args.NpcId >= Main.npc.Length) return;

            // El índice del NPC se reutiliza: borra restos de un NPC anterior en este slot
            int slot = args.NpcId;
            lastHitter.Remove(slot);
            npcParticipants.Remove(slot);
            eliteNpcs.Remove(slot);
            mobXp.Remove(slot);
            mobLevel.Remove(slot);

            var npc = Main.npc[args.NpcId];
            if (npc == null || !npc.active || !IsLevelable(npc)) return;

            int level;
            if (npc.boss)
            {
                level = AverageLevel();
            }
            else
            {
                level = NearestLevel(npc) + rng.Next(-2, 4);
            }
            level = Math.Max(1, Math.Min(MaxLevel, level));

            bool elite = !npc.boss && eliteNpcs.Count < MaxElites && rng.NextDouble() < EliteSpawnChance;
            if (elite)
            {
                level = Math.Min(MaxLevel, level + EliteLevelBonus);
            }

            int baseLife = npc.lifeMax;
            int baseDamage = npc.damage;
            float levelMult = 1f + LifePerLevel * level;

            int scaledLife = (int)(baseLife * levelMult);
            npc.lifeMax = elite ? (int)(scaledLife * EliteLifeMult) : scaledLife;
            npc.life = npc.lifeMax;
            npc.damage = (int)(baseDamage * levelMult);

            // XP: un quinto de la vida (ya escalada por nivel); élite x5
            int xp = Math.Max(1, scaledLife / EnemyLifeDivisor) * (elite ? EliteXpMult : 1);
            mobXp[npc.whoAmI] = xp;
            mobLevel[npc.whoAmI] = level;

            string prefix = elite ? "Élite [Lv " + level + "] " : "[Lv " + level + "] ";
            npc.GivenName = prefix + npc.GivenOrTypeName;
            npc.netUpdate = true;   // sincroniza vida y nombre con los clientes

            if (elite)
            {
                eliteNpcs.Add(npc.whoAmI);
                TShock.Utils.Broadcast("¡Ha aparecido un élite: " + npc.GivenName + "!", Color.Orange);
            }
        }

        // ================= Golpes: registrar atacante y bloquear a fantasmas =================

        void OnNpcStrike(object sender, GetDataHandlers.NPCStrikeEventArgs args)
        {
            var d = GetData(args.Player);
            if (d == null) return;

            if (d.Ghost)
            {
                args.Handled = true;   // un fantasma no puede atacar
                return;
            }

            RegisterHit(args.ID, args.Player.Index);
        }

        // ================= Muerte del enemigo: XP y cristal de élite =================

        void OnNpcKilled(NpcKilledEventArgs args)
        {
            var npc = args.npc;
            if (npc == null) return;

            int id = npc.whoAmI;
            bool hadXp = mobXp.TryGetValue(id, out int xp);
            bool elite = eliteNpcs.Remove(id);
            mobXp.Remove(id);
            mobLevel.Remove(id);

            bool hasKiller = lastHitter.TryGetValue(id, out int killerIdx);
            lastHitter.Remove(id);

            if (npc.boss)
            {
                HandleBossKill(npc, hadXp ? xp : 0, hasKiller ? killerIdx : -1, TakeParticipants(id));
                return;
            }

            var participants = TakeParticipants(id);
            if (!hadXp || participants.Count == 0) return;

            DistributeXp(participants, xp, npc.Center);

            var killer = hasKiller && killerIdx >= 0 && killerIdx < TShock.Players.Length ? TShock.Players[killerIdx] : null;
            if (killer == null || !killer.Active) return;

            if (elite && rng.NextDouble() < EliteCrystalChance)
            {
                Item.NewItem(null, npc.Center, Terraria.ID.ItemID.LifeCrystal);
                TShock.Utils.Broadcast(killer.Name + " ha conseguido un cristal de vida de un élite.", Color.LightGreen);
            }
        }

        // ================= Limpieza periódica (cada 10 s) =================

        void CleanupMobs()
        {
            foreach (int id in eliteNpcs.ToList())
            {
                if (id < 0 || id >= Main.npc.Length || !Main.npc[id].active) eliteNpcs.Remove(id);
            }
            foreach (int id in mobXp.Keys.ToList())
            {
                if (id < 0 || id >= Main.npc.Length || !Main.npc[id].active)
                {
                    mobXp.Remove(id);
                    mobLevel.Remove(id);
                    lastHitter.Remove(id);
                }
            }
            ExpireInvites();
            CleanupParticipants();
        }
    }
}
