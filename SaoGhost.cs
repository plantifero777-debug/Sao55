using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;

namespace SaoPlugin
{
    public partial class SaoPlugin
    {
        // ================= Recoger objetos (fantasma) =================
        // Cada fantasma recuerda lo que llevaba al morir. Si su inventario gana objetos, se devuelven al suelo.
        readonly Dictionary<int, Dictionary<int, int>> ghostInventory = new Dictionary<int, Dictionary<int, int>>();

        // Slots 0-57: inventario, monedas y munición (el 58 es el objeto del cursor)
        const int InvSlots = 58;

        static Dictionary<int, int> CountInventory(TSPlayer p)
        {
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < InvSlots; i++)
            {
                var item = p.TPlayer.inventory[i];
                if (item == null || item.type == 0 || item.stack <= 0) continue;
                counts[item.type] = counts.TryGetValue(item.type, out int c) ? c + item.stack : item.stack;
            }
            return counts;
        }

        void CheckGhostPickups()
        {
            foreach (var p in TShock.Players)
            {
                if (p == null || !p.Active) continue;
                var d = GetData(p);
                if (d == null || !d.Ghost) continue;

                if (!ghostInventory.TryGetValue(p.Index, out var known))
                {
                    ghostInventory[p.Index] = CountInventory(p);
                    continue;
                }

                // Sin personajes en el servidor (SSC), el cliente manda sobre su inventario e ignora
                // los cambios que haga el servidor. Soltar una copia en el suelo DUPLICARÍA el objeto,
                // así que en ese caso solo se bloquea la recogida (BlockGhostGrab) y no se toca nada.
                if (!Main.ServerSideCharacter)
                {
                    ghostInventory[p.Index] = CountInventory(p);
                    continue;
                }

                var now = CountInventory(p);
                foreach (var kv in now)
                {
                    int before = known.TryGetValue(kv.Key, out int b) ? b : 0;
                    int extra = kv.Value - before;
                    if (extra <= 0) continue;

                    // Quita lo recogido. El objeto sigue en el suelo (OnItemDrop cancela la recogida),
                    // así que NO se suelta otra copia: duplicaría el objeto.
                    RemoveItems(p, kv.Key, extra);
                    p.SendWarningMessage("Un fantasma no puede recoger objetos.");
                }

                ghostInventory[p.Index] = CountInventory(p);
            }
        }

        // ================= Bloquear la recogida a los fantasmas =================
        // El cliente solo recoge objetos "reservados" para él. Para cada objeto cercano al fantasma
        // enviamos el paquete ItemOwner con el dueño del servidor (255), así el cliente no lo recoge.
        const float GhostGrabBlockRange = 400f;   // píxeles
        readonly Dictionary<int, HashSet<int>> ghostBlocked = new Dictionary<int, HashSet<int>>();

        void BlockGhostGrab()
        {
            foreach (var p in TShock.Players)
            {
                if (p == null || !p.Active) continue;
                var d = GetData(p);
                if (d == null || !d.Ghost) continue;

                if (!ghostBlocked.TryGetValue(p.Index, out var sent))
                {
                    sent = new HashSet<int>();
                    ghostBlocked[p.Index] = sent;
                }

                Vector2 c = p.TPlayer.Center;
                for (int i = 0; i < Main.maxItems; i++)
                {
                    var it = Main.item[i];
                    bool near = it != null && it.active && Vector2.Distance(it.Center, c) <= GhostGrabBlockRange;
                    if (!near)
                    {
                        sent.Remove(i);
                        continue;
                    }
                    // Se reenvía también si el servidor volvió a reservarlo para este fantasma
                    if (sent.Add(i) || it.playerIndexTheItemIsReservedFor == p.Index)
                    {
                        it.playerIndexTheItemIsReservedFor = 255;
                        NetMessage.SendData((int)PacketTypes.ItemOwner, p.Index, -1, null, i);
                    }
                }
            }
        }

        // ================= Registro de hooks anti-recogida =================
        // Llamar a RegisterGhostPickupHooks() en Initialize y a UnregisterGhostPickupHooks() en Dispose.
        void RegisterGhostPickupHooks()
        {
            GetDataHandlers.ItemDrop += OnItemDrop;
            ServerApi.Hooks.GameUpdate.Register(this, OnGhostGameUpdate);
        }

        void UnregisterGhostPickupHooks()
        {
            GetDataHandlers.ItemDrop -= OnItemDrop;
            ServerApi.Hooks.GameUpdate.Deregister(this, OnGhostGameUpdate);
        }

        // Cada tick: evita que el cliente de un fantasma pueda recoger objetos cercanos
        void OnGhostGameUpdate(EventArgs e)
        {
            BlockGhostGrab();
        }

        // ================= Muerte -> fantasma =================

        void OnKillMe(object sender, GetDataHandlers.KillMeEventArgs args)
        {
            var d = GetData(args.Player);
            if (d == null || d.Ghost) return;

            d.Ghost = true;
            ghostInventory[args.Player.Index] = CountInventory(args.Player);
            Save(args.Player, d);

            args.Player.SendWarningMessage("Has muerto: ahora eres un fantasma. No pierdes nada.");
            args.Player.SendWarningMessage("Otro jugador puede revivirte con /revivir " + args.Player.Name + " (" + ReviveCost + " cristales de vida).");
            TShock.Utils.Broadcast(args.Player.Name + " ha caído y es ahora un fantasma.", Color.LightGray);
        }

