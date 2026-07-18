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

        // --- Character customisation (creation screen) ---
        private int _skinTone = 1;
        private int _faceIndex;
        private int _hairStyle;
        private int _hairColor;
        private int _beardStyle;
        private int _beardColor;

        // --- Lobby flow (auth → [register] → your character (or creation) → world) ---
        private enum LobbyScreen { Auth, Register, Browser, Connecting, Character, Creation }

        private sealed class ServerEntry
        {
            public string Host;
            public int Port;
            public ServerProbe Probe;
            public bool HasInfo;
            public ServerInfo Info;
            public bool Failed;

            public string Address { get { return Host + ":" + Port; } }
        }

        private readonly LobbyStage _lobby = new LobbyStage();
        private readonly WorldDecor _decor = new WorldDecor();
        private readonly SheetPreview _sheetPreview = new SheetPreview();
        private LobbyScreen _lobbyScreen = LobbyScreen.Auth;
        private string _secret2 = "";
        private bool _wasRegistering;
        private bool _serverChosen; // the player picked this realm in the browser (creation allowed)
        private Vector3 _rightDownPos; // to tell a right-TAP (context click) from a camera drag
        private float _gcdReadyTime;   // local mirror of the 1.5s global cooldown (display + pre-check)
        private string _uiError = "";
        private float _uiErrorUntil;
        private int _ringTargetId = -1; // entity currently wearing the selection ring

        private struct FloatText { public Vector3 World; public string Text; public Color Color; public float Born; }
        private readonly List<FloatText> _floatTexts = new List<FloatText>();
        private Vector3 _leftDownPos;  // same for the left button (tap = select, drag = orbit)
        private bool _jumpQueued;      // Space pressed since the last input packet

        // --- Bank (a physical chest in the sanctuary) ---
        private int _nearbyBankId = -1;
        private bool _bankOpen;

        // --- Chat (players' words only) ---
        private readonly List<string> _chatLog = new List<string>();
        private string _chatInput = "";
        private bool _chatInputActive;
        private readonly List<ServerEntry> _servers = new List<ServerEntry>();
        private bool _enterSent;
        private bool _lastServerSaved;
        private int _previewKey = -1;

        private static readonly string[] SkinLabels = { "Clair", "Moyen", "Halé", "Sombre" };
        private static readonly string[] FaceLabels = { "Classique", "Nez fort", "Fin" };
        private static readonly string[] HairStyleLabels = { "Courte", "Longue", "Iroquoise", "Chauve" };
        private static readonly string[] HairColorLabels = { "Brun", "Noir", "Blond", "Roux", "Blanc", "Bleu nuit" };
        private static readonly string[] BeardStyleLabels = { "Aucune", "Courte", "Longue", "Tressée" };

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
        private float _lastFacing;
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
            bool inWorld = _connected && _client != null && _client.EntityId.HasValue;

            // Lobby: 3D campsite backdrop + character preview + server probes.
            if (!inWorld)
            {
                _lobby.EnsureBuilt();
                UpdateLobbyPreview();
                _lobby.Tick(Time.deltaTime);
                PumpServerProbes();
                if (_decor.Active) { _decor.Teardown(); }
            }
            else
            {
                if (_lobby.Active) { _lobby.Teardown(); }
                _decor.EnsureBuilt(); // zone scenery: sanctuary, path, goblin camp, wolf field
            }

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

            // Lobby phases: drive the auto-flow, no world logic yet.
            if (!_client.EntityId.HasValue)
            {
                if (_client.LoggedIn && _lobbyScreen == LobbyScreen.Connecting)
                {
                    if (_client.HasCharacter)
                    {
                        _lobbyScreen = LobbyScreen.Character; // your character (3D) + JOUER
                    }
                    else if (_serverChosen)
                    {
                        _lobbyScreen = LobbyScreen.Creation; // realm picked in the browser → create here
                    }
                    else
                    {
                        // No character anywhere obvious: FIRST pick the realm, THEN create.
                        Disconnect();
                        OpenServerBrowser();
                        _error = "Choisis un serveur pour créer ton personnage.";
                    }
                }
                else if (_client.LoginError.Length > 0 && _lobbyScreen == LobbyScreen.Connecting)
                {
                    _error = _client.LoginError; // refused → back to where the attempt started
                    bool wasRegistering = _wasRegistering;
                    Disconnect();
                    _lobbyScreen = wasRegistering ? LobbyScreen.Register : LobbyScreen.Auth;
                }

                return;
            }

            // Just entered the world: remember this server as the account's last-played one.
            if (!_lastServerSaved)
            {
                PlayerPrefs.SetString("aeth.last." + _account.Trim().ToLowerInvariant(), _host + ":" + _port);
                PlayerPrefs.Save();
                _lastServerSaved = true;
            }

            // Chat input: Enter opens the box; Enter again sends; Escape cancels.
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (_chatInputActive)
                {
                    string text = _chatInput.Trim();
                    if (text.Length > 0) { _client.SendChat(text); }
                    _chatInput = "";
                    _chatInputActive = false;
                }
                else if (!_menuOpen && _awaitingBind == null)
                {
                    _chatInputActive = true;
                }
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_chatInputActive) { _chatInputActive = false; _chatInput = ""; }
                else if (_awaitingBind != null) { _awaitingBind = null; }
                else if (_contextEntityId >= 0) { _contextEntityId = -1; }
                else if (_bankOpen) { _bankOpen = false; }
                else if (_client.OpenCorpseId >= 0) { _client.CloseCorpse(); }
                else if (_sheetOpen) { _sheetOpen = false; }
                else if (_targetId >= 0) { SetAttackIntent(-1); } // Escape drops the target first
                else { _menuOpen = !_menuOpen; _optionsTab = -1; _layoutEditMode = false; }
            }

            SyncViews();
            PlayCombatAnimations();
            UpdateTargetRing();
            FindNearbyCorpse();
            FindNearbyBank();
            PumpChat();
            TrackSocialState();

            if (!_menuOpen && _awaitingBind == null && !_chatInputActive)
            {
                // Jump: play locally right away (snappy), and relay through the next input packet.
                if (Input.GetKeyDown(KeyCode.Space))
                {
                    _jumpQueued = true;
                    EntityView selfView;
                    if (_client.EntityId.HasValue &&
                        _views.TryGetValue(_client.EntityId.Value, out selfView) && selfView != null)
                    {
                        selfView.TriggerJump();
                    }
                }

                HandleKeys();
                HandleMouse();
                SendMovement();
            }

            // Live 3D portrait while the character sheet is open.
            if (_sheetOpen && _client.TryGetSelf(out EntitySnapshot sheetSelf))
            {
                _sheetPreview.EnsureFor(sheetSelf);
                _sheetPreview.Tick(Time.deltaTime);
            }

            FollowSelf();
        }

        // ------------------------------------------------------------- Session

        /// <summary>The last server this account played on (the auto-connect target), or the default.</summary>
        private string LastServerFor(string account)
        {
            string saved = PlayerPrefs.GetString("aeth.last." + account.ToLowerInvariant(), "");
            return string.IsNullOrEmpty(saved) ? _host + ":" + _port : saved;
        }

        private static bool SplitAddress(string address, out string host, out int port)
        {
            host = address;
            port = SimulationConstants.DefaultPort;
            int colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.Substring(colon + 1), out int p) && p > 0)
            {
                host = address.Substring(0, colon);
                port = p;
            }

            return host.Trim().Length > 0;
        }

        /// <summary>Authenticate: sign in to the given server, or the account's last-played one.</summary>
        private void Connect(string address, bool createAccount, bool fromBrowser = false)
        {
            string account = _account.Trim();
            if (account.Length == 0)
            {
                _error = "Entre un identifiant de compte.";
                return;
            }

            if (string.IsNullOrEmpty(_secret))
            {
                _error = "Entre ton secret de compte.";
                return;
            }

            if (!SplitAddress(address, out string host, out int port))
            {
                _error = "Adresse de serveur invalide.";
                return;
            }

            try
            {
                _transport = new UdpClientTransport();
                _client = new GameClient(_transport);
                _client.Connect(host, port, account, _secret, createAccount);
                _host = host;
                _port = port.ToString();
                _connected = true;
                _error = "";
                _enterSent = false;
                _lastServerSaved = false;
                _wasRegistering = createAccount;
                _serverChosen = fromBrowser;
                _lobbyScreen = LobbyScreen.Connecting;
            }
            catch (System.Exception ex)
            {
                _error = ex.Message;
                _transport?.Dispose();
                _transport = null;
                _client = null;
            }
        }

        // --- Lobby helpers: 3D preview + server list/probes ---

        /// <summary>Keep the 3D dais preview in sync: your character, or the creation pickers.</summary>
        private void UpdateLobbyPreview()
        {
            bool connected = _connected && _client != null;

            // Character screen: show THE character (as saved on the server), slowly turning.
            if (connected && _lobbyScreen == LobbyScreen.Character && _client.LoggedIn && _client.HasCharacter)
            {
                int charKey = 0x40000000 | _client.CharacterRaceId | (_client.CharacterClassId << 4) |
                              ((byte)_client.CharacterGender << 8);
                if (charKey != _previewKey)
                {
                    _previewKey = charKey;
                    byte charRace = _client.CharacterRaceId;
                    Faction charFaction = charRace == 2 || charRace == 3 ? Faction.Horde : Faction.Alliance;
                    _lobby.ShowPreview(charRace, _client.CharacterClassId, _client.CharacterGender,
                        _client.CharacterAppearance, charFaction);
                }

                return;
            }

            bool creating = connected && _lobbyScreen == LobbyScreen.Creation;
            if (!creating)
            {
                if (_previewKey != -1) { _lobby.ClearPreview(); _previewKey = -1; }
                return;
            }

            bool beardAllowed = _genderIndex == 0 || Races[_raceIndex].id == 4;
            int beard = beardAllowed ? _beardStyle : 0;
            int key = _raceIndex | (_classIndex << 3) | (_genderIndex << 6) | (_skinTone << 7) |
                      (_faceIndex << 10) | (_hairStyle << 12) | (_hairColor << 15) |
                      (beard << 18) | (_beardColor << 21);
            if (key == _previewKey) { return; }

            _previewKey = key;
            byte raceId = Races[_raceIndex].id;
            Faction faction = raceId == 2 || raceId == 3 ? Faction.Horde : Faction.Alliance;
            var look = new Appearance((byte)_skinTone, (byte)_faceIndex, (byte)_hairStyle,
                (byte)_hairColor, (byte)beard, (byte)_beardColor);
            _lobby.ShowPreview(raceId, Classes[_classIndex].id,
                _genderIndex == 1 ? Gender.Female : Gender.Male, look, faction);
        }

        /// <summary>
        /// The OFFICIAL server list — predefined, players cannot add their own entries.
        /// Extend this array when new realms open.
        /// </summary>
        private static readonly string[] PredefinedServers =
        {
            "127.0.0.1:27015",
            "127.0.0.1:27016",
        };

        private void LoadServerList()
        {
            if (_servers.Count > 0) { return; }

            foreach (string address in PredefinedServers)
            {
                if (SplitAddress(address, out string host, out int port))
                {
                    _servers.Add(new ServerEntry { Host = host, Port = port });
                }
            }
        }

        /// <summary>(Re)query every listed server: name, population, your character there.</summary>
        private void RefreshServers()
        {
            foreach (ServerEntry e in _servers)
            {
                e.Probe?.Dispose();
                e.Probe = null;
                e.HasInfo = false;
                e.Failed = false;
                try
                {
                    e.Probe = new ServerProbe(new UdpClientTransport(), e.Host, e.Port, _account.Trim());
                }
                catch (System.Exception)
                {
                    e.Failed = true;
                }
            }
        }

        private void PumpServerProbes()
        {
            foreach (ServerEntry e in _servers)
            {
                if (e.Probe == null) { continue; }

                e.Probe.Pump();
                if (e.Probe.Completed)
                {
                    e.Info = e.Probe.Info;
                    e.HasInfo = true;
                    e.Probe.Dispose();
                    e.Probe = null;
                }
                else if (e.Probe.TimedOut)
                {
                    e.Failed = true;
                    e.Probe.Dispose();
                    e.Probe = null;
                }
            }
        }

        private void OpenServerBrowser()
        {
            LoadServerList();
            RefreshServers();
            _lobbyScreen = LobbyScreen.Browser;
        }

        /// <summary>Leave the world but stay signed in: reconnect straight to the character screen.</summary>
        private void DisconnectToCharacter()
        {
            string address = _host + ":" + _port;
            Disconnect();
            Connect(address, createAccount: false); // logs back in → lands on the character screen
        }

        /// <summary>Map the existing character onto the HUD state, then enter the world.</summary>
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

            bool beardAllowed = _genderIndex == 0 || Races[_raceIndex].id == 4; // males — and every Dwarf
            var look = new Appearance(
                (byte)_skinTone, (byte)_faceIndex, (byte)_hairStyle, (byte)_hairColor,
                (byte)(beardAllowed ? _beardStyle : 0), (byte)_beardColor);

            _client.SendCreateCharacter(_name, Races[_raceIndex].id, _classId, gender, look);
        }

        private void Disconnect()
        {
            if (_client != null) { _client.SendDisconnect(); }
            _transport?.Dispose();
            _transport = null;
            _client = null;
            _connected = false;
            _enterSent = false;
            _serverChosen = false;
            _lobbyScreen = LobbyScreen.Auth;
            _previewKey = -1;
            _lobby.ClearPreview();
            _targetId = -1;
            _menuOpen = false;
            _sheetOpen = false;
            _contextEntityId = -1;
            _cooldownReadyAt.Clear();

            foreach (EntityView view in _views.Values)
            {
                if (view != null) { Destroy(view.gameObject); }
            }

            _views.Clear();
            _chatLog.Clear();
            _chatInput = "";
            _chatInputActive = false;
            _bankOpen = false;
            _nearbyBankId = -1;
            _sheetPreview.Teardown();
            _wasRegistering = false;
        }

        private void OnDestroy()
        {
            _cfg.Save();
            Disconnect();
            foreach (ServerEntry e in _servers) { e.Probe?.Dispose(); }
            _lobby.Teardown();
            _decor.Teardown();
            _sheetPreview.Teardown();
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
                if (_targetId == toRemove[i]) { _targetId = -1; }
                if (_contextEntityId == toRemove[i]) { _contextEntityId = -1; }
                if (_client.OpenCorpseId == toRemove[i]) { _client.CloseCorpse(); }
            }
        }

        /// <summary>Combat events → attack swings, hit flashes, and FLOATING damage numbers.</summary>
        private void PlayCombatAnimations()
        {
            int selfId = _client.EntityId ?? -1;
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

                    // WoW-style floating number: white for your hits, red when YOU take it.
                    Color c = evt.TargetId == selfId
                        ? new Color(1f, 0.35f, 0.3f)
                        : evt.AttackerId == selfId ? Color.white : new Color(0.85f, 0.85f, 0.6f);
                    _floatTexts.Add(new FloatText
                    {
                        World = target.transform.position + (Vector3.up * (target.HeadHeight + 0.4f)),
                        Text = evt.Damage.ToString(),
                        Color = c,
                        Born = Time.time,
                    });
                }
            }

            if (_floatTexts.Count > 60) { _floatTexts.RemoveRange(0, _floatTexts.Count - 60); }
        }

        /// <summary>Keep the SELECTION RING under the current target, WoW-style.</summary>
        private void UpdateTargetRing()
        {
            if (_ringTargetId == _targetId) { return; }

            EntityView oldView;
            if (_ringTargetId >= 0 && _views.TryGetValue(_ringTargetId, out oldView) && oldView != null)
            {
                oldView.SetSelected(false);
            }

            EntityView newView;
            if (_targetId >= 0 && _views.TryGetValue(_targetId, out newView) && newView != null)
            {
                newView.SetSelected(true);
            }

            _ringTargetId = _targetId;
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

        /// <summary>The bank chest you stand next to, if any (the sanctuary's Npc chest).</summary>
        private void FindNearbyBank()
        {
            _nearbyBankId = -1;
            EntitySnapshot self;
            if (!_client.TryGetSelf(out self)) { return; }

            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                if (e.Kind != EntityKind.Npc) { continue; }
                if (Vec2.DistanceSquared(self.Position, e.Position) <=
                    SimulationConstants.BankInteractRange * SimulationConstants.BankInteractRange)
                {
                    _nearbyBankId = e.Id;
                    break;
                }
            }

            if (_bankOpen && _nearbyBankId < 0)
            {
                _bankOpen = false; // walked away: the chest closes
            }
        }

        /// <summary>Pull received player chat into the local log. Chat carries ONLY players' words.</summary>
        private void PumpChat()
        {
            IReadOnlyList<ChatMessage> feed = _client.DrainChatFeed();
            for (int i = 0; i < feed.Count; i++)
            {
                _chatLog.Add("<b>" + feed[i].From + " :</b> " + feed[i].Text);
                if (_chatLog.Count > 40) { _chatLog.RemoveAt(0); }
            }
        }

        /// <summary>Track duel/trade transitions and apply the drag-drop auto-offer when a trade opens.</summary>
        private void TrackSocialState()
        {
            if (_client.DuelMessage != _lastDuelMessage && !string.IsNullOrEmpty(_client.DuelMessage))
            {
                _lastDuelMessage = _client.DuelMessage; // shown by the duel notice UI, not the chat
            }

            if (_client.TradeMessage != _lastTradeMessage && !string.IsNullOrEmpty(_client.TradeMessage))
            {
                _lastTradeMessage = _client.TradeMessage; // shown by the trade window, not the chat
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

                // Running with no drag: the camera drifts back behind the character, WoW-style.
                bool moving = Input.GetAxisRaw("Horizontal") != 0f || Input.GetAxisRaw("Vertical") != 0f;
                if (moving && !_menuOpen && !_chatInputActive)
                {
                    _cameraRig.RecenterBehind(_lastFacing, Time.deltaTime);
                }
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
            // Left button doubles as free camera orbit: a quick TAP selects a target, a DRAG
            // orbits (handled by the rig) without selecting anything.
            if (Input.GetMouseButtonDown(0))
            {
                _leftDownPos = Input.mousePosition;
            }

            if (Input.GetMouseButtonUp(0) &&
                (Input.mousePosition - _leftDownPos).sqrMagnitude < 64f) // < 8 px: a tap
            {
                _contextEntityId = -1; // any left tap closes a context menu
                HandleLeftClick();
            }

            // Right button doubles as WoW mouselook: a quick TAP is a context click,
            // press-and-DRAG turns the camera (handled by the rig) without opening menus.
            if (Input.GetMouseButtonDown(1))
            {
                _rightDownPos = Input.mousePosition;
            }

            if (Input.GetMouseButtonUp(1) && _draggingItem == null &&
                (Input.mousePosition - _rightDownPos).sqrMagnitude < 64f) // < 8 px: a tap
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

            SetAttackIntent(picked?.Id ?? -1); // tap on the ground clears the target, WoW-style
        }

        /// <summary>
        /// Select a target and declare the attack intent: ONE message, then the SERVER swings the
        /// class's basic attack (or recasts its incantation) until the target drops. -1 clears.
        /// </summary>
        private void SetAttackIntent(int targetId)
        {
            _targetId = targetId;
            _client.SendAttackTarget(targetId < 0 ? 0 : targetId);
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
                    SetAttackIntent(target.Id);
                    break;

                case EntityKind.Player when target.Faction != self.Faction || target.Id == _client.DuelOpponentId:
                    SetAttackIntent(target.Id);
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
            else if (_bankOpen) { _bankOpen = false; }
            else if (_nearbyBankId >= 0) { _bankOpen = true; }
        }

        private void TryCastOnTarget(byte abilityId)
        {
            if (_targetId < 0) { ShowError("Aucune cible."); return; }
            if (Time.time < _gcdReadyTime) { return; }
            if (IsOnCooldown(abilityId)) { ShowError("Pas encore prêt."); return; }

            AbilityDefinition def = Data.GetAbility(abilityId);
            EntitySnapshot self;
            EntitySnapshot target;
            if (_client.TryGetSelf(out self) && _client.TryGetEntity(_targetId, out target))
            {
                if (def.ResourceCost > 0 && self.Resource < def.ResourceCost)
                {
                    ShowError(ResourceErrorFor(_classId));
                    return;
                }

                if (Vec2.DistanceSquared(self.Position, target.Position) > def.Range * def.Range)
                {
                    ShowError("Trop loin.");
                    return;
                }
            }

            _client.SendUseAbility(abilityId, _targetId);
            StartLocalCooldown(abilityId);
            _gcdReadyTime = Time.time + (SimulationConstants.GlobalCooldownTicks * SimulationConstants.TickDelta);
        }

        private void TryCastSelf(byte abilityId)
        {
            if (!_client.EntityId.HasValue || Time.time < _gcdReadyTime) { return; }
            if (IsOnCooldown(abilityId)) { ShowError("Pas encore prêt."); return; }
            _client.SendUseAbility(abilityId, _client.EntityId.Value);
            StartLocalCooldown(abilityId);
            _gcdReadyTime = Time.time + (SimulationConstants.GlobalCooldownTicks * SimulationConstants.TickDelta);
        }

        private void TryUseRacial()
        {
            if (IsOnCooldown(_racialId)) { ShowError("Pas encore prêt."); return; }
            _client.SendUseRacial();
            StartLocalCooldown(_racialId);
        }

        private string ResourceErrorFor(byte classId)
        {
            switch (classId)
            {
                case 1: return "Pas assez de rage.";
                case 2: return "Pas assez de mana.";
                default: return "Pas assez d'énergie.";
            }
        }

        /// <summary>WoW-style red error line, centre-screen, fading after two seconds.</summary>
        private void ShowError(string message)
        {
            _uiError = message;
            _uiErrorUntil = Time.time + 2f;
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

            if (hostiles.Count == 0) { SetAttackIntent(-1); return; }

            hostiles.Sort((a, b) =>
                Vec2.DistanceSquared(self.Position, a.Position)
                    .CompareTo(Vec2.DistanceSquared(self.Position, b.Position)));

            int currentIndex = hostiles.FindIndex(e => e.Id == _targetId);
            SetAttackIntent(hostiles[(currentIndex + 1) % hostiles.Count].Id);
        }

        private void SendMovement()
        {
            _inputTimer += Time.deltaTime;
            if (_inputTimer < SimulationConstants.TickDelta) { return; }
            _inputTimer = 0f;

            // WoW grammar:
            //  - RIGHT drag = mouselook: the character turns with the camera.
            //  - LEFT drag  = free camera orbit: the character keeps its own direction.
            //  - Movement is relative to the CHARACTER's facing: W forward, S backpedal, A/D strafe.
            if (_cameraRig != null && Input.GetMouseButton(1))
            {
                _lastFacing = _cameraRig.FacingRadians;
            }

            float h = Input.GetAxisRaw("Horizontal");
            float v = Input.GetAxisRaw("Vertical");

            Vec2 dir = Vec2.Zero;
            if (h != 0f || v != 0f)
            {
                var forward = new Vec2(Mathf.Cos(_lastFacing), Mathf.Sin(_lastFacing));
                var strafe = new Vec2(Mathf.Sin(_lastFacing), -Mathf.Cos(_lastFacing));
                dir = ((forward * v) + (strafe * h)).Normalized();
            }

            _client.SendInput(dir, _lastFacing, _jumpQueued);
            _jumpQueued = false;
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
            DrawBankPrompt();
            DrawPlayerFrame();
            DrawTargetFrame();
            DrawXpBar();
            DrawActionBar();
            DrawMessages();
            DrawFloatingTexts();
            DrawUiError();
            DrawCastBar();
            DrawChat();
            DrawLootWindow();
            DrawBankWindow();
            DrawCharacterSheet();
            DrawContextMenu();
            DrawSocialNotices();
            DrawTradeWindow();
            DrawInspectWindow();
            DrawDragGhost();
            DrawVersionTag();
            if (_layoutEditMode) { DrawLayoutEditor(); }
            if (_menuOpen) { DrawEscapeMenu(); }
        }

        // --- Auth flow screens: login → your character (or creation) → world ---

        private void DrawAuthScreens()
        {
            if (_connected && _client != null)
            {
                if (_lobbyScreen == LobbyScreen.Character && _client.LoggedIn && _client.HasCharacter)
                {
                    DrawCharacterScreen();
                }
                else if (_lobbyScreen == LobbyScreen.Creation && _client.LoggedIn && !_client.HasCharacter)
                {
                    DrawCreationScreen();
                }
                else
                {
                    DrawWaitScreen(_client.LoggedIn ? "Entrée dans le monde…" : "Connexion au serveur…");
                }
            }
            else if (_lobbyScreen == LobbyScreen.Browser)
            {
                DrawServerBrowser();
            }
            else if (_lobbyScreen == LobbyScreen.Register)
            {
                DrawRegisterScreen();
            }
            else
            {
                DrawLoginScreen();
            }
        }

        /// <summary>Auth panels sit on the LEFT so the 3D campsite (and preview) shows on the right.</summary>
        private void DrawTitledBox(out Rect box, float height, string subtitle, float width = 360f)
        {
            box = new Rect(VirtW * 0.06f, VirtH * 0.10f, width, height);
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

        /// <summary>Screen 1 — sign-in only: login + password. Nothing else.</summary>
        private void DrawLoginScreen()
        {
            DrawTitledBox(out Rect box, 235, "Connexion");
            GUILayout.BeginArea(new Rect(box.x + 15, box.y + 48, box.width - 30, box.height - 60));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Identifiant", GUILayout.Width(90));
            _account = GUILayout.TextField(_account);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Mot de passe", GUILayout.Width(90));
            _secret = GUILayout.PasswordField(_secret, '*');
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            if (GUILayout.Button("SE CONNECTER", GUILayout.Height(34)))
            {
                Connect(LastServerFor(_account.Trim()), createAccount: false);
            }

            GUILayout.Space(4);
            if (GUILayout.Button("Créer un compte", GUILayout.Height(24)))
            {
                _secret2 = "";
                _error = "";
                _lobbyScreen = LobbyScreen.Register;
            }

            DrawErrors();
            GUILayout.EndArea();
        }

        /// <summary>Screen 1b — its own registration modal: login + password + confirmation.</summary>
        private void DrawRegisterScreen()
        {
            DrawTitledBox(out Rect box, 265, "Créer un compte");
            GUILayout.BeginArea(new Rect(box.x + 15, box.y + 48, box.width - 30, box.height - 60));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Identifiant", GUILayout.Width(110));
            _account = GUILayout.TextField(_account);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Mot de passe", GUILayout.Width(110));
            _secret = GUILayout.PasswordField(_secret, '*');
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Confirmation", GUILayout.Width(110));
            _secret2 = GUILayout.PasswordField(_secret2, '*');
            GUILayout.EndHorizontal();

            GUILayout.Space(10);
            if (GUILayout.Button("CRÉER LE COMPTE", GUILayout.Height(34)))
            {
                if (_secret != _secret2)
                {
                    _error = "Les deux mots de passe ne correspondent pas.";
                }
                else
                {
                    Connect(LastServerFor(_account.Trim()), createAccount: true);
                }
            }

            GUILayout.Space(4);
            if (GUILayout.Button("Retour", GUILayout.Height(24)))
            {
                _error = "";
                _lobbyScreen = LobbyScreen.Auth;
            }

            DrawErrors();
            GUILayout.EndArea();
        }

        /// <summary>Screen 2 — YOUR character in 3D, and a big JOUER button.</summary>
        private void DrawCharacterScreen()
        {
            DrawTitledBox(out Rect box, 300, "Ton personnage");
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

            GUILayout.Space(14);
            if (GUILayout.Button("JOUER", GUILayout.Height(40))) { EnterExisting(); }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Changer de serveur", GUILayout.Height(24)))
            {
                Disconnect();
                OpenServerBrowser();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Déconnexion", GUILayout.Height(26))) { Disconnect(); }
            if (GUILayout.Button("Quitter le jeu", GUILayout.Height(26)))
            {
                _cfg.Save();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }

            GUILayout.EndHorizontal();
            DrawErrors();
            GUILayout.EndArea();
        }

        /// <summary>The server browser: NAMED servers, their population, and your character there.</summary>
        private void DrawServerBrowser()
        {
            float height = 150f + (_servers.Count * 54f) + 70f;
            DrawTitledBox(out Rect box, Mathf.Min(height, VirtH * 0.8f), "Liste des serveurs", 560f);
            GUILayout.BeginArea(new Rect(box.x + 15, box.y + 48, box.width - 30, box.height - 60));

            GUILayout.Space(6);

            foreach (ServerEntry e in _servers)
            {
                GUILayout.BeginHorizontal(GUI.skin.box);

                // Column 1: name + address.
                GUILayout.BeginVertical(GUILayout.Width(190));
                string name = e.HasInfo ? e.Info.Name : e.Failed ? "(injoignable)" : "(interrogation…)";
                GUILayout.Label("<b>" + name + "</b>", Rich());
                GUILayout.Label("<size=9>" + e.Address + "</size>", Rich());
                GUILayout.EndVertical();

                // Column 2: population badge.
                GUILayout.BeginVertical(GUILayout.Width(130));
                if (e.HasInfo)
                {
                    float fill = e.Info.Capacity > 0 ? e.Info.Online / (float)e.Info.Capacity : 0f;
                    string badge = fill >= 1f ? "<color=#ff5050>Complet</color>"
                        : fill >= 0.8f ? "<color=#ffa040>Presque complet</color>"
                        : fill >= 0.3f ? "<color=#ffe060>Moyenne</color>"
                        : "<color=#60e070>Faible</color>";
                    GUILayout.Label(badge, Rich());
                    GUILayout.Label("<size=9>" + e.Info.Online + " / " + e.Info.Capacity + " joueurs</size>", Rich());
                }
                else
                {
                    GUILayout.Label("<size=9>—</size>", Rich());
                }

                GUILayout.EndVertical();

                // Column 3: your character on this server.
                GUILayout.BeginVertical(GUILayout.Width(130));
                if (e.HasInfo && e.Info.HasCharacter)
                {
                    GUILayout.Label("<size=10>" + e.Info.CharacterName + " (niv. " + e.Info.CharacterLevel + ")</size>", Rich());
                }
                else if (e.HasInfo)
                {
                    bool blocked = !e.Info.AcceptsNewCharacters;
                    GUILayout.Label(blocked
                        ? "<size=10><color=#ff7070>Création bloquée (complet)</color></size>"
                        : "<size=10>Aucun personnage</size>", Rich());
                }

                GUILayout.EndVertical();

                // Column 4: join. A full server still lets an EXISTING character play,
                // but refuses newcomers (no character there + no room to create one).
                bool canJoin = e.HasInfo && (e.Info.HasCharacter || e.Info.AcceptsNewCharacters);
                GUI.enabled = canJoin;
                if (GUILayout.Button("REJOINDRE", GUILayout.Width(90), GUILayout.Height(36)))
                {
                    // First visit on this realm: the account is created there automatically.
                    Connect(e.Address, createAccount: !e.Info.HasAccount, fromBrowser: true);
                }

                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Actualiser", GUILayout.Width(90))) { RefreshServers(); }
            if (GUILayout.Button("Retour", GUILayout.Width(80))) { _lobbyScreen = LobbyScreen.Auth; }
            GUILayout.EndHorizontal();

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

        /// <summary>One "◀ valeur ▶" row of the customisation panel, with an optional colour swatch.</summary>
        private int DrawOptionPicker(string label, int index, string[] options, Color? swatch)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(92));
            if (GUILayout.Button("<", GUILayout.Width(22))) { index = (index + options.Length - 1) % options.Length; }
            GUILayout.Label(options[index], GUILayout.ExpandWidth(true));
            if (swatch.HasValue)
            {
                Rect r = GUILayoutUtility.GetRect(20f, 18f, GUILayout.Width(20));
                Color prev = GUI.color;
                GUI.color = swatch.Value;
                GUI.DrawTexture(new Rect(r.x, r.y + 2f, 18f, 14f), Texture2D.whiteTexture);
                GUI.color = prev;
            }

            if (GUILayout.Button(">", GUILayout.Width(22))) { index = (index + 1) % options.Length; }
            GUILayout.EndHorizontal();
            return index;
        }

        private void DrawCreationScreen()
        {
            DrawTitledBox(out Rect box, 500, "Crée ton personnage");
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
            int newGender = GUILayout.SelectionGrid(_genderIndex, new[] { "Male", "Female" }, 2);
            if (newGender != _genderIndex)
            {
                _genderIndex = newGender;
                _hairStyle = _genderIndex == 1 ? 1 : 0; // suggestion : cheveux longs par défaut au féminin
            }

            // --- Customisation ---
            GUILayout.Space(8);
            GUILayout.Label("<size=11><b>Apparence</b></size>", Rich());
            byte raceId = Races[_raceIndex].id;
            _skinTone = DrawOptionPicker("Teint", _skinTone, SkinLabels,
                CharacterModelBuilder.SkinColor(raceId, (byte)_skinTone));
            _faceIndex = DrawOptionPicker("Visage", _faceIndex, FaceLabels, null);
            _hairStyle = DrawOptionPicker("Coiffure", _hairStyle, HairStyleLabels, null);
            if (_hairStyle != 3) // pas de couleur pour un crâne chauve
            {
                _hairColor = DrawOptionPicker("Cheveux", _hairColor, HairColorLabels,
                    CharacterModelBuilder.HairColors[_hairColor]);
            }

            bool beardAllowed = _genderIndex == 0 || raceId == 4; // hommes — et tous les Nains
            if (beardAllowed)
            {
                _beardStyle = DrawOptionPicker("Barbe", _beardStyle, BeardStyleLabels, null);
                if (_beardStyle != 0)
                {
                    _beardColor = DrawOptionPicker("Couleur barbe", _beardColor, HairColorLabels,
                        CharacterModelBuilder.HairColors[_beardColor]);
                }
            }

            GUILayout.Space(10);
            if (GUILayout.Button("CRÉER ET ENTRER EN JEU", GUILayout.Height(38))) { CreateAndEnter(); }

            GUILayout.Space(4);
            GUILayout.Label("<size=10><i>Tu apparaîtras dans le sanctuaire — une zone sans PvP ni monstres.</i></size>", Rich());
            if (GUILayout.Button("Liste des serveurs", GUILayout.Height(22)))
            {
                Disconnect();
                OpenServerBrowser();
            }

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
                FormatMoney(_client.Gold) +
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
                if (abilityId != _racialId)
                {
                    cd = Mathf.Max(cd, _gcdReadyTime - Time.time); // the GCD sweeps the whole bar
                }
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
                if (view == null || view.Kind == EntityKind.Corpse ||
                    view.Kind == EntityKind.MonsterCorpse || view.MaxHealth <= 0)
                {
                    continue;
                }

                Vector3 screen = cam.WorldToScreenPoint(view.transform.position + (Vector3.up * (view.HeadHeight + 0.5f)));
                if (screen.z < 0f) { continue; }

                float x = screen.x / _cfg.UiScale;
                float y = (Screen.height - screen.y) / _cfg.UiScale;

                if (_cfg.ShowNameplates)
                {
                    bool isSelf = _client.EntityId.HasValue && view.EntityId == _client.EntityId.Value;
                    bool hostile = view.Kind == EntityKind.Monster ||
                                   (view.Kind == EntityKind.Player && view.Faction != myFaction);
                    string colour = isSelf ? "#50e060" : hostile ? "#ff6060" :
                        view.Kind == EntityKind.Npc ? "#f0d060" : "#60a0ff";
                    string levelTag = view.Kind == EntityKind.Npc
                        ? "" : "  <size=9>niv." + view.Level + "</size>";
                    string label = "<color=" + colour + "><size=11>" + view.DisplayName +
                                   levelTag + "</size></color>";
                    GUI.Label(new Rect(x - 80, y - 18, 160, 16), label, RichCentered());
                }

                if (_cfg.ShowHealthBars && view.Kind != EntityKind.Npc)
                {
                    DrawBar(new Rect(x - 22, y, 44, 6), view.Health / (float)view.MaxHealth,
                        new Color(0.2f, 0.8f, 0.25f), "");

                    // Incantation: a small golden bar under the health bar.
                    if (view.CastAbilityId != 0)
                    {
                        DrawBar(new Rect(x - 22, y + 7, 44, 4), view.CastFraction,
                            new Color(0.95f, 0.8f, 0.25f), "");
                    }
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

        // --- Bank chest: prompt + window ---

        private void DrawBankPrompt()
        {
            if (_nearbyBankId < 0 || _bankOpen || _nearbyCorpseId >= 0) { return; }

            EntityView view;
            if (!_views.TryGetValue(_nearbyBankId, out view) || view == null || Camera.main == null) { return; }

            Vector3 screen = Camera.main.WorldToScreenPoint(
                view.transform.position + (Vector3.up * (view.HeadHeight + 0.35f)));
            if (screen.z < 0f) { return; }

            Rect r = new Rect((screen.x / _cfg.UiScale) - 100,
                ((Screen.height - screen.y) / _cfg.UiScale) - 14, 200, 24);
            GUI.Box(r, "[" + KeyLabel(HudConfig.Bind.Interact) + "] Ouvrir la banque");
        }

        /// <summary>An item as an ICON tile: coloured square, abbreviation, stack count, hover name.</summary>
        private void DrawItemIcon(Rect rect, byte itemId, int quantity)
        {
            ItemDefinition def = Data.GetItem(itemId);

            GUI.Box(rect, "");
            Color prev = GUI.color;
            GUI.color = ItemColor(def);
            GUI.DrawTexture(new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4),
                Texture2D.whiteTexture);
            GUI.color = prev;

            string abbrev = def.Name.Length <= 2 ? def.Name : def.Name.Substring(0, 2);
            GUI.Label(new Rect(rect.x, rect.y + 2, rect.width, 16),
                "<size=11><b><color=#101010>" + abbrev + "</color></b></size>", RichCentered());
            if (quantity > 1)
            {
                GUI.Label(new Rect(rect.x, rect.y + rect.height - 16, rect.width - 3, 14),
                    "<size=10><b><color=#101010>" + quantity + "</color></b></size>",
                    new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.LowerRight });
            }

            if (rect.Contains(Event.current.mousePosition))
            {
                GUI.Label(new Rect(rect.x - 40, rect.y - 18, rect.width + 120, 16),
                    "<size=10>" + def.Name + "</size>", RichCentered());
            }
        }

        private static Color ItemColor(ItemDefinition def)
        {
            switch (def.Type)
            {
                case ItemType.Weapon: return new Color(0.75f, 0.78f, 0.85f);
                case ItemType.Armor: return new Color(0.62f, 0.45f, 0.28f);
                case ItemType.Consumable: return new Color(0.85f, 0.35f, 0.35f);
                default:
                    // Materials: a stable hue per item id so goblin ears never look like wolf pelts.
                    float hue = (def.Id * 47 % 256) / 256f;
                    return Color.HSVToRGB(hue, 0.45f, 0.85f);
            }
        }

        private void DrawBankWindow()
        {
            if (!_bankOpen) { return; }

            const float W = 460f;
            const float Icon = 34f;
            const int Cols = 10;

            int bagRows = Mathf.Max(1, ((_client.InventoryItems.Count - 1) / Cols) + 1);
            int bankRows = Mathf.Max(1, ((_client.BankItems.Count - 1) / Cols) + 1);
            float height = 150f + ((bagRows + bankRows) * (Icon + 4f)) + 40f;
            Rect win = new Rect((VirtW - W) / 2f, (VirtH - height) / 2f, W, height);
            GUI.Box(win, "<b>Coffre de banque</b>", RichCenteredBox());

            if (GUI.Button(new Rect(win.x + win.width - 24, win.y + 4, 20, 20), "X"))
            {
                _bankOpen = false;
                return;
            }

            float y = win.y + 30f;

            // Gold, both sides.
            GUI.Label(new Rect(win.x + 14, y, 220, 20),
                "Sur toi : <b>" + FormatMoney(_client.Gold) + "</b> · au coffre : <b>" + FormatMoney(_client.BankGold) + "</b>", Rich());
            if (GUI.Button(new Rect(win.x + W - 224, y - 2, 68, 22), "Dép. 1pa"))
            {
                _client.SendBank(BankOp.DepositGold, 0, 100);
            }

            if (GUI.Button(new Rect(win.x + W - 152, y - 2, 68, 22), "Dép. tout"))
            {
                _client.SendBank(BankOp.DepositGold, 0, _client.Gold);
            }

            if (GUI.Button(new Rect(win.x + W - 80, y - 2, 68, 22), "Ret. 1pa"))
            {
                _client.SendBank(BankOp.WithdrawGold, 0, 100);
            }

            y += 30f;
            GUI.Label(new Rect(win.x + 14, y, 330, 18),
                "<b>Ton sac</b> <size=9>(clic : déposer 1 · clic droit : tout le stack)</size>", Rich());
            if (GUI.Button(new Rect(win.x + W - 110, y - 2, 96, 22), "Tout déposer"))
            {
                // Everything, one stack after another (gold stays: use the gold buttons for it).
                var stacks = new List<ItemStack>(_client.InventoryItems);
                for (int i = 0; i < stacks.Count; i++)
                {
                    _client.SendBank(BankOp.DepositItem, stacks[i].ItemId, stacks[i].Quantity);
                }
            }

            y += 20f;
            y = DrawIconGrid(win.x + 14, y, Cols, Icon, _client.InventoryItems,
                (itemId, qty) => _client.SendBank(BankOp.DepositItem, itemId, qty));

            y += 8f;
            GUI.Label(new Rect(win.x + 14, y, 330, 18),
                "<b>Le coffre</b> <size=9>(clic : retirer 1 · clic droit : tout le stack)</size>", Rich());
            y += 20f;
            DrawIconGrid(win.x + 14, y, Cols, Icon, _client.BankItems,
                (itemId, qty) => _client.SendBank(BankOp.WithdrawItem, itemId, qty));
        }

        /// <summary>
        /// Lay stacks out as clickable icon tiles. Left click acts on ONE item, right click on the
        /// WHOLE stack (the callback receives the quantity). Returns the next free y.
        /// </summary>
        private float DrawIconGrid(float x, float y, int cols, float icon,
            IReadOnlyList<ItemStack> items, System.Action<byte, int> onClick)
        {
            if (items.Count == 0)
            {
                GUI.Label(new Rect(x, y, 200, 18), "<i><size=10>(vide)</size></i>", Rich());
                return y + icon + 4f;
            }

            for (int i = 0; i < items.Count; i++)
            {
                Rect cell = new Rect(x + ((i % cols) * (icon + 4f)), y + ((i / cols) * (icon + 4f)), icon, icon);
                DrawItemIcon(cell, items[i].ItemId, items[i].Quantity);
                Event e = Event.current;
                if (e.type == EventType.MouseDown && cell.Contains(e.mousePosition))
                {
                    if (e.button == 0) { onClick(items[i].ItemId, 1); e.Use(); }
                    else if (e.button == 1) { onClick(items[i].ItemId, items[i].Quantity); e.Use(); }
                }
            }

            int rows = ((items.Count - 1) / cols) + 1;
            return y + (rows * (icon + 4f));
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
                GUI.Label(new Rect(win.x + 12, y + 3, 160, 20), FormatMoney(_client.OpenCorpseGold), Rich());
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

            const float W = 470f;
            const float Icon = 34f;
            const int Cols = 7;
            int invRows = Mathf.Max(1, ((_client.InventoryItems.Count - 1) / Cols) + 1);
            float height = 250f + (invRows * (Icon + 4f)) + 130f;
            Rect win = new Rect(VirtW - W - 16, 40, W, Mathf.Min(height, VirtH - 70));
            GUI.Box(win, "<b>Fiche de personnage</b>", RichCenteredBox());

            if (GUI.Button(new Rect(win.x + win.width - 24, win.y + 4, 20, 20), "X"))
            {
                _sheetOpen = false;
                return;
            }

            // LEFT: the live 3D portrait.
            Rect portrait = new Rect(win.x + 12, win.y + 30, 170, 226);
            GUI.Box(portrait, "");
            if (_sheetPreview.Texture != null)
            {
                GUI.DrawTexture(new Rect(portrait.x + 2, portrait.y + 2, portrait.width - 4, portrait.height - 4),
                    _sheetPreview.Texture, ScaleMode.ScaleAndCrop);
            }

            // RIGHT: identity, stats, equipment slots.
            float rx = portrait.xMax + 12f;
            float y = win.y + 32f;
            GUI.Label(new Rect(rx, y, win.xMax - rx - 12, 20),
                "<b>" + _name + "</b> — niveau " + _client.Level, Rich());
            y += 20f;
            GUI.Label(new Rect(rx, y, win.xMax - rx - 12, 20),
                "PV " + (haveSelf ? self.Health + "/" + self.MaxHealth : "—"));
            y += 18f;
            GUI.Label(new Rect(rx, y, win.xMax - rx - 12, 20),
                "Attaque " + _client.EffectiveAttack + " · Défense " + _client.EffectiveDefense);
            y += 18f;
            GUI.Label(new Rect(rx, y, win.xMax - rx - 12, 20),
                "XP " + _client.TotalXp + (_client.XpForNextLevel > 0 ? " / " + _client.XpForNextLevel : " (max)"));
            y += 18f;
            GUI.Label(new Rect(rx, y, win.xMax - rx - 12, 20), "<b>" + FormatMoney(_client.Gold) + "</b>", Rich());
            y += 26f;

            // Equipment slots: icon squares; click a slot to UNEQUIP into the bags.
            GUI.Label(new Rect(rx, y, 200, 18), "<b>Équipement</b> <size=9>(clic : retirer)</size>", Rich());
            y += 20f;
            DrawEquipSlot(new Rect(rx, y, Icon + 6, Icon + 6), _client.EquippedWeaponId, EquipSlot.Weapon, "Arme");
            DrawEquipSlot(new Rect(rx + Icon + 16, y, Icon + 6, Icon + 6), _client.EquippedArmorId, EquipSlot.Armor, "Armure");
            y += Icon + 30f;

            // BOTTOM: the bags as an icon grid. Click an equippable piece to WEAR it;
            // drag any item onto an ally (trade) or the ground (drop).
            float gy = Mathf.Max(y, portrait.yMax + 10f);
            GUI.Label(new Rect(win.x + 12, gy, 320, 18),
                "<b>Sac</b> <size=9>(clic : équiper · glisser : échanger/poser)</size>", Rich());
            gy += 20f;

            if (_client.InventoryItems.Count == 0)
            {
                GUI.Label(new Rect(win.x + 12, gy, 200, 18), "<i><size=10>(vide)</size></i>", Rich());
            }

            for (int i = 0; i < _client.InventoryItems.Count; i++)
            {
                ItemStack stack = _client.InventoryItems[i];
                Rect cell = new Rect(win.x + 12 + ((i % Cols) * (Icon + 4f)),
                    gy + ((i / Cols) * (Icon + 4f)), Icon, Icon);
                DrawItemIcon(cell, stack.ItemId, stack.Quantity);

                Event e = Event.current;
                if (e.type == EventType.MouseDown && cell.Contains(e.mousePosition))
                {
                    ItemDefinition def = Data.GetItem(stack.ItemId);
                    if (e.button == 0 && def.Slot != EquipSlot.None && !_client.TradeActive)
                    {
                        _client.SendEquipItem(stack.ItemId, (byte)def.Slot); // wear it
                        e.Use();
                    }
                    else if (e.button == 1 && _draggingItem == null && !_client.TradeActive)
                    {
                        _draggingItem = stack; // right-button grab starts the drag
                        e.Use();
                    }
                }
            }
        }

        /// <summary>One equipment slot tile; click to unequip the piece back into the bags.</summary>
        private void DrawEquipSlot(Rect rect, byte equippedId, EquipSlot slot, string label)
        {
            GUI.Box(rect, "");
            if (equippedId != 0)
            {
                DrawItemIcon(new Rect(rect.x + 3, rect.y + 3, rect.width - 6, rect.height - 6), equippedId, 1);
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                    rect.Contains(Event.current.mousePosition))
                {
                    _client.SendEquipItem(0, (byte)slot);
                    Event.current.Use();
                }
            }
            else
            {
                GUI.Label(new Rect(rect.x, rect.y + 10, rect.width, 18),
                    "<size=9><color=#909090>" + label + "</color></size>", RichCentered());
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
            GUI.Label(new Rect(leftX, topY + 20, 110, 20), FormatMoney(_myOfferGold), Rich());
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
            GUI.Label(new Rect(rightX, topY + 20, colW, 20), FormatMoney(_client.TradeTheirGold), Rich());
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

            // Release (the drag is held on the RIGHT button): over an ally → propose a trade with
            // this item pre-offered; elsewhere → drop it on the ground.
            if (Input.GetMouseButtonUp(1))
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
            if (GUILayout.Button("Se déconnecter", GUILayout.Height(28))) { DisconnectToCharacter(); }
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

        }

        /// <summary>Rising, fading damage numbers above the heads they belong to.</summary>
        private void DrawFloatingTexts()
        {
            Camera cam = Camera.main;
            if (cam == null || _floatTexts.Count == 0) { return; }

            const float Life = 1.1f;
            for (int i = _floatTexts.Count - 1; i >= 0; i--)
            {
                FloatText ft = _floatTexts[i];
                float age = Time.time - ft.Born;
                if (age > Life) { _floatTexts.RemoveAt(i); continue; }

                Vector3 screen = cam.WorldToScreenPoint(ft.World);
                if (screen.z < 0f) { continue; }

                float x = screen.x / _cfg.UiScale;
                float y = ((Screen.height - screen.y) / _cfg.UiScale) - (age * 34f); // rises
                float alpha = Mathf.Clamp01(1.2f - (age / Life));
                Color prev = GUI.color;
                GUI.color = new Color(ft.Color.r, ft.Color.g, ft.Color.b, alpha);
                GUI.Label(new Rect(x - 40, y, 80, 22), "<size=15><b>" + ft.Text + "</b></size>", RichCentered());
                GUI.color = prev;
            }
        }

        /// <summary>The centre-screen red error line ("Trop loin.", "Pas assez de mana.").</summary>
        private void DrawUiError()
        {
            if (Time.time >= _uiErrorUntil || string.IsNullOrEmpty(_uiError)) { return; }

            float alpha = Mathf.Clamp01(_uiErrorUntil - Time.time);
            Color prev = GUI.color;
            GUI.color = new Color(1f, 0.3f, 0.25f, alpha);
            GUI.Label(new Rect((VirtW / 2f) - 200, VirtH * 0.3f, 400, 24),
                "<size=14><b>" + _uiError + "</b></size>", RichCentered());
            GUI.color = prev;
        }

        /// <summary>Your own WoW-style cast bar: ability name + golden progress, bottom-centre.</summary>
        private void DrawCastBar()
        {
            EntitySnapshot self;
            if (!_client.TryGetSelf(out self) || self.CastAbilityId == 0) { return; }

            const float W = 260f;
            var rect = new Rect((VirtW - W) / 2f, VirtH - 150f, W, 22f);
            string name = Data.GetAbility(self.CastAbilityId).Name;
            DrawBar(rect, self.CastProgress / 255f, new Color(0.95f, 0.78f, 0.20f), "");
            GUI.Label(new Rect(rect.x, rect.y + 1, rect.width, 20),
                "<size=11><b>" + name + "</b></size>", RichCentered());
        }

        /// <summary>
        /// The world chat: ONLY what players write — no combat spam, no system noise. Enter opens
        /// the input, Enter sends, Escape cancels.
        /// </summary>
        private void DrawChat()
        {
            const float W = 360f;
            const float LineH = 17f;
            int lines = Mathf.Min(_chatLog.Count, 8);
            float historyH = lines * LineH;
            float inputH = _chatInputActive ? 24f : 0f;
            float y0 = VirtH - 34f - inputH - historyH;

            if (lines > 0)
            {
                Dim(new Rect(8f, y0 - 4f, W, historyH + 8f), 0.35f);
                for (int i = 0; i < lines; i++)
                {
                    string line = _chatLog[_chatLog.Count - lines + i];
                    GUI.Label(new Rect(14f, y0 + (i * LineH), W - 12f, LineH + 2f),
                        "<size=11>" + line + "</size>", Rich());
                }
            }

            if (_chatInputActive)
            {
                GUI.SetNextControlName("ChatInput");
                _chatInput = GUI.TextField(new Rect(8f, VirtH - 32f, W, 24f), _chatInput, 200);
                GUI.FocusControl("ChatInput");
            }
        }

        private void DrawVersionTag()
        {
            GUI.Label(new Rect(VirtW - 150, 8, 142, 18),
                "<size=10>v" + SimulationConstants.GameVersion + " · proto " +
                SimulationConstants.ProtocolVersion + "</size>",
                new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.UpperRight });
        }

        /// <summary>
        /// WoW-style money: the stored integer is COPPER; 100 pc = 1 pa (argent), 100 pa = 1 po
        /// (or). Shows only the coins you actually have: "3po 12pa 45pc", "8pa 20pc", "35pc".
        /// </summary>
        private static string FormatMoney(int copper)
        {
            if (copper < 0) { copper = 0; }
            int gold = copper / 10000;
            int silver = copper % 10000 / 100;
            int cents = copper % 100;

            if (gold > 0)
            {
                return "<color=#ffd700>" + gold + "po</color> <color=#c0c0c0>" + silver +
                       "pa</color> <color=#b87333>" + cents + "pc</color>";
            }

            if (silver > 0)
            {
                return "<color=#c0c0c0>" + silver + "pa</color> <color=#b87333>" + cents + "pc</color>";
            }

            return "<color=#b87333>" + cents + "pc</color>";
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
