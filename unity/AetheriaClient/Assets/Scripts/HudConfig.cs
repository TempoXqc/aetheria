using System.Collections.Generic;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// Everything the player can customize about the interface, saved in PlayerPrefs under one of
    /// three PROFILES: UI scale, toggles, keybinds, and the screen position of each movable HUD
    /// frame. Reconnecting (or restarting the game) restores the active profile automatically.
    /// </summary>
    public sealed class HudConfig
    {
        /// <summary>Rebindable actions (shown in Options → Raccourcis).</summary>
        public enum Bind
        {
            Attack1, Attack2, Renew, Racial, Interact, CharSheet,
            Invite, AcceptInvite, LeaveParty, Dungeon, Raid, LeaveInstance, NextTarget,
            Bags, WorldMap, QuestLog,
        }

        /// <summary>Movable HUD frames (shown in Options → Déplacer l'interface).</summary>
        public enum Frame
        {
            PlayerFrame, TargetFrame, ActionBar, Messages, XpBar,
            Minimap, QuestTracker, CharSheet, Bags, PartyFrames,
            Chat, MicroBar, CastBar, Shop, ConsumableBar,
        }

        private static readonly Dictionary<Bind, KeyCode> Defaults = new Dictionary<Bind, KeyCode>
        {
            { Bind.Attack1, KeyCode.Alpha1 },
            { Bind.Attack2, KeyCode.Alpha2 },
            { Bind.Renew, KeyCode.Alpha3 },
            { Bind.Racial, KeyCode.R },
            { Bind.Interact, KeyCode.F },
            { Bind.CharSheet, KeyCode.C },
            { Bind.Bags, KeyCode.B },
            { Bind.Invite, KeyCode.G },
            { Bind.AcceptInvite, KeyCode.H },
            { Bind.LeaveParty, KeyCode.J },
            { Bind.WorldMap, KeyCode.M },
            { Bind.QuestLog, KeyCode.L },
            { Bind.NextTarget, KeyCode.Tab },
        };

        public static readonly Dictionary<Bind, string> Labels = new Dictionary<Bind, string>
        {
            { Bind.Attack1, "Attaque de base" },
            { Bind.Attack2, "Sort avancé" },
            { Bind.Renew, "Régénération" },
            { Bind.Racial, "Capacité raciale" },
            { Bind.Interact, "Interagir / fouiller" },
            { Bind.CharSheet, "Fiche de personnage" },
            { Bind.Bags, "Sacs" },
            { Bind.Invite, "Inviter la cible" },
            { Bind.AcceptInvite, "Accepter l'invitation" },
            { Bind.LeaveParty, "Quitter le groupe" },
            { Bind.WorldMap, "Carte du monde" },
            { Bind.QuestLog, "Carnet de quêtes" },
            { Bind.NextTarget, "Cible suivante" },
        };

        private readonly Dictionary<Bind, KeyCode> _binds = new Dictionary<Bind, KeyCode>();
        private readonly Dictionary<Frame, Vector2> _offsets = new Dictionary<Frame, Vector2>();

        public int Profile { get; private set; } = 1;
        public float UiScale = 1f;
        public bool ShowHealthBars = true;
        public bool ShowHelp = true;
        public bool ShowNameplates = true;

        /// <summary>Auto-select the nearest hostile you're FACING when you have no target.</summary>
        public bool AutoTarget = false;

        public KeyCode Key(Bind bind) => _binds.TryGetValue(bind, out KeyCode k) ? k : Defaults[bind];

        public void SetKey(Bind bind, KeyCode key) => _binds[bind] = key;

        public bool Down(Bind bind) => Input.GetKeyDown(Key(bind));

        /// <summary>Offset added to a frame's default position (drag-to-move stores it here).</summary>
        public Vector2 Offset(Frame frame) => _offsets.TryGetValue(frame, out Vector2 o) ? o : Vector2.zero;

        public void SetOffset(Frame frame, Vector2 offset) => _offsets[frame] = offset;

        public void ResetLayout() => _offsets.Clear();

        // ------------------------------------------------------------ Storage

        private static string P(int profile, string key) => "aetheria.p" + profile + "." + key;

        public void Load(int profile)
        {
            Profile = Mathf.Clamp(profile, 1, 3);
            UiScale = PlayerPrefs.GetFloat(P(Profile, "uiScale"), 1f);
            ShowHealthBars = PlayerPrefs.GetInt(P(Profile, "healthBars"), 1) == 1;
            ShowHelp = PlayerPrefs.GetInt(P(Profile, "help"), 1) == 1;
            ShowNameplates = PlayerPrefs.GetInt(P(Profile, "nameplates"), 1) == 1;
            AutoTarget = PlayerPrefs.GetInt(P(Profile, "autoTarget"), 0) == 1;

            _binds.Clear();
            foreach (KeyValuePair<Bind, KeyCode> pair in Defaults)
            {
                int stored = PlayerPrefs.GetInt(P(Profile, "bind." + pair.Key), (int)pair.Value);
                _binds[pair.Key] = (KeyCode)stored;
            }

            _offsets.Clear();
            foreach (Frame frame in System.Enum.GetValues(typeof(Frame)))
            {
                float x = PlayerPrefs.GetFloat(P(Profile, "off." + frame + ".x"), 0f);
                float y = PlayerPrefs.GetFloat(P(Profile, "off." + frame + ".y"), 0f);
                if (x != 0f || y != 0f)
                {
                    _offsets[frame] = new Vector2(x, y);
                }
            }

            PlayerPrefs.SetInt("aetheria.activeProfile", Profile);
        }

        public void Save()
        {
            PlayerPrefs.SetFloat(P(Profile, "uiScale"), UiScale);
            PlayerPrefs.SetInt(P(Profile, "healthBars"), ShowHealthBars ? 1 : 0);
            PlayerPrefs.SetInt(P(Profile, "help"), ShowHelp ? 1 : 0);
            PlayerPrefs.SetInt(P(Profile, "nameplates"), ShowNameplates ? 1 : 0);
            PlayerPrefs.SetInt(P(Profile, "autoTarget"), AutoTarget ? 1 : 0);

            foreach (KeyValuePair<Bind, KeyCode> pair in _binds)
            {
                PlayerPrefs.SetInt(P(Profile, "bind." + pair.Key), (int)pair.Value);
            }

            foreach (Frame frame in System.Enum.GetValues(typeof(Frame)))
            {
                Vector2 o = Offset(frame);
                PlayerPrefs.SetFloat(P(Profile, "off." + frame + ".x"), o.x);
                PlayerPrefs.SetFloat(P(Profile, "off." + frame + ".y"), o.y);
            }

            PlayerPrefs.SetInt("aetheria.activeProfile", Profile);
            PlayerPrefs.Save();
        }

        public static int ActiveProfile() => PlayerPrefs.GetInt("aetheria.activeProfile", 1);
    }
}
