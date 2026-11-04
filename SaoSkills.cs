using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TShockAPI;

namespace SaoPlugin
{
    public class SkillDef
    {
        public string Name;
        public int Level;
        public int Cooldown;   // segundos
        public float Mult;     // multiplicador sobre el daño del arma
        public int Hits;       // golpes seguidos
        public float Range;    // alcance en bloques, hacia delante
    }

    public partial class SaoPlugin
    {
        public const int FuerzaWrathAt = 5;   // a partir de 5 puntos de Fuerza: buff Ira (más daño)

        // El número de la habilidad (1..5) es lo que se guarda en Slot1 / Slot2
        static readonly SkillDef[] Skills =
        {
            new SkillDef { Name = "Horizontal",       Level = 1,  Cooldown = 3,  Mult = 1.0f, Hits = 1, Range = 3f  },
            new SkillDef { Name = "Vertical",         Level = 10, Cooldown = 6,  Mult = 2.0f, Hits = 1, Range = 2f  },
            new SkillDef { Name = "Sonic Leap",       Level = 25, Cooldown = 8,  Mult = 1.5f, Hits = 1, Range = 5f  },
            new SkillDef { Name = "Starburst Stream", Level = 50, Cooldown = 10, Mult = 0.5f, Hits = 6, Range = 3f  },
            new SkillDef { Name = "Vorpal Strike",    Level = 75, Cooldown = 12, Mult = 3.0f, Hits = 1, Range = 10f },
        };

        readonly Dictionary<int, bool> lastUse = new Dictionary<int, bool>();
        readonly Dictionary<int, DateTime[]> cooldowns = new Dictionary<int, DateTime[]>();

        DateTime[] CooldownsOf(int playerIndex)
        {
            if (!cooldowns.TryGetValue(playerIndex, out var arr))
            {
                arr = new DateTime[Skills.Length];
                cooldowns[playerIndex] = arr;
            }
            return arr;
        }

        static bool IsSword(Item item)
        {
            return item != null && item.melee && item.damage > 0 && item.pick == 0 && item.axe == 0 && item.hammer == 0;
        }

        static string SkillLabel(int number)
        {
            return number <= 0 ? "ninguna" : Skills[number - 1].Name;
        }

        // ================= Gestos: se ejecuta cada tick =================

        void CheckGestures()
        {
            foreach (var p in TShock.Players)
            {
                if (p == null || !p.Active) continue;

                bool use = p.TPlayer.controlUseItem;
                bool wasUsing = lastUse.TryGetValue(p.Index, out bool prev) && prev;
                lastUse[p.Index] = use;

                // Solo actúa en el momento en que se pulsa atacar
                if (!use || wasUsing) continue;

                var d = GetData(p);
                if (d == null || d.Ghost || !d.Gestos) continue;

                int skill = p.TPlayer.controlDown ? d.Slot1 : (p.TPlayer.controlUp ? d.Slot2 : 0);
                if (skill > 0) TryCast(p, d, skill, false);
            }
        }

        // ================= Lanzar una habilidad =================

        void TryCast(TSPlayer p, PlayerData d, int skillNumber, bool verbose)
        {
            if (skillNumber < 1 || skillNumber > Skills.Length) return;
            var s = Skills[skillNumber - 1];
            var held = p.TPlayer.HeldItem;

            if (d.Ghost || p.Dead)
            {
                if (verbose) p.SendErrorMessage("No puedes usar habilidades ahora mismo.");
                return;
            }
            if (!IsSword(held))
            {
                if (verbose) p.SendErrorMessage("Necesitas llevar una espada en la mano.");
                return;
            }
            if (d.Level < s.Level)
            {
                if (verbose) p.SendErrorMessage(s.Name + " se desbloquea en el nivel " + s.Level + ".");
                return;
            }

            var cd = CooldownsOf(p.Index);
            double left = (cd[skillNumber - 1] - DateTime.UtcNow).TotalSeconds;
            if (left > 0)
            {
                if (verbose) p.SendErrorMessage(s.Name + " en enfriamiento: " + Math.Ceiling(left) + " s.");
                return;
            }

            cd[skillNumber - 1] = DateTime.UtcNow.AddSeconds(s.Cooldown);
            ExecuteSkill(p, s, held);
        }