        // ================= Acciones bloqueadas para fantasmas =================

        void OnTileEdit(object sender, GetDataHandlers.TileEditEventArgs args)
        {
            var d = GetData(args.Player);
            if (d == null || !d.Ghost) return;

            args.Handled = true;
            args.Player.SendTileSquareCentered(args.X, args.Y, 4);
        }

        void OnNewProjectile(object sender, GetDataHandlers.NewProjectileEventArgs args)
        {
            var d = GetData(args.Player);
            if (d == null || !d.Ghost) return;

            args.Handled = true;

        }

        // Cancela la recogida de objetos del suelo: el cliente avisa con ItemDrop (ID existente, Type 0).
        // Se reenvía el objeto real para que siga en el suelo.
        void OnItemDrop(object sender, GetDataHandlers.ItemDropEventArgs args)
        {
            var d = GetData(args.Player);
            if (d == null || !d.Ghost) return;

            // ID 400 = objeto nuevo soltado; cualquier otro ID con Type 0 = recogida
            if (args.ID != 400 && args.Type == 0)
            {
                args.Handled = true;
                NetMessage.SendData((int)PacketTypes.ItemDrop, args.Player.Index, -1, null, args.ID);
            }
        }

        void OnChestOpen(object sender, GetDataHandlers.ChestOpenEventArgs args)
        {
            var d = GetData(args.Player);
            if (d == null || !d.Ghost) return;

            args.Handled = true;
        }

        // ================= /revivir <jugador> =================

        void CmdRevive(CommandArgs a)
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
                me.SendErrorMessage("Un fantasma no puede revivir a nadie, ni a sí mismo.");
                return;
            }
            if (a.Parameters.Count == 0)
            {
                me.SendInfoMessage("Uso: /revivir <jugador>   (cuesta " + ReviveCost + " cristales de vida)");
                return;
            }

            if (a.Parameters[0] == "@a")
            {
                ReviveAll(me);
                return;
            }

            var found = TSPlayer.FindByNameOrID(a.Parameters[0]);
            if (found.Count != 1)
            {
                me.SendErrorMessage(found.Count == 0 ? "No encuentro a ese jugador." : "Hay varios jugadores con ese nombre, sé más específico.");
                return;
            }

            var target = found[0];
            var td = GetData(target);
            if (td == null || !td.Ghost)
            {
                me.SendErrorMessage(target.Name + " no es un fantasma.");
                return;
            }

            // Los administradores (permiso sao.admin) reviven gratis; sirve para probar
            bool free = me.HasPermission("sao.admin");
            if (!free)
            {
                int have = CountItem(me, ItemID.LifeCrystal);
                if (have < ReviveCost)
                {
                    me.SendErrorMessage("Necesitas " + ReviveCost + " cristales de vida y tienes " + have + ".");
                    return;
                }
                RemoveItems(me, ItemID.LifeCrystal, ReviveCost);
            }

            Unghost(target, td);
            TShock.Utils.Broadcast(me.Name + " ha revivido a " + target.Name + ".", Color.LightGreen);
        }

        // /revivir @a: revive a todos los fantasmas conectados, sin coste. Solo owner.
        void ReviveAll(TSPlayer me)
        {
            if (me.Group.Name != "owner" && !me.HasPermission("sao.reviveall"))
            {
                me.SendErrorMessage("Solo el grupo owner puede usar /revivir @a.");
                return;
            }

            int count = 0;
            foreach (var t in TShock.Players)
            {
                if (t == null || !t.Active) continue;
                var td = GetData(t);
                if (td == null || !td.Ghost) continue;

                Unghost(t, td);
                count++;
            }

            TShock.Utils.Broadcast(me.Name + " ha revivido a todos los fantasmas (" + count + ").", Color.LightGreen);
        }

        void Unghost(TSPlayer t, PlayerData d)
        {
            d.Ghost = false;
            ghostInventory.Remove(t.Index);
            Save(t, d);

            t.GodMode = false;

            for (int i = 0; i < t.TPlayer.buffType.Length; i++)
            {
                if (t.TPlayer.buffType[i] == BuffID.Invisibility)
                    t.TPlayer.DelBuff(i);
            }
            NetMessage.SendData((int)PacketTypes.PlayerBuff, -1, -1, null, t.Index);

            t.Heal(t.TPlayer.statLifeMax2);
            t.SendSuccessMessage("¡Has sido revivido!");
        }

        // ================= Utilidades de inventario =================

        static int CountItem(TSPlayer p, int itemType)
        {
            int total = 0;
            for (int i = 0; i < 50; i++)
            {
                var item = p.TPlayer.inventory[i];
                if (item != null && item.type == itemType) total += item.stack;
            }
            return total;
        }

        static void RemoveItems(TSPlayer p, int itemType, int amount)
        {
            for (int i = 0; i < InvSlots && amount > 0; i++)
            {
                var item = p.TPlayer.inventory[i];
                if (item == null || item.type != itemType) continue;

                int take = Math.Min(amount, item.stack);
                item.stack -= take;
                amount -= take;
                if (item.stack <= 0) item.SetDefaults(0);

                p.SendData(PacketTypes.PlayerSlot, null, p.Index, i);
            }
        }
    }
}
