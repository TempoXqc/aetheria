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
    /// The playable isometric client. WoW-style HUD with movable frames, rebindable keys and three
    /// saved interface profiles; nameplates with names/levels; right-click context actions; a
    /// character sheet; per-item corpse looting; and an Escape menu. All protocol/session logic
    /// lives in Aetheria.Shared.dll — the exact code the server's test harness uses.
    /// </summary>
    public sealed class AetheriaClientBehaviour : MonoBehaviour
    {
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
        private bool _autoAttack;
        private float _inputTimer;
        private float _lastFacing;
        private readonly List<string> _combatLog = new List<string>();
        private int _lastLoggedCombatCount;
        private int _nearbyCorpseId = -1;

        // --- HUD state ---
        private readonly HudConfig _cfg = new HudConfig();
        private bool _menuOpen;
        private int _optionsTab = -1; // -1 menu, 0 général, 1 raccourcis, 2 disposition
        private bool _layoutEditMode;
        private HudConfig.Bind? _awaitingBind;
        private bool _sheetOpen;
        private readonly Dictionary<byte, float> _cooldownReadyAt = new Dictionary<byte, float>();

        // Right-click context menu on a friendly player.
        private int _contextEntityId = -1;
        private Vector2 _contextPos;

        // Trade window local offer (mirrored to the server on every change).
        private readonly List<ItemStack> _myOffer = new List<ItemStack>();
        private int _myOfferGold;
        private bool _wasTradeActive;
        private ItemStack? _pendingAutoOffer; // set when a drag-drop onto a player starts the trade

        // Drag & drop from the character-sheet bag.
        private ItemStack? _draggingItem;
        private string _lastDuelMessage = "";
        private string _lastTradeMessage = "";

        // Layout drag state.
        private HudConfig.Frame? _draggingFrame;
        private Vector2 _dragStartMouse;
        private Vector2 _dragStartOffset;

        private void Start()
        {
            _cfg.Load(HudConfig.ActiveProfile());
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

            // Lobby phases (login / character screen): just pump the socket, no world logic yet.
            if (!_client.EntityId.HasValue)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_awaitingBind != null) { _awaitingBind = null; }
                else if (_contextEntityId >= 0) { _contextEntityId = -1; }
                else if (_client.OpenCorpseId >= 0) { _client.CloseCorpse(); }
                else if (_sheetOpen) { _sheetOpen = false; }
                else { _menuOpen = !_menuOpen; _optionsTab = -1; _layoutEditMode = false; }
            }

            SyncViews();
            PlayCombatAnimations();
            FindNearbyCorpse();
            AppendCombatLog();
            TrackSocialState();

            if (!_menuOpen && _awaitingBind == null)
            {
                HandleKeys();
                HandleMouse();
                AutoAttackTick();
                SendMovement();
            }

            FollowSelf();
        }

        // ------------------------------------------------------------- Session

        private void Connect()
        {
            int.TryParse(_port, out int port);
            if (port <= 0) { port = SimulationConstants.DefaultPort; }

            string account = _account.Trim();
            if (account.Length == 0)
            {
                _error = "Entre un identifiant de compte.";
                return;
            }

            string secret = string.IsNullOrEmpty(_secret) ? account : _secret;

            try
            {
                _transport = new UdpClientTransport();
                _client = new GameClient(_transport);
                _client.Connect(_host, port, account, secret);
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

        /// <summary>Play the existing character shown on the character screen.</summary>
        private void EnterExisting()
        {
            _name = _client.CharacterName;
            _classId = _client.CharacterClassId;
            for (int i = 0; i < Classes.Length; i++)
            {
                if (Classes[i].id == _classId) { _classIndex = i; }
            }

            for (int i = 0; i < Races.Length; i++)
            {
                if (Races[i].id == _client.CharacterRaceId) { _racialId = Races[i].racial; }
            }

            _client.SendEnterWorld();
        }

        /// <summary>Create the account's character on this server, then enter the world.</summary>
        private void CreateAndEnter()
        {
            _classId = Classes[_classIndex].id;
            _racialId = Races[_raceIndex].racial;
            Gender gender = _genderIndex == 1 ? Gender.Female : Gender.Male;
            _client.SendCreateCharacter(_name, Races[_raceIndex].id, _classId, gender);
        }

        private void Disconnect()
        {
            if (_client != null) { _client.SendDisconnect(); }
            _transport?.Dispose();
            _transport = null;
            _client = null;
            _connected = false;
            _targetId = -1;
            _autoAttack = false;
            _menuOpen = false;
            _sheetOpen = false;
            _contextEntityId = -1;
            _cooldownReadyAt.Clear();

            foreach (EntityView view in _views.Values)
            {
                if (view != null) { Destroy(view.gameObject); }
            }

            _views.Clear();
            _combatLog.Clear();
            _lastLoggedCombatCount = 0;
        }

        private void OnDestroy()
        {
            _cfg.Save();
            Disconnect();
        }

        // --------------------------------------------------------------- World

        private void SyncViews()
        {
            _seenThisFrame.Clear();
            Faction myFaction = Faction.Neutral;
            EntitySnapshot self;
            if (_client.TryGetSelf(out self)) { myFaction = self.Faction; }

            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot snapshot = visible[i];
                _seenThisFrame.Add(snapshot.Id);

                EntityView view;
                if (!_views.TryGetValue(snapshot.Id, out view) || view == null)
                {
                    view = EntityView.Create(snapshot);
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
                    if (pair.Value != null) { Destroy(pair.Value.gameObject); }
                    toRemove.Add(pair.Key);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                _views.Remove(toRemove[i]);
                if (_targetId == toRemove[i]) { _targetId = -1; _autoAttack = false; }
                if (_contextEntityId == toRemove[i]) { _contextEntityId = -1; }
                if (_client.OpenCorpseId == toRemove[i]) { _client.CloseCorpse(); }
            }
        }

        /// <summary>Turn this frame's combat events into attack swings and hit flashes on the models.</summary>
        private void PlayCombatAnimations()
        {
            IReadOnlyList<CombatEventMessage> feed = _client.DrainCombatFeed();
            for (int i = 0; i < feed.Count; i++)
            {
                CombatEventMessage evt = feed[i];
                EntityView attacker;
                if (_views.TryGetValue(evt.AttackerId, out attacker) && attacker != null)
                {
                    attacker.TriggerAttack();
                }

                EntityView target;
                if (evt.Damage > 0 && _views.TryGetValue(evt.TargetId, out target) && target != null)
                {
                    target.TriggerHit();
                }
            }
        }

        private void FindNearbyCorpse()
        {
            _nearbyCorpseId = -1;
            EntitySnapshot self;
            if (!_client.TryGetSelf(out self)) { return; }

            float best = SimulationConstants.LootRange * SimulationConstants.LootRange;
            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                if (e.Kind != EntityKind.Corpse) { continue; }
                float d = Vec2.DistanceSquared(self.Position, e.Position);
                if (d <= best) { best = d; _nearbyCorpseId = e.Id; }
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
            if (_combatLog.Count > 6) { _combatLog.RemoveAt(0); }
        }

        /// <summary>Log duel/trade transitions and apply the drag-drop auto-offer when a trade opens.</summary>
        private void TrackSocialState()
        {
            if (_client.DuelMessage != _lastDuelMessage && !string.IsNullOrEmpty(_client.DuelMessage))
            {
                _lastDuelMessage = _client.DuelMessage;
                _combatLog.Add("<color=#f0c040>" + _client.DuelMessage + "</color>");
                if (_combatLog.Count > 6) { _combatLog.RemoveAt(0); }
            }

            if (_client.TradeMessage != _lastTradeMessage && !string.IsNullOrEmpty(_client.TradeMessage))
            {
                _lastTradeMessage = _client.TradeMessage;
                _combatLog.Add("<color=#40d0f0>" + _client.TradeMessage + "</color>");
                if (_combatLog.Count > 6) { _combatLog.RemoveAt(0); }
            }

            if (_client.TradeActive && !_wasTradeActive)
            {
                _myOffer.Clear();
                _myOfferGold = 0;
                if (_pendingAutoOffer != null)
                {
                    _myOffer.Add(_pendingAutoOffer.Value); // the dragged item opens as the first offer
                    _pendingAutoOffer = null;
                    PushOffer();
                }
            }
            else if (!_client.TradeActive && _wasTradeActive)
            {
                _myOffer.Clear();
                _myOfferGold = 0;
            }

            _wasTradeActive = _client.TradeActive;
        }

        private void PushOffer() => _client.SendTradeSetOffer(_myOfferGold, _myOffer);

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

        private void HandleKeys()
        {
            if (_cfg.Down(HudConfig.Bind.NextTarget)) { CycleTarget(); }
            if (_cfg.Down(HudConfig.Bind.Attack1)) { TryCastOnTarget(_classId); }
            if (_cfg.Down(HudConfig.Bind.Attack2)) { TryCastOnTarget(Classes[_classIndex].advancedAbility); }
            if (_cfg.Down(HudConfig.Bind.Renew)) { TryCastSelf(5); }
            if (_cfg.Down(HudConfig.Bind.Racial)) { TryUseRacial(); }
            if (_cfg.Down(HudConfig.Bind.Interact)) { PressLoot(); }
            if (_cfg.Down(HudConfig.Bind.CharSheet)) { _sheetOpen = !_sheetOpen; }
            if (_cfg.Down(HudConfig.Bind.Invite) && _targetId >= 0) { _client.SendPartyInvite(_targetId); }
            if (_cfg.Down(HudConfig.Bind.AcceptInvite) && _client.PendingInviteFrom != null) { _client.SendPartyRespond(true); }
            if (_cfg.Down(HudConfig.Bind.LeaveParty)) { _client.SendPartyLeave(); }
            if (_cfg.Down(HudConfig.Bind.Dungeon)) { _client.SendEnterInstance(1); }
            if (_cfg.Down(HudConfig.Bind.Raid)) { _client.SendEnterInstance(2); }
            if (_cfg.Down(HudConfig.Bind.LeaveInstance)) { _client.SendLeaveInstance(); }
        }

        private void HandleMouse()
        {
            if (Input.GetMouseButtonDown(0))
            {
                _contextEntityId = -1; // any left click closes a context menu
                HandleLeftClick();
            }

            if (Input.GetMouseButtonDown(1))
            {
                HandleRightClick();
            }
        }

        /// <summary>Left-click: select the hostile under the cursor and start fighting it.</summary>
        private void HandleLeftClick()
        {
            if (_client.OpenCorpseId >= 0) { return; }

            EntitySnapshot self;
            if (!_client.TryGetSelf(out self)) { return; }

            EntitySnapshot? picked = PickEntityUnderMouse(e =>
                (e.Kind == EntityKind.Monster ||
                 (e.Kind == EntityKind.Player && e.Faction != self.Faction) ||
                 e.Id == _client.DuelOpponentId) && e.Id != self.Id);

            _targetId = picked?.Id ?? -1;
            _autoAttack = picked != null;
        }

        /// <summary>
        /// Right-click = context action: a corpse opens its loot, a hostile becomes the attack
        /// target, a same-faction player opens a context menu (invite / inspect / duel).
        /// </summary>
        private void HandleRightClick()
        {
            EntitySnapshot self;
            if (!_client.TryGetSelf(out self)) { return; }

            EntitySnapshot? picked = PickEntityUnderMouse(e => e.Id != self.Id);
            if (picked == null) { return; }

            EntitySnapshot target = picked.Value;
            switch (target.Kind)
            {
                case EntityKind.Corpse:
                    _client.SendOpenCorpse(target.Id); // range enforced server-side
                    break;

                case EntityKind.Monster:
                    _targetId = target.Id;
                    _autoAttack = true;
                    break;

                case EntityKind.Player when target.Faction != self.Faction || target.Id == _client.DuelOpponentId:
                    _targetId = target.Id;
                    _autoAttack = true;
                    break;

                case EntityKind.Player:
                    _contextEntityId = target.Id; // ally: open the context menu
                    _contextPos = new Vector2(Input.mousePosition.x / _cfg.UiScale,
                        (Screen.height - Input.mousePosition.y) / _cfg.UiScale);
                    break;
            }
        }

        private EntitySnapshot? PickEntityUnderMouse(System.Func<EntitySnapshot, bool> filter)
        {
            if (Camera.main == null) { return null; }

            const float PickRadiusPx = 36f;
            Vector3 mouse = Input.mousePosition;
            EntitySnapshot? best = null;
            float bestDist = PickRadiusPx * PickRadiusPx;

            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                if (!filter(e)) { continue; }

                Vector3 screen = Camera.main.WorldToScreenPoint(new Vector3(e.Position.X, 1f, e.Position.Y));
                if (screen.z < 0f) { continue; }

                float dx = screen.x - mouse.x;
                float dy = screen.y - mouse.y;
                float d = (dx * dx) + (dy * dy);
                if (d < bestDist) { bestDist = d; best = e; }
            }

            return best;
        }

        private void PressLoot()
        {
            if (_client.OpenCorpseId >= 0) { _client.CloseCorpse(); }
            else if (_nearbyCorpseId >= 0) { _client.SendOpenCorpse(_nearbyCorpseId); }
        }

        private void TryCastOnTarget(byte abilityId)
        {
            if (_targetId < 0 || IsOnCooldown(abilityId)) { return; }
            _client.SendUseAbility(abilityId, _targetId);
            StartLocalCooldown(abilityId);
        }

        private void TryCastSelf(byte abilityId)
        {
            if (IsOnCooldown(abilityId) || !_client.EntityId.HasValue) { return; }
            _client.SendUseAbility(abilityId, _client.EntityId.Value);
            StartLocalCooldown(abilityId);
        }

        private void TryUseRacial()
        {
            if (IsOnCooldown(_racialId)) { return; }
            _client.SendUseRacial();
            StartLocalCooldown(_racialId);
        }

        private void AutoAttackTick()
        {
            if (!_autoAttack || _targetId < 0) { return; }

            EntitySnapshot self;
            EntitySnapshot target;
            if (!_client.TryGetSelf(out self) || !_client.TryGetEntity(_targetId, out target)) { return; }

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
            if (!_cooldownReadyAt.TryGetValue(abilityId, out readyAt)) { return 0f; }
            return Mathf.Max(0f, readyAt - Time.time);
        }

        private void StartLocalCooldown(byte abilityId)
        {
            AbilityDefinition def = Data.GetAbility(abilityId);
            _cooldownReadyAt[abilityId] = Time.time + (def.CooldownTicks * SimulationConstants.TickDelta);
        }

        private void CycleTarget()
        {
            EntitySnapshot self;
            if (!_client.TryGetSelf(out self)) { return; }

            var hostiles = new List<EntitySnapshot>();
            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                bool hostile = e.Kind == EntityKind.Monster ||
                               (e.Kind == EntityKind.Player && e.Faction != self.Faction) ||
                               e.Id == _client.DuelOpponentId;
                if (hostile && e.Id != self.Id) { hostiles.Add(e); }
            }

            if (hostiles.Count == 0) { _targetId = -1; return; }

            hostiles.Sort((a, b) =>
                Vec2.DistanceSquared(self.Position, a.Position)
                    .CompareTo(Vec2.DistanceSquared(self.Position, b.Position)));

            int currentIndex = hostiles.FindIndex(e => e.Id == _targetId);
            _targetId = hostiles[(currentIndex + 1) % hostiles.Count].Id;
        }

        private void SendMovement()
        {
            _inputTimer += Time.deltaTime;
            if (_inputTimer < SimulationConstants.TickDelta) { return; }
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

            EntitySnapshot self;
            Vector3 mouseGround;
            if (_client.TryGetSelf(out self) && TryMouseGroundPoint(out mouseGround))
            {
                float dx = mouseGround.x - self.Position.X;
                float dz = mouseGround.z - self.Position.Y;
                if ((dx * dx) + (dz * dz) > 0.04f) { _lastFacing = Mathf.Atan2(dz, dx); }
            }

            _client.SendInput(dir, _lastFacing);
        }

        private static bool TryMouseGroundPoint(out Vector3 point)
        {
            point = Vector3.zero;
            Camera cam = Camera.main;
            if (cam == null) { return false; }

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            var ground = new Plane(Vector3.up, Vector3.zero);
            float enter;
            if (!ground.Raycast(ray, out enter)) { return false; }
            point = ray.GetPoint(enter);
            return true;
        }

        // ----------------------------------------------------------------- HUD

        private float VirtW => Screen.width / _cfg.UiScale;
        private float VirtH => Screen.height / _cfg.UiScale;

        /// <summary>A frame's rect = its default position + the profile's saved drag offset.</summary>
        private Rect FrameRect(HudConfig.Frame frame)
        {
            Vector2 o = _cfg.Offset(frame);
            switch (frame)
            {
                case HudConfig.Frame.PlayerFrame: return new Rect(12 + o.x, 12 + o.y, 250, 96);
                case HudConfig.Frame.TargetFrame: return new Rect((VirtW / 2f) - 130 + o.x, 12 + o.y, 260, 62);
                case HudConfig.Frame.ActionBar: return new Rect((VirtW / 2f) - 120 + o.x, VirtH - 66 + o.y, 240, 52);
                case HudConfig.Frame.XpBar: return new Rect((VirtW / 2f) - 220 + o.x, VirtH - 88 + o.y, 440, 16);
                case HudConfig.Frame.Messages: return new Rect(12 + o.x, VirtH - 200 + o.y, 480, 160);
                default: return new Rect(0, 0, 100, 100);
            }
        }

        private void OnGUI()
        {
            GUI.matrix = Matrix4x4.Scale(new Vector3(_cfg.UiScale, _cfg.UiScale, 1f));

            if (!_connected || _client == null || !_client.EntityId.HasValue)
            {
                DrawAuthScreens();
                return;
            }

            if (_cfg.ShowHealthBars || _cfg.ShowNameplates) { DrawNameplates(); }
            DrawCorpsePrompt();
            DrawPlayerFrame();
            DrawTargetFrame();
            DrawXpBar();
            DrawActionBar();
            DrawMessages();
            DrawLootWindow();
            DrawCharacterSheet();
            DrawContextMenu();
            DrawSocialNotices();
            DrawTradeWindow();
            DrawInspectWindow();
            DrawDragGhost();
            DrawVersionTag();
            if (_cfg.ShowHelp) { DrawHelp(); }
            if (_layoutEditMode) { DrawLayoutEditor(); }
            if (_menuOpen) { DrawEscapeMenu(); }
        }

        // --- Auth flow screens: login → your character (or creation) → world ---

        private void DrawAuthScreens()
        {
            if (!_connected || _client == null)
            {
                DrawLoginScreen();
            }
            else if (!_client.LoggedIn)
            {
                DrawWaitScreen("Connexion au serveur…");
            }
            else if (_client.HasCharacter)
            {
                DrawCharacterScreen();
            }
            else
            {
                DrawCreationScreen();
            }
        }

        private void DrawTitledBox(out Rect box, float height, string subtitle)
        {
            const int W = 360;
            box = new Rect((VirtW - W) / 2f, VirtH * 0.14f, W, height);
            GUI.Box(box, "<size=16><b>AETHERIA</b></size>   <size=10>v" + SimulationConstants.GameVersion +
                         "</size>\n<size=11>" + subtitle + "</size>", RichCenteredBox());
        }

        private void DrawErrors()
        {
            string error = _error;
            if (string.IsNullOrEmpty(error) && _client != null) { error = _client.LoginError; }
            if (!string.IsNullOrEmpty(error))
            {
                GUILayout.Label("<color=#ff7070>" + error + "</color>",
                    new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });
            }
        }

        /// <summary>Screen 1 — the account: server address, account id, secret.</summary>
        private void DrawLoginScreen()
        {
            DrawTitledBox(out Rect box, 240, "Connexion");
            GUILayout.BeginArea(new Rect(box.x + 15, box.y + 48, box.width - 30, box.height - 60));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Serveur", GUILayout.Width(70));
            _host = GUILayout.TextField(_host);
            GUILayout.Label(":", GUILayout.Width(8));
            _port = GUILayout.TextField(_port, GUILayout.Width(56));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Compte", GUILayout.Width(70));
            _account = GUILayout.TextField(_account);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Secret", GUILayout.Width(70));
            _secret = GUILayout.PasswordField(_secret, '*');
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            if (GUILayout.Button("SE CONNECTER", GUILayout.Height(34))) { Connect(); }

            GUILayout.Space(4);
            GUILayout.Label("<size=10><i>Un personnage par serveur — change d'adresse pour jouer un autre héros.</i></size>", Rich());
            DrawErrors();
            GUILayout.EndArea();
        }

        private void DrawWaitScreen(string message)
        {
            DrawTitledBox(out Rect box, 150, message);
            GUILayout.BeginArea(new Rect(box.x + 15, box.y + 60, box.width - 30, 70));
            DrawErrors();
            if (GUILayout.Button("Annuler", GUILayout.Height(26))) { Disconnect(); }
            GUILayout.EndArea();
        }

        /// <summary>Screen 2a — the account already has its character on this server.</summary>
        private void DrawCharacterScreen()
        {
            DrawTitledBox(out Rect box, 250, "Ton personnage");
            GUILayout.BeginArea(new Rect(box.x + 15, box.y + 48, box.width - 30, box.height - 60));

            string race = "?";
            for (int i = 0; i < Races.Length; i++)
            {
                if (Races[i].id == _client.CharacterRaceId) { race = Races[i].label; }
            }

            string cls = "?";
            for (int i = 0; i < Classes.Length; i++)
            {
                if (Classes[i].id == _client.CharacterClassId) { cls = Classes[i].label; }
            }

            GUILayout.Label("<size=15><b>" + _client.CharacterName + "</b></size>", Rich());
            GUILayout.Label(race + " · " + cls + " · niveau " + _client.CharacterLevel);
            GUILayout.Label("<size=10><i>" + _client.CharacterGender + "</i></size>", Rich());

            GUILayout.Space(12);
            if (GUILayout.Button("ENTRER EN JEU", GUILayout.Height(38))) { EnterExisting(); }

            GUILayout.Space(6);
            if (GUILayout.Button("Changer de serveur", GUILayout.Height(24))) { Disconnect(); }

            DrawErrors();
            GUILayout.EndArea();
        }

        /// <summary>Screen 2b — no character on this server yet: create and name it.</summary>
        private void DrawCreationScreen()
        {
            DrawTitledBox(out Rect box, 330, "Crée ton personnage");
            GUILayout.BeginArea(new Rect(box.x + 15, box.y + 48, box.width - 30, box.height - 60));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Nom", GUILayout.Width(70));
            _name = GUILayout.TextField(_name);
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

            GUILayout.Space(10);
            if (GUILayout.Button("CRÉER ET ENTRER EN JEU", GUILayout.Height(38))) { CreateAndEnter(); }

            GUILayout.Space(4);
            GUILayout.Label("<size=10><i>Tu apparaîtras dans le sanctuaire — une zone sans PvP ni monstres.</i></size>", Rich());
            if (GUILayout.Button("Changer de serveur", GUILayout.Height(22))) { Disconnect(); }

            DrawErrors();
            GUILayout.EndArea();
        }

        // --- Frames ---

        private void DrawPlayerFrame()
        {
            Rect frame = FrameRect(HudConfig.Frame.PlayerFrame);
            EntitySnapshot self;
            bool haveSelf = _client.TryGetSelf(out self);

            GUI.Box(frame, "");
            GUI.Label(new Rect(frame.x + 8, frame.y + 4, 170, 20), "<b>" + _name + "</b>", Rich());
            GUI.Label(new Rect(frame.x + frame.width - 70, frame.y + 4, 64, 20), "Niv. " + _client.Level, Rich());

            float hpFill = haveSelf && self.MaxHealth > 0 ? self.Health / (float)self.MaxHealth : 0f;
            DrawBar(new Rect(frame.x + 8, frame.y + 26, frame.width - 16, 16), hpFill,
                new Color(0.20f, 0.75f, 0.25f), haveSelf ? self.Health + " / " + self.MaxHealth : "—");

            float resFill = haveSelf && self.MaxResource > 0 ? self.Resource / (float)self.MaxResource : 0f;
            DrawBar(new Rect(frame.x + 8, frame.y + 46, frame.width - 16, 14), resFill,
                ResourceColor(), haveSelf ? self.Resource + " / " + self.MaxResource : "—");

            bool inSanctuary = false;
            if (haveSelf && !_client.InInstance)
            {
                float dx = self.Position.X - SimulationConstants.SafeZoneCenterX;
                float dy = self.Position.Y - SimulationConstants.SafeZoneCenterY;
                inSanctuary = (dx * dx) + (dy * dy)
                    <= SimulationConstants.SafeZoneRadius * SimulationConstants.SafeZoneRadius;
            }

            GUI.Label(new Rect(frame.x + 8, frame.y + 64, frame.width - 16, 26),
                "Or " + _client.Gold + "   Banque " + _client.BankGold +
                (_client.InInstance ? "   [INSTANCE]" : "") +
                (inSanctuary ? "   <color=#70d0ff>⛨ Sanctuaire</color>" : "") +
                (_client.PartySize > 0 ? "   Groupe " + _client.PartySize : ""), Rich());
        }

        private Color ResourceColor()
        {
            switch (_classId)
            {
                case 1: return new Color(0.85f, 0.20f, 0.20f);
                case 2: return new Color(0.25f, 0.45f, 0.95f);
                default: return new Color(0.95f, 0.85f, 0.25f);
            }
        }

        private void DrawTargetFrame()
        {
            if (_targetId < 0) { return; }

            EntitySnapshot target;
            if (!_client.TryGetEntity(_targetId, out target)) { return; }

            Rect frame = FrameRect(HudConfig.Frame.TargetFrame);
            GUI.Box(frame, "");
            string name = string.IsNullOrEmpty(target.Name) ? target.Kind.ToString() : target.Name;
            GUI.Label(new Rect(frame.x + 8, frame.y + 4, frame.width - 16, 20),
                "<b>" + name + "</b>  <size=10>niv. " + target.Level +
                (target.Kind == EntityKind.Player ? " · " + target.Faction : "") + "</size>", Rich());
            DrawBar(new Rect(frame.x + 8, frame.y + 28, frame.width - 16, 16),
                target.Health / (float)Mathf.Max(1, target.MaxHealth),
                new Color(0.85f, 0.2f, 0.2f), target.Health + " / " + target.MaxHealth);
        }

        /// <summary>The big XP bar, WoW-style, its own movable frame with a percentage caption.</summary>
        private void DrawXpBar()
        {
            Rect r = FrameRect(HudConfig.Frame.XpBar);
            float fill = _client.XpForNextLevel > 0
                ? Mathf.Clamp01(_client.TotalXp / (float)_client.XpForNextLevel)
                : 1f;
            string caption = _client.XpForNextLevel > 0
                ? "XP  " + _client.TotalXp + " / " + _client.XpForNextLevel + "  (" + Mathf.FloorToInt(fill * 100f) + " %)"
                : "XP  " + _client.TotalXp + "  (niveau max)";
            DrawBar(r, fill, new Color(0.55f, 0.35f, 0.85f), caption);
        }

        private void DrawActionBar()
        {
            Rect bar = FrameRect(HudConfig.Frame.ActionBar);
            const float Pad = 6f;
            float slot = bar.height;
            EntitySnapshot self;
            bool haveSelf = _client.TryGetSelf(out self);
            int myResource = haveSelf ? self.Resource : 0;

            var slots = new List<(string key, byte ability)>
            {
                (KeyLabel(HudConfig.Bind.Attack1), _classId),
                (KeyLabel(HudConfig.Bind.Attack2), Classes[_classIndex].advancedAbility),
                (KeyLabel(HudConfig.Bind.Renew), (byte)5),
                (KeyLabel(HudConfig.Bind.Racial), _racialId),
            };

            for (int i = 0; i < slots.Count; i++)
            {
                (string key, byte abilityId) = slots[i];
                AbilityDefinition def = Data.GetAbility(abilityId);
                Rect r = new Rect(bar.x + (i * (slot + Pad)), bar.y, slot, slot);

                bool locked = def.UnlockLevel > _client.Level;
                bool tooPoor = def.ResourceCost > 0 && myResource < def.ResourceCost;
                float cd = CooldownRemaining(abilityId);
                bool usable = !locked && !tooPoor && cd <= 0f && (_targetId >= 0 || def.Range <= 0f);

                GUI.enabled = usable;
                if (GUI.Button(r, ""))
                {
                    if (abilityId == _racialId) { TryUseRacial(); }
                    else if (def.Range <= 0f) { TryCastSelf(abilityId); }
                    else { TryCastOnTarget(abilityId); }
                }

                GUI.enabled = true;

                GUI.Label(new Rect(r.x + 3, r.y + 2, r.width - 6, 16), "<size=10>" + def.Name + "</size>", Rich());
                GUI.Label(new Rect(r.x + 3, r.y + r.height - 18, 30, 16), "<b>" + key + "</b>", Rich());
                if (def.ResourceCost > 0)
                {
                    GUI.Label(new Rect(r.x + r.width - 24, r.y + r.height - 18, 22, 16),
                        "<size=10>" + def.ResourceCost + "</size>", Rich());
                }

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

        private string KeyLabel(HudConfig.Bind bind)
        {
            KeyCode key = _cfg.Key(bind);
            string s = key.ToString();
            return s.StartsWith("Alpha") ? s.Substring(5) : s;
        }

        // --- Nameplates ---

        private void DrawNameplates()
        {
            Camera cam = Camera.main;
            if (cam == null) { return; }

            Faction myFaction = Faction.Neutral;
            EntitySnapshot self;
            if (_client.TryGetSelf(out self)) { myFaction = self.Faction; }

            foreach (EntityView view in _views.Values)
            {
                if (view == null || view.Kind == EntityKind.Corpse || view.MaxHealth <= 0) { continue; }

                Vector3 screen = cam.WorldToScreenPoint(view.transform.position + (Vector3.up * (view.HeadHeight + 0.5f)));
                if (screen.z < 0f) { continue; }

                float x = screen.x / _cfg.UiScale;
                float y = (Screen.height - screen.y) / _cfg.UiScale;

                if (_cfg.ShowNameplates)
                {
                    bool isSelf = _client.EntityId.HasValue && view.EntityId == _client.EntityId.Value;
                    bool hostile = view.Kind == EntityKind.Monster ||
                                   (view.Kind == EntityKind.Player && view.Faction != myFaction);
                    string colour = isSelf ? "#50e060" : hostile ? "#ff6060" : "#60a0ff";
                    string label = "<color=" + colour + "><size=11>" + view.DisplayName +
                                   "  <size=9>niv." + view.Level + "</size></size></color>";
                    GUI.Label(new Rect(x - 80, y - 18, 160, 16), label, RichCentered());
                }

                if (_cfg.ShowHealthBars)
                {
                    DrawBar(new Rect(x - 22, y, 44, 6), view.Health / (float)view.MaxHealth,
                        new Color(0.2f, 0.8f, 0.25f), "");
                }
            }
        }

        // --- Corpse prompt & loot window ---

        private void DrawCorpsePrompt()
        {
            if (_nearbyCorpseId < 0 || _client.OpenCorpseId >= 0) { return; }

            EntityView view;
            if (!_views.TryGetValue(_nearbyCorpseId, out view) || view == null || Camera.main == null) { return; }

            Vector3 screen = Camera.main.WorldToScreenPoint(view.transform.position + (Vector3.up * (view.HeadHeight + 0.35f)));
            if (screen.z < 0f) { return; }

            Rect r = new Rect((screen.x / _cfg.UiScale) - 100, ((Screen.height - screen.y) / _cfg.UiScale) - 14, 200, 24);
            GUI.Box(r, "[" + KeyLabel(HudConfig.Bind.Interact) + "] ou clic droit : fouiller");
        }

        private void DrawLootWindow()
        {
            if (_client.OpenCorpseId < 0) { return; }

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

        // --- Character sheet ---

        private void DrawCharacterSheet()
        {
            if (!_sheetOpen) { return; }

            EntitySnapshot self;
            bool haveSelf = _client.TryGetSelf(out self);

            int invRows = Mathf.Max(1, _client.InventoryItems.Count);
            float height = 210f + (invRows * 22f);
            Rect win = new Rect(VirtW - 320, 40, 300, Mathf.Min(height, VirtH - 80));
            GUI.Box(win, "<b>Fiche de personnage</b>", RichCenteredBox());

            if (GUI.Button(new Rect(win.x + win.width - 24, win.y + 4, 20, 20), "X"))
            {
                _sheetOpen = false;
                return;
            }

            float y = win.y + 28f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20),
                "<b>" + _name + "</b> — niveau " + _client.Level, Rich());
            y += 20f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20),
                "PV " + (haveSelf ? self.Health + "/" + self.MaxHealth : "—") +
                "    Attaque " + _client.EffectiveAttack + "    Défense " + _client.EffectiveDefense);
            y += 20f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20),
                "XP " + _client.TotalXp + (_client.XpForNextLevel > 0 ? " / " + _client.XpForNextLevel : " (max)"));
            y += 24f;

            // Equipment slots.
            string weapon = _client.EquippedWeaponId != 0 ? Data.GetItem(_client.EquippedWeaponId).Name : "(aucune)";
            string armor = _client.EquippedArmorId != 0 ? Data.GetItem(_client.EquippedArmorId).Name : "(aucune)";
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20), "Arme : " + weapon, Rich());
            y += 20f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20), "Armure : " + armor, Rich());
            y += 24f;

            // Gold + bank.
            GUI.Label(new Rect(win.x + 12, y, 140, 20), "Or " + _client.Gold + " · Banque " + _client.BankGold);
            if (GUI.Button(new Rect(win.x + win.width - 140, y - 2, 62, 22), "Dép. 10"))
            {
                _client.SendBank(BankOp.DepositGold, 0, 10);
            }

            if (GUI.Button(new Rect(win.x + win.width - 74, y - 2, 62, 22), "Ret. 10"))
            {
                _client.SendBank(BankOp.WithdrawGold, 0, 10);
            }

            y += 26f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20), "<b>Sac</b>", Rich());
            y += 20f;

            if (_client.InventoryItems.Count == 0)
            {
                GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20), "<i>(vide)</i>", Rich());
            }
            else
            {
                for (int i = 0; i < _client.InventoryItems.Count && y < win.y + win.height - 26; i++)
                {
                    ItemStack stack = _client.InventoryItems[i];
                    Rect row = new Rect(win.x + 12, y, win.width - 24, 20);
                    GUI.Label(row,
                        "· " + Data.GetItem(stack.ItemId).Name + (stack.Quantity > 1 ? " ×" + stack.Quantity : ""));

                    // Grab to drag: release on an ally to trade it, on the ground to drop it.
                    Event e = Event.current;
                    if (e.type == EventType.MouseDown && e.button == 0 && row.Contains(e.mousePosition) &&
                        _draggingItem == null && !_client.TradeActive)
                    {
                        _draggingItem = stack;
                        e.Use();
                    }

                    y += 22f;
                }

                GUI.Label(new Rect(win.x + 12, win.y + win.height - 22, win.width - 24, 18),
                    "<size=9><i>Glisse un objet vers un allié (échange) ou le sol (poser)</i></size>", Rich());
            }
        }

        // --- Right-click context menu (ally) ---

        private void DrawContextMenu()
        {
            if (_contextEntityId < 0) { return; }

            EntitySnapshot target;
            if (!_client.TryGetEntity(_contextEntityId, out target)) { _contextEntityId = -1; return; }

            Rect win = new Rect(_contextPos.x, _contextPos.y, 180, 202);
            GUI.Box(win, "<b>" + (string.IsNullOrEmpty(target.Name) ? "Joueur" : target.Name) + "</b>", RichCenteredBox());

            float y = win.y + 26f;
            if (GUI.Button(new Rect(win.x + 8, y, win.width - 16, 24), "Inviter au groupe"))
            {
                _client.SendPartyInvite(_contextEntityId);
                _contextEntityId = -1;
                return;
            }

            y += 28f;
            if (GUI.Button(new Rect(win.x + 8, y, win.width - 16, 24), "Inspecter"))
            {
                _client.SendInspect(_contextEntityId);
                _contextEntityId = -1;
                return;
            }

            y += 28f;
            if (GUI.Button(new Rect(win.x + 8, y, win.width - 16, 24), "Échanger"))
            {
                _client.SendTradeRequest(_contextEntityId);
                _contextEntityId = -1;
                return;
            }

            y += 28f;
            if (GUI.Button(new Rect(win.x + 8, y, win.width - 16, 24), "Duel amical"))
            {
                _client.SendDuelRequest(_contextEntityId, toDeath: false);
                _contextEntityId = -1;
                return;
            }

            y += 28f;
            GUI.color = new Color(1f, 0.5f, 0.5f);
            if (GUI.Button(new Rect(win.x + 8, y, win.width - 16, 24), "DUEL À MORT"))
            {
                _client.SendDuelRequest(_contextEntityId, toDeath: true);
                _contextEntityId = -1;
            }

            GUI.color = Color.white;
        }

        // --- Social windows ---

        private void DrawSocialNotices()
        {
            float y = 110f;

            if (_client.PendingDuelFrom != null)
            {
                string kind = _client.PendingDuelToDeath ? "<color=#ff6060>DUEL À MORT</color>" : "duel amical";
                Rect box = new Rect((VirtW / 2f) - 170, y, 340, 54);
                GUI.Box(box, "<b>" + _client.PendingDuelFrom + "</b> te propose un " + kind + " !", RichCenteredBox());
                if (GUI.Button(new Rect(box.x + 30, box.y + 26, 130, 22), "Accepter"))
                {
                    _client.SendDuelRespond(true);
                    _client.ClearPendingDuel();
                }

                if (GUI.Button(new Rect(box.x + 180, box.y + 26, 130, 22), "Refuser"))
                {
                    _client.SendDuelRespond(false);
                    _client.ClearPendingDuel();
                }

                y += 60f;
            }

            if (_client.PendingTradeFrom != null && !_client.TradeActive)
            {
                Rect box = new Rect((VirtW / 2f) - 170, y, 340, 54);
                GUI.Box(box, "<b>" + _client.PendingTradeFrom + "</b> propose un échange.", RichCenteredBox());
                if (GUI.Button(new Rect(box.x + 30, box.y + 26, 130, 22), "Accepter"))
                {
                    _client.SendTradeRespond(true);
                    _client.ClearPendingTrade();
                }

                if (GUI.Button(new Rect(box.x + 180, box.y + 26, 130, 22), "Refuser"))
                {
                    _client.SendTradeRespond(false);
                    _client.ClearPendingTrade();
                }
            }

            if (_client.DuelOpponentId >= 0)
            {
                GUI.Label(new Rect((VirtW / 2f) - 150, 76, 300, 20),
                    _client.DuelToDeath
                        ? "<color=#ff6060><b>⚔ DUEL À MORT EN COURS ⚔</b></color>"
                        : "<color=#f0c040><b>⚔ Duel en cours ⚔</b></color>", RichCentered());
            }
        }

        private void DrawTradeWindow()
        {
            if (!_client.TradeActive) { return; }

            Rect win = new Rect((VirtW / 2f) - 210, (VirtH / 2f) - 160, 420, 320);
            GUI.Box(win, "<b>Échange avec " + _client.TradePartner + "</b>", RichCenteredBox());

            float colW = (win.width - 36f) / 2f;
            float leftX = win.x + 12f;
            float rightX = win.x + 24f + colW;
            float topY = win.y + 28f;

            // My side.
            GUI.Label(new Rect(leftX, topY, colW, 18), "<b>Mon offre</b>" +
                (_client.TradeMyAccepted ? "  <color=#50e060>✔</color>" : ""), Rich());
            GUI.Label(new Rect(leftX, topY + 20, 90, 20), "Or : " + _myOfferGold);
            if (GUI.Button(new Rect(leftX + 92, topY + 19, 30, 20), "+10") && _client.Gold >= _myOfferGold + 10)
            {
                _myOfferGold += 10;
                PushOffer();
            }

            if (GUI.Button(new Rect(leftX + 126, topY + 19, 30, 20), "−10") && _myOfferGold >= 10)
            {
                _myOfferGold -= 10;
                PushOffer();
            }

            float y = topY + 44f;
            for (int i = 0; i < _myOffer.Count; i++)
            {
                ItemStack s = _myOffer[i];
                GUI.Label(new Rect(leftX, y, colW - 34, 20),
                    Data.GetItem(s.ItemId).Name + (s.Quantity > 1 ? " ×" + s.Quantity : ""));
                if (GUI.Button(new Rect(leftX + colW - 30, y, 24, 20), "−"))
                {
                    _myOffer.RemoveAt(i);
                    PushOffer();
                    break;
                }

                y += 22f;
            }

            // Add from bag (items not already offered).
            GUI.Label(new Rect(leftX, y + 4, colW, 18), "<i>Sac (cliquer pour offrir) :</i>", Rich());
            y += 24f;
            IReadOnlyList<ItemStack> bag = _client.InventoryItems;
            for (int i = 0; i < bag.Count && y < win.y + win.height - 60; i++)
            {
                ItemStack s = bag[i];
                bool alreadyOffered = false;
                for (int k = 0; k < _myOffer.Count; k++)
                {
                    if (_myOffer[k].ItemId == s.ItemId) { alreadyOffered = true; }
                }

                if (alreadyOffered) { continue; }

                if (GUI.Button(new Rect(leftX, y, colW - 8, 20),
                        Data.GetItem(s.ItemId).Name + (s.Quantity > 1 ? " ×" + s.Quantity : "")))
                {
                    _myOffer.Add(s);
                    PushOffer();
                }

                y += 22f;
            }

            // Their side.
            GUI.Label(new Rect(rightX, topY, colW, 18), "<b>Son offre</b>" +
                (_client.TradeTheirAccepted ? "  <color=#50e060>✔</color>" : ""), Rich());
            GUI.Label(new Rect(rightX, topY + 20, colW, 20), "Or : " + _client.TradeTheirGold);
            float ty = topY + 44f;
            IReadOnlyList<ItemStack> theirs = _client.TradeTheirItems;
            for (int i = 0; i < theirs.Count; i++)
            {
                ItemStack s = theirs[i];
                GUI.Label(new Rect(rightX, ty, colW, 20),
                    Data.GetItem(s.ItemId).Name + (s.Quantity > 1 ? " ×" + s.Quantity : ""));
                ty += 22f;
            }

            if (!string.IsNullOrEmpty(_client.TradeMessage))
            {
                GUI.Label(new Rect(win.x + 12, win.y + win.height - 56, win.width - 24, 20),
                    "<color=#f0c040>" + _client.TradeMessage + "</color>", Rich());
            }

            bool locked = _client.TradeMyAccepted;
            GUI.enabled = !locked;
            if (GUI.Button(new Rect(win.x + 12, win.y + win.height - 32, 190, 24),
                    locked ? "En attente de l'autre…" : "Accepter l'échange"))
            {
                _client.SendTradeAccept();
            }

            GUI.enabled = true;
            if (GUI.Button(new Rect(win.x + win.width - 202, win.y + win.height - 32, 190, 24), "Annuler"))
            {
                _client.SendTradeCancel();
            }
        }

        private void DrawInspectWindow()
        {
            if (!(_client.LastInspect is InspectResult info)) { return; }

            Rect win = new Rect(40, 120, 260, 190);
            GUI.Box(win, "<b>Inspection — " + info.Name + "</b>", RichCenteredBox());
            if (GUI.Button(new Rect(win.x + win.width - 24, win.y + 4, 20, 20), "X"))
            {
                _client.ClearInspect();
                return;
            }

            string race = "?";
            for (int i = 0; i < Races.Length; i++)
            {
                if (Races[i].id == info.RaceId) { race = Races[i].label; }
            }

            string cls = "?";
            for (int i = 0; i < Classes.Length; i++)
            {
                if (Classes[i].id == info.ClassId) { cls = Classes[i].label; }
            }

            float y = win.y + 28f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20), race + " · " + cls);
            y += 22f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20), "Niveau " + info.Level + "   XP " + info.TotalXp);
            y += 22f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20),
                "PV max " + info.MaxHealth + "   Atk " + info.Attack + "   Déf " + info.Defense);
            y += 22f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20),
                "Arme : " + (info.WeaponId != 0 ? Data.GetItem(info.WeaponId).Name : "(aucune)"));
            y += 22f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20),
                "Armure : " + (info.ArmorId != 0 ? Data.GetItem(info.ArmorId).Name : "(aucune)"));
        }

        /// <summary>The item ghost that follows the cursor during a bag drag.</summary>
        private void DrawDragGhost()
        {
            if (_draggingItem == null) { return; }

            ItemStack s = _draggingItem.Value;
            Vector2 m = new Vector2(Input.mousePosition.x / _cfg.UiScale,
                (Screen.height - Input.mousePosition.y) / _cfg.UiScale);
            GUI.Box(new Rect(m.x - 60, m.y - 30, 140, 24),
                Data.GetItem(s.ItemId).Name + (s.Quantity > 1 ? " ×" + s.Quantity : ""));
            GUI.Label(new Rect(m.x - 100, m.y - 6, 220, 18),
                "<size=10>→ joueur allié : échanger · sol : poser</size>", RichCentered());

            // Release: over an ally → propose a trade with this item pre-offered; elsewhere → drop.
            if (Input.GetMouseButtonUp(0))
            {
                EntitySnapshot self;
                bool haveSelf = _client.TryGetSelf(out self);
                EntitySnapshot? ally = !haveSelf ? null : PickEntityUnderMouse(e =>
                    e.Kind == EntityKind.Player && e.Faction == self.Faction && e.Id != self.Id);

                if (ally != null)
                {
                    _pendingAutoOffer = s;
                    _client.SendTradeRequest(ally.Value.Id);
                }
                else
                {
                    _client.SendDropItem(s.ItemId, s.Quantity);
                }

                _draggingItem = null;
            }
        }

        // --- Escape menu & options ---

        private void DrawEscapeMenu()
        {
            Dim(new Rect(0, 0, VirtW, VirtH), 0.45f);

            const float W = 300f;
            float h = _optionsTab >= 0 ? 380f : 210f;
            Rect win = new Rect((VirtW - W) / 2f, (VirtH - h) / 2f, W, h);
            string title = _optionsTab switch
            {
                0 => "<b>Options — Général</b>",
                1 => "<b>Options — Raccourcis</b>",
                2 => "<b>Options — Disposition</b>",
                _ => "<b>Menu</b>",
            };
            GUI.Box(win, title, RichCenteredBox());

            GUILayout.BeginArea(new Rect(win.x + 16, win.y + 30, W - 32, h - 40));

            if (_optionsTab == 0) { DrawOptionsGeneral(); }
            else if (_optionsTab == 1) { DrawOptionsBindings(); }
            else if (_optionsTab == 2) { DrawOptionsLayout(); }
            else { DrawMenuRoot(); }

            GUILayout.EndArea();
        }

        private void DrawMenuRoot()
        {
            if (GUILayout.Button("Retour au jeu", GUILayout.Height(28))) { _menuOpen = false; }
            if (GUILayout.Button("Options", GUILayout.Height(28))) { _optionsTab = 0; }
            if (GUILayout.Button("Se déconnecter", GUILayout.Height(28))) { Disconnect(); }
            if (GUILayout.Button("Quitter le jeu", GUILayout.Height(28)))
            {
                _cfg.Save();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
        }

        private void DrawOptionsGeneral()
        {
            GUILayout.Label("Profil d'interface :");
            GUILayout.BeginHorizontal();
            for (int p = 1; p <= 3; p++)
            {
                if (GUILayout.Toggle(_cfg.Profile == p, " Profil " + p, "Button") && _cfg.Profile != p)
                {
                    _cfg.Save();
                    _cfg.Load(p);
                }
            }

            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            GUILayout.Label("Échelle de l'interface : " + _cfg.UiScale.ToString("0.00"));
            _cfg.UiScale = Mathf.Round(GUILayout.HorizontalSlider(_cfg.UiScale, 0.8f, 1.6f) * 20f) / 20f;
            GUILayout.Space(6);
            _cfg.ShowNameplates = GUILayout.Toggle(_cfg.ShowNameplates, " Noms et niveaux au-dessus des têtes");
            _cfg.ShowHealthBars = GUILayout.Toggle(_cfg.ShowHealthBars, " Barres de vie au-dessus des entités");
            _cfg.ShowHelp = GUILayout.Toggle(_cfg.ShowHelp, " Ligne d'aide en bas de l'écran");
            GUILayout.Space(10);
            if (GUILayout.Button("Raccourcis…", GUILayout.Height(24))) { _optionsTab = 1; }
            if (GUILayout.Button("Déplacer l'interface…", GUILayout.Height(24))) { _optionsTab = 2; }
            GUILayout.Space(6);
            if (GUILayout.Button("Retour", GUILayout.Height(26))) { _optionsTab = -1; _cfg.Save(); }
        }

        private Vector2 _bindScroll;

        private void DrawOptionsBindings()
        {
            GUILayout.Label(_awaitingBind != null
                ? "<b>Appuie sur une touche pour « " + HudConfig.Labels[_awaitingBind.Value] + " »…</b>"
                : "Clique sur un raccourci pour le changer :", Rich());

            _bindScroll = GUILayout.BeginScrollView(_bindScroll, GUILayout.Height(250));
            foreach (KeyValuePair<HudConfig.Bind, string> pair in HudConfig.Labels)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(pair.Value, GUILayout.Width(170));
                string key = _awaitingBind == pair.Key ? "…" : _cfg.Key(pair.Key).ToString();
                if (GUILayout.Button(key, GUILayout.Width(80)))
                {
                    _awaitingBind = pair.Key;
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.EndScrollView();

            // Capture the next key press for the awaited bind.
            if (_awaitingBind != null && Event.current != null &&
                Event.current.type == EventType.KeyDown && Event.current.keyCode != KeyCode.None &&
                Event.current.keyCode != KeyCode.Escape)
            {
                _cfg.SetKey(_awaitingBind.Value, Event.current.keyCode);
                _awaitingBind = null;
                _cfg.Save();
                Event.current.Use();
            }

            GUILayout.Space(6);
            if (GUILayout.Button("Retour", GUILayout.Height(26))) { _optionsTab = 0; _awaitingBind = null; _cfg.Save(); }
        }

        private void DrawOptionsLayout()
        {
            GUILayout.Label("Active le mode déplacement puis glisse les cadres\n(cadre joueur, cible, barre d'action, barre d'XP,\nmessages) où tu veux. Sauvegardé dans le profil " + _cfg.Profile + ".");
            GUILayout.Space(8);
            if (GUILayout.Button(_layoutEditMode ? "Terminer le déplacement" : "Déplacer l'interface", GUILayout.Height(28)))
            {
                _layoutEditMode = !_layoutEditMode;
                if (_layoutEditMode) { _menuOpen = false; } // let the frames be dragged
                else { _cfg.Save(); }
            }

            if (GUILayout.Button("Réinitialiser la disposition", GUILayout.Height(24)))
            {
                _cfg.ResetLayout();
                _cfg.Save();
            }

            GUILayout.Space(6);
            if (GUILayout.Button("Retour", GUILayout.Height(26))) { _optionsTab = 0; _cfg.Save(); }
        }

        /// <summary>Layout-edit overlay: each movable frame gets a draggable handle.</summary>
        private void DrawLayoutEditor()
        {
            GUI.Label(new Rect(0, 40, VirtW, 24),
                "<b>Mode déplacement — glisse les cadres, Échap ou « Terminer » pour finir</b>", RichCentered());
            if (GUI.Button(new Rect((VirtW / 2f) - 80, 70, 160, 26), "Terminer"))
            {
                _layoutEditMode = false;
                _cfg.Save();
                return;
            }

            foreach (HudConfig.Frame frame in System.Enum.GetValues(typeof(HudConfig.Frame)))
            {
                Rect r = FrameRect(frame);
                GUI.color = new Color(1f, 0.85f, 0.2f, 0.35f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(r, "<b>" + frame + "</b>", RichCentered());

                Event e = Event.current;
                Vector2 mouse = e.mousePosition;
                if (e.type == EventType.MouseDown && r.Contains(mouse))
                {
                    _draggingFrame = frame;
                    _dragStartMouse = mouse;
                    _dragStartOffset = _cfg.Offset(frame);
                    e.Use();
                }
                else if (e.type == EventType.MouseDrag && _draggingFrame == frame)
                {
                    _cfg.SetOffset(frame, _dragStartOffset + (mouse - _dragStartMouse));
                    e.Use();
                }
                else if (e.type == EventType.MouseUp && _draggingFrame == frame)
                {
                    _draggingFrame = null;
                    _cfg.Save();
                    e.Use();
                }
            }
        }

        // --- Shared HUD bits ---

        private void DrawMessages()
        {
            Rect area = FrameRect(HudConfig.Frame.Messages);
            float y = area.y;
            if (_client.PendingInviteFrom != null)
            {
                GUI.Box(new Rect(area.x, y - 30, 340, 26),
                    "Invitation de groupe de " + _client.PendingInviteFrom +
                    " — [" + KeyLabel(HudConfig.Bind.AcceptInvite) + "] accepter");
            }

            if (!string.IsNullOrEmpty(_client.LastInstanceMessage))
            {
                GUI.Label(new Rect(area.x, y, area.width, 22), _client.LastInstanceMessage);
            }

            for (int i = 0; i < _combatLog.Count; i++)
            {
                GUI.Label(new Rect(area.x, y + 22 + (i * 17), area.width, 18),
                    "<size=11>" + _combatLog[i] + "</size>", Rich());
            }
        }

        private void DrawVersionTag()
        {
            GUI.Label(new Rect(VirtW - 150, 8, 142, 18),
                "<size=10>v" + SimulationConstants.GameVersion + " · proto " +
                SimulationConstants.ProtocolVersion + "</size>",
                new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.UpperRight });
        }

        private void DrawHelp()
        {
            string help =
                "WASD bouger · souris orienter · clic G attaquer · clic D contexte · " +
                KeyLabel(HudConfig.Bind.NextTarget) + " cible · " +
                KeyLabel(HudConfig.Bind.Attack1) + "/" + KeyLabel(HudConfig.Bind.Attack2) + " sorts · " +
                KeyLabel(HudConfig.Bind.Renew) + " régén · " + KeyLabel(HudConfig.Bind.Racial) + " racial · " +
                KeyLabel(HudConfig.Bind.Interact) + " fouiller · " + KeyLabel(HudConfig.Bind.CharSheet) + " fiche · " +
                "Échap menu";
            GUI.Label(new Rect(12, VirtH - 24, VirtW - 24, 20), "<size=11>" + help + "</size>", Rich());
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
