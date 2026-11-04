using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TShockAPI;

namespace SaoPlugin
{
    public class Party
    {
        public int Leader;
        public List<int> Members = new List<int>();
    }

    public class PendingInvite
    {
        public Party Party;
        public int From;
        public DateTime Expires;
    }

    public partial class SaoPlugin
    {
        readonly Dictionary<int, Party> partyOf = new Dictionary<int, Party>();
        readonly Dictionary<int, PendingInvite> invites = new Dictionary<int, PendingInvite>();
        readonly Dictionary<int, DateTime> lastTp = new Dictionary<int, DateTime>();

        // ================= Utilidades =================

        Party CreateParty(TSPlayer p)
        {
            var party = new Party { Leader = p.Index };
            party.Members.Add(p.Index);
            partyOf[p.Index] = party;
            return party;
        }

        void NotifyParty(Party party, string message)
        {
            foreach (int idx in party.Members.ToArray())
            {
                var m = TShock.Players[idx];
                if (m != null && m.Active) m.SendInfoMessage(message);
            }
        }

        // Saca a un jugador de su party. Si solo queda uno, la party se disuelve.
        void LeaveParty(int idx)
        {
            if (!partyOf.TryGetValue(idx, out var party)) return;

            partyOf.Remove(idx);
            party.Members.Remove(idx);

            if (party.Members.Count <= 1)
            {
                NotifyParty(party, "La party se ha disuelto.");
                foreach (int m in party.Members.ToArray()) partyOf.Remove(m);
                party.Members.Clear();
                return;
            }

            if (party.Leader == idx) party.Leader = party.Members[0];
            NotifyParty(party, "Un miembro ha salido de la party.");
        }

        void ExpireInvites()
        {
            var now = DateTime.UtcNow;
            foreach (var kv in invites.ToList())
            {
                if (now <= kv.Value.Expires) continue;

                invites.Remove(kv.Key);
                var target = TShock.Players[kv.Key];
                if (target != null && target.Active) target.SendWarningMessage("La invitación de party ha caducado.");
            }
        }

        void ShowPartyHelp(TSPlayer p)
        {
            p.SendInfoMessage("Uso: /p (crear party) | /parti <jugador> (invitar) | /parti salir | /parti lista | /parti expulsar <jugador>");
            p.SendInfoMessage("Responde a una invitación con /si o /no. Teletransporte a un miembro: /pt <jugador>.");
        }

        // ================= /p =================

        void CmdPartyCreate(CommandArgs a)
        {
            if (partyOf.ContainsKey(a.Player.Index))
            {
                a.Player.SendErrorMessage("Ya estás en una party. Usa /parti salir para salir.");
                return;
            }
            CreateParty(a.Player);
            a.Player.SendSuccessMessage("Has creado una party. Invita con /parti <jugador>.");
        }

        // ================= /parti =================

        void CmdParti(CommandArgs a)
        {
            var me = a.Player;
            if (a.Parameters.Count == 0)
            {
                ShowPartyHelp(me);
                return;
            }

            string sub = a.Parameters[0].ToLower();

            if (sub == "c" || sub == "create")
            {
                CmdPartyCreate(a);
                return;
            }

            if (sub == "salir")
            {
                if (!partyOf.ContainsKey(me.Index))
                {
                    me.SendErrorMessage("No estás en ninguna party.");
                    return;
                }
                LeaveParty(me.Index);
                me.SendInfoMessage("Has salido de la party.");
                return;
            }

            if (sub == "lista")
            {
                if (!partyOf.TryGetValue(me.Index, out var party))
                {
                    me.SendErrorMessage("No estás en ninguna party.");
                    return;
                }
                var names = party.Members.Select(i => TShock.Players[i]?.Name ?? "?");
                me.SendInfoMessage("Party: " + string.Join(", ", names) + (party.Leader == me.Index ? " (eres el líder)" : ""));
                return;
            }

            if (sub == "expulsar")
            {
                if (a.Parameters.Count < 2)
                {
                    me.SendInfoMessage("Uso: /parti expulsar <jugador>");
                    return;
                }
                if (!partyOf.TryGetValue(me.Index, out var party) || party.Leader != me.Index)
                {
                    me.SendErrorMessage("Solo el líder de la party puede expulsar.");
                    return;
                }
                var found = TSPlayer.FindByNameOrID(a.Parameters[1]);
                if (found.Count != 1)
                {
                    me.SendErrorMessage(found.Count == 0 ? "No encuentro a ese jugador." : "Hay varios jugadores con ese nombre.");
                    return;
                }
                var target = found[0];
                if (target.Index == me.Index || !party.Members.Contains(target.Index))
                {
                    me.SendErrorMessage(target.Name + " no está en tu party.");
                    return;
                }
                LeaveParty(target.Index);
                target.SendWarningMessage("Has sido expulsado de la party.");
                return;
            }

            InviteToParty(me, a.Parameters[0]);
        }