        void ExecuteSkill(TSPlayer p, SkillDef s, Item weapon)
        {
            int dir = p.TPlayer.direction;      // 1 = derecha, -1 = izquierda
            Vector2 origin = p.TPlayer.Center;
            float range = s.Range * 16f;        // bloques a píxeles
            var pd = GetData(p);
            float strMult = 1f + (pd != null ? pd.Stats[0] * StrengthDamagePerPoint : 0f);
            // Daño real del arma (prefijos, mejoras...) con los multiplicadores de la habilidad
            int damage = (int)Math.Max(1, p.TPlayer.GetWeaponDamage(weapon) * s.Mult * strMult);

            // Enemigos delante del jugador, dentro del alcance
            var targets = new List<NPC>();
            foreach (var npc in Main.npc)
            {
                if (!npc.active || npc.friendly || npc.townNPC || npc.dontTakeDamage) continue;
                float dx = (npc.Center.X - origin.X) * dir;
                float dy = Math.Abs(npc.Center.Y - origin.Y);
                if (dx < 0 || dx > range || dy > range * 0.6f) continue;
                targets.Add(npc);
            }

            // Así la XP y los cristales de élite se atribuyen a quien usó la habilidad
            foreach (var npc in targets) RegisterHit(npc.whoAmI, p.Index);

            for (int h = 0; h < s.Hits; h++)
            {
                foreach (var npc in targets)
                {
                    if (!npc.active) continue;
                    // SimpleStrikeNPC crea el golpe internamente (sin nombrar NPC.HitInfo) y ya aplica la defensa
                    npc.SimpleStrikeNPC(damage, dir, false, 2f);
                    npc.netUpdate = true;
                }
            }

            p.SendData(PacketTypes.CreateCombatText, s.Name, (int)Color.White.PackedValue, p.X, p.Y - 40f);
        }

        // ================= /hab =================

        void ShowSkillHelp(TSPlayer p, PlayerData d)
        {
            p.SendInfoMessage("Uso: /hab lista | /hab equipar <número> <ranura 1|2> | /hab usar <ranura> | /hab gestos");
            p.SendInfoMessage("Gestos (con espada en la mano): agacharte + atacar = ranura 1; mirar arriba + atacar = ranura 2.");
            p.SendInfoMessage("Equipadas: ranura 1 = " + SkillLabel(d.Slot1) + ", ranura 2 = " + SkillLabel(d.Slot2) +
                ". Gestos " + (d.Gestos ? "activados" : "desactivados") + ".");
        }

        void CmdSkills(CommandArgs a)
        {
            var p = a.Player;
            var d = GetData(p);
            if (d == null)
            {
                p.SendErrorMessage("Debes iniciar sesión.");
                return;
            }
            if (a.Parameters.Count == 0)
            {
                ShowSkillHelp(p, d);
                return;
            }

            string sub = a.Parameters[0].ToLower();

            if (sub == "lista")
            {
                for (int i = 0; i < Skills.Length; i++)
                {
                    var s = Skills[i];
                    string state = d.Level >= s.Level ? "desbloqueada" : "nivel " + s.Level;
                    p.SendInfoMessage((i + 1) + ". " + s.Name + " (" + state + ", enfriamiento " + s.Cooldown + " s)");
                }
                return;
            }

            if (sub == "gestos")
            {
                d.Gestos = !d.Gestos;
                Save(p, d);
                p.SendInfoMessage("Gestos de habilidad: " + (d.Gestos ? "activados" : "desactivados"));
                return;
            }

            if (sub == "equipar")
            {
                if (a.Parameters.Count < 3 ||
                    !int.TryParse(a.Parameters[1], out int num) ||
                    !int.TryParse(a.Parameters[2], out int ranura) ||
                    ranura < 1 || ranura > 2 || num < 0 || num > Skills.Length)
                {
                    ShowSkillHelp(p, d);
                    return;
                }
                if (num > 0 && d.Level < Skills[num - 1].Level)
                {
                    p.SendErrorMessage(Skills[num - 1].Name + " requiere nivel " + Skills[num - 1].Level + ".");
                    return;
                }

                if (ranura == 1) d.Slot1 = num;
                else d.Slot2 = num;
                Save(p, d);
                p.SendSuccessMessage("Ranura " + ranura + ": " + SkillLabel(num));
                return;
            }

            if (sub == "usar")
            {
                if (a.Parameters.Count < 2 || !int.TryParse(a.Parameters[1], out int ranuraUso) || ranuraUso < 1 || ranuraUso > 2)
                {
                    ShowSkillHelp(p, d);
                    return;
                }
                int skill = ranuraUso == 1 ? d.Slot1 : d.Slot2;
                if (skill <= 0)
                {
                    p.SendErrorMessage("Esa ranura está vacía. Equipa una con /hab equipar.");
                    return;
                }
                TryCast(p, d, skill, true);
                return;
            }

            ShowSkillHelp(p, d);
        }
    }
}
