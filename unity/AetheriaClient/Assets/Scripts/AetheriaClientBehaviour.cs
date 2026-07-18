using System.Collections.Generic;
using Aetheria.Shared;
using Aetheria.Shared.Client;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Data;
using Aetheria.Shared.Items;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;
using UnityEngine;
using Vec2 = Aetheria.Shared.Math.Vec2;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The playable isometric client with a WoW-style HUD: player/target frames, a clickable action
    /// bar with cooldown sweeps, a corpse "[F]" prompt + per-item loot window, and an Escape menu
    /// (resume / options / disconnect / quit). Reuses the exact same protocol/session code as the
    /// server's test harness (Aetheria.Shared.dll), so the wire format can never drift.
    /// </summary>
    public sealed class AetheriaClientBehaviour : MonoBehaviour
    {
        // Content metadata (names, cooldowns, costs) — same defaults the server ships.
        private static readonly GameData Data = GameData.CreateDefault();

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

        // Matches the server's data: race id, label, allowed class ids, racial ability id.
        private static readonly (byte id, string label, byte[] classes, byte racial)[] Races =
        {
            (1, "Human (Alliance)", new byte[] { 1, 2 }, (byte)10),
            (4, "Dwarf (Alliance)", new byte[] { 1, 3 }, (byte)11),
            (2, "Orc (Horde)", new byte[] { 1, 3 }, (byte)12),
            (3, "Elf (Horde)", new byte[] { 2, 3 }, (byte)13),
        };

        private static readonly (byte id, string label, byte advancedAbility)[] Classes =
        {
            (1, "Warrior (Rage)", (byte)20),
            (2, "Mage (Mana)", (byte)21),
            (3, "Ranger (Energy)", (byte)22),
        };

        // --- Session ---
        private UdpClientTransport _transport;
        private GameClient _client;
        private bool _connected;
        private byte _classId = 1;
        private byte _racialId = 10;

        // --- World view ---
        private readonly Dictionary<int, EntityView> _views = new Dictionary<int, EntityView>();
        private readonly HashSet<int> _seenThisFrame = new HashSet<int>();
        private IsoCameraRig _cameraRig;
        private int _targetId = -1;
        private float _inputTimer;
        private readonly List<string> _combatLog = new List<string>();
        private int _lastLoggedCombatCount;

        // --- HUD state ---
        private bool _menuOpen;
        private bool _optionsOpen;
        private float _uiScale = 1f;
        private bool _showHealthBars = true;
        private bool _showHelp = true;
        private readonly Dictionary<byte, float> _cooldownReadyAt = new Dictionary<byte, float>();
        private int _nearbyCorpseId = -1;

        private void Start()
        {
            _uiScale = PlayerPrefs.GetFloat("aetheria.uiScale", 1f);
            _showHealthBars = PlayerPrefs.GetInt("aetheria.healthBars", 1) == 1;
            _showHelp = PlayerPrefs.GetInt("aetheria.help", 1) == 1;
        }

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

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_client.OpenCorpseId >= 0)
                {
                    _client.CloseCorpse();
                }
                else
                {
                    _menuOpen = !_menuOpen;
                    _optionsOpen = false;
                }
            }

            SyncViews();
            FindNearbyCorpse();
            AppendCombatLog();

            if (!_menuOpen)
            {
                HandleKeys();
                HandleMouseSelect();
                AutoAttackTick();
                SendMovement();
            }

            FollowSelf();
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
            _racialId = Races[_raceIndex].racial;
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
            _menuOpen = false;
            _optionsOpen = false;
            _cooldownReadyAt.Clear();

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
            SaveOptions();
            Disconnect();
        }

        private void SaveOptions()
        {
            PlayerPrefs.SetFloat("aetheria.uiScale", _uiScale);
            PlayerPrefs.SetInt("aetheria.healthBars", _showHealthBars ? 1 : 0);
            PlayerPrefs.SetInt("aetheria.help", _showHelp ? 1 : 0);
            PlayerPrefs.Save();
        }

        // --------------------------------------------------------------- World

        private void SyncViews()
        {
            _seenThisFrame.Clear();
            Faction myFaction = Faction.Neutral;
            EntitySnapshot self;
            if (_client.TryGetSelf(out self))
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

                if (_client.OpenCorpseId == toRemove[i])
                {
                    _client.CloseCorpse(); // the corpse we were looting despawned
                }
            }
        }

        /// <summary>Track the nearest corpse within loot range for the "[F]" prompt.</summary>
        private void FindNearbyCorpse()
        {
            _nearbyCorpseId = -1;
            EntitySnapshot self;
            if (!_client.TryGetSelf(out self))
            {
                return;
            }

            float best = SimulationConstants.LootRange * SimulationConstants.LootRange;
            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                if (e.Kind != EntityKind.Corpse)
                {
                    continue;
                }

                float d = Vec2.DistanceSquared(self.Position, e.Position);
                if (d <= best)
                {
                    best = d;
                    _nearbyCorpseId = e.Id;
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
            string line = c.AttackerId + " → " + c.TargetId + " : " + c.Damage +
                          (c.TargetKilled ? "  — MORT !" : "  (" + c.TargetRemainingHealth + " pv)");
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

        private float _lastFacing;

        private void SendMovement()
        {
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
                Transform camT = Camera.main != null ? Camera.main.transform : null;
                Vector3 fwd = camT != null ? camT.forward : Vector3.forward;
                Vector3 right = camT != null ? camT.right : Vector3.right;
                fwd.y = 0f;
                right.y = 0f;
                Vector3 world = (fwd.normalized * v) + (right.normalized * h);
                dir = new Vec2(world.x, world.z).Normalized();
            }

            // The character faces the mouse cursor (WoW-style aiming on the ground plane).
            EntitySnapshot self;
            Vector3 mouseGround;
            if (_client.TryGetSelf(out self) && TryMouseGroundPoint(out mouseGround))
            {
                float dx = mouseGround.x - self.Position.X;
                float dz = mouseGround.z - self.Position.Y;
                if ((dx * dx) + (dz * dz) > 0.04f)
                {
                    _lastFacing = Mathf.Atan2(dz, dx); // server plane: 0 = +X
                }
            }

            _client.SendInput(dir, _lastFacing);
        }

        /// <summary>Ray from the camera through the mouse onto the ground plane (y = 0).</summary>
        private static bool TryMouseGroundPoint(out Vector3 point)
        {
            point = Vector3.zero;
            Camera cam = Camera.main;
            if (cam == null)
            {
                return false;
            }

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            var ground = new Plane(Vector3.up, Vector3.zero);
            float enter;
            if (!ground.Raycast(ray, out enter))
            {
                return false;
            }

            point = ray.GetPoint(enter);
            return true;
        }

        /// <summary>Left-click selects the hostile under (or nearest to) the cursor; empty ground deselects.</summary>
        private void HandleMouseSelect()
        {
            if (!Input.GetMouseButtonDown(0) || _client.OpenCorpseId >= 0)
            {
                return;
            }

            EntitySnapshot self;
            if (!_client.TryGetSelf(out self) || Camera.main == null)
            {
                return;
            }

            const float PickRadiusPx = 36f;
            Vector3 mouse = Input.mousePosition;
            int best = -1;
            float bestDist = PickRadiusPx * PickRadiusPx;

            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                bool hostile = e.Kind == EntityKind.Monster ||
                               (e.Kind == EntityKind.Player && e.Faction != self.Faction);
                if (!hostile || e.Id == self.Id)
                {
                    continue;
                }

                Vector3 screen = Camera.main.WorldToScreenPoint(new Vector3(e.Position.X, 1f, e.Position.Y));
                if (screen.z < 0f)
                {
                    continue;
                }

                float dx = screen.x - mouse.x;
                float dy = screen.y - mouse.y;
                float d = (dx * dx) + (dy * dy);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = e.Id;
                }
            }

            _targetId = best; // clicking empty ground deselects (best stays -1)
            _autoAttack = best >= 0; // clicking an enemy starts fighting it
        }

        private void HandleKeys()
        {
            if (Input.GetKeyDown(KeyCode.Tab))
            {
                CycleTarget();
            }

            if (Input.GetKeyDown(KeyCode.Alpha1))
            {
                TryCastOnTarget(_classId);
            }

            if (Input.GetKeyDown(KeyCode.Alpha2))
            {
                TryCastOnTarget(Classes[_classIndex].advancedAbility);
            }

            if (Input.GetKeyDown(KeyCode.Alpha3))
            {
                TryCastSelf(5); // Renew: heal (and mana) over time
            }

            if (Input.GetKeyDown(KeyCode.R))
            {
                TryUseRacial();
            }

            if (Input.GetKeyDown(KeyCode.F))
            {
                PressLoot();
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

        private void PressLoot()
        {
            if (_client.OpenCorpseId >= 0)
            {
                _client.CloseCorpse();
            }
            else if (_nearbyCorpseId >= 0)
            {
                _client.SendOpenCorpse(_nearbyCorpseId);
            }
        }

        private void TryCastOnTarget(byte abilityId)
        {
            if (_targetId < 0 || IsOnCooldown(abilityId))
            {
                return;
            }

            _client.SendUseAbility(abilityId, _targetId);
            StartLocalCooldown(abilityId);
        }

        private void TryUseRacial()
        {
            if (IsOnCooldown(_racialId))
            {
                return;
            }

            _client.SendUseRacial();
            StartLocalCooldown(_racialId);
        }

        /// <summary>Cast a self-targeted class ability (range 0, e.g. Renew).</summary>
        private void TryCastSelf(byte abilityId)
        {
            if (IsOnCooldown(abilityId) || !_client.EntityId.HasValue)
            {
                return;
            }

            _client.SendUseAbility(abilityId, _client.EntityId.Value);
            StartLocalCooldown(abilityId);
        }

        private bool _autoAttack;

        /// <summary>
        /// Click-to-fight: after left-clicking an enemy, keep swinging the class's basic attack
        /// (sword / bow / bare hands) whenever the target is in range and the swing is ready.
        /// </summary>
        private void AutoAttackTick()
        {
            if (!_autoAttack || _targetId < 0)
            {
                return;
            }

            EntitySnapshot self;
            EntitySnapshot target;
            if (!_client.TryGetSelf(out self) || !_client.TryGetEntity(_targetId, out target))
            {
                return;
            }

            AbilityDefinition basic = Data.GetAbility(_classId);
            float rangeSq = basic.Range * basic.Range;
            if (Vec2.DistanceSquared(self.Position, target.Position) <= rangeSq &&
                !IsOnCooldown(_classId) &&
                (basic.ResourceCost <= 0 || self.Resource >= basic.ResourceCost))
            {
                TryCastOnTarget(_classId);
            }
        }

        private bool IsOnCooldown(byte abilityId)
        {
            float readyAt;
            return _cooldownReadyAt.TryGetValue(abilityId, out readyAt) && Time.time < readyAt;
        }

        private float CooldownRemaining(byte abilityId)
        {
            float readyAt;
            if (!_cooldownReadyAt.TryGetValue(abilityId, out readyAt))
            {
                return 0f;
            }

            return Mathf.Max(0f, readyAt - Time.time);
        }

        /// <summary>Local cooldown mirror for the HUD sweep. The server stays the real authority.</summary>
        private void StartLocalCooldown(byte abilityId)
        {
            AbilityDefinition def = Data.GetAbility(abilityId);
            _cooldownReadyAt[abilityId] = Time.time + (def.CooldownTicks * SimulationConstants.TickDelta);
        }

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

        private float VirtW => Screen.width / _uiScale;
        private float VirtH => Screen.height / _uiScale;

        private void OnGUI()
        {
            GUI.matrix = Matrix4x4.Scale(new Vector3(_uiScale, _uiScale, 1f));

            if (!_connected)
            {
                DrawLogin();
                return;
            }

            if (_showHealthBars)
            {
                DrawWorldHealthBars();
            }

            DrawCorpsePrompt();
            DrawPlayerFrame();
            DrawTargetFrame();
            DrawActionBar();
            DrawMessages();
            DrawLootWindow();

            if (_showHelp)
            {
                DrawHelp();
            }

            if (_menuOpen)
            {
                DrawEscapeMenu();
            }
        }

        // --- Login ---

        private void DrawLogin()
        {
            const int W = 340;
            Rect box = new Rect((VirtW - W) / 2f, VirtH * 0.16f, W, 360);
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

        // --- Frames ---

        private void DrawPlayerFrame()
        {
            EntitySnapshot self;
            bool haveSelf = _client.TryGetSelf(out self);

            Rect frame = new Rect(12, 12, 250, 92);
            GUI.Box(frame, "");
            GUI.Label(new Rect(frame.x + 8, frame.y + 4, 170, 20), "<b>" + _name + "</b>", Rich());
            GUI.Label(new Rect(frame.x + frame.width - 70, frame.y + 4, 64, 20),
                "Niv. " + _client.Level, Rich());

            float hpFill = haveSelf && self.MaxHealth > 0 ? self.Health / (float)self.MaxHealth : 0f;
            DrawBar(new Rect(frame.x + 8, frame.y + 26, frame.width - 16, 16), hpFill,
                new Color(0.20f, 0.75f, 0.25f),
                haveSelf ? self.Health + " / " + self.MaxHealth : "—");

            float resFill = haveSelf && self.MaxResource > 0 ? self.Resource / (float)self.MaxResource : 0f;
            DrawBar(new Rect(frame.x + 8, frame.y + 46, frame.width - 16, 14), resFill,
                ResourceColor(), haveSelf ? self.Resource + " / " + self.MaxResource : "—");

            float xpFill = _client.XpForNextLevel > 0 ? Mathf.Clamp01(_client.TotalXp / (float)_client.XpForNextLevel) : 1f;
            DrawBar(new Rect(frame.x + 8, frame.y + 64, frame.width - 16, 8), xpFill,
                new Color(0.55f, 0.35f, 0.85f), "");

            GUI.Label(new Rect(frame.x + 8, frame.y + 72, frame.width - 16, 18),
                "Or " + _client.Gold + "   Banque " + _client.BankGold +
                (_client.InInstance ? "   [INSTANCE]" : "") +
                (_client.PartySize > 0 ? "   Groupe " + _client.PartySize : ""));
        }

        private Color ResourceColor()
        {
            switch (_classId)
            {
                case 1: return new Color(0.85f, 0.20f, 0.20f);  // Rage
                case 2: return new Color(0.25f, 0.45f, 0.95f);  // Mana
                default: return new Color(0.95f, 0.85f, 0.25f); // Energy
            }
        }

        private void DrawTargetFrame()
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

            Rect frame = new Rect((VirtW / 2f) - 130, 12, 260, 58);
            GUI.Box(frame, "");
            string label = target.Kind == EntityKind.Player
                ? (target.Faction.ToString() + " (joueur)")
                : LookupMonsterLabel(target);
            GUI.Label(new Rect(frame.x + 8, frame.y + 4, frame.width - 16, 20), "<b>" + label + "</b>", Rich());
            DrawBar(new Rect(frame.x + 8, frame.y + 28, frame.width - 16, 16),
                target.Health / (float)Mathf.Max(1, target.MaxHealth),
                new Color(0.85f, 0.2f, 0.2f), target.Health + " / " + target.MaxHealth);
        }

        private static string LookupMonsterLabel(EntitySnapshot target)
        {
            // Names aren't networked yet; infer the archetype from its stats.
            if (target.MaxHealth >= 2000) return "Boss mondial";
            if (target.MaxHealth >= 300) return "Élite";
            return "Monstre";
        }

        // --- Action bar ---

        private void DrawActionBar()
        {
            const float Slot = 52f;
            const float Pad = 6f;
            EntitySnapshot self;
            bool haveSelf = _client.TryGetSelf(out self);
            int myResource = haveSelf ? self.Resource : 0;

            var slots = new List<(string key, byte ability, string label)>
            {
                ("1", _classId, Data.GetAbility(_classId).Name),
                ("2", Classes[_classIndex].advancedAbility, Data.GetAbility(Classes[_classIndex].advancedAbility).Name),
                ("3", (byte)5, Data.GetAbility(5).Name),
                ("R", _racialId, Data.GetAbility(_racialId).Name),
            };

            float totalW = (slots.Count * (Slot + Pad)) - Pad;
            float x = (VirtW - totalW) / 2f;
            float y = VirtH - Slot - 14f;

            for (int i = 0; i < slots.Count; i++)
            {
                (string key, byte abilityId, string label) = slots[i];
                AbilityDefinition def = Data.GetAbility(abilityId);
                Rect r = new Rect(x + (i * (Slot + Pad)), y, Slot, Slot);

                bool locked = def.UnlockLevel > _client.Level;
                bool tooPoor = def.ResourceCost > 0 && myResource < def.ResourceCost;
                float cd = CooldownRemaining(abilityId);
                bool usable = !locked && !tooPoor && cd <= 0f && (_targetId >= 0 || def.Range <= 0f);

                GUI.enabled = usable;
                if (GUI.Button(r, ""))
                {
                    if (abilityId == _racialId)
                    {
                        TryUseRacial();
                    }
                    else if (def.Range <= 0f)
                    {
                        TryCastSelf(abilityId);
                    }
                    else
                    {
                        TryCastOnTarget(abilityId);
                    }
                }

                GUI.enabled = true;

                // Slot content: name, hotkey, cost.
                GUI.Label(new Rect(r.x + 3, r.y + 2, r.width - 6, 16), "<size=10>" + label + "</size>", Rich());
                GUI.Label(new Rect(r.x + 3, r.y + r.height - 18, 20, 16), "<b>" + key + "</b>", Rich());
                if (def.ResourceCost > 0)
                {
                    GUI.Label(new Rect(r.x + r.width - 24, r.y + r.height - 18, 22, 16),
                        "<size=10>" + def.ResourceCost + "</size>", Rich());
                }

                // Overlays: lock, cooldown sweep.
                if (locked)
                {
                    Dim(r, 0.75f);
                    GUI.Label(new Rect(r.x, r.y + (r.height / 2f) - 9, r.width, 18),
                        "<size=10>Niv. " + def.UnlockLevel + "</size>", RichCentered());
                }
                else if (cd > 0f)
                {
                    float frac = cd / Mathf.Max(0.01f, def.CooldownTicks * SimulationConstants.TickDelta);
                    Dim(new Rect(r.x, r.y, r.width, r.height * Mathf.Clamp01(frac)), 0.65f);
                    GUI.Label(new Rect(r.x, r.y + (r.height / 2f) - 10, r.width, 20),
                        "<b>" + Mathf.CeilToInt(cd) + "</b>", RichCentered());
                }
                else if (tooPoor)
                {
                    Dim(r, 0.45f);
                }
            }
        }

        // --- Corpse prompt & loot window ---

        private void DrawCorpsePrompt()
        {
            if (_nearbyCorpseId < 0 || _client.OpenCorpseId >= 0)
            {
                return;
            }

            EntityView view;
            if (!_views.TryGetValue(_nearbyCorpseId, out view) || view == null || Camera.main == null)
            {
                return;
            }

            Vector3 screen = Camera.main.WorldToScreenPoint(view.transform.position + (Vector3.up * 1.2f));
            if (screen.z < 0f)
            {
                return;
            }

            Rect r = new Rect((screen.x / _uiScale) - 70, ((Screen.height - screen.y) / _uiScale) - 14, 140, 24);
            GUI.Box(r, "[F] Fouiller le cadavre");
        }

        private void DrawLootWindow()
        {
            if (_client.OpenCorpseId < 0)
            {
                return;
            }

            int rows = _client.OpenCorpseItems.Count + (_client.OpenCorpseGold > 0 ? 1 : 0);
            float height = 74f + (rows * 26f) + 34f;
            Rect win = new Rect((VirtW / 2f) - 140, (VirtH / 2f) - (height / 2f), 280, height);
            GUI.Box(win, "<b>Butin</b>", RichCenteredBox());

            if (GUI.Button(new Rect(win.x + win.width - 24, win.y + 4, 20, 20), "X"))
            {
                _client.CloseCorpse();
                return;
            }

            float y = win.y + 30f;

            if (_client.OpenCorpseGold > 0)
            {
                GUI.Label(new Rect(win.x + 12, y + 3, 160, 20), "Or : " + _client.OpenCorpseGold);
                if (GUI.Button(new Rect(win.x + win.width - 86, y, 74, 22), "Prendre"))
                {
                    _client.SendLootItem(_client.OpenCorpseId, 0);
                }

                y += 26f;
            }

            IReadOnlyList<ItemStack> items = _client.OpenCorpseItems;
            for (int i = 0; i < items.Count; i++)
            {
                ItemStack stack = items[i];
                string label = Data.GetItem(stack.ItemId).Name + (stack.Quantity > 1 ? " ×" + stack.Quantity : "");
                GUI.Label(new Rect(win.x + 12, y + 3, 160, 20), label);
                if (GUI.Button(new Rect(win.x + win.width - 86, y, 74, 22), "Prendre"))
                {
                    _client.SendLootItem(_client.OpenCorpseId, stack.ItemId);
                }

                y += 26f;
            }

            if (GUI.Button(new Rect(win.x + 12, y + 6, win.width - 24, 24), "Tout prendre"))
            {
                _client.SendLootCorpse(_client.OpenCorpseId);
            }
        }

        // --- Escape menu ---

        private void DrawEscapeMenu()
        {
            Dim(new Rect(0, 0, VirtW, VirtH), 0.45f);

            const float W = 260f;
            float h = _optionsOpen ? 240f : 200f;
            Rect win = new Rect((VirtW - W) / 2f, (VirtH - h) / 2f, W, h);
            GUI.Box(win, _optionsOpen ? "<b>Options</b>" : "<b>Menu</b>", RichCenteredBox());

            GUILayout.BeginArea(new Rect(win.x + 16, win.y + 30, W - 32, h - 40));

            if (_optionsOpen)
            {
                GUILayout.Label("Échelle de l'interface : " + _uiScale.ToString("0.00"));
                _uiScale = Mathf.Round(GUILayout.HorizontalSlider(_uiScale, 0.8f, 1.6f) * 20f) / 20f;
                GUILayout.Space(6);
                _showHealthBars = GUILayout.Toggle(_showHealthBars, " Barres de vie au-dessus des entités");
                _showHelp = GUILayout.Toggle(_showHelp, " Ligne d'aide en bas de l'écran");
                GUILayout.Space(10);
                if (GUILayout.Button("Retour", GUILayout.Height(26)))
                {
                    _optionsOpen = false;
                    SaveOptions();
                }
            }
            else
            {
                if (GUILayout.Button("Retour au jeu", GUILayout.Height(28)))
                {
                    _menuOpen = false;
                }

                if (GUILayout.Button("Options", GUILayout.Height(28)))
                {
                    _optionsOpen = true;
                }

                if (GUILayout.Button("Se déconnecter", GUILayout.Height(28)))
                {
                    Disconnect();
                }

                if (GUILayout.Button("Quitter le jeu", GUILayout.Height(28)))
                {
                    SaveOptions();
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                }
            }

            GUILayout.EndArea();
        }

        // --- Shared HUD bits ---

        private void DrawMessages()
        {
            float y = VirtH - 170f;
            if (_client.PendingInviteFrom != null)
            {
                GUI.Box(new Rect(12, y - 30, 320, 26),
                    "Invitation de groupe de " + _client.PendingInviteFrom + " — [H] accepter");
            }

            if (!string.IsNullOrEmpty(_client.LastInstanceMessage))
            {
                GUI.Label(new Rect(12, y, 480, 22), _client.LastInstanceMessage);
            }

            for (int i = 0; i < _combatLog.Count; i++)
            {
                GUI.Label(new Rect(12, y + 22 + (i * 17), 480, 18),
                    "<size=11>" + _combatLog[i] + "</size>", Rich());
            }
        }

        private void DrawHelp()
        {
            const string help =
                "WASD bouger · souris orienter · clic gauche = attaquer l'ennemi · Tab cible · 1/2 sorts · " +
                "3 régén · R racial · F fouiller · G/H/J groupe · I/O/L instances · B/N banque · Échap menu";
            GUI.Label(new Rect(12, VirtH - 24, VirtW - 24, 20), "<size=11>" + help + "</size>", Rich());
        }

        private void DrawWorldHealthBars()
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

                var rect = new Rect((screen.x / _uiScale) - 22, ((Screen.height - screen.y) / _uiScale), 44, 6);
                DrawBar(rect, view.Health / (float)view.MaxHealth, new Color(0.2f, 0.8f, 0.25f), "");
            }
        }

        private static void DrawBar(Rect rect, float fill, Color color, string caption)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fill), rect.height),
                Texture2D.whiteTexture);
            GUI.color = old;

            if (!string.IsNullOrEmpty(caption) && rect.height >= 12f)
            {
                GUI.Label(new Rect(rect.x, rect.y - 2, rect.width, rect.height + 4),
                    "<size=10>" + caption + "</size>", RichCentered());
            }
        }

        private static void Dim(Rect rect, float alpha)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, alpha);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private static GUIStyle Rich()
        {
            return new GUIStyle(GUI.skin.label) { richText = true };
        }

        private static GUIStyle RichCentered()
        {
            return new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.MiddleCenter };
        }

        private static GUIStyle RichCenteredBox()
        {
            return new GUIStyle(GUI.skin.box) { richText = true, alignment = TextAnchor.UpperCenter };
        }
    }
}
