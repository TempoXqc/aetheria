using System.Collections.Generic;
using Aetheria.Shared;
using Aetheria.Shared.Client;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;
using UnityEngine;
using Vec2 = Aetheria.Shared.Math.Vec2;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The playable isometric client. Reuses the exact same protocol/session code as the server's
    /// test harness (Aetheria.Shared.dll — the netstandard build of the server's shared layer), so
    /// the wire format can never drift between Unity and the server.
    ///
    /// Controls: WASD/arrows move · Tab target · 1 basic attack · 2 advanced ability · R racial ·
    /// F loot corpse · G invite target · J leave party · I dungeon (1) · O raid (2) · L leave
    /// instance · B/N deposit/withdraw 10 gold · Esc disconnect.
    /// </summary>
    public sealed class AetheriaClientBehaviour : MonoBehaviour
    {
        // --- Login form state ---
        private string _host = "127.0.0.1";
        private string _port = SimulationConstants.DefaultPort.ToString();
        private string _name = "Hero" + (System.DateTime.Now.Ticks % 1000);
        private string _account = "";
        private string _secret = "";
        private int _raceIndex;
        private int _classIndex;
        private int _genderIndex;
        private string _error = "";

        // Matches the server's data: race id, label, allowed class ids.
        private static readonly (byte id, string label, byte[] classes)[] Races =
        {
            (1, "Human (Alliance)", new byte[] { 1, 2 }),
            (4, "Dwarf (Alliance)", new byte[] { 1, 3 }),
            (2, "Orc (Horde)", new byte[] { 1, 3 }),
            (3, "Elf (Horde)", new byte[] { 2, 3 }),
        };

        private static readonly (byte id, string label, byte advancedAbility)[] Classes =
        {
            (1, "Warrior (Rage)", 20),
            (2, "Mage (Mana)", 21),
            (3, "Ranger (Energy)", 22),
        };

        // --- Session ---
        private UdpClientTransport _transport;
        private GameClient _client;
        private bool _connected;
        private byte _classId = 1;

        // --- World view ---
        private readonly Dictionary<int, EntityView> _views = new Dictionary<int, EntityView>();
        private readonly HashSet<int> _seenThisFrame = new HashSet<int>();
        private IsoCameraRig _cameraRig;
        private int _targetId = -1;
        private float _inputTimer;
        private readonly List<string> _combatLog = new List<string>();
        private int _lastLoggedCombatCount;

        private void Update()
        {
            if (!_connected)
            {
                return;
            }

            _client.Pump();

            if (_client.WasRejected)
            {
                _error = _client.RejectReason ?? "Rejected.";
                Disconnect();
                return;
            }

            SyncViews();
            AppendCombatLog();
            HandleKeys();
            SendMovement();
            FollowSelf();

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Disconnect();
            }
        }

        // ------------------------------------------------------------- Session

        private void Connect()
        {
            int.TryParse(_port, out int port);
            if (port <= 0)
            {
                port = SimulationConstants.DefaultPort;
            }

            byte raceId = Races[_raceIndex].id;
            _classId = Classes[_classIndex].id;
            Gender gender = _genderIndex == 1 ? Gender.Female : Gender.Male;
            string account = string.IsNullOrWhiteSpace(_account) ? _name : _account;
            string secret = string.IsNullOrEmpty(_secret) ? account : _secret;

            try
            {
                _transport = new UdpClientTransport();
                _client = new GameClient(_transport);
                _client.Connect(_host, port, _name, raceId, _classId, gender, account, secret);
                _connected = true;
                _error = "";
            }
            catch (System.Exception ex)
            {
                _error = ex.Message;
                _transport?.Dispose();
                _transport = null;
                _client = null;
            }
        }

        private void Disconnect()
        {
            if (_client != null)
            {
                _client.SendDisconnect();
            }

            _transport?.Dispose();
            _transport = null;
            _client = null;
            _connected = false;
            _targetId = -1;

            foreach (EntityView view in _views.Values)
            {
                if (view != null)
                {
                    Destroy(view.gameObject);
                }
            }

            _views.Clear();
            _combatLog.Clear();
            _lastLoggedCombatCount = 0;
        }

        private void OnDestroy()
        {
            Disconnect();
        }

        // --------------------------------------------------------------- World

        private void SyncViews()
        {
            _seenThisFrame.Clear();
            Faction myFaction = Faction.Neutral;
            EntitySnapshot self;
            bool haveSelf = _client.TryGetSelf(out self);
            if (haveSelf)
            {
                myFaction = self.Faction;
            }

            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot snapshot = visible[i];
                _seenThisFrame.Add(snapshot.Id);

                EntityView view;
                if (!_views.TryGetValue(snapshot.Id, out view) || view == null)
                {
                    view = EntityView.Create(snapshot.Id, snapshot.Kind);
                    _views[snapshot.Id] = view;
                }

                bool isSelf = _client.EntityId.HasValue && snapshot.Id == _client.EntityId.Value;
                view.ApplySnapshot(snapshot, myFaction, isSelf);
            }

            // Entities that left our area of interest (or died) vanish.
            var toRemove = new List<int>();
            foreach (KeyValuePair<int, EntityView> pair in _views)
            {
                if (!_seenThisFrame.Contains(pair.Key))
                {
                    if (pair.Value != null)
                    {
                        Destroy(pair.Value.gameObject);
                    }

                    toRemove.Add(pair.Key);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                _views.Remove(toRemove[i]);
                if (_targetId == toRemove[i])
                {
                    _targetId = -1;
                }
            }
        }

        private void AppendCombatLog()
        {
            if (_client.CombatEventsSeen == _lastLoggedCombatCount || !(_client.LastCombat is CombatEventMessage c))
            {
                return;
            }

            _lastLoggedCombatCount = _client.CombatEventsSeen;
            string line = c.AttackerId + " hit " + c.TargetId + " for " + c.Damage +
                          (c.TargetKilled ? "  — KILL!" : "  (" + c.TargetRemainingHealth + " hp left)");
            _combatLog.Add(line);
            if (_combatLog.Count > 6)
            {
                _combatLog.RemoveAt(0);
            }
        }

        private void FollowSelf()
        {
            if (_cameraRig == null)
            {
                Camera cam = Camera.main;
                _cameraRig = cam != null ? cam.GetComponent<IsoCameraRig>() : null;
            }

            EntitySnapshot self;
            if (_cameraRig != null && _client.TryGetSelf(out self))
            {
                _cameraRig.Target = new Vector3(self.Position.X, 0f, self.Position.Y);
            }
        }

        // --------------------------------------------------------------- Input

        private void SendMovement()
        {
            // The server simulates at a fixed 20 Hz; sending input faster is wasted packets.
            _inputTimer += Time.deltaTime;
            if (_inputTimer < SimulationConstants.TickDelta)
            {
                return;
            }

            _inputTimer = 0f;

            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            Vec2 dir = Vec2.Zero;
            if (h != 0f || v != 0f)
            {
                // Camera-relative: screen-up moves away from the camera across the ground plane.
                Transform camT = Camera.main != null ? Camera.main.transform : null;
                Vector3 fwd = camT != null ? camT.forward : Vector3.forward;
                Vector3 right = camT != null ? camT.right : Vector3.right;
                fwd.y = 0f;
                right.y = 0f;
                Vector3 world = (fwd.normalized * v) + (right.normalized * h);
                dir = new Vec2(world.x, world.z).Normalized();
            }

            _client.SendInput(dir);
        }

        private void HandleKeys()
        {
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                CycleTarget();
            }

            if (Input.GetKeyDown(KeyCode.Alpha1) && _targetId >= 0)
            {
                _client.SendUseAbility(_classId, _targetId); // basic ability id == class id in data
            }

            if (Input.GetKeyDown(KeyCode.Alpha2) && _targetId >= 0)
            {
                _client.SendUseAbility(Classes[_classIndex].advancedAbility, _targetId);
            }

            if (Input.GetKeyDown(KeyCode.R))
            {
                _client.SendUseRacial();
            }

            if (Input.GetKeyDown(KeyCode.F))
            {
                int corpse = _client.FindNearestCorpse();
                if (corpse >= 0)
                {
                    _client.SendLootCorpse(corpse);
                }
            }

            if (Input.GetKeyDown(KeyCode.G) && _targetId >= 0)
            {
                _client.SendPartyInvite(_targetId);
            }

            if (Input.GetKeyDown(KeyCode.H) && _client.PendingInviteFrom != null)
            {
                _client.SendPartyRespond(true);
            }

            if (Input.GetKeyDown(KeyCode.J))
            {
                _client.SendPartyLeave();
            }

            if (Input.GetKeyDown(KeyCode.I))
            {
                _client.SendEnterInstance(1);
            }

            if (Input.GetKeyDown(KeyCode.O))
            {
                _client.SendEnterInstance(2);
            }

            if (Input.GetKeyDown(KeyCode.L))
            {
                _client.SendLeaveInstance();
            }

            if (Input.GetKeyDown(KeyCode.B))
            {
                _client.SendBank(BankOp.DepositGold, 0, 10);
            }

            if (Input.GetKeyDown(KeyCode.N))
            {
                _client.SendBank(BankOp.WithdrawGold, 0, 10);
            }
        }

        /// <summary>Tab-cycle through hostile things (monsters and enemy players), nearest first.</summary>
        private void CycleTarget()
        {
            EntitySnapshot self;
            if (!_client.TryGetSelf(out self))
            {
                return;
            }

            var hostiles = new List<EntitySnapshot>();
            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                bool hostile = e.Kind == EntityKind.Monster ||
                               (e.Kind == EntityKind.Player && e.Faction != self.Faction);
                if (hostile && e.Id != self.Id)
                {
                    hostiles.Add(e);
                }
            }

            if (hostiles.Count == 0)
            {
                _targetId = -1;
                return;
            }

            hostiles.Sort((a, b) =>
                Vec2.DistanceSquared(self.Position, a.Position)
                    .CompareTo(Vec2.DistanceSquared(self.Position, b.Position)));

            int currentIndex = hostiles.FindIndex(e => e.Id == _targetId);
            _targetId = hostiles[(currentIndex + 1) % hostiles.Count].Id;
        }

        // ----------------------------------------------------------------- HUD

        private void OnGUI()
        {
            if (!_connected)
            {
                DrawLogin();
                return;
            }

            DrawHealthBars();
            DrawSelfPanel();
            DrawTargetPanel();
            DrawMessages();
            DrawHelp();
        }

        private void DrawLogin()
        {
            const int W = 340;
            Rect box = new Rect((Screen.width - W) / 2f, Screen.height * 0.18f, W, 330);
            GUI.Box(box, "AETHERIA — Connexion");

            GUILayout.BeginArea(new Rect(box.x + 15, box.y + 30, W - 30, box.height - 45));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Serveur", GUILayout.Width(70));
            _host = GUILayout.TextField(_host);
            GUILayout.Label(":", GUILayout.Width(8));
            _port = GUILayout.TextField(_port, GUILayout.Width(56));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Nom", GUILayout.Width(70));
            _name = GUILayout.TextField(_name);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Compte", GUILayout.Width(70));
            _account = GUILayout.TextField(_account);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Secret", GUILayout.Width(70));
            _secret = GUILayout.PasswordField(_secret, '*');
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            string[] raceLabels = { Races[0].label, Races[1].label, Races[2].label, Races[3].label };
            int newRace = GUILayout.SelectionGrid(_raceIndex, raceLabels, 2);
            if (newRace != _raceIndex)
            {
                _raceIndex = newRace;
                // Snap the class to something this race can play.
                if (System.Array.IndexOf(Races[_raceIndex].classes, Classes[_classIndex].id) < 0)
                {
                    for (int i = 0; i < Classes.Length; i++)
                    {
                        if (System.Array.IndexOf(Races[_raceIndex].classes, Classes[i].id) >= 0)
                        {
                            _classIndex = i;
                            break;
                        }
                    }
                }
            }

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Classes.Length; i++)
            {
                bool allowed = System.Array.IndexOf(Races[_raceIndex].classes, Classes[i].id) >= 0;
                GUI.enabled = allowed;
                if (GUILayout.Toggle(_classIndex == i, Classes[i].label, "Button") && allowed)
                {
                    _classIndex = i;
                }

                GUI.enabled = true;
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            _genderIndex = GUILayout.SelectionGrid(_genderIndex, new[] { "Male", "Female" }, 2);

            GUILayout.Space(8);
            if (GUILayout.Button("JOUER", GUILayout.Height(34)))
            {
                Connect();
            }

            if (!string.IsNullOrEmpty(_error))
            {
                GUILayout.Label("<color=#ff7070>" + _error + "</color>",
                    new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
            }

            GUILayout.EndArea();
        }

        private void DrawSelfPanel()
        {
            EntitySnapshot self;
            bool haveSelf = _client.TryGetSelf(out self);
            string hp = haveSelf ? self.Health + "/" + self.MaxHealth : "—";
            string res = haveSelf ? self.Resource + "/" + self.MaxResource : "—";
            string xp = _client.XpForNextLevel >= 0
                ? _client.TotalXp + "/" + _client.XpForNextLevel
                : _client.TotalXp + " (max)";

            GUI.Box(new Rect(10, 10, 230, 118), _name);
            GUI.Label(new Rect(20, 34, 210, 20), "Niveau " + _client.Level + "   XP " + xp);
            GUI.Label(new Rect(20, 52, 210, 20), "PV " + hp + "    Ressource " + res);
            GUI.Label(new Rect(20, 70, 210, 20), "Or " + _client.Gold + "    Banque " + _client.BankGold);
            string zone = _client.InInstance ? "INSTANCE" : "Monde ouvert";
            string party = _client.PartySize > 0
                ? "Groupe " + _client.PartySize + " (chef " + _client.PartyLeader + ")"
                : "Sans groupe";
            GUI.Label(new Rect(20, 88, 210, 20), zone + "  ·  " + party);
        }

        private void DrawTargetPanel()
        {
            if (_targetId < 0)
            {
                return;
            }

            EntitySnapshot target;
            if (!_client.TryGetEntity(_targetId, out target))
            {
                return;
            }

            Rect r = new Rect((Screen.width / 2f) - 110, 10, 220, 48);
            GUI.Box(r, "Cible : " + target.Kind + " #" + target.Id);
            DrawBar(new Rect(r.x + 10, r.y + 26, r.width - 20, 12),
                target.Health / (float)Mathf.Max(1, target.MaxHealth),
                new Color(0.85f, 0.2f, 0.2f));
        }

        private void DrawMessages()
        {
            float y = Screen.height - 150f;
            if (_client.PendingInviteFrom != null)
            {
                GUI.Box(new Rect(10, y - 30, 320, 26),
                    "Invitation de groupe de " + _client.PendingInviteFrom + " — [H] accepter");
            }

            if (!string.IsNullOrEmpty(_client.LastInstanceMessage))
            {
                GUI.Label(new Rect(10, y, 480, 22), _client.LastInstanceMessage);
            }

            for (int i = 0; i < _combatLog.Count; i++)
            {
                GUI.Label(new Rect(10, y + 22 + (i * 18), 480, 18), _combatLog[i]);
            }
        }

        private void DrawHelp()
        {
            const string help =
                "ZQSD/WASD bouger · Tab cible · 1 attaque · 2 sort avancé · R racial · F loot · " +
                "G inviter · H accepter · J quitter groupe · I donjon · O raid · L sortir · " +
                "B/N banque ±10 or · Échap quitter";
            GUI.Label(new Rect(10, Screen.height - 24, Screen.width - 20, 20), help);
        }

        private void DrawHealthBars()
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                return;
            }

            foreach (EntityView view in _views.Values)
            {
                if (view == null || view.Kind == EntityKind.Corpse || view.MaxHealth <= 0)
                {
                    continue;
                }

                Vector3 screen = cam.WorldToScreenPoint(view.transform.position + (Vector3.up * 1.6f));
                if (screen.z < 0f)
                {
                    continue;
                }

                var rect = new Rect(screen.x - 22, Screen.height - screen.y, 44, 6);
                DrawBar(rect, view.Health / (float)view.MaxHealth, new Color(0.2f, 0.8f, 0.25f));
            }
        }

        private static void DrawBar(Rect rect, float fill, Color color)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fill), rect.height),
                Texture2D.whiteTexture);
            GUI.color = old;
        }
    }
}