        void InviteToParty(TSPlayer me, string targetName)
        {
            var found = TSPlayer.FindByNameOrID(targetName);
            if (found.Count != 1)
            {
                me.SendErrorMessage(found.Count == 0 ? "No encuentro a ese jugador." : "Hay varios jugadores con ese nombre.");
                return;
            }

            var target = found[0];
            if (target.Index == me.Index)
            {
                me.SendErrorMessage("No puedes invitarte a ti mismo.");
                return;
            }

            if (partyOf.TryGetValue(target.Index, out var targetParty))
            {
                if (partyOf.TryGetValue(me.Index, out var myParty) && myParty == targetParty)
                {
                    me.SendErrorMessage(target.Name + " ya está en tu party.");
                    return;
                }
                me.SendErrorMessage(target.Name + " ya está en otra party.");
                return;
            }

            Party party = partyOf.TryGetValue(me.Index, out var mine) ? mine : CreateParty(me);

            invites[target.Index] = new PendingInvite
            {
                Party = party,
                From = me.Index,
                Expires = DateTime.UtcNow.AddSeconds(InviteExpireSec)
            };

            target.SendInfoMessage(me.Name + " te invita a su party. Escribe /si o /no (caduca en " + InviteExpireSec + " s).");
            me.SendSuccessMessage("Invitación enviada a " + target.Name + ".");
        }

        // ================= /si y /no =================

        void CmdYes(CommandArgs a)
        {
            var me = a.Player;
            if (!invites.TryGetValue(me.Index, out var inv))
            {
                me.SendErrorMessage("No tienes invitaciones pendientes.");
                return;
            }
            invites.Remove(me.Index);

            if (DateTime.UtcNow > inv.Expires)
            {
                me.SendErrorMessage("La invitación ha caducado.");
                return;
            }
            if (!partyOf.TryGetValue(inv.From, out var current) || current != inv.Party)
            {
                me.SendErrorMessage("La party ya no existe.");
                return;
            }

            LeaveParty(me.Index);
            inv.Party.Members.Add(me.Index);
            partyOf[me.Index] = inv.Party;

            NotifyParty(inv.Party, me.Name + " se ha unido a la party.");
        }

        void CmdNo(CommandArgs a)
        {
            var me = a.Player;
            if (!invites.TryGetValue(me.Index, out var inv))
            {
                me.SendErrorMessage("No tienes invitaciones pendientes.");
                return;
            }
            invites.Remove(me.Index);

            me.SendInfoMessage("Has rechazado la invitación.");
            var from = TShock.Players[inv.From];
            if (from != null && from.Active) from.SendWarningMessage(me.Name + " ha rechazado tu invitación.");
        }

        // ================= /pt <jugador> =================

        void CmdPartyTp(CommandArgs a)
        {
            var me = a.Player;
            var md = GetData(me);
            if (md == null)
            {
                me.SendErrorMessage("Debes iniciar sesión.");
                return;
            }
            if (md.Ghost)
            {
                me.SendErrorMessage("Un fantasma no puede teletransportarse.");
                return;
            }
            if (a.Parameters.Count == 0)
            {
                me.SendInfoMessage("Uso: /pt <jugador de tu party>  (enfriamiento de " + TeleportCooldownSec + " s)");
                return;
            }
            if (!partyOf.TryGetValue(me.Index, out var party))
            {
                me.SendErrorMessage("No estás en ninguna party.");
                return;
            }

            var found = TSPlayer.FindByNameOrID(a.Parameters[0]);
            if (found.Count != 1)
            {
                me.SendErrorMessage(found.Count == 0 ? "No encuentro a ese jugador." : "Hay varios jugadores con ese nombre.");
                return;
            }
            var target = found[0];
            if (target.Index == me.Index || !party.Members.Contains(target.Index))
            {
                me.SendErrorMessage(target.Name + " no está en tu party.");
                return;
            }

            if (lastTp.TryGetValue(me.Index, out var last))
            {
                double left = (last.AddSeconds(TeleportCooldownSec) - DateTime.UtcNow).TotalSeconds;
                if (left > 0)
                {
                    me.SendErrorMessage("Espera " + Math.Ceiling(left) + " s antes de volver a teletransportarte.");
                    return;
                }
            }

            // Teletransporte directo, sin que el otro tenga que aceptar
            me.Teleport(target.TPlayer.position.X, target.TPlayer.position.Y);
            lastTp[me.Index] = DateTime.UtcNow;
            me.SendSuccessMessage("Te has teletransportado junto a " + target.Name + ".");
        }
    }
}
