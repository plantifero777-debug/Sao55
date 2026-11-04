using System;
using TShockAPI;

namespace SaoPlugin
{
    public partial class SaoPlugin
    {
        // ================= /sao (solo sao.admin) =================
        // /sao nivel <jugador> <n>   -> fija el nivel (y reinicia la XP actual)
        // /sao puntos <jugador> <n> -> fija los puntos de habilidad disponibles

        void CmdSao(CommandArgs a)
        {
            var me = a.Player;
            if (a.Parameters.Count < 3)
            {
                ShowSaoHelp(me);
                return;
            }

            string sub = a.Parameters[0].ToLower();
            if (sub != "nivel" && sub != "puntos")
            {
                ShowSaoHelp(me);
                return;
            }

            var found = TSPlayer.FindByNameOrID(a.Parameters[1]);
            if (found.Count != 1)
            {
                me.SendErrorMessage(found.Count == 0 ? "No encuentro a ese jugador." : "Hay varios jugadores con ese nombre.");
                return;
            }

            var target = found[0];
            var d = GetData(target);
            if (d == null)
            {
                me.SendErrorMessage(target.Name + " no tiene datos de SAO (¿ha iniciado sesión?).");
                return;
            }

            if (!int.TryParse(a.Parameters[2], out int n))
            {
                me.SendErrorMessage("El valor debe ser un número.");
                return;
            }

            if (sub == "nivel")
            {
                d.Level = Math.Max(1, Math.Min(MaxLevel, n));
                d.Xp = 0;
                me.SendSuccessMessage("Nivel de " + target.Name + " fijado en " + d.Level + ".");
                target.SendInfoMessage("Un administrador ha fijado tu nivel en " + d.Level + ".");
            }
            else
            {
                d.Points = Math.Max(0, n);
                me.SendSuccessMessage("Puntos de " + target.Name + " fijados en " + d.Points + ".");
                target.SendInfoMessage("Un administrador ha fijado tus puntos de habilidad en " + d.Points + ".");
            }

            Save(target, d);
        }

        void ShowSaoHelp(TSPlayer p)
        {
            p.SendInfoMessage("Uso: /sao nivel <jugador> <n>   |   /sao puntos <jugador> <n>");
        }

        // ================= /ayuda =================

        void CmdAyuda(CommandArgs a)
        {
            var p = a.Player;
            p.SendInfoMessage("— Progreso —");
            p.SendInfoMessage("/stats  ver nivel, XP y estadísticas");
            p.SendInfoMessage("/up <fue|agi|vit> [cantidad]  repartir puntos de habilidad");
            p.SendInfoMessage("/barra  mostrar u ocultar tu nivel y XP sobre el personaje");
            p.SendInfoMessage("— Muerte —");
            p.SendInfoMessage("/revivir <jugador>  revivir a un fantasma (cuesta " + ReviveCost + " cristales de vida)");
            p.SendInfoMessage("— Party —");
            p.SendInfoMessage("/p  crear party  |  /parti <jugador>  invitar  |  /parti salir, lista, expulsar <jugador>");
            p.SendInfoMessage("/si y /no  responder a una invitación  |  /pt <jugador>  teletransportarte a un miembro");
            p.SendInfoMessage("— Habilidades —");
            p.SendInfoMessage("/hab  ayuda de habilidades  |  /hab lista  |  /hab equipar <n> <ranura>  |  /hab usar <ranura>  |  /hab gestos");
        }
    }
}
