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
        private int _appliedQuestCatalog;

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
            public string Label = ""; // realm name shown before (and instead of) the probe's
            public ServerProbe Probe;
            public bool HasInfo;
            public ServerInfo Info;
            public bool Failed;

            public string Address { get { return Host + ":" + Port; } }
        }

        private readonly LobbyStage _lobby = new LobbyStage();
        private readonly WorldDecor _decor = new WorldDecor();
        private readonly SheetPreview _sheetPreview = new SheetPreview();
        private readonly MinimapView _minimap = new MinimapView();
        private LobbyScreen _lobbyScreen = LobbyScreen.Auth;
        private string _secret2 = "";
        private bool _wasRegistering;
        private bool _serverChosen; // the player picked this realm in the browser (creation allowed)
        private bool _confirmDeleteChar; // the "delete character?" confirmation dialog is open
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
        private sealed class ChatLine
        {
            public ChatChannel Channel;
            public string From = "";
            public string To = "";
            public string Text = "";
        }

        private sealed class ChatTab
        {
            public string Name = "Général";
            public int Mask = 0xFF; // bit per ChatChannel
        }

        private readonly List<ChatLine> _chatHistory = new List<ChatLine>();
        private readonly List<ChatTab> _chatTabs = new List<ChatTab>();
        private int _chatTabIndex;
        private int _chatTabConfig = -1; // tab being configured (right-click), -1 = none
        private ChatChannel _chatChannel = ChatChannel.Say; // outgoing channel
        private string _whisperTarget = "";
        private string _lastWhisperFrom = "";
        private string _chatInput = "";
        private bool _chatInputActive;
        private readonly List<ServerEntry> _servers = new List<ServerEntry>();
        private bool _lastServerSaved;
        private int _previewKey = -1;

        private static readonly string[] SkinLabels = { "Clair", "Moyen", "Halé", "Sombre" };
        private static readonly string[] FaceLabels = { "Classique", "Nez fort", "Fin" };
        private static readonly string[] HairStyleLabels = { "Courte", "Longue", "Iroquoise", "Chauve" };
        private static readonly string[] HairColorLabels = { "Brun", "Noir", "Blond", "Roux", "Blanc", "Bleu nuit" };
        private static readonly string[] BeardStyleLabels = { "Aucune", "Courte", "Longue", "Tressée" };

        private static readonly (byte id, string label, byte[] classes, byte racial)[] Races =
        {
            (1, "Human (Alliance)", new byte[] { 1, 2, 4, 5 }, (byte)10),
            (4, "Dwarf (Alliance)", new byte[] { 1, 3, 5 }, (byte)11),
            (2, "Orc (Horde)", new byte[] { 1, 3, 5 }, (byte)12),
            (3, "Elf (Horde)", new byte[] { 2, 3, 4, 5 }, (byte)13),
        };

        private static readonly (byte id, string label, byte advancedAbility)[] Classes =
        {
            (1, "Warrior (Rage)", (byte)20),
            (2, "Mage (Mana)", (byte)21),
            (3, "Ranger (Energy)", (byte)22),
            (4, "Druide (Mana)", (byte)33),
            (5, "Clerc (Mana)", (byte)51),
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

        // --- World map (M), quest log (L), logout timer ---
        private bool _worldMapOpen;
        private bool _questLogOpen;
        private byte _trackedQuestId; // quest whose hunting zone is highlighted on the maps
        private int _questLogTab;     // 0 = actives, 1 = terminées
        private float _mapBlinkUntil; // the zone circle pulses until this time (tracker click)
        private float _logoutAt = -1f; // Time.time when the seated logout completes (-1 = off)
        private static Texture2D _zoneDisc; // soft translucent circle for map zone overlays
        private bool _targetHostile; // stance & auto-attack only apply to hostile targets
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
        private bool _bagsOpen;
        private bool _uiPress; // the current mouse press began over the character sheet
        private bool _questWindowOpen;
        private int _nearbyQuestGiverId = -1;
        private bool _shopOpen;
        private int _nearbyMerchantId = -1;
        private int _nearbyInnkeeperId = -1;
        private float _hearthReadyTime; // local display only — the server is the judge
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
        private int _dragFromIndex = -1; // bag cell the dragged stack came from
        private EquipSlot _dragFromEquip = EquipSlot.None; // sheet slot it came from (if any)

        // Bag filter tabs: 0 Tout, 1 Équipement, 2 Consommables, 3 Butin — order is customisable.
        private int _bagFilter;
        private readonly List<int> _bagFilterOrder = new List<int> { 0, 1, 2, 3 };

        private static string BagFilterLabel(int filter)
        {
            switch (filter)
            {
                case 1: return "Équipement";
                case 2: return "Consommables";
                case 3: return "Butin";
                default: return "Tout";
            }
        }

        private static bool BagFilterMatches(ItemDefinition def, int filter)
        {
            switch (filter)
            {
                case 1: return def.IsEquippable;
                case 2: return def.Type == ItemType.Consumable;
                case 3: return def.Type == ItemType.Material;
                default: return true;
            }
        }

        /// <summary>Next bag index (from the cursor) matching the active filter, or -1.</summary>
        private int NextMatchingIndex(IReadOnlyList<ItemStack> items, ref int cursor)
        {
            while (cursor < items.Count)
            {
                int i = cursor++;
                if (items[i].ItemId == 0) { continue; }
                if (BagFilterMatches(Data.GetItem(items[i].ItemId), _bagFilter)) { return i; }
            }

            return -1;
        }

        private void SaveBagFilterOrder()
            => PlayerPrefs.SetString("aetheria.bagFilters", string.Join(",", _bagFilterOrder));

        private void LoadBagFilterOrder()
        {
            string saved = PlayerPrefs.GetString("aetheria.bagFilters", "");
            if (string.IsNullOrEmpty(saved)) { return; }

            var order = new List<int>();
            foreach (string part in saved.Split(','))
            {
                if (int.TryParse(part, out int f) && f >= 0 && f <= 3 && !order.Contains(f)) { order.Add(f); }
            }

            if (order.Count == 4)
            {
                _bagFilterOrder.Clear();
                _bagFilterOrder.AddRange(order);
            }
        }
        private string _lastDuelMessage = "";
        private string _lastTradeMessage = "";

        // Layout drag state.
        private HudConfig.Frame? _draggingFrame;
        private Vector2 _dragStartMouse;
        private Vector2 _dragStartOffset;

        private void Start()
        {
            _cfg.Load(HudConfig.ActiveProfile());
            LoadBagFilterOrder();
            ApplyLaunchArguments();
        }

        /// <summary>
        /// LAUNCHER handoff: `Aetheria.exe --account X --secret Y --autologin` connects straight
        /// to the first realm and lands on the character screen. Without these arguments the
        /// login screen works exactly as before (needed to switch accounts or play sans launcher).
        /// </summary>
        private void ApplyLaunchArguments()
        {
            string[] args = System.Environment.GetCommandLineArgs();
            bool auto = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--account" && i + 1 < args.Length) { _account = args[i + 1]; }
                else if (args[i] == "--secret" && i + 1 < args.Length) { _secret = args[i + 1]; }
                else if (args[i] == "--autologin") { auto = true; }
            }

            if (auto && _account.Length > 0 && _secret.Length > 0)
            {
                // Don't connect blind: PROBE the realm's routes first and take the one that
                // answers (public IP for friends, localhost for the host — never the hairpin).
                LoadServerList();
                if (_servers.Count > 0)
                {
                    RefreshServers();
                    _autoLoginPending = true;
                    _autoLoginDeadline = Time.realtimeSinceStartup + 6f;
                }
            }
        }

        private bool _autoLoginPending;
        private float _autoLoginDeadline;

        /// <summary>Ticked from Update while the launcher's auto-login waits for a probe.</summary>
        private void TickAutoLogin()
        {
            if (!_autoLoginPending || _connected)
            {
                _autoLoginPending = false;
                return;
            }

            List<ServerEntry> rows = RealmRows();
            if (rows.Count == 0)
            {
                _autoLoginPending = false;
                return;
            }

            ServerEntry realm = rows[0]; // the first realm (Zul'jin) is the auto-login target
            if (realm.HasInfo)
            {
                _autoLoginPending = false;
                Connect(realm.Address, createAccount: false);
            }
            else if (Time.realtimeSinceStartup > _autoLoginDeadline)
            {
                _autoLoginPending = false;
                Connect(realm.Address, createAccount: false); // no probe answered: best effort
            }
        }

        private void Update()
        {
            bool inWorld = _connected && _client != null && _client.EntityId.HasValue;

            // Lobby: 3D campsite backdrop + character preview + server probes.
            if (!inWorld)
            {
                _lobby.EnsureBuilt();
                UpdateLobbyPreview();
                TickAutoLogin(); // launcher handoff: connect once a realm route answers

                // The preview never spins on its own: hold the RIGHT button and drag to turn it.
                if (Input.GetMouseButton(1))
                {
                    _lobby.PreviewYaw += Input.GetAxis("Mouse X") * 6f;
                }

                _lobby.Tick(Time.deltaTime);
                PumpServerProbes();
                if (_decor.Active) { _decor.Teardown(); }
                _minimap.Teardown();
            }
            else
            {
                if (_lobby.Active) { _lobby.Teardown(); }
                _decor.EnsureBuilt(); // zone scenery: sanctuary, path, goblin camp, wolf field
                _minimap.EnsureBuilt();
                if (_client.TryGetSelf(out EntitySnapshot mapSelf))
                {
                    _minimap.Tick(new Vector3(mapSelf.Position.X, 0f, mapSelf.Position.Y));
                }

                // Day/night from the SERVER clock — every player shares the same sky.
                // +150s so a freshly started server opens mid-morning, not at midnight.
                DayNight.Apply(AetheriaBootstrap.SunLight, Camera.main,
                    DayNight.PhaseFor((_client.LastTick * SimulationConstants.TickDelta) + 150f));
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
                else if (_lobbyScreen == LobbyScreen.Character && _client.LoggedIn && !_client.HasCharacter)
                {
                    // The character was just DELETED: the server confirmed with an empty
                    // summary — straight to creation on this same server.
                    _confirmDeleteChar = false;
                    _lobby.ClearPreview();
                    _previewKey = -1;
                    _lobbyScreen = LobbyScreen.Creation;
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

            TickLogout(); // the seated 10-second logout, when armed

            // Chat input: Enter OPENS the box. Sending/closing happens in DrawChat's OnGUI
            // capture — the focused TextField swallows KeyDown there, Update never sees it.
            if ((Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) &&
                !_chatInputActive && !_menuOpen && _awaitingBind == null)
            {
                _chatInputActive = true;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                // ÉCHAP ferme les fenêtres UNE PAR UNE (la plus « haute » d'abord),
                // puis vide la cible, et enfin seulement ouvre le menu.
                if (_chatInputActive) { _chatInputActive = false; _chatInput = ""; }
                else if (_chatTabConfig >= 0) { _chatTabConfig = -1; }
                else if (_awaitingBind != null) { _awaitingBind = null; }
                else if (_partyMenuFor >= 0) { _partyMenuFor = -1; }
                else if (_contextEntityId >= 0) { _contextEntityId = -1; }
                else if (_client.TradeActive) { _client.SendTradeCancel(); }
                else if (_client.LastInspect != null) { _client.ClearInspect(); }
                else if (_questWindowOpen) { _questWindowOpen = false; }
                else if (_shopOpen) { _shopOpen = false; }
                else if (_bankOpen) { _bankOpen = false; }
                else if (_client.OpenCorpseId >= 0) { _client.CloseCorpse(); }
                else if (_bagsOpen) { _bagsOpen = false; }
                else if (_sheetOpen) { _sheetOpen = false; }
                else if (_worldMapOpen) { _worldMapOpen = false; }
                else if (_questLogOpen) { _questLogOpen = false; }
                else if (_logoutAt > 0f) { _logoutAt = -1f; } // Escape cancels the logout timer
                else if (_targetId >= 0) { SetAttackIntent(-1); } // Escape drops the target first
                else { _menuOpen = !_menuOpen; _optionsTab = -1; _layoutEditMode = false; }
            }

            // A press that BEGINS over the character sheet belongs to the UI (portrait spin,
            // slot clicks) — the camera must not steer with it, and taps must not hit the world.
            if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
            {
                Vector2 gm = GuiMouse();
                _uiPress = (_sheetOpen && SheetWindowRect().Contains(gm)) ||
                           (_bagsOpen && FrameRect(HudConfig.Frame.Bags).Contains(gm)) ||
                           (_client.OpenCorpseId >= 0 && LootWindowRect().Contains(gm));
            }
            else if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1) &&
                     !Input.GetMouseButtonUp(0) && !Input.GetMouseButtonUp(1))
            {
                _uiPress = false;
            }

            // Layout-edit mode or an item drag: the camera must hold still.
            if (_cameraRig != null)
            {
                _cameraRig.SuppressDrag = _uiPress || _layoutEditMode || _draggingItem != null;
            }

            // The server's quest catalogue overrides the built-in defaults: quests written in
            // the Studio go live for every player without touching the client.
            if (_client.QuestCatalogVersion != _appliedQuestCatalog && _client.QuestCatalog != null)
            {
                Data.ReplaceQuests(_client.QuestCatalog);
                _appliedQuestCatalog = _client.QuestCatalogVersion;
            }

            SyncViews();
            PlayCombatAnimations();
            UpdateTargetRing();

            // WoW combat stance: as soon as a hostile is engaged, MY character raises the weapon.
            EntityView stanceView;
            if (_client.EntityId.HasValue &&
                _views.TryGetValue(_client.EntityId.Value, out stanceView) && stanceView != null)
            {
                stanceView.CombatStance = _targetId >= 0 && _targetHostile;
            }
            FindNearbyCorpse();
            FindNearbyBank();
            FindNearbyQuestGiver();
            FindNearbyMerchant();
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

                if (!_layoutEditMode) // frame-dragging: no world clicks, no ability keys
                {
                    HandleKeys();
                    HandleMouse();
                }

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
                for (int i = 0; i < _client.CharacterEquipment.Length; i++)
                {
                    charKey = (charKey * 31) ^ (_client.CharacterEquipment[i] + (i << 8));
                }

                if (charKey != _previewKey)
                {
                    _previewKey = charKey;
                    byte charRace = _client.CharacterRaceId;
                    Faction charFaction = charRace == 2 || charRace == 3 ? Faction.Horde : Faction.Alliance;
                    _lobby.ShowPreview(charRace, _client.CharacterClassId, _client.CharacterGender,
                        _client.CharacterAppearance, charFaction, _client.CharacterEquipment);
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
        /// The OFFICIAL realm list — players just CLICK a named realm, WoW-style; nobody types
        /// addresses. Each entry: realm name shown in the list + where it lives. To open your
        /// realm to the internet, put your PUBLIC IP here (and port-forward UDP 27015) — or ship
        /// a servers.txt beside the .exe ("Zul'jin|82.65.12.34:27015" per line) to override
        /// without rebuilding.
        /// </summary>
        private static readonly (string Label, string Address)[] PredefinedServers =
        {
#if UNITY_EDITOR
            // In the EDITOR both realms show, so dev tests can reach the shared PTR server.
            ("Zul'jin", "127.0.0.1:27015"),
            ("PTR", "127.0.0.1:27016"),
#else
            // A real build without servers.txt (shouldn't happen — every channel ships one)
            // falls back to the playable realm only. Each channel's servers.txt is the truth:
            // the Aetheria channel lists Zul'jin, the PTR channel lists PTR.
            ("Zul'jin", "127.0.0.1:27015"),
#endif
        };

        private void LoadServerList()
        {
            if (_servers.Count > 0) { return; }

            // realms.txt beside the game overrides the built-in realms entirely when present.
            // (Renamed from servers.txt so STALE old files lying around are simply ignored.)
            bool fromFile = false;
            try
            {
                string path = System.IO.Path.Combine(Application.dataPath, "..", "realms.txt");
                if (System.IO.File.Exists(path))
                {
                    foreach (string line in System.IO.File.ReadAllLines(path))
                    {
                        fromFile |= AddRealmLine(line);
                    }
                }
            }
            catch (System.Exception) { /* unreadable file: fall back to the built-in list */ }

            if (!fromFile)
            {
                foreach ((string label, string address) in PredefinedServers)
                {
                    AddServerEntry((label, address));
                }
            }
        }

        /// <summary>
        /// "Zul'jin|82.65.12.34:27015|192.168.1.10:27015|127.0.0.1:27015" → the SAME realm with
        /// several routes. The browser shows ONE row per realm and joins whichever route answers —
        /// the host plays via localhost while friends come in through the public IP, and nobody
        /// fights their own router (hairpin NAT).
        /// </summary>
        private bool AddRealmLine(string line)
        {
            line = line.Trim();
            if (line.Length == 0) { return false; }

            string[] parts = line.Split('|');
            string label = parts.Length > 1 ? parts[0].Trim() : line;
            bool any = false;
            for (int i = parts.Length > 1 ? 1 : 0; i < parts.Length; i++)
            {
                any |= AddServerEntry((label, parts[i].Trim()));
            }

            return any;
        }

        /// <summary>Add one named route to the list (ignoring blanks and duplicates).</summary>
        private bool AddServerEntry((string Label, string Address) realm)
        {
            if (string.IsNullOrEmpty(realm.Address) ||
                !SplitAddress(realm.Address, out string host, out int port))
            {
                return false;
            }

            foreach (ServerEntry existing in _servers)
            {
                if (existing.Host == host && existing.Port == port) { return false; }
            }

            _servers.Add(new ServerEntry { Host = host, Port = port, Label = realm.Label });
            return true;
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

        /// <summary>
        /// HARDCORE logout: outside the sanctuary it takes 10 seconds, seated by the fire so
        /// the world can still kill you — inside the safe zone it's instant. Moving (or Échap)
        /// cancels the timer.
        /// </summary>
        private void BeginLogout()
        {
            EntitySnapshot self;
            bool inSanctuary = false;
            if (_client.TryGetSelf(out self) && !_client.InInstance)
            {
                float dx = self.Position.X - SimulationConstants.SafeZoneCenterX;
                float dy = self.Position.Y - SimulationConstants.SafeZoneCenterY;
                inSanctuary = (dx * dx) + (dy * dy)
                    <= SimulationConstants.SafeZoneRadius * SimulationConstants.SafeZoneRadius;
            }

            if (inSanctuary)
            {
                DisconnectToCharacter();
                return;
            }

            _logoutAt = Time.time + 10f;
        }

        /// <summary>The seated countdown: sit the character, watch for movement, disconnect at 0.</summary>
        private void TickLogout()
        {
            bool active = _logoutAt > 0f;

            // Sitting is a pose on OUR view; it follows the timer's life.
            EntityView selfView;
            if (_client.EntityId.HasValue &&
                _views.TryGetValue(_client.EntityId.Value, out selfView) && selfView != null)
            {
                selfView.Sitting = active;
            }

            if (!active)
            {
                return;
            }

            // Any movement input stands you back up and cancels the logout.
            if (Input.GetAxisRaw("Horizontal") != 0f || Input.GetAxisRaw("Vertical") != 0f ||
                (Input.GetMouseButton(0) && Input.GetMouseButton(1)) || Input.GetKeyDown(KeyCode.Space))
            {
                _logoutAt = -1f;
                return;
            }

            if (Time.time >= _logoutAt)
            {
                _logoutAt = -1f;
                DisconnectToCharacter();
            }
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
            _serverChosen = false;
            _lobbyScreen = LobbyScreen.Auth;
            _previewKey = -1;
            _lobby.ClearPreview();
            _targetId = -1;
            _menuOpen = false;
            _sheetOpen = false;
            _bagsOpen = false;
            _contextEntityId = -1;
            _cooldownReadyAt.Clear();

            foreach (EntityView view in _views.Values)
            {
                if (view != null) { Destroy(view.gameObject); }
            }

            _views.Clear();
            SaveChatHistory(); // kept channels (groupe, guilde, chuchote, système) survive…
            LoadChatHistory(); // …the rest (dire, commerce, raid, monde) starts fresh
            _chatInput = "";
            _chatInputActive = false;
            _bankOpen = false;
            _nearbyBankId = -1;
            _sheetPreview.Teardown();
            _wasRegistering = false;
        }

        private void OnDestroy()
        {
            // NO blanket save here: with two test windows open, the window you did NOT customize
            // would overwrite the other's freshly saved layout on quit. Every real change
            // (drag end, options close, profile switch) already saves immediately.
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
                if (e.Kind != EntityKind.Corpse && e.Kind != EntityKind.MonsterCorpse) { continue; }
                float d = Vec2.DistanceSquared(self.Position, e.Position);
                if (d <= best) { best = d; _nearbyCorpseId = e.Id; }
            }
        }

        /// <summary>The quest giver you stand next to, if any (Npc type 2).</summary>
        private void FindNearbyQuestGiver()
        {
            _nearbyQuestGiverId = -1;
            EntitySnapshot self;
            if (!_client.TryGetSelf(out self)) { return; }

            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                if (e.Kind != EntityKind.Npc || e.RaceId != 2) { continue; }
                if (Vec2.DistanceSquared(self.Position, e.Position) <=
                    SimulationConstants.QuestGiverRange * SimulationConstants.QuestGiverRange)
                {
                    _nearbyQuestGiverId = e.Id;
                    break;
                }
            }

            if (_questWindowOpen && _nearbyQuestGiverId < 0)
            {
                _questWindowOpen = false; // walked away: the dialogue closes
            }
        }

        /// <summary>The merchant NPC you stand next to, if any (npcType 4).</summary>
        private void FindNearbyMerchant()
        {
            _nearbyMerchantId = -1;
            EntitySnapshot self;
            if (!_client.TryGetSelf(out self)) { return; }

            IReadOnlyList<EntitySnapshot> visible = _client.Visible;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                if (e.Kind != EntityKind.Npc || e.RaceId != 4) { continue; }
                if (Vec2.DistanceSquared(self.Position, e.Position) <=
                    SimulationConstants.VendorRange * SimulationConstants.VendorRange)
                {
                    _nearbyMerchantId = e.Id;
                    break;
                }
            }

            if (_shopOpen && _nearbyMerchantId < 0)
            {
                _shopOpen = false; // walked away: the shop closes
            }

            // The innkeeper too (npcType 7): F binds the hearthstone at her inn.
            _nearbyInnkeeperId = -1;
            for (int i = 0; i < visible.Count; i++)
            {
                EntitySnapshot e = visible[i];
                if (e.Kind != EntityKind.Npc || e.RaceId != 7) { continue; }
                if (Vec2.DistanceSquared(self.Position, e.Position) <=
                    SimulationConstants.InnkeeperRange * SimulationConstants.InnkeeperRange)
                {
                    _nearbyInnkeeperId = e.Id;
                    break;
                }
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
                if (e.Kind != EntityKind.Npc || e.RaceId != 1) { continue; }
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

        /// <summary>Pull received chat into the structured history (channel-aware).</summary>
        private void PumpChat()
        {
            IReadOnlyList<ChatMessage> feed = _client.DrainChatFeed();
            for (int i = 0; i < feed.Count; i++)
            {
                ChatMessage m = feed[i];
                _chatHistory.Add(new ChatLine { Channel = m.Channel, From = m.From, To = m.To, Text = m.Text });
                if (m.Channel == ChatChannel.Whisper && m.From.Length > 0)
                {
                    _lastWhisperFrom = m.From; // « /rep » answers here
                }

                if (_chatHistory.Count > 200) { _chatHistory.RemoveAt(0); }
            }
        }

        private static readonly (ChatChannel ch, string label, string color)[] ChannelStyles =
        {
            (ChatChannel.Say, "Dire", "#ffffff"),
            (ChatChannel.Party, "Groupe", "#55aaff"),
            (ChatChannel.Raid, "Raid", "#ff7f00"),
            (ChatChannel.Guild, "Guilde", "#40ff40"),
            (ChatChannel.Trade, "Commerce", "#ffb0a0"),
            (ChatChannel.World, "Monde", "#ffd870"),
            (ChatChannel.Whisper, "Chuchote", "#ff80ff"),
            (ChatChannel.System, "Système", "#ffd100"),
        };

        private static (ChatChannel ch, string label, string color) StyleOf(ChatChannel ch)
        {
            for (int i = 0; i < ChannelStyles.Length; i++)
            {
                if (ChannelStyles[i].ch == ch) { return ChannelStyles[i]; }
            }

            return ChannelStyles[0];
        }

        /// <summary>Parse « /g coucou », « /w Nom message », etc., then send on the right channel.</summary>
        private void ParseAndSendChat(string text)
        {
            if (text.StartsWith("/", System.StringComparison.Ordinal))
            {
                int sp = text.IndexOf(' ');
                string cmd = (sp < 0 ? text : text.Substring(0, sp)).ToLowerInvariant();
                string rest = sp < 0 ? "" : text.Substring(sp + 1).Trim();

                switch (cmd)
                {
                    case "/d": case "/s": case "/dire": _chatChannel = ChatChannel.Say; break;
                    case "/g": case "/groupe": case "/p": _chatChannel = ChatChannel.Party; break;
                    case "/r": case "/raid": _chatChannel = ChatChannel.Raid; break;
                    case "/gu": case "/guilde": _chatChannel = ChatChannel.Guild; break;
                    case "/co": case "/commerce": _chatChannel = ChatChannel.Trade; break;
                    case "/m": case "/monde": _chatChannel = ChatChannel.World; break;
                    case "/w": case "/chuchoter":
                        int sp2 = rest.IndexOf(' ');
                        if (sp2 <= 0) { _chatChannel = ChatChannel.Whisper; return; }
                        _whisperTarget = rest.Substring(0, sp2);
                        _chatChannel = ChatChannel.Whisper;
                        rest = rest.Substring(sp2 + 1).Trim();
                        break;
                    case "/rep":
                        if (_lastWhisperFrom.Length == 0) { return; }
                        _whisperTarget = _lastWhisperFrom;
                        _chatChannel = ChatChannel.Whisper;
                        break;
                    default:
                        return; // unknown command: swallow silently
                }

                if (rest.Length == 0) { return; } // just switched channel
                text = rest;
            }

            _client.SendChat(_chatChannel, text,
                _chatChannel == ChatChannel.Whisper ? _whisperTarget : "");
        }

        // --- Chat persistence: kept channels survive reconnection; the rest reset. ---

        private static bool ChannelPersists(ChatChannel ch)
            => ch is ChatChannel.Party or ChatChannel.Guild or ChatChannel.Whisper or ChatChannel.System;

        private string ChatHistoryKey => "aeth.chathist." + _account.Trim().ToLowerInvariant();

        private void SaveChatHistory()
        {
            var sb = new System.Text.StringBuilder();
            int start = Mathf.Max(0, _chatHistory.Count - 60);
            for (int i = start; i < _chatHistory.Count; i++)
            {
                ChatLine l = _chatHistory[i];
                if (!ChannelPersists(l.Channel)) { continue; }
                sb.Append((int)l.Channel).Append('\u001f').Append(l.From).Append('\u001f')
                  .Append(l.To).Append('\u001f').Append(l.Text).Append('\u001e');
            }

            PlayerPrefs.SetString(ChatHistoryKey, sb.ToString());
            PlayerPrefs.Save();
        }

        private void LoadChatHistory()
        {
            _chatHistory.Clear();
            string raw = PlayerPrefs.GetString(ChatHistoryKey, "");
            foreach (string entry in raw.Split('\u001e'))
            {
                string[] parts = entry.Split('\u001f');
                if (parts.Length == 4 && int.TryParse(parts[0], out int ch))
                {
                    _chatHistory.Add(new ChatLine
                    {
                        Channel = (ChatChannel)ch, From = parts[1], To = parts[2], Text = parts[3],
                    });
                }
            }
        }

        // --- Chat tabs: named, per-tab channel filters, saved with the interface. ---

        private void EnsureChatTabs()
        {
            if (_chatTabs.Count > 0) { return; }

            string raw = PlayerPrefs.GetString("aeth.chattabs", "");
            foreach (string entry in raw.Split('\u001e'))
            {
                string[] parts = entry.Split('\u001f');
                if (parts.Length == 2 && int.TryParse(parts[1], out int mask))
                {
                    _chatTabs.Add(new ChatTab { Name = parts[0], Mask = mask });
                }
            }

            if (_chatTabs.Count == 0)
            {
                _chatTabs.Add(new ChatTab { Name = "Général", Mask = 0xFF });
            }
        }

        private void SaveChatTabs()
        {
            var sb = new System.Text.StringBuilder();
            foreach (ChatTab tab in _chatTabs)
            {
                sb.Append(tab.Name).Append('\u001f').Append(tab.Mask).Append('\u001e');
            }

            PlayerPrefs.SetString("aeth.chattabs", sb.ToString());
            PlayerPrefs.Save();
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
                _bagsOpen = true; // WoW opens the bags with the trade window
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
                // The camera KEEPS whatever angle the player gave it — no auto-recenter while
                // walking. It only turns when the player drags it (left or right button).
                _cameraRig.Target = new Vector3(self.Position.X, 0f, self.Position.Y);
            }
        }

        // --------------------------------------------------------------- Input

        private void HandleKeys()
        {
            if (_cfg.Down(HudConfig.Bind.NextTarget)) { CycleTarget(); }
            if (_cfg.Down(HudConfig.Bind.Attack1)) { TryCastOnTarget(CurrentBasicAbility()); }

            // Druid shapeshifts on 4/5/6 (bear/owl/cat) — same key again = back to humanoid.
            if (_classId == 4)
            {
                byte current = SelfForm();
                if (Input.GetKeyDown(KeyCode.Alpha4)) { _client.SendShapeShift(current == 1 ? (byte)0 : (byte)1); }
                if (Input.GetKeyDown(KeyCode.Alpha5)) { _client.SendShapeShift(current == 2 ? (byte)0 : (byte)2); }
                if (Input.GetKeyDown(KeyCode.Alpha6)) { _client.SendShapeShift(current == 3 ? (byte)0 : (byte)3); }
            }
            if (_cfg.Down(HudConfig.Bind.Attack2)) { TryCastOnTarget(Classes[_classIndex].advancedAbility); }
            if (_cfg.Down(HudConfig.Bind.Renew)) { TryCastSelf(5); }
            if (_cfg.Down(HudConfig.Bind.Racial)) { TryUseRacial(); }
            if (_cfg.Down(HudConfig.Bind.Interact)) { PressLoot(); }
            if (_cfg.Down(HudConfig.Bind.CharSheet)) { _sheetOpen = !_sheetOpen; }
            if (_cfg.Down(HudConfig.Bind.Bags)) { _bagsOpen = !_bagsOpen; }
            if (_cfg.Down(HudConfig.Bind.Invite) && _targetId >= 0) { _client.SendPartyInvite(_targetId); }
            if (_cfg.Down(HudConfig.Bind.AcceptInvite) && _client.PendingInviteFrom != null) { _client.SendPartyRespond(true); }
            if (_cfg.Down(HudConfig.Bind.LeaveParty)) { _client.SendPartyLeave(); }
            // Instances have PHYSICAL gates now: walk into the portal, no keyboard shortcut.
            if (_cfg.Down(HudConfig.Bind.WorldMap)) { _worldMapOpen = !_worldMapOpen; }
            if (_cfg.Down(HudConfig.Bind.QuestLog)) { _questLogOpen = !_questLogOpen; }
        }

        private void HandleMouse()
        {
            // Left button doubles as free camera orbit: a quick TAP selects a target, a DRAG
            // orbits (handled by the rig) without selecting anything.
            if (Input.GetMouseButtonDown(0))
            {
                _leftDownPos = Input.mousePosition;
            }

            if (Input.GetMouseButtonUp(0) && !_uiPress &&
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

            if (Input.GetMouseButtonUp(1) && !_uiPress && _draggingItem == null &&
                (Input.mousePosition - _rightDownPos).sqrMagnitude < 64f) // < 8 px: a tap
            {
                HandleRightClick();
            }
        }

        /// <summary>Left-click: SELECT whatever is under the cursor — a hostile becomes the
        /// attack target; a friendly player or an NPC is just targeted (no aggression).</summary>
        private void HandleLeftClick()
        {
            if (_client.OpenCorpseId >= 0) { return; }

            EntitySnapshot self;
            if (!_client.TryGetSelf(out self)) { return; }

            EntitySnapshot? picked = PickEntityUnderMouse(e =>
                e.Id != self.Id &&
                (e.Kind == EntityKind.Monster || e.Kind == EntityKind.Player || e.Kind == EntityKind.Npc));

            if (picked == null)
            {
                // Clicking the GROUND keeps the current target — only Échap clears it, and
                // clicking another entity switches it (the user asked for sticky targeting).
                return;
            }

            EntitySnapshot target = picked.Value;
            bool hostile = target.Kind == EntityKind.Monster ||
                           (target.Kind == EntityKind.Player && target.Faction != self.Faction) ||
                           target.Id == _client.DuelOpponentId;

            if (hostile)
            {
                _targetHostile = true;
                SetAttackIntent(target.Id);
            }
            else
            {
                // Friendly selection: target frame + ring, but no attack order.
                _targetHostile = false;
                _targetId = target.Id;
                _client.SendAttackTarget(0);
            }
        }

        /// <summary>
        /// Select a target and declare the attack intent: ONE message, then the SERVER swings the
        /// class's basic attack (or recasts its incantation) until the target drops. -1 clears.
        /// </summary>
        private void SetAttackIntent(int targetId)
        {
            _targetId = targetId;
            if (targetId >= 0) { _targetHostile = true; } // every attack intent is hostile by definition
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
                case EntityKind.MonsterCorpse:
                    _client.SendOpenCorpse(target.Id); // range enforced server-side
                    break;

                case EntityKind.Npc when target.RaceId == 2:
                    // Right-click on the quest giver: TARGET him too, then open his window.
                    _targetHostile = false;
                    _targetId = target.Id;
                    _client.SendAttackTarget(0);
                    if (_nearbyQuestGiverId >= 0) { _questWindowOpen = true; }
                    break;

                case EntityKind.Npc:
                    // Any other NPC: right-click selects it, same sticky rules as left-click.
                    _targetHostile = false;
                    _targetId = target.Id;
                    _client.SendAttackTarget(0);
                    break;

                case EntityKind.Monster:
                    SetAttackIntent(target.Id);
                    break;

                case EntityKind.Player when target.Faction != self.Faction || target.Id == _client.DuelOpponentId:
                    SetAttackIntent(target.Id);
                    break;

                case EntityKind.Player:
                    _targetHostile = false;                // ally: select AND open the menu
                    _targetId = target.Id;
                    _client.SendAttackTarget(0);
                    _contextEntityId = target.Id;
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
            // Loot window already open: F = take EVERYTHING (the window closes once emptied).
            if (_client.OpenCorpseId >= 0) { _client.SendLootCorpse(_client.OpenCorpseId); }
            else if (_nearbyCorpseId >= 0) { _client.SendOpenCorpse(_nearbyCorpseId); }
            else if (_questWindowOpen) { _questWindowOpen = false; }
            else if (_nearbyQuestGiverId >= 0) { _questWindowOpen = true; }
            else if (_shopOpen) { _shopOpen = false; }
            else if (_nearbyMerchantId >= 0) { _shopOpen = true; _bagsOpen = true; }
            else if (_bankOpen) { _bankOpen = false; }
            else if (_nearbyBankId >= 0) { _bankOpen = true; }
            else if (_nearbyInnkeeperId >= 0) { _client.SendSetHome(); }
        }

        private void TryCastOnTarget(byte abilityId)
        {
            // BENEFICIAL spells (heals): fall back to yourself, ignore facing — WoW self-cast.
            AbilityDefinition castDef = Data.GetAbility(abilityId);
            bool beneficial = castDef.Id == abilityId && castDef.BaseDamage == 0 &&
                              castDef.Effect != EffectType.None && castDef.Range > 0f;
            if (beneficial)
            {
                int healTarget = _targetId >= 0 && !_targetHostile ? _targetId
                    : _client.EntityId ?? -1;
                if (healTarget < 0) { return; }
                if (Time.time < _gcdReadyTime || IsOnCooldown(abilityId)) { ShowError("Pas encore prêt."); return; }
                _client.SendUseAbility(abilityId, healTarget);
                StartLocalCooldown(abilityId);
                _gcdReadyTime = Time.time + (SimulationConstants.GlobalCooldownTicks * SimulationConstants.TickDelta);
                return;
            }

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

                // FACING (WoW rule): the server refuses a strike at what's behind you — warn
                // here so the refusal is never silent.
                float fdx = target.Position.X - self.Position.X;
                float fdy = target.Position.Y - self.Position.Y;
                if ((Mathf.Cos(_lastFacing) * fdx) + (Mathf.Sin(_lastFacing) * fdy) < 0f)
                {
                    ShowError("Tu dois faire face à ta cible.");
                    return;
                }

                // SAFE ZONE or an OBSTACLE in the way: the server will say no — say it here.
                bool inSanctuary = !_client.InInstance && (
                    IsSafePos(self.Position) || IsSafePos(target.Position));
                bool blocked = !_client.InInstance && def.Range > 3f &&
                    !Aetheria.Shared.Data.WorldLayout.HasLineOfSight(self.Position, target.Position);
                if (_targetHostile && (inSanctuary || blocked))
                {
                    ShowError("Tu ne peux pas faire ça pour l'instant.");
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
                case 2: case 5: return "Pas assez de mana.";
                default: return "Pas assez d'énergie.";
            }
        }

        private static bool IsSafePos(Vec2 position)
        {
            float dx = position.X - SimulationConstants.SafeZoneCenterX;
            float dy = position.Y - SimulationConstants.SafeZoneCenterY;
            return (dx * dx) + (dy * dy)
                <= SimulationConstants.SafeZoneRadius * SimulationConstants.SafeZoneRadius;
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

            // WoW classic: BOTH mouse buttons held = run forward (facing follows the camera).
            if (v == 0f && !_uiPress && Input.GetMouseButton(0) && Input.GetMouseButton(1))
            {
                if (_cameraRig != null) { _lastFacing = _cameraRig.FacingRadians; }
                v = 1f;
            }

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
                case HudConfig.Frame.Minimap: return new Rect(VirtW - 196 + o.x, 8 + o.y, 188, 226);
                case HudConfig.Frame.QuestTracker: return new Rect(VirtW - 236 + o.x, 232 + o.y, 228, 66);
                case HudConfig.Frame.PartyFrames: return new Rect(12 + o.x, 116 + o.y, 180, 4 * 64);
                case HudConfig.Frame.CharSheet: return new Rect(16 + o.x, 128 + o.y, 342, 396);
                case HudConfig.Frame.Chat:
                    return new Rect(8 + o.x, VirtH - 220 + o.y, 380, 186);
                case HudConfig.Frame.MicroBar:
                    return new Rect(VirtW - 16 - (5 * 29f) + o.x, VirtH - 34 + o.y, 5 * 29f, 26);
                case HudConfig.Frame.CastBar:
                    return new Rect(((VirtW - 260f) / 2f) + o.x, VirtH - 150f + o.y, 260f, 22f);
                case HudConfig.Frame.Bags:
                {
                    const float Icon = 34f;
                    const int Cols = 8;
                    int cells = _client != null ? _client.InventoryCapacity : SimulationConstants.PlayerInventoryCapacity;
                    int rows = ((cells - 1) / Cols) + 1;
                    float w = 24f + (Cols * (Icon + 4f));
                    float h = 78f + (rows * (Icon + 4f)) + 24f; // +filter tab row
                    return new Rect(VirtW - w - 16 + o.x, VirtH - h - 52 + o.y, w, h);
                }

                default: return new Rect(0, 0, 100, 100);
            }
        }

        private void OnGUI()
        {
            GUI.matrix = Matrix4x4.Scale(new Vector3(_cfg.UiScale, _cfg.UiScale, 1f));
            _tooltip = null; // rebuilt each frame by whatever the mouse is over
            _tooltipCompare = null;
            _tooltipAnchor = null;

            if (!_connected || _client == null || !_client.EntityId.HasValue)
            {
                DrawAuthScreens();
                DrawTooltip();
                return;
            }

            if (_cfg.ShowHealthBars || _cfg.ShowNameplates) { DrawNameplates(); }
            DrawMinimap();
            DrawCorpsePrompt();
            DrawBankPrompt();
            DrawMerchantPrompt();
            DrawInnkeeperPrompt();
            DrawPlayerFrame();
            DrawPartyFrames();
            DrawTargetFrame();
            DrawInviteDialog();
            DrawXpBar();
            DrawActionBar();
            DrawMessages();
            DrawFloatingTexts();
            DrawUiError();
            DrawCastBar();
            DrawChat();
            DrawQuestTracker();
            DrawQuestWindow();
            DrawShopWindow();
            DrawLootWindow();
            DrawBankWindow();
            DrawBagsWindow();
            DrawCharacterSheet();
            DrawContextMenu();
            DrawSocialNotices();
            DrawTradeWindow();
            DrawMicroBar();
            DrawInspectWindow();
            DrawQuestLogWindow();
            DrawWorldMapWindow();
            DrawLogoutCountdown();
            DrawDragGhost();
            DrawVersionTag();
            if (_layoutEditMode) { DrawLayoutEditor(); }
            if (_menuOpen) { DrawEscapeMenu(); }

            SetWorldHoverTooltip(); // world entities only speak up when no UI element did
            DrawTooltip();
            DrawCursorIcon();
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
        /// <summary>Golden game title + version + the news panel — the WoW login chrome.</summary>
        private void DrawAuthChrome(bool withNews)
        {
            WowUi.Title(new Rect((VirtW / 2f) - 300, 34, 600, 40), "<size=34>AETHERIA</size>");
            WowUi.GoldCentered(new Rect((VirtW / 2f) - 300, 74, 600, 18),
                "<size=11><color=#c04040>H A R D C O R E</color></size>");

            GUI.Label(new Rect(16, VirtH - 24, 400, 18),
                "<size=10><color=#b0b0b0>Version " + SimulationConstants.GameVersion + " (proto " +
                SimulationConstants.ProtocolVersion + ")</color></size>", Rich());

            if (withNews)
            {
                Rect news = new Rect(24, 120, 300, 250);
                WowUi.Panel(news, "Dernières nouvelles");
                WowUi.Body(new Rect(news.x + 14, news.y + 44, news.width - 28, news.height - 56),
                    "Bienvenue en Aetheria !\n\n" +
                    "· Monde re-décoré : arbres, pierres levées et végétation.\n" +
                    "· Équipement complet façon WoW : 10 emplacements, sacs, butin fenêtré.\n" +
                    "· MODE HARDCORE : la mort est définitive. Dépose tes biens à la banque du sanctuaire…\n\n" +
                    "Bon courage, aventurier.");
            }
        }

        private void DrawErrorsAt(Rect r)
        {
            string error = _error;
            if (string.IsNullOrEmpty(error) && _client != null) { error = _client.LoginError; }
            if (!string.IsNullOrEmpty(error))
            {
                GUI.Label(r, "<color=#ff7070>" + error + "</color>", RichCentered());
            }
        }

        /// <summary>True exactly once when Enter (or keypad Enter) is pressed this GUI frame.
        /// MUST be called BEFORE the text fields are drawn — a focused field eats the key.</summary>
        private static bool EnterPressed()
        {
            Event e = Event.current;
            if (e != null && e.type == EventType.KeyDown &&
                (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n'))
            {
                e.Use();
                return true;
            }

            return false;
        }

        private void DrawLoginScreen()
        {
            bool enter = EnterPressed(); // BEFORE the fields: they'd swallow the key otherwise
            DrawAuthChrome(withNews: true);

            float cx = VirtW / 2f;
            float y = VirtH * 0.40f;

            WowUi.GoldCentered(new Rect(cx - 140, y, 280, 20), "Nom de compte");
            _account = WowUi.TextField(new Rect(cx - 140, y + 22, 280, 30), _account);

            WowUi.GoldCentered(new Rect(cx - 140, y + 62, 280, 20), "Mot de passe");
            _secret = WowUi.TextField(new Rect(cx - 140, y + 84, 280, 30), _secret, password: true);

            // Enter anywhere on this screen = the big button (WoW's login works the same way).
            if (WowUi.Button(new Rect(cx - 110, y + 132, 220, 38), "Se connecter") || enter)
            {
                Connect(LastServerFor(_account.Trim()), createAccount: false);
            }

            DrawErrorsAt(new Rect(cx - 240, y + 178, 480, 40));

            // The account column, bottom-left — like the launcher's stacked buttons.
            if (WowUi.Button(new Rect(24, VirtH - 148, 190, 30), "Créer un compte"))
            {
                _secret2 = "";
                _error = "";
                _lobbyScreen = LobbyScreen.Register;
            }

            if (WowUi.Button(new Rect(24, VirtH - 112, 190, 30), "Liste des serveurs"))
            {
                OpenServerBrowser();
            }

            if (WowUi.Button(new Rect(VirtW - 140, VirtH - 64, 116, 30), "Quitter"))
            {
                _cfg.Save();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
        }

        /// <summary>Screen 1b — its own registration modal: login + password + confirmation.</summary>
        private void DrawRegisterScreen()
        {
            bool enter = EnterPressed(); // BEFORE the fields: they'd swallow the key otherwise
            DrawAuthChrome(withNews: false);

            float cx = VirtW / 2f;
            Rect panel = new Rect(cx - 180, VirtH * 0.30f, 360, 330);
            WowUi.Panel(panel, "Créer un compte");

            float y = panel.y + 52;
            WowUi.GoldCentered(new Rect(cx - 140, y, 280, 20), "Nom de compte");
            _account = WowUi.TextField(new Rect(cx - 140, y + 22, 280, 30), _account);

            WowUi.GoldCentered(new Rect(cx - 140, y + 60, 280, 20), "Mot de passe");
            _secret = WowUi.TextField(new Rect(cx - 140, y + 82, 280, 30), _secret, password: true);

            WowUi.GoldCentered(new Rect(cx - 140, y + 120, 280, 20), "Confirmation");
            _secret2 = WowUi.TextField(new Rect(cx - 140, y + 142, 280, 30), _secret2, password: true);

            if (WowUi.Button(new Rect(cx - 120, y + 188, 240, 36), "Créer le compte") || enter)
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

            if (WowUi.Button(new Rect(cx - 60, y + 230, 120, 28), "Retour"))
            {
                _error = "";
                _lobbyScreen = LobbyScreen.Auth;
            }

            DrawErrorsAt(new Rect(cx - 240, panel.yMax + 8, 480, 40));
        }

        /// <summary>Screen 2 — YOUR character in 3D centre-stage, WoW character-select layout.</summary>
        private void DrawCharacterScreen()
        {
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

            // NO server/character list here: one character per server — seeing your character
            // MEANS you're on its server. Just the character, and the actions that matter.

            // BOTTOM CENTRE: name, class and level over the big enter button.
            WowUi.GoldCentered(new Rect((VirtW / 2f) - 220, VirtH - 152, 440, 26),
                "<size=17>" + _client.CharacterName + "</size>");
            // Just class, level, race — no resource type, no faction (the scene's banners wear
            // the faction colour), no server address.
            WowUi.GoldCentered(new Rect((VirtW / 2f) - 220, VirtH - 128, 440, 20),
                "<size=12><color=#c8c8c8>" + StripParen(cls) + " niveau " + _client.CharacterLevel +
                " — " + StripParen(race) + "</color></size>");
            // Enter = enter the world, same as the big button.
            if (!_confirmDeleteChar &&
                (WowUi.Button(new Rect((VirtW / 2f) - 150, VirtH - 100, 300, 42), "Entrer dans le monde") ||
                 EnterPressed()))
            {
                EnterExisting();
            }

            if (WowUi.Button(new Rect(24, VirtH - 64, 150, 30), "Déconnexion")) { Disconnect(); }
            if (WowUi.Button(new Rect(184, VirtH - 64, 150, 30), "Quitter le jeu"))
            {
                _cfg.Save();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }

            // BOTTOM RIGHT: switch to a server where you have no character yet, or free this one.
            if (WowUi.Button(new Rect(VirtW - 174, VirtH - 64, 150, 30), "Changer de serveur"))
            {
                Disconnect();
                OpenServerBrowser();
                return;
            }

            if (WowUi.Button(new Rect(VirtW - 174, VirtH - 104, 150, 30), "Supprimer le perso"))
            {
                _confirmDeleteChar = true;
            }

            if (_confirmDeleteChar)
            {
                DrawDeleteCharacterDialog();
            }

            DrawErrorsAt(new Rect((VirtW / 2f) - 240, VirtH - 188, 480, 30));
        }

        private static string RaceNameOf(byte raceId)
        {
            for (int i = 0; i < Races.Length; i++)
            {
                if (Races[i].id == raceId) { return StripParen(Races[i].label); }
            }

            return "";
        }

        private static string ClassNameOf(byte classId)
        {
            for (int i = 0; i < Classes.Length; i++)
            {
                if (Classes[i].id == classId) { return StripParen(Classes[i].label); }
            }

            return "";
        }

        /// <summary>"Warrior (Rage)" → "Warrior" — labels without their parenthetical detail.</summary>
        private static string StripParen(string label)
        {
            int at = label.IndexOf(" (", System.StringComparison.Ordinal);
            return at > 0 ? label.Substring(0, at) : label;
        }

        /// <summary>"Warrior (Rage)" → "Rage" — the parenthetical detail alone.</summary>
        private static string ParenPart(string label)
        {
            int open = label.IndexOf('(');
            int close = label.IndexOf(')');
            return open >= 0 && close > open ? label.Substring(open + 1, close - open - 1) : "";
        }

        /// <summary>HARDCORE-worthy confirmation: deletion is forever, say it plainly.</summary>
        private void DrawDeleteCharacterDialog()
        {
            Rect box = new Rect((VirtW / 2f) - 190, (VirtH / 2f) - 90, 380, 180);
            WowUi.Panel(box);
            WowUi.GoldCentered(new Rect(box.x, box.y + 14, box.width, 24),
                "<size=15>Supprimer " + _client.CharacterName + " ?</size>");
            WowUi.Body(new Rect(box.x + 24, box.y + 48, box.width - 48, 64),
                "Ce personnage, son équipement et sa progression seront effacés " +
                "<color=#ff6a5e>DÉFINITIVEMENT</color> de ce serveur. Son nom redevient libre.");

            if (WowUi.Button(new Rect(box.x + 28, box.y + box.height - 48, 150, 32), "Supprimer"))
            {
                _confirmDeleteChar = false;
                _client.SendDeleteCharacter();
            }

            if (WowUi.Button(new Rect(box.x + box.width - 178, box.y + box.height - 48, 150, 32), "Annuler"))
            {
                _confirmDeleteChar = false;
            }
        }

        /// <summary>ONE row per realm: among its routes, an ONLINE one wins (localhost for the
        /// host, public IP for friends), else a still-probing one, else the first.</summary>
        private List<ServerEntry> RealmRows()
        {
            var rows = new List<ServerEntry>();
            var bestByLabel = new Dictionary<string, int>();
            foreach (ServerEntry e in _servers)
            {
                string key = string.IsNullOrEmpty(e.Label) ? e.Address : e.Label;
                int at;
                if (!bestByLabel.TryGetValue(key, out at))
                {
                    bestByLabel[key] = rows.Count;
                    rows.Add(e);
                    continue;
                }

                ServerEntry current = rows[at];
                bool better = (e.HasInfo && !current.HasInfo) ||
                              (!e.Failed && current.Failed && !current.HasInfo);
                if (better) { rows[at] = e; }
            }

            return rows;
        }

        /// <summary>The server browser: NAMED realms, their population, and your character there.</summary>
        private int _realmSelected;

        /// <summary>WoW's Realm Selection: a real TABLE — name / type / characters / population —
        /// with the selected row highlighted green. Click selects, double-click joins.</summary>
        private void DrawServerBrowser()
        {
            List<ServerEntry> rows = RealmRows();
            if (_realmSelected >= rows.Count) { _realmSelected = 0; }

            float height = 118f + (rows.Count * 30f) + 96f;
            DrawTitledBox(out Rect box, Mathf.Min(height, VirtH * 0.85f), "Sélection du royaume", 640f);

            float x = box.x + 18;
            float w = box.width - 36;
            float y = box.y + 52;
            float cType = x + (w * 0.40f);
            float cChar = x + (w * 0.64f);
            float cPop = x + (w * 0.83f);

            // Column headers on a darker strip.
            Color prev = GUI.color;
            GUI.color = new Color(0.13f, 0.12f, 0.10f);
            GUI.DrawTexture(new Rect(x - 6, y, w + 12, 24), Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.Label(new Rect(x + 4, y + 3, 220, 18), "<b>Nom du royaume</b>", Rich());
            GUI.Label(new Rect(cType, y + 3, 140, 18), "<b>Type</b>", Rich());
            GUI.Label(new Rect(cChar, y + 3, 120, 18), "<b>Personnages</b>", Rich());
            GUI.Label(new Rect(cPop, y + 3, 100, 18), "<b>Population</b>", Rich());
            y += 28f;

            for (int i = 0; i < rows.Count; i++)
            {
                ServerEntry e = rows[i];
                var line = new Rect(x - 6, y, w + 12, 28);

                // Selected row: the green WoW highlight bar.
                if (i == _realmSelected)
                {
                    GUI.color = new Color(0.09f, 0.38f, 0.10f, 0.90f);
                    GUI.DrawTexture(line, Texture2D.whiteTexture);
                    GUI.color = prev;
                }

                string name = !string.IsNullOrEmpty(e.Label) ? e.Label
                    : e.HasInfo ? e.Info.Name : "?";
                string nameColor = e.Failed ? "#8a8a8a"
                    : e.HasInfo && e.Info.HasCharacter ? "#30e030"   // you live there → green
                    : "#ffd100";                                      // gold, like WoW
                GUI.Label(new Rect(x + 4, y + 5, cType - x - 10, 20),
                    "<size=13><color=" + nameColor + "><b>" + name + "</b></color></size>", Rich());

                string type = "<color=#30d040>Hardcore</color>" +
                    (name == "PTR" ? " <color=#a0a0a0>(test)</color>" : "");
                GUI.Label(new Rect(cType, y + 5, cChar - cType - 8, 20), type, Rich());

                string chars = e.HasInfo && e.Info.HasCharacter
                    ? e.Info.CharacterName + " <color=#a0a0a0>(" + e.Info.CharacterLevel + ")</color>"
                    : e.HasInfo && !e.Info.AcceptsNewCharacters ? "<color=#ff7070>complet</color>"
                    : "—";
                GUI.Label(new Rect(cChar, y + 5, cPop - cChar - 8, 20), "<size=11>" + chars + "</size>", Rich());

                string pop;
                if (e.Failed) { pop = "<color=#ff6060>Hors ligne</color>"; }
                else if (!e.HasInfo) { pop = "<color=#909090>…</color>"; }
                else
                {
                    float fill = e.Info.Capacity > 0 ? e.Info.Online / (float)e.Info.Capacity : 0f;
                    pop = fill >= 1f ? "<color=#ff5050>Complet</color>"
                        : fill >= 0.8f ? "<color=#ffa040>Élevée</color>"
                        : fill >= 0.3f ? "<color=#ffe060>Moyenne</color>"
                        : "<color=#30e030>Faible</color>";
                }

                GUI.Label(new Rect(cPop, y + 5, 100, 20), pop, Rich());

                // Click selects; double-click joins (WoW muscle memory).
                Event evt = Event.current;
                if (evt != null && evt.type == EventType.MouseDown && line.Contains(evt.mousePosition))
                {
                    _realmSelected = i;
                    if (evt.clickCount == 2)
                    {
                        TryJoinRealm(e);
                    }

                    evt.Use();
                }

                y += 30f;
            }

            float by = box.yMax - 44f;
            DrawErrorsAt(new Rect(box.x, by - 26, box.width, 22));
            if (WowUi.Button(new Rect(x, by, 110, 30), "Actualiser")) { RefreshServers(); }

            ServerEntry sel = rows.Count > 0 ? rows[Mathf.Clamp(_realmSelected, 0, rows.Count - 1)] : null;
            bool canJoin = sel != null && sel.HasInfo &&
                           (sel.Info.HasCharacter || sel.Info.AcceptsNewCharacters);
            GUI.enabled = canJoin;
            if (WowUi.Button(new Rect(x + w - 236, by, 112, 30), "Rejoindre") && canJoin)
            {
                TryJoinRealm(sel);
            }

            GUI.enabled = true;
            if (WowUi.Button(new Rect(x + w - 112, by, 112, 30), "Retour")) { _lobbyScreen = LobbyScreen.Auth; }
        }

        /// <summary>Join a realm from the browser (first visit auto-creates the account there).</summary>
        private void TryJoinRealm(ServerEntry e)
        {
            if (e != null && e.HasInfo && (e.Info.HasCharacter || e.Info.AcceptsNewCharacters))
            {
                Connect(e.Address, createAccount: !e.Info.HasAccount, fromBrowser: true);
            }
        }

        private void DrawWaitScreen(string message)
        {
            DrawAuthChrome(withNews: false);
            float cx = VirtW / 2f;
            Rect panel = new Rect(cx - 180, VirtH * 0.40f, 360, 130);
            WowUi.Panel(panel, message);
            DrawErrorsAt(new Rect(cx - 170, panel.y + 48, 340, 30));
            if (WowUi.Button(new Rect(cx - 60, panel.y + 86, 120, 28), "Annuler")) { Disconnect(); }
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

        // Index-aligned with Races: Human, Dwarf, Orc, Elf.
        private static readonly string[] RaceLore =
        {
            "Fiers et adaptables, les Humains de l'Alliance tiennent la ligne du sanctuaire depuis la première nuit.",
            "Courts sur pattes, durs comme la pierre : les Nains de l'Alliance ne reculent JAMAIS.",
            "Les Orcs de la Horde vivent pour l'honneur du combat ; leur fureur fait plier les armures.",
            "Les Elfes de la Horde sont vifs et précis — flèches et sorts trouvent toujours leur cible.",
        };

        // Index-aligned with Races: the racial ability, as a perk line.
        private static readonly string[] RacialLore =
        {
            "Second souffle : récupère de la vie dans un dernier sursaut.",
            "Forme de pierre : la peau devient roc, les coups glissent.",
            "Furie sanguinaire : la rage monte, les dégâts aussi.",
            "Célérité naturelle : le prochain sort part instantanément.",
        };

        private static readonly string[] ClassLore =
        {
            "Le Guerrier vit au corps à corps : la rage nourrit ses coups, l'acier fait le reste.",
            "Le Mage incante feu et givre à distance — fragile, mais dévastateur.",
            "Le Chasseur harcèle à l'arc et ne laisse aucune proie s'échapper.",
            "Le Druide épouse la nature : ours pour encaisser, hibou pour foudroyer à distance, tigre pour lacérer.",
            "Le Clerc châtie à distance et SOIGNE : le seul à pouvoir refermer les plaies des autres — dans les deux camps.",
        };

        private static readonly string[] FactionLore =
        {
            "L'Alliance rassemble les Humains et les Nains : la discipline, la pierre et la foi, dressées contre la nuit.",
            "La Horde unit les Orcs et les Elfes : la fureur et l'instinct, libres et indomptés.",
        };

        // Index-aligned with Classes: the item icon that represents each class.
        private static readonly byte[] ClassIconItem = { 2, 8, 7, 40, 20 }; // épée, bâton, arc, patte, fiole

        /// <summary>A rect-based "◀ valeur ▶" appearance row (WoW's arrow pickers).</summary>
        private int ArrowPicker(float x, ref float y, string label, int index, string[] options, Color? swatch)
        {
            WowUi.Body(new Rect(x, y, 96, 20), "<b>" + label + "</b>");
            if (WowUi.Button(new Rect(x + 96, y, 22, 20), "<")) { index = (index + options.Length - 1) % options.Length; }
            GUI.Label(new Rect(x + 122, y, 84, 20), "<size=10>" + options[index] + "</size>", RichCentered());
            if (swatch.HasValue)
            {
                Color prev = GUI.color;
                GUI.color = swatch.Value;
                GUI.DrawTexture(new Rect(x + 208, y + 3, 16, 14), Texture2D.whiteTexture);
                GUI.color = prev;
            }

            if (WowUi.Button(new Rect(x + 228, y, 22, 20), ">")) { index = (index + 1) % options.Length; }
            y += 26f;
            return index;
        }

        private void SelectRace(int index)
        {
            _raceIndex = index;
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

        /// <summary>An image button: portrait/icon, gold frame, selection glow. Returns clicked.</summary>
        private static bool IconButton(Rect r, Texture image, bool selected, bool enabled)
        {
            if (selected)
            {
                WowUi.Highlight(new Rect(r.x - 3, r.y - 3, r.width + 6, r.height + 6));
            }

            Color prev = GUI.color;
            GUI.color = new Color(0.75f, 0.60f, 0.22f); // gold frame
            GUI.DrawTexture(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), Texture2D.whiteTexture);
            GUI.color = enabled ? Color.white : new Color(0.35f, 0.35f, 0.35f);
            if (image != null)
            {
                GUI.DrawTexture(r, image);
            }

            GUI.color = prev;
            return enabled && GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        private void DrawCreationScreen()
        {
            Gender gender = _genderIndex == 1 ? Gender.Female : Gender.Male;

            // LEFT: factions, races, sex, class, appearance — the WoW creation column.
            Rect left = new Rect(20, 24, 288, VirtH - 140);
            WowUi.Panel(left);
            float x = left.x + 16;
            float y = left.y + 14;

            // Faction banners side by side, their race PORTRAITS below.
            Color prevColor = GUI.color;
            GUI.color = new Color(0.16f, 0.28f, 0.55f);
            GUI.DrawTexture(new Rect(x, y, 118, 22), Texture2D.whiteTexture);
            GUI.color = new Color(0.55f, 0.14f, 0.12f);
            GUI.DrawTexture(new Rect(x + 132, y, 118, 22), Texture2D.whiteTexture);
            GUI.color = prevColor;
            WowUi.GoldCentered(new Rect(x, y + 2, 118, 20), "Alliance");
            WowUi.GoldCentered(new Rect(x + 132, y + 2, 118, 20), "Horde");
            y += 30f;

            int[] allianceRaces = { 0, 1 }; // Humain, Nain
            int[] hordeRaces = { 2, 3 };    // Orc, Elfe
            for (int row = 0; row < 2; row++)
            {
                Rect a = new Rect(x + 27, y, 64, 64);
                if (IconButton(a, RacePortraits.Head(Races[allianceRaces[row]].id, gender),
                    _raceIndex == allianceRaces[row], true))
                {
                    SelectRace(allianceRaces[row]);
                }

                Rect h = new Rect(x + 132 + 27, y, 64, 64);
                if (IconButton(h, RacePortraits.Head(Races[hordeRaces[row]].id, gender),
                    _raceIndex == hordeRaces[row], true))
                {
                    SelectRace(hordeRaces[row]);
                }

                y += 74f;
            }

            byte raceId = Races[_raceIndex].id;

            // Sex: the selected race, full body, in both genders — click the silhouette.
            float gx = x + ((250f - 148f) / 2f);
            if (IconButton(new Rect(gx, y, 66, 100), RacePortraits.Body(raceId, Gender.Male),
                _genderIndex == 0, true) && _genderIndex != 0)
            {
                _genderIndex = 0;
                _hairStyle = 0;
            }

            if (IconButton(new Rect(gx + 82, y, 66, 100), RacePortraits.Body(raceId, Gender.Female),
                _genderIndex == 1, true) && _genderIndex != 1)
            {
                _genderIndex = 1;
                _hairStyle = 1;
            }

            y += 112f;

            // Classes as ICONS (sword, staff, bow, paw) — greyed when the race can't play them.
            float iconW = Mathf.Min(46f, (250f - ((Classes.Length - 1) * 6f)) / Classes.Length);
            float cx0 = x + ((250f - ((iconW * Classes.Length) + ((Classes.Length - 1) * 6f))) / 2f);
            for (int i = 0; i < Classes.Length; i++)
            {
                bool allowed = System.Array.IndexOf(Races[_raceIndex].classes, Classes[i].id) >= 0;
                Rect r = new Rect(cx0 + (i * (iconW + 6f)), y, iconW, iconW);
                if (IconButton(r, IconFor(ClassIconItem[i]), _classIndex == i, allowed) && allowed)
                {
                    _classIndex = i;
                }
            }

            y += 50f;
            WowUi.GoldCentered(new Rect(x, y, 250, 18),
                "<size=12>" + StripParen(Classes[_classIndex].label) + "</size>");
            y += 26f;

            WowUi.Gold(new Rect(x, y, 200, 20), "Apparence");
            y += 24f;
            _skinTone = ArrowPicker(x, ref y, "Teint", _skinTone, SkinLabels,
                CharacterModelBuilder.SkinColor(raceId, (byte)_skinTone));
            _faceIndex = ArrowPicker(x, ref y, "Visage", _faceIndex, FaceLabels, null);
            _hairStyle = ArrowPicker(x, ref y, "Coiffure", _hairStyle, HairStyleLabels, null);
            if (_hairStyle != 3) // pas de couleur pour un crâne chauve
            {
                _hairColor = ArrowPicker(x, ref y, "Cheveux", _hairColor, HairColorLabels,
                    CharacterModelBuilder.HairColors[_hairColor]);
            }

            bool beardAllowed = _genderIndex == 0 || raceId == 4; // hommes — et tous les Nains
            if (beardAllowed)
            {
                _beardStyle = ArrowPicker(x, ref y, "Barbe", _beardStyle, BeardStyleLabels, null);
                if (_beardStyle != 0)
                {
                    _beardColor = ArrowPicker(x, ref y, "Couleur barbe", _beardColor, HairColorLabels,
                        CharacterModelBuilder.HairColors[_beardColor]);
                }
            }

            y += 8f;
            if (WowUi.Button(new Rect(x + 45, y, 160, 26), "Aléatoire"))
            {
                _skinTone = Random.Range(0, SkinLabels.Length);
                _faceIndex = Random.Range(0, FaceLabels.Length);
                _hairStyle = Random.Range(0, HairStyleLabels.Length);
                _hairColor = Random.Range(0, HairColorLabels.Length);
                _beardStyle = beardAllowed ? Random.Range(0, BeardStyleLabels.Length) : 0;
                _beardColor = Random.Range(0, HairColorLabels.Length);
            }

            // RIGHT: faction, race and class panels, each headed by its icon — WoW-style.
            bool horde = _raceIndex >= 2;
            Rect factionPanel = new Rect(VirtW - 320, 24, 296, 108);
            WowUi.Panel(factionPanel);
            Color prev2 = GUI.color;
            GUI.color = horde ? new Color(0.55f, 0.14f, 0.12f) : new Color(0.16f, 0.28f, 0.55f);
            GUI.DrawTexture(new Rect(factionPanel.x + 14, factionPanel.y + 12, 34, 34), Texture2D.whiteTexture);
            GUI.color = prev2;
            WowUi.GoldCentered(new Rect(factionPanel.x + 14, factionPanel.y + 18, 34, 22),
                "<size=16><b>" + (horde ? "H" : "A") + "</b></size>");
            WowUi.Gold(new Rect(factionPanel.x + 58, factionPanel.y + 18, 220, 22),
                "<size=14>" + (horde ? "Horde" : "Alliance") + "</size>");
            WowUi.Body(new Rect(factionPanel.x + 14, factionPanel.y + 52, factionPanel.width - 28, 50),
                FactionLore[horde ? 1 : 0]);

            Rect racePanel = new Rect(VirtW - 320, 142, 296, 158);
            WowUi.Panel(racePanel);
            GUI.DrawTexture(new Rect(racePanel.x + 14, racePanel.y + 12, 40, 40),
                RacePortraits.Head(raceId, gender));
            WowUi.Gold(new Rect(racePanel.x + 64, racePanel.y + 22, 214, 22),
                "<size=14>" + StripParen(Races[_raceIndex].label) + "</size>");
            WowUi.Body(new Rect(racePanel.x + 14, racePanel.y + 58, racePanel.width - 28, 56),
                RaceLore[_raceIndex]);
            WowUi.Body(new Rect(racePanel.x + 14, racePanel.y + 112, racePanel.width - 28, 40),
                "<color=#e8c15a>Racial — " + RacialLore[_raceIndex] + "</color>");

            Rect classPanel = new Rect(VirtW - 320, 310, 296, 158);
            WowUi.Panel(classPanel);
            Texture classIcon = IconFor(ClassIconItem[_classIndex]);
            if (classIcon != null)
            {
                GUI.DrawTexture(new Rect(classPanel.x + 14, classPanel.y + 12, 40, 40), classIcon);
            }

            WowUi.Gold(new Rect(classPanel.x + 64, classPanel.y + 22, 214, 22),
                "<size=14>" + StripParen(Classes[_classIndex].label) + "</size>");
            WowUi.Body(new Rect(classPanel.x + 14, classPanel.y + 58, classPanel.width - 28, 56),
                ClassLore[_classIndex]);
            WowUi.Body(new Rect(classPanel.x + 14, classPanel.y + 112, classPanel.width - 28, 40),
                "<color=#e8c15a>Ressource — " + ParenPart(Classes[_classIndex].label) + "</color>");

            // BOTTOM CENTRE: name + accept, like WoW's Name / Accept row. The server's refusal
            // (name taken, etc.) shows ABOVE the name field, impossible to miss.
            float cx = VirtW / 2f;
            DrawErrorsAt(new Rect(cx - 300, VirtH - 168, 600, 26));
            WowUi.GoldCentered(new Rect(cx - 130, VirtH - 138, 260, 20), "Nom");
            _name = WowUi.TextField(new Rect(cx - 130, VirtH - 116, 260, 30), _name);
            WowUi.Body(new Rect(cx - 220, VirtH - 82, 440, 18),
                "<i>Tu apparaîtras dans le sanctuaire — une zone sans PvP ni monstres.</i>");

            if (WowUi.Button(new Rect(VirtW - 268, VirtH - 64, 118, 32), "Accepter")) { CreateAndEnter(); }
            if (WowUi.Button(new Rect(VirtW - 142, VirtH - 64, 118, 32), "Retour"))
            {
                Disconnect();
                OpenServerBrowser();
            }
        }

        // --- Frames ---

        private void DrawPlayerFrame()
        {
            Rect frame = FrameRect(HudConfig.Frame.PlayerFrame);
            EntitySnapshot self;
            bool haveSelf = _client.TryGetSelf(out self);

            WowUi.Panel(frame);

            // WoW-style: round class-coloured portrait on the LEFT, name + bars beside it.
            Rect portrait = new Rect(frame.x + 6, frame.y + 8, 52, 52);
            WowUi.Portrait(portrait, ResourceColor() * 0.85f,
                _name.Length > 0 ? _name.Substring(0, 1).ToUpperInvariant() : "?");
            WowUi.GoldCentered(new Rect(portrait.x, portrait.yMax - 4, portrait.width, 16),
                "<size=10>" + _client.Level + "</size>");

            float bx = portrait.xMax + 6;
            float bw = frame.xMax - bx - 8;
            WowUi.Gold(new Rect(bx, frame.y + 4, bw, 18), _name);

            float hpFill = haveSelf && self.MaxHealth > 0 ? self.Health / (float)self.MaxHealth : 0f;
            DrawBar(new Rect(bx, frame.y + 26, bw, 16), hpFill,
                new Color(0.20f, 0.75f, 0.25f), haveSelf ? self.Health + " / " + self.MaxHealth : "—");

            float resFill = haveSelf && self.MaxResource > 0 ? self.Resource / (float)self.MaxResource : 0f;
            DrawBar(new Rect(bx, frame.y + 46, bw, 14), resFill,
                ResourceColor(), haveSelf ? self.Resource + " / " + self.MaxResource : "—");

            bool inSanctuary = false;
            if (haveSelf && !_client.InInstance)
            {
                float dx = self.Position.X - SimulationConstants.SafeZoneCenterX;
                float dy = self.Position.Y - SimulationConstants.SafeZoneCenterY;
                inSanctuary = (dx * dx) + (dy * dy)
                    <= SimulationConstants.SafeZoneRadius * SimulationConstants.SafeZoneRadius;
            }

            // Under the frame: STATUS only — no gold here (the bags carry the money bar).
            // Safe zone = a single icon (hover it for the meaning), like WoW's rest icon.
            string status = (_client.InInstance ? "   [INSTANCE]" : "") +
                (_client.PartySize > 0 ? "   Groupe " + _client.PartySize : "");
            if (inSanctuary)
            {
                var safe = new Rect(frame.x + 8, frame.y + 64, 20, 20);
                GUI.Label(safe, "<size=15><color=#70d0ff>★</color></size>", Rich());
                if (safe.Contains(Event.current.mousePosition))
                {
                    _tooltip = "<b><color=#70d0ff>Zone protégée</color></b>\n<color=#a0a0a0>Aucune attaque possible ici.</color>";
                }
            }

            if (status.Length > 0)
            {
                GUI.Label(new Rect(frame.x + 30, frame.y + 64, frame.width - 38, 26), status.TrimStart(), Rich());
            }
        }

        private Color ResourceColor()
        {
            return ResourceColorFor(_classId);
        }

        private static Color ResourceColorFor(byte classId)
        {
            switch (classId)
            {
                case 1: return new Color(0.85f, 0.20f, 0.20f);  // rage
                case 2: return new Color(0.25f, 0.45f, 0.95f);  // mana
                default: return new Color(0.95f, 0.85f, 0.25f); // energy
            }
        }

        // ------------------------------------------------------- Party frames

        private int _partyMenuFor = -1; // entity id whose frame menu is open
        private Vector2 _partyMenuPos;

        /// <summary>French label for a timed effect type (party frame buff icons).</summary>
        private static string EffectLabel(byte type)
        {
            switch (type)
            {
                case 3: return "Attaque augmentée";
                case 4: return "Défense augmentée";
                case 5: return "Vitesse augmentée";
                case 6: return "Régénération";
                default: return "Effet";
            }
        }

        private static string EffectLetter(byte type)
        {
            switch (type)
            {
                case 3: return "A";
                case 4: return "D";
                case 5: return "V";
                case 6: return "R";
                default: return "?";
            }
        }

        /// <summary>WoW party frames: one rectangle per member (never yourself) — live HP and
        /// resource, buffs with their time left, right-click for actions. Movable in layout mode.</summary>
        private void DrawPartyFrames()
        {
            IReadOnlyList<PartyMemberInfo> members = _client.PartyMembers;
            if (members == null || members.Count <= 1) { return; }

            Rect area = FrameRect(HudConfig.Frame.PartyFrames);
            int selfId = _client.EntityId ?? -1;
            float y = area.y;
            foreach (PartyMemberInfo m in members)
            {
                if (m.EntityId == selfId) { continue; } // your own frame covers you

                var frame = new Rect(area.x, y, area.width, 58);
                WowUi.Panel(frame);

                bool isLeader = m.Name == _client.PartyLeader;
                WowUi.Gold(new Rect(frame.x + 8, frame.y + 3, frame.width - 16, 16),
                    "<size=11>" + (isLeader ? "★ " : "") + m.Name +
                    "  <color=#909090><size=9>niv." + m.Level + "</size></color></size>");

                float hpFill = m.MaxHealth > 0 ? m.Health / (float)m.MaxHealth : 0f;
                DrawBar(new Rect(frame.x + 8, frame.y + 21, frame.width - 16, 12), hpFill,
                    new Color(0.20f, 0.75f, 0.25f), m.Health + " / " + m.MaxHealth);
                float resFill = m.MaxResource > 0 ? m.Resource / (float)m.MaxResource : 0f;
                DrawBar(new Rect(frame.x + 8, frame.y + 35, frame.width - 16, 8), resFill,
                    ResourceColorFor(m.ClassId), "");

                // Buff icons with remaining time on hover.
                for (int b = 0; b < m.Effects.Length && b < 8; b++)
                {
                    var cell = new Rect(frame.x + 8 + (b * 15f), frame.y + 45, 13, 13);
                    WowUi.Slot(cell);
                    GUI.Label(cell, "<size=8><color=#80d0ff>" + EffectLetter(m.Effects[b].Type) + "</color></size>",
                        RichCentered());
                    if (cell.Contains(Event.current.mousePosition))
                    {
                        _tooltip = "<b>" + EffectLabel(m.Effects[b].Type) + "</b>\n<color=#a0a0a0>" +
                            Mathf.CeilToInt(m.Effects[b].Seconds) + " s restantes</color>";
                    }
                }

                // Clicks on the frame: LEFT = target them, RIGHT = the actions menu.
                Event e = Event.current;
                if (e.type == EventType.MouseDown && frame.Contains(e.mousePosition))
                {
                    if (e.button == 0)
                    {
                        _targetId = m.EntityId; // friendly selection: no attack intent
                        _targetHostile = false;
                        _client.SendAttackTarget(0);
                    }
                    else if (e.button == 1)
                    {
                        _partyMenuFor = m.EntityId;
                        _partyMenuPos = new Vector2(frame.xMax + 4, frame.y);
                    }

                    e.Use();
                }

                y += 64f;
            }

            DrawPartyFrameMenu();
        }

        private void DrawPartyFrameMenu()
        {
            if (_partyMenuFor < 0) { return; }

            PartyMemberInfo member = null;
            foreach (PartyMemberInfo m in _client.PartyMembers)
            {
                if (m.EntityId == _partyMenuFor) { member = m; }
            }

            if (member == null) { _partyMenuFor = -1; return; }

            bool iAmLeader = _client.PartyLeader == _name;
            float h = 12 + 26 + 26 + (iAmLeader ? 26 : 0) + 26;
            var win = new Rect(_partyMenuPos.x, _partyMenuPos.y, 170, h);
            WowUi.Panel(win);
            float y = win.y + 6;

            if (GUI.Button(new Rect(win.x + 8, y, win.width - 16, 22), "Cibler"))
            {
                _targetId = member.EntityId;
                _targetHostile = false;
                _client.SendAttackTarget(0);
                _partyMenuFor = -1;
            }

            y += 26;
            if (GUI.Button(new Rect(win.x + 8, y, win.width - 16, 22), "Inspecter"))
            {
                _client.SendInspect(member.EntityId);
                _partyMenuFor = -1;
            }

            y += 26;
            if (iAmLeader)
            {
                if (GUI.Button(new Rect(win.x + 8, y, win.width - 16, 22), "Expulser du groupe"))
                {
                    _client.SendPartyKick(member.EntityId);
                    _partyMenuFor = -1;
                }

                y += 26;
            }

            if (GUI.Button(new Rect(win.x + 8, y, win.width - 16, 22), "Fermer"))
            {
                _partyMenuFor = -1;
            }

            // Click anywhere else: the menu folds.
            Event e = Event.current;
            if (e.type == EventType.MouseDown && !win.Contains(e.mousePosition))
            {
                _partyMenuFor = -1;
            }
        }

        /// <summary>WoW-style invite dialog: Accepter / Refuser, front and centre.</summary>
        private void DrawInviteDialog()
        {
            if (string.IsNullOrEmpty(_client.PendingInviteFrom)) { return; }

            var win = new Rect((VirtW / 2f) - 150, VirtH * 0.24f, 300, 100);
            WowUi.Panel(win);
            WowUi.GoldCentered(new Rect(win.x, win.y + 12, win.width, 20),
                "<b>" + _client.PendingInviteFrom + "</b> t'invite dans un groupe");

            if (WowUi.Button(new Rect(win.x + 18, win.y + 52, 126, 32), "Accepter"))
            {
                _client.SendPartyRespond(true);
                _client.ClearPendingInvite();
            }

            if (WowUi.Button(new Rect(win.x + win.width - 144, win.y + 52, 126, 32), "Refuser"))
            {
                _client.SendPartyRespond(false);
                _client.ClearPendingInvite();
            }
        }

        private void DrawTargetFrame()
        {
            if (_targetId < 0) { return; }

            EntitySnapshot target;
            if (!_client.TryGetEntity(_targetId, out target)) { return; }

            Rect frame = FrameRect(HudConfig.Frame.TargetFrame);
            WowUi.Panel(frame);
            string name = string.IsNullOrEmpty(target.Name) ? target.Kind.ToString() : target.Name;

            // Mirrored WoW target frame: bars left, round portrait on the RIGHT.
            Rect portrait = new Rect(frame.xMax - 58, frame.y + 5, 52, 52);
            WowUi.Portrait(portrait, new Color(0.62f, 0.16f, 0.14f),
                name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?");
            WowUi.GoldCentered(new Rect(portrait.x, portrait.yMax - 4, portrait.width, 16),
                "<size=10>" + target.Level + "</size>");

            float bw = portrait.x - frame.x - 14;
            GUI.Label(new Rect(frame.x + 8, frame.y + 4, bw, 20),
                "<b><color=#ffd100>" + name + "</color></b>" +
                (target.Kind == EntityKind.Player ? "  <size=10>" + target.Faction + "</size>" : ""), Rich());
            DrawBar(new Rect(frame.x + 8, frame.y + 28, bw, 16),
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

        /// <summary>Your current shapeshift form (0 humanoid) — from your own snapshot.</summary>
        private byte SelfForm()
        {
            EntitySnapshot self;
            return _client != null && _client.TryGetSelf(out self) ? self.FormId : (byte)0;
        }

        /// <summary>The class basic attack — for the druid it follows the FORM (Wrath/Maul/Shred).</summary>
        private byte CurrentBasicAbility()
        {
            if (_classId != 4)
            {
                return _classId; // classes 1-3: ability id == class id
            }

            switch (SelfForm())
            {
                case 1: return 31;  // bear: Maul
                case 3: return 32;  // cat: Shred
                default: return 30; // humanoid & owl: Wrath
            }
        }

        private static readonly (byte form, string label)[] DruidForms =
        {
            (1, "Ours"), (2, "Hibou"), (3, "Tigre"),
        };

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
                (KeyLabel(HudConfig.Bind.Attack1), CurrentBasicAbility()),
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

                // OUT OF RANGE (WoW): the button dims red while the target is too far.
                bool outOfRange = false;
                EntitySnapshot rangeSelf;
                EntitySnapshot rangeTarget;
                if (def.Range > 0f && _targetId >= 0 &&
                    _client.TryGetSelf(out rangeSelf) && _client.TryGetEntity(_targetId, out rangeTarget))
                {
                    outOfRange = Vec2.DistanceSquared(rangeSelf.Position, rangeTarget.Position)
                        > def.Range * def.Range;
                }

                WowUi.Slot(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4)); // WoW slot trim
                if (outOfRange)
                {
                    Color prevRange = GUI.color;
                    GUI.color = new Color(0.75f, 0.15f, 0.12f, 0.45f);
                    GUI.DrawTexture(r, Texture2D.whiteTexture);
                    GUI.color = prevRange;
                }

                GUI.enabled = usable;
                if (GUI.Button(r, ""))
                {
                    if (abilityId == _racialId) { TryUseRacial(); }
                    else if (def.Range <= 0f) { TryCastSelf(abilityId); }
                    else { TryCastOnTarget(abilityId); }
                }

                GUI.enabled = true;

                if (r.Contains(Event.current.mousePosition))
                {
                    _tooltip = AbilityTooltip(def); // WoW spell tooltip at the screen edge
                }

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

            // HEARTHSTONE: the last slot of the bar — teleports home, 15-minute cooldown.
            {
                int hearthIndex = slots.Count + (_classId == 4 ? DruidForms.Length : 0);
                Rect hr = new Rect(bar.x + (hearthIndex * (slot + Pad)) + 20f, bar.y, slot, slot);
                WowUi.Slot(new Rect(hr.x - 2, hr.y - 2, hr.width + 4, hr.height + 4));
                float hearthCd = _hearthReadyTime - Time.time;

                EntitySnapshot hearthSelf;
                bool channelling = _client.TryGetSelf(out hearthSelf) &&
                                   hearthSelf.CastAbilityId == SimulationConstants.HearthstoneCastId;
                if (channelling && hearthSelf.CastProgress >= 245)
                {
                    _hearthReadyTime = Time.time + (15f * 60f); // the channel is completing: cooldown starts
                }

                if (GUI.Button(hr, "") && hearthCd <= 0f && !channelling)
                {
                    _client.SendHearthstone(); // starts the 5-second channel (cast bar shows)
                }

                GUI.Label(new Rect(hr.x + 3, hr.y + 2, hr.width - 6, 16), "<size=10>Foyer</size>", Rich());
                GUI.Label(new Rect(hr.x + (hr.width / 2f) - 10, hr.y + (hr.height / 2f) - 10, 20, 20),
                    "<size=14>⌂</size>", RichCentered());
                if (hearthCd > 0f)
                {
                    Dim(hr, 0.7f);
                    GUI.Label(new Rect(hr.x, hr.y + (hr.height / 2f) - 9, hr.width, 18),
                        "<size=10>" + Mathf.CeilToInt(hearthCd / 60f) + " min</size>", RichCentered());
                }

                if (hr.Contains(Event.current.mousePosition))
                {
                    _tooltip = "<b><color=#ffffff>Pierre de foyer</color></b>\n<color=#a0a0a0>" +
                        "Canalisation de 5 s — bouger ou être touché l'interrompt." +
                        "\nTe ramène à ton auberge (demande à un aubergiste pour en changer)." +
                        "\nRecharge : 15 minutes.</color>";
                }
            }

            // DRUID: three shapeshift slots after the abilities — keys 4/5/6 toggle the form.
            if (_classId == 4)
            {
                byte current = SelfForm();
                for (int f = 0; f < DruidForms.Length; f++)
                {
                    (byte form, string label) = DruidForms[f];
                    Rect r = new Rect(bar.x + ((slots.Count + f) * (slot + Pad)) + 10f, bar.y, slot, slot);
                    WowUi.Slot(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4));
                    if (current == form) { WowUi.Highlight(r); } // active form glows

                    if (GUI.Button(r, ""))
                    {
                        _client.SendShapeShift(current == form ? (byte)0 : form);
                    }

                    GUI.Label(new Rect(r.x + 3, r.y + 2, r.width - 6, 16),
                        "<size=10>" + label + "</size>", Rich());
                    GUI.Label(new Rect(r.x + 3, r.y + r.height - 18, 30, 16),
                        "<b>" + (4 + f) + "</b>", Rich());

                    if (r.Contains(Event.current.mousePosition))
                    {
                        _tooltip = "<b><color=#ffffff>Forme : " + label + "</color></b>\n<color=#a0a0a0>" +
                            (form == 1 ? "Tank : +60% défense, +30% PV — attaque Maul."
                             : form == 2 ? "Sorts à distance renforcés (+25%) — incante Wrath."
                             : "Mêlée féroce : +25% attaque — attaque Shred.") +
                            "\nRé-appuie pour reprendre forme humaine.</color>";
                    }
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

                // Nothing over YOUR OWN head (WoW): your unit frame already says it all.
                bool isSelf = _client.EntityId.HasValue && view.EntityId == _client.EntityId.Value;
                if (isSelf) { continue; }

                Vector3 screen = cam.WorldToScreenPoint(view.transform.position + (Vector3.up * (view.HeadHeight + 0.5f)));
                if (screen.z < 0f) { continue; }

                float x = screen.x / _cfg.UiScale;
                float y = (Screen.height - screen.y) / _cfg.UiScale;

                if (_cfg.ShowNameplates)
                {
                    // Monsters: YELLOW while passive, RED once aggressive (WoW colour language).
                    bool hostile = (view.Kind == EntityKind.Monster && view.Aggro) ||
                                   (view.Kind == EntityKind.Player && view.Faction != myFaction);
                    string colour = hostile ? "#ff6060" :
                        view.Kind == EntityKind.Monster ? "#ffd100" :
                        view.Kind == EntityKind.Npc ? "#f0d060" : "#60a0ff";

                    // NAME ONLY — level, stats and gear are for right-click → Inspecter.
                    string label = "<color=" + colour + "><size=11>" + view.DisplayName + "</size></color>";
                    GUI.Label(new Rect(x - 80, y - 18, 160, 16), label, RichCentered());
                }

                // QUEST MARKERS over the quest giver: « ! » = a quest awaits, « ? » = turn-in
                // ready (grey « ? » while the hunt is still under way), WoW-style.
                if (view.Kind == EntityKind.Npc && view.RaceId == 2)
                {
                    string marker = QuestGiverMarker();
                    if (marker.Length > 0)
                    {
                        GUI.Label(new Rect(x - 30, y - 52, 60, 34), marker, RichCentered());
                    }
                }

                // Health bars: players always (option) — monsters ONLY once aggressive.
                if (_cfg.ShowHealthBars && view.Kind != EntityKind.Npc &&
                    (view.Kind != EntityKind.Monster || view.Aggro))
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

        // --- Contextual mouse cursor (WoW): sword on attackables, purse on merchants,
        // --- anvil on the smith. 0 none, 1 sword, 2 purse, 3 anvil.
        private int _cursorIcon;
        private static readonly Texture2D[] CursorIcons = new Texture2D[4];

        private static Texture2D CursorIconTex(int kind)
        {
            if (CursorIcons[kind] != null)
            {
                return CursorIcons[kind];
            }

            const int S = 24;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var clear = new Color(0, 0, 0, 0);
            for (int y = 0; y < S; y++) { for (int x = 0; x < S; x++) { tex.SetPixel(x, y, clear); } }

            void Px(int x, int y, Color c)
            {
                if (x >= 0 && x < S && y >= 0 && y < S) { tex.SetPixel(x, S - 1 - y, c); }
            }

            var steel = new Color(0.85f, 0.88f, 0.95f);
            var gold = new Color(0.95f, 0.78f, 0.20f);
            var brown = new Color(0.55f, 0.38f, 0.18f);
            var dark = new Color(0.12f, 0.12f, 0.14f);

            if (kind == 1)
            {
                // SWORD: diagonal blade, gold guard, dark grip.
                for (int i = 0; i < 12; i++)
                {
                    Px(6 + i, 4 + i, steel); Px(7 + i, 4 + i, steel); Px(6 + i, 5 + i, steel);
                }

                Px(6, 3, steel); Px(7, 3, steel); // tip
                for (int i = -2; i <= 3; i++) { Px(15 + i, 17 - i, gold); } // guard
                for (int i = 0; i < 4; i++) { Px(18 + i, 18 + i, brown); Px(19 + i, 18 + i, brown); } // grip
            }
            else if (kind == 2)
            {
                // PURSE: a pouch with a gold tie.
                for (int y = 9; y < 20; y++)
                {
                    int half = y < 12 ? y - 7 : (y > 17 ? 20 - y + 4 : 6);
                    for (int x = 12 - half; x <= 12 + half; x++) { Px(x, y, brown); }
                }

                for (int x = 9; x <= 15; x++) { Px(x, 8, gold); }
                Px(11, 6, gold); Px(12, 5, gold); Px(13, 6, gold); // tied neck
                Px(11, 13, gold); Px(12, 14, gold); Px(13, 13, gold); // coin glint
            }
            else if (kind == 3)
            {
                // ANVIL: the classic silhouette.
                for (int x = 4; x <= 20; x++) { Px(x, 10, dark); Px(x, 11, dark); Px(x, 12, dark); }
                for (int x = 2; x <= 7; x++) { Px(x, 9, dark); } // horn
                for (int x = 9; x <= 15; x++) { Px(x, 13, dark); Px(x, 14, dark); }
                for (int x = 8; x <= 16; x++) { Px(x, 17, dark); Px(x, 18, dark); } // base
                for (int x = 10; x <= 14; x++) { Px(x, 15, dark); Px(x, 16, dark); }
                for (int x = 5; x <= 19; x++) { Px(x, 9, new Color(0.35f, 0.36f, 0.42f)); } // top shine
            }

            tex.Apply();
            tex.filterMode = FilterMode.Point;
            CursorIcons[kind] = tex;
            return tex;
        }

        /// <summary>The little WoW cursor companion, drawn beside the OS pointer.</summary>
        private void DrawCursorIcon()
        {
            if (_cursorIcon == 0 || _draggingItem != null)
            {
                return;
            }

            Vector2 m = GuiMouse();
            GUI.DrawTexture(new Rect(m.x + 12, m.y + 8, 22, 22), CursorIconTex(_cursorIcon));
        }

        /// <summary>The quest giver's overhead marker: "!", gold "?", grey "?", or nothing.</summary>
        private string QuestGiverMarker()
        {
            if (_client.QuestCatalog == null)
            {
                return "";
            }

            if (_client.ActiveQuestId != 0)
            {
                foreach (Aetheria.Shared.Data.QuestDefinition q in _client.QuestCatalog)
                {
                    if (q.Id == _client.ActiveQuestId)
                    {
                        return _client.QuestKills >= q.RequiredKills
                            ? "<size=26><b><color=#ffd100>?</color></b></size>"   // ready to turn in
                            : "<size=22><b><color=#9a9a9a>?</color></b></size>"; // hunt in progress
                    }
                }

                return "";
            }

            // No active quest: is there a NEXT one in the chain to offer?
            foreach (Aetheria.Shared.Data.QuestDefinition q in _client.QuestCatalog)
            {
                if (q.Id > _client.QuestCompletedUpTo)
                {
                    return "<size=26><b><color=#ffd100>!</color></b></size>";
                }
            }

            return "";
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

        private void DrawMerchantPrompt()
        {
            if (_nearbyMerchantId < 0 || _shopOpen || _nearbyCorpseId >= 0) { return; }

            EntityView view;
            if (!_views.TryGetValue(_nearbyMerchantId, out view) || view == null || Camera.main == null) { return; }

            Vector3 screen = Camera.main.WorldToScreenPoint(
                view.transform.position + (Vector3.up * (view.HeadHeight + 0.35f)));
            if (screen.z < 0f) { return; }

            Rect r = new Rect((screen.x / _cfg.UiScale) - 100,
                ((Screen.height - screen.y) / _cfg.UiScale) - 14, 200, 24);
            GUI.Box(r, "[" + KeyLabel(HudConfig.Bind.Interact) + "] Commercer");
        }

        private void DrawInnkeeperPrompt()
        {
            if (_nearbyInnkeeperId < 0 || _nearbyCorpseId >= 0 || _nearbyMerchantId >= 0) { return; }

            EntityView view;
            if (!_views.TryGetValue(_nearbyInnkeeperId, out view) || view == null || Camera.main == null) { return; }

            Vector3 screen = Camera.main.WorldToScreenPoint(
                view.transform.position + (Vector3.up * (view.HeadHeight + 0.35f)));
            if (screen.z < 0f) { return; }

            Rect r = new Rect((screen.x / _cfg.UiScale) - 130,
                ((Screen.height - screen.y) / _cfg.UiScale) - 14, 260, 24);
            GUI.Box(r, "[" + KeyLabel(HudConfig.Bind.Interact) + "] Faire de cette auberge ton foyer");
        }

        /// <summary>An item tile: its ICON (generated PNG), stack count, and hover name.</summary>
        private void DrawItemIcon(Rect rect, byte itemId, int quantity)
        {
            ItemDefinition def = Data.GetItem(itemId);

            if (itemId == 0) { WowUi.Slot(rect); return; } // a hole is just an empty socket

            Texture2D icon = IconFor(itemId);
            WowUi.Slot(rect);
            if (icon != null)
            {
                GUI.DrawTexture(new Rect(rect.x + 1, rect.y + 1, rect.width - 2, rect.height - 2),
                    icon, ScaleMode.ScaleToFit);
            }
            else
            {
                // Fallback: the old coloured tile with an abbreviation.
                Color prev = GUI.color;
                GUI.color = ItemColor(def);
                GUI.DrawTexture(new Rect(rect.x + 2, rect.y + 2, rect.width - 4, rect.height - 4),
                    Texture2D.whiteTexture);
                GUI.color = prev;
                string abbrev = def.Name.Length <= 2 ? def.Name : def.Name.Substring(0, 2);
                GUI.Label(new Rect(rect.x, rect.y + 2, rect.width, 16),
                    "<size=11><b><color=#101010>" + abbrev + "</color></b></size>", RichCentered());
            }

            if (quantity > 1)
            {
                GUI.Label(new Rect(rect.x, rect.y + rect.height - 16, rect.width - 3, 14),
                    "<size=10><b>" + quantity + "</b></size>",
                    new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.LowerRight });
            }

            if (rect.Contains(Event.current.mousePosition))
            {
                _tooltip = ItemTooltip(def, quantity);
                _tooltipAnchor = rect; // the panel opens right beside this cell, WoW-style
            }
        }

        // ------------------------------------------------- Tooltips (WoW-style)

        /// <summary>The tooltip to show this frame (bottom-right corner), or null.</summary>
        private string _tooltip;

        /// <summary>The "Équipé actuellement" panel shown beside the tooltip, or null.</summary>
        private string _tooltipCompare;

        /// <summary>The hovered item's cell: when set, the tooltip opens NEXT TO it (WoW-style)
        /// instead of at the screen edge — bags, loot and character sheet alike.</summary>
        private Rect? _tooltipAnchor;

        private string ItemTooltip(ItemDefinition def, int quantity)
        {
            string text = ItemTooltipCore(def, quantity);

            // WoW comparison — HOLD SHIFT: the equipped piece appears beside the hovered one,
            // plus the stat changes a swap would cause (green = gain, red = loss).
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift && def.IsEquippable && _client != null)
            {
                IReadOnlyList<byte> gear = _client.EquipmentSlots;
                byte equippedId = gear != null && (int)def.Slot < gear.Count ? gear[(int)def.Slot] : (byte)0;
                if (equippedId != 0) // même le MÊME objet : WoW montre les deux fiches quand même
                {
                    ItemDefinition worn = Data.GetItem(equippedId);
                    _tooltipCompare = "<b><color=#20d848>Équipé actuellement</color></b>\n"
                        + ItemTooltipCore(worn, 1);

                    var sb = new System.Text.StringBuilder(text);
                    sb.Append("\n<color=#ffd100>Si tu remplaces cet objet :</color>");
                    bool any = false;
                    any |= AppendStatDelta(sb, def.AttackBonus - worn.AttackBonus, "Attaque");
                    any |= AppendStatDelta(sb, def.DefenseBonus - worn.DefenseBonus, "Défense");
                    any |= AppendStatDelta(sb, def.HealthBonus - worn.HealthBonus, "PV");
                    if (!any) { sb.Append("\n<color=#a0a0a0>Aucun changement de stats</color>"); }
                    text = sb.ToString();
                }
            }
            else if (!shift && def.IsEquippable && _client != null)
            {
                IReadOnlyList<byte> gear = _client.EquipmentSlots;
                byte worn = gear != null && (int)def.Slot < gear.Count ? gear[(int)def.Slot] : (byte)0;
                if (worn != 0)
                {
                    text += "\n<size=9><color=#808080>Maj : comparer avec l'objet équipé</color></size>";
                }
            }

            return text;
        }

        /// <summary>One WoW-style stat-change line: green when it's a gain, red when it's a loss.</summary>
        private static bool AppendStatDelta(System.Text.StringBuilder sb, int delta, string stat)
        {
            if (delta == 0) { return false; }
            sb.Append(delta > 0 ? "\n<color=#20ff20>+" : "\n<color=#ff4040>")
              .Append(delta).Append(' ').Append(stat).Append("</color>");
            return true;
        }

        /// <summary>WoW item-quality colour, from the same power scale as the Quest Studio.</summary>
        private static string QualityHex(ItemDefinition def)
        {
            if (def.Type == ItemType.Material) { return "#9d9d9d"; }        // junk grey
            int power = (def.AttackBonus * 2) + def.DefenseBonus + (def.HealthBonus / 5);
            if (power >= 16) { return "#a335ee"; }                           // epic
            if (power >= 9) { return "#0070dd"; }                            // rare
            if (power >= 3) { return "#1eff00"; }                            // uncommon
            return "#ffffff";                                                // common
        }

        /// <summary>The item card itself (name, slot, stats, value) — shared by both panels.</summary>
        private string ItemTooltipCore(ItemDefinition def, int quantity)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<b><color=").Append(QualityHex(def)).Append('>')
              .Append(def.Name).Append("</color></b>");
            if (quantity > 1) { sb.Append(" ×").Append(quantity); }

            sb.Append('\n');
            if (def.IsEquippable)
            {
                sb.Append("<color=#a0a0a0>").Append(SlotLabel(def.Slot)).Append(" · ")
                  .Append(ItemTypeLabel(def.Type)).Append("</color>");
            }
            else
            {
                sb.Append("<color=#a0a0a0>").Append(ItemTypeLabel(def.Type)).Append("</color>");
            }

            if (def.AttackBonus > 0) { sb.Append("\n<color=#ffffff>+").Append(def.AttackBonus).Append(" Attaque</color>"); }
            if (def.DefenseBonus > 0) { sb.Append("\n<color=#ffffff>+").Append(def.DefenseBonus).Append(" Défense</color>"); }
            if (def.HealthBonus > 0) { sb.Append("\n<color=#20ff20>+").Append(def.HealthBonus).Append(" PV</color>"); }
            if (def.Type == ItemType.Consumable && def.ConsumeEffect != EffectType.None)
            {
                string use = def.ConsumeEffect == EffectType.Heal
                    ? "rend " + Mathf.RoundToInt(def.ConsumeMagnitude * 100f) + "% de la vie"
                    : def.ConsumeEffect == EffectType.RestoreResource
                    ? "rend " + Mathf.RoundToInt(def.ConsumeMagnitude * 100f) + "% de la ressource"
                    : "régénère vie et mana pendant " +
                      Mathf.RoundToInt(def.ConsumeDurationTicks * SimulationConstants.TickDelta) +
                      " s (hors combat)";
                sb.Append("\n<color=#20ff20>Clic droit : ").Append(use).Append("</color>");
            }

            if (def.Stackable && def.MaxStack > 1) { sb.Append("\n<color=#a0a0a0>Se cumule par ").Append(def.MaxStack).Append("</color>"); }
            // « Prix de vente » = what the merchant ACTUALLY pays (WoW semantics) — computed by
            // the same shared formula as the server, so the credited money always matches.
            if (def.GoldValue > 0)
            {
                sb.Append("\nPrix de vente : ")
                  .Append(FormatMoney(SimulationConstants.VendorSellPrice(def.GoldValue)));

                // At a merchant, a stack shows what the WHOLE pile brings (right-click sells it).
                if (_shopOpen && quantity > 1)
                {
                    sb.Append("  <color=#a0a0a0>· la pile : ")
                      .Append(FormatMoney(SimulationConstants.VendorSellPrice(def.GoldValue) * quantity))
                      .Append("</color>");
                }
            }

            return sb.ToString();
        }

        private static string ItemTypeLabel(ItemType type)
        {
            switch (type)
            {
                case ItemType.Weapon: return "Arme";
                case ItemType.Armor: return "Armure";
                case ItemType.Consumable: return "Consommable";
                default: return "Butin";
            }
        }

        private string AbilityTooltip(AbilityDefinition def)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<b><color=#ffffff>").Append(def.Name).Append("</color></b>");

            if (def.CastTimeTicks > 0)
            {
                sb.Append("\n<color=#a0a0a0>Incantation : ")
                  .Append((def.CastTimeTicks * SimulationConstants.TickDelta).ToString("0.#"))
                  .Append("s</color>");
            }
            else
            {
                sb.Append("\n<color=#a0a0a0>Instantané</color>");
            }

            if (def.BaseDamage > 0) { sb.Append("\nDégâts : ").Append(def.BaseDamage); }
            if (def.ResourceCost > 0) { sb.Append("\nCoût : ").Append(def.ResourceCost); }
            if (def.Range > 0f) { sb.Append("\nPortée : ").Append(def.Range.ToString("0.#")).Append(" m"); }

            float cd = def.CooldownTicks * SimulationConstants.TickDelta;
            if (cd > 1.6f) { sb.Append("\nRecharge : ").Append(cd.ToString("0.#")).Append("s"); }
            else { sb.Append("\nVitesse d'attaque : ").Append(cd.ToString("0.#")).Append("s"); }

            return sb.ToString();
        }

        /// <summary>Hovering something in the WORLD (monster, player, corpse) — when no UI tooltip won.</summary>
        private void SetWorldHoverTooltip()
        {
            if (_tooltip != null || _menuOpen || _chatInputActive) { return; }

            _cursorIcon = 0;
            EntitySnapshot? hovered = PickEntityUnderMouse(_ => true);
            if (hovered == null) { return; }

            EntitySnapshot e = hovered.Value;
            EntitySnapshot selfSnap;
            Faction myFaction = _client.TryGetSelf(out selfSnap) ? selfSnap.Faction : Faction.Neutral;

            // WoW's hover card: NAME, title, level, allegiance — facts only, never instructions.
            var sb = new System.Text.StringBuilder();
            switch (e.Kind)
            {
                case EntityKind.Monster:
                    _cursorIcon = 1; // sword: attackable
                    sb.Append("<b><color=#ffd0a0>").Append(e.Name).Append("</color></b>");
                    sb.Append("\n<color=#ffffff>Niveau ").Append(e.Level).Append("</color>");
                    sb.Append("\nPV ").Append(e.Health).Append('/').Append(e.MaxHealth);

                    Aetheria.Shared.Data.QuestDefinition? hunted = ActiveQuestFor(e.RaceId);
                    if (hunted != null)
                    {
                        sb.Append("\n<color=#ffd100>").Append(hunted.Name)
                          .Append(" : ").Append(Mathf.Min(_client.QuestKills, hunted.RequiredKills))
                          .Append('/').Append(hunted.RequiredKills).Append("</color>");
                    }

                    break;

                case EntityKind.Player when e.Id != (_client.EntityId ?? -1):
                    if (e.Faction != myFaction) { _cursorIcon = 1; } // sword: enemy player
                    string pColor = e.Faction == myFaction ? "#a0c8ff" : "#ff6060";
                    sb.Append("<b><color=").Append(pColor).Append('>').Append(e.Name).Append("</color></b>");
                    sb.Append("\n<color=#ffffff>Niveau ").Append(e.Level).Append(' ')
                      .Append(RaceNameOf(e.RaceId)).Append(' ').Append(ClassNameOf(e.ClassId)).Append("</color>");
                    sb.Append('\n').Append(e.Faction == Faction.Horde
                        ? "<color=#ff6a5e>Horde</color>" : "<color=#4a7bd0>Alliance</color>");
                    break;

                case EntityKind.Corpse:
                case EntityKind.MonsterCorpse:
                    sb.Append("<b><color=#c0c0c0>").Append(string.IsNullOrEmpty(e.Name) ? "Dépouille" : e.Name)
                      .Append("</color></b>");
                    break;

                case EntityKind.Npc:
                    _cursorIcon = e.RaceId switch { 4 => 2, 3 => 3, _ => 0 }; // purse / anvil
                    sb.Append("<b><color=#40d040>").Append(e.Name).Append("</color></b>");
                    string title = e.RaceId switch
                    {
                        1 => "Banque",
                        4 => "Marchande de fournitures",
                        5 => "Portail d'instance",
                        6 => "Pierre de rencontre",
                        7 => "Aubergiste",
                        3 => "Artisan",
                        _ => "",
                    };
                    if (title.Length > 0) { sb.Append("\n<color=#ffffff>").Append(title).Append("</color>"); }
                    if (e.RaceId is 2 or 3 or 4 or 7)
                    {
                        sb.Append("\n<color=#ffffff>Niveau ").Append(e.Level).Append("</color>");
                    }

                    sb.Append('\n').Append("<color=#a0a0a0>").Append(ZoneName(e)).Append("</color>");
                    break;

                default:
                    return;
            }

            _tooltip = sb.ToString();
        }

        /// <summary>The ACTIVE quest that hunts this monster def id, or null.</summary>
        private Aetheria.Shared.Data.QuestDefinition? ActiveQuestFor(byte monsterDefId)
        {
            if (_client.QuestCatalog == null || _client.ActiveQuestId == 0)
            {
                return null;
            }

            foreach (Aetheria.Shared.Data.QuestDefinition q in _client.QuestCatalog)
            {
                if (q.Id == _client.ActiveQuestId && q.TargetMonsterId == monsterDefId &&
                    _client.QuestKills < q.RequiredKills)
                {
                    return q;
                }
            }

            return null;
        }

        /// <summary>The WoW-style tooltip panel, anchored at the bottom-right edge of the screen —
        /// plus the "Équipé actuellement" comparison panel to its left when relevant.</summary>
        private void DrawTooltip()
        {
            if (string.IsNullOrEmpty(_tooltip)) { return; }

            const float W = 240f;
            float h = TooltipHeight(_tooltip);

            Rect win;
            bool openLeft = false;
            if (_tooltipAnchor.HasValue)
            {
                // NEXT TO the hovered cell (WoW): to its right, or to its left near the edge.
                Rect a = _tooltipAnchor.Value;
                float x = a.xMax + 10f;
                if (x + W > VirtW - 8f)
                {
                    x = a.x - W - 10f;
                    openLeft = true;
                }

                float y = Mathf.Clamp(a.y, 8f, VirtH - h - 8f);
                win = new Rect(x, y, W, h);
            }
            else
            {
                // No anchor (spells, world hover): bottom-right, ABOVE the micro-bar.
                win = new Rect(VirtW - W - 12, VirtH - h - 72, W, h);
                openLeft = true;
            }

            Dim(win, 0.82f);
            GUI.Label(new Rect(win.x + 9, win.y + 7, win.width - 18, win.height - 12), _tooltip, Rich());

            if (!string.IsNullOrEmpty(_tooltipCompare))
            {
                // The "Équipé actuellement" panel continues in the same direction.
                float h2 = TooltipHeight(_tooltipCompare);
                float x2 = openLeft ? win.x - W - 8f : win.xMax + 8f;
                if (x2 < 8f) { x2 = win.xMax + 8f; }
                if (x2 + W > VirtW - 8f) { x2 = win.x - W - 8f; }
                float y2 = Mathf.Clamp(win.y, 8f, VirtH - h2 - 8f);
                Rect win2 = new Rect(x2, y2, W, h2);
                Dim(win2, 0.82f);
                GUI.Label(new Rect(win2.x + 9, win2.y + 7, win2.width - 18, win2.height - 12),
                    _tooltipCompare, Rich());
            }
        }

        private static float TooltipHeight(string text)
        {
            int lines = 1;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') { lines++; }
            }

            return (lines * 17f) + 16f;
        }

        private static readonly Dictionary<byte, Texture2D> IconCache = new Dictionary<byte, Texture2D>();

        private static Texture2D IconFor(byte itemId)
        {
            Texture2D cached;
            if (!IconCache.TryGetValue(itemId, out cached))
            {
                cached = Resources.Load<Texture2D>("Icons/item_" + itemId);
                IconCache[itemId] = cached;
            }

            return cached;
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
                    if (stacks[i].ItemId == 0) { continue; } // layout hole
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
                if (items[i].ItemId == 0) // layout hole: an empty socket, nothing clickable
                {
                    WowUi.Slot(cell);
                    continue;
                }

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

        // ---------------------------------------------------------------- Quests

        /// <summary>The quest giver's dialogue: offer, progress, or turn-in — WoW-style panel.</summary>
        private void DrawQuestWindow()
        {
            if (!_questWindowOpen) { return; }

            Rect win = new Rect((VirtW / 2f) - 230, (VirtH / 2f) - 170, 460, 320);
            WowUi.Panel(win, "Aldric le Guetteur");
            if (GUI.Button(new Rect(win.xMax - 26, win.y + 6, 20, 20), "X")) { _questWindowOpen = false; return; }

            float y = win.y + 48;
            byte active = _client.ActiveQuestId;

            if (active != 0)
            {
                QuestDefinition q = Data.GetQuest(active);
                if (q == null) { _questWindowOpen = false; return; }

                WowUi.Gold(new Rect(win.x + 20, y, win.width - 40, 22), "<size=14>" + q.Name + "</size>");
                y += 30;

                if (_client.QuestKills >= q.RequiredKills)
                {
                    WowUi.Body(new Rect(win.x + 20, y, win.width - 40, 90), q.TurnInText);
                    y += 96;
                    WowUi.Body(new Rect(win.x + 20, y, win.width - 40, 40),
                        "<b>Récompenses :</b> " + QuestRewardText(q));
                    if (WowUi.Button(new Rect(win.x + (win.width / 2f) - 90, win.yMax - 46, 180, 32), "Rendre la quête"))
                    {
                        _client.SendQuestAction(active, turnIn: true);
                    }
                }
                else
                {
                    WowUi.Body(new Rect(win.x + 20, y, win.width - 40, 70),
                        "Reviens me voir quand ce sera fait, aventurier.");
                    y += 60;
                    WowUi.Gold(new Rect(win.x + 20, y, win.width - 40, 22),
                        "Progression : " + _client.QuestKills + " / " + q.RequiredKills);
                }
            }
            else
            {
                byte next = (byte)(_client.QuestCompletedUpTo + 1);
                QuestDefinition q = Data.GetQuest(next);
                if (q != null)
                {
                    WowUi.Gold(new Rect(win.x + 20, y, win.width - 40, 22), "<size=14>" + q.Name + "</size>");
                    y += 30;
                    WowUi.Body(new Rect(win.x + 20, y, win.width - 40, 110), q.Description);
                    y += 116;
                    WowUi.Body(new Rect(win.x + 20, y, win.width - 40, 40),
                        "<b>Récompenses :</b> " + QuestRewardText(q));
                    if (WowUi.Button(new Rect(win.x + (win.width / 2f) - 80, win.yMax - 46, 160, 32), "Accepter"))
                    {
                        _client.SendQuestAction(next, turnIn: false);
                    }
                }
                else
                {
                    WowUi.Body(new Rect(win.x + 20, y, win.width - 40, 110),
                        "Le sanctuaire est en paix, grâce à toi. Repose-toi, héros — " +
                        "d'autres dangers viendront bien assez tôt.");
                }
            }
        }

        private string QuestRewardText(QuestDefinition q)
        {
            string text = "+" + q.RewardXp + " XP";
            if (q.RewardGold > 0) { text += " · " + FormatMoney(q.RewardGold); }
            if (q.RewardItemId != 0) { text += " · " + Data.GetItem(q.RewardItemId).Name; }
            return text;
        }

        /// <summary>The on-screen objective tracker under the minimap, WoW-style.</summary>
        /// <summary>The merchant's shop: buy from the stock; sell by right-clicking bag items.</summary>
        private void DrawShopWindow()
        {
            if (!_shopOpen) { return; }

            byte[] stock = SimulationConstants.VendorStock;
            float height = 78f + (stock.Length * 30f) + 40f;
            Rect win = new Rect(20, (VirtH / 2f) - (height / 2f), 330, height);
            WowUi.Panel(win);
            WowUi.GoldCentered(new Rect(win.x, win.y + 7, win.width, 18), "<b>Mira la Marchande</b>");

            if (GUI.Button(new Rect(win.x + win.width - 26, win.y + 5, 21, 21), "X"))
            {
                _shopOpen = false;
                return;
            }

            float y = win.y + 32f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 18),
                "<size=10><color=#c0c0c0>« Regarde ma marchandise, voyageur ! »</color></size>", Rich());
            y += 22f;

            for (int i = 0; i < stock.Length; i++)
            {
                ItemDefinition def = Data.GetItem(stock[i]);
                var iconRect = new Rect(win.x + 12, y, 26, 26);
                DrawItemIcon(iconRect, stock[i], 1);
                GUI.Label(new Rect(win.x + 44, y + 3, 150, 20),
                    "<color=" + QualityHex(def) + ">" + def.Name + "</color>", Rich());
                GUI.Label(new Rect(win.x + 186, y + 3, 70, 20), FormatMoney(def.GoldValue),
                    new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.MiddleRight });
                if (GUI.Button(new Rect(win.x + win.width - 66, y + 1, 54, 24), "Acheter"))
                {
                    _client.SendVendor(sell: false, stock[i], 1);
                }

                y += 30f;
            }

            GUI.Label(new Rect(win.x + 12, win.yMax - 30, win.width - 24, 24),
                "<size=10><color=#909090>Clic droit sur un objet de TES SACS : le vendre (au quart de sa valeur).</color></size>",
                Rich());
        }

        private void DrawQuestTracker()
        {
            byte active = _client.ActiveQuestId;
            if (active == 0) { return; }

            QuestDefinition q = Data.GetQuest(active);
            if (q == null) { return; }

            Rect r = FrameRect(HudConfig.Frame.QuestTracker);
            WowUi.Gold(new Rect(r.x, r.y, r.width, 18), "Objectifs");
            WowUi.Body(new Rect(r.x, r.y + 20, r.width, 20), "<b>" + q.Name + "</b>");
            bool done = _client.QuestKills >= q.RequiredKills;
            WowUi.Body(new Rect(r.x, r.y + 40, r.width, 20), done
                ? "<color=#70e070>Terminé — retourne voir Aldric</color>"
                : "Tués : " + _client.QuestKills + " / " + q.RequiredKills);

            // LEFT-CLICK the tracked quest: the world map opens and the hunting zone BLINKS.
            var clickZone = new Rect(r.x, r.y + 18, r.width, 44);
            Event evt = Event.current;
            if (evt.type == EventType.MouseDown && evt.button == 0 && clickZone.Contains(evt.mousePosition) &&
                !_layoutEditMode)
            {
                _trackedQuestId = active;
                _worldMapOpen = true;
                _mapBlinkUntil = Time.time + 4f;
                evt.Use();
            }
        }

        /// <summary>The loot window's rect (also used to keep clicks there off the camera).</summary>
        private Rect LootWindowRect()
        {
            int rows = _client.OpenCorpseItems.Count + (_client.OpenCorpseGold > 0 ? 1 : 0);
            float height = 74f + (rows * 26f) + 34f;
            return new Rect((VirtW / 2f) - 140, (VirtH / 2f) - (height / 2f), 280, height);
        }

        private void DrawLootWindow()
        {
            if (_client.OpenCorpseId < 0) { return; }

            Rect win = LootWindowRect();
            WowUi.Panel(win); // opaque, framed
            WowUi.GoldCentered(new Rect(win.x, win.y + 7, win.width, 18), "<b>Butin</b>");

            if (GUI.Button(new Rect(win.x + win.width - 26, win.y + 5, 21, 21), "X"))
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
                ItemDefinition def = Data.GetItem(stack.ItemId);

                // Real icon (with its hover tooltip) + quality-coloured name, like a WoW loot row.
                DrawItemIcon(new Rect(win.x + 10, y, 22, 22), stack.ItemId, stack.Quantity);
                string label = "<color=" + QualityHex(def) + ">" + def.Name + "</color>" +
                               (stack.Quantity > 1 ? " ×" + stack.Quantity : "");
                GUI.Label(new Rect(win.x + 38, y + 2, 134, 20), label, Rich());
                if (GUI.Button(new Rect(win.x + win.width - 86, y, 74, 22), "Prendre"))
                {
                    _client.SendLootItem(_client.OpenCorpseId, stack.ItemId);
                }

                y += 26f;
            }

            if (GUI.Button(new Rect(win.x + 12, y + 6, win.width - 24, 24),
                "Tout prendre [" + KeyLabel(HudConfig.Bind.Interact) + "]"))
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

            // WoW layout: the 3D portrait in the middle, five slots down each side,
            // stats + money in the footer. The bags live in their own window (B).
            const float Slot = 40f;
            Rect win = SheetWindowRect();
            float W = win.width;

            // OPAQUE WoW-style framed panel — no more see-through window.
            WowUi.Panel(win);
            WowUi.GoldCentered(new Rect(win.x, win.y + 8, win.width, 20),
                "<size=13><b>" + _name + "</b> — niveau " + _client.Level + "</size>");

            if (GUI.Button(new Rect(win.x + win.width - 26, win.y + 5, 21, 21), "X"))
            {
                _sheetOpen = false;
                return;
            }

            // CENTRE: the live 3D portrait between the two slot columns.
            Rect portrait = new Rect(win.x + 12 + Slot + 8, win.y + 30, W - 24 - ((Slot + 8) * 2), (Slot * 5) + 16);
            WowUi.Slot(portrait);
            if (_sheetPreview.Texture != null)
            {
                GUI.DrawTexture(new Rect(portrait.x + 2, portrait.y + 2, portrait.width - 4, portrait.height - 4),
                    _sheetPreview.Texture, ScaleMode.ScaleAndCrop);
            }

            // No automatic spin: RIGHT-drag over the portrait turns the character.
            Event pe = Event.current;
            if (pe.type == EventType.MouseDrag && pe.button == 1 && portrait.Contains(pe.mousePosition))
            {
                _sheetPreview.Yaw += pe.delta.x * 0.8f;
                pe.Use();
            }

            // Portrait zoom: − / + buttons in the corner (and the scroll wheel over the portrait).
            if (GUI.Button(new Rect(portrait.xMax - 46, portrait.yMax - 24, 20, 20), "−"))
            {
                _sheetPreview.Zoom -= 0.2f;
            }

            if (GUI.Button(new Rect(portrait.xMax - 24, portrait.yMax - 24, 20, 20), "+"))
            {
                _sheetPreview.Zoom += 0.2f;
            }

            if (pe.type == EventType.ScrollWheel && portrait.Contains(pe.mousePosition))
            {
                _sheetPreview.Zoom -= pe.delta.y * 0.05f;
                pe.Use();
            }

            // SIDES: five equipment slots per column, WoW order. Click a piece to unequip it.
            IReadOnlyList<byte> gear = _client.EquipmentSlots;
            EquipSlot[] left = { EquipSlot.Head, EquipSlot.Shoulders, EquipSlot.Back, EquipSlot.Chest, EquipSlot.Hands };
            EquipSlot[] right = { EquipSlot.Waist, EquipSlot.Legs, EquipSlot.Feet, EquipSlot.Weapon, EquipSlot.OffHand };
            for (int i = 0; i < left.Length; i++)
            {
                float sy = win.y + 34f + (i * (Slot + 4f));
                DrawEquipSlot(new Rect(win.x + 12, sy, Slot, Slot),
                    gear[(int)left[i]], left[i], SlotLabel(left[i]));
                DrawEquipSlot(new Rect(win.xMax - 12 - Slot, sy, Slot, Slot),
                    gear[(int)right[i]], right[i], SlotLabel(right[i]));
            }

            // FOOTER, WoW-style: two stat blocks — attributes on the left, combat on the right.
            // (No XP, no current HP, no gold here: the XP bar, unit frame and bags show those.)
            float y = win.y + 34f + (5 * (Slot + 4f)) + 6f;
            float half = (W - 30f) / 2f;
            float blockH = win.yMax - y - 12f; // fill the sheet to its bottom edge — no dead space
            Rect blockL = new Rect(win.x + 12, y, half, blockH);
            Rect blockR = new Rect(win.x + 18 + half, y, half, blockH);
            WowUi.Highlight(blockL);
            WowUi.Highlight(blockR);

            AbilityDefinition basic = Data.GetAbility(_classId); // the class's basic strike
            int dmg = basic.BaseDamage + _client.EffectiveAttack;

            float rowH = (blockH - 30f) / 3f; // rows breathe to fill the block

            GUI.Label(new Rect(blockL.x + 8, blockL.y + 5, half - 16, 18),
                "<b><color=#ffd100>Attributs</color></b>", Rich());
            GUI.Label(new Rect(blockL.x + 8, blockL.y + 26, half - 16, 18),
                StatLine("Endurance", haveSelf ? self.MaxHealth.ToString() : "—"), Rich());
            GUI.Label(new Rect(blockL.x + 8, blockL.y + 26 + rowH, half - 16, 18),
                StatLine("Force", _client.EffectiveAttack.ToString()), Rich());
            GUI.Label(new Rect(blockL.x + 8, blockL.y + 26 + (rowH * 2), half - 16, 18),
                StatLine("Armure", _client.EffectiveDefense.ToString()), Rich());

            GUI.Label(new Rect(blockR.x + 8, blockR.y + 5, half - 16, 18),
                "<b><color=#ffd100>Combat</color></b>", Rich());
            GUI.Label(new Rect(blockR.x + 8, blockR.y + 26, half - 16, 18),
                StatLine("Dégâts", dmg.ToString()), Rich());
            GUI.Label(new Rect(blockR.x + 8, blockR.y + 26 + rowH, half - 16, 18),
                StatLine("Puissance", _client.EffectiveAttack.ToString()), Rich());
            GUI.Label(new Rect(blockR.x + 8, blockR.y + 26 + (rowH * 2), half - 16, 18),
                StatLine("Défense", _client.EffectiveDefense.ToString()), Rich());
        }

        /// <summary>The character sheet's window rect (movable via Options → Déplacer l'interface).</summary>
        private Rect SheetWindowRect() => FrameRect(HudConfig.Frame.CharSheet);

        /// <summary>A WoW stat row: grey label on the left, white value on the right.</summary>
        private static string StatLine(string label, string value)
            => "<color=#c8c8c8>" + label + " :</color> <b><color=#ffffff>" + value + "</color></b>";

        /// <summary>The mouse position in virtual GUI coordinates (same space as OnGUI rects).</summary>
        private Vector2 GuiMouse()
        {
            return new Vector2(Input.mousePosition.x / _cfg.UiScale,
                (Screen.height - Input.mousePosition.y) / _cfg.UiScale);
        }

        /// <summary>French display name for an equipment slot (the WoW sheet labels).</summary>
        private static string SlotLabel(EquipSlot slot)
        {
            switch (slot)
            {
                case EquipSlot.Head: return "Tête";
                case EquipSlot.Shoulders: return "Épaules";
                case EquipSlot.Back: return "Dos";
                case EquipSlot.Chest: return "Torse";
                case EquipSlot.Hands: return "Mains";
                case EquipSlot.Waist: return "Ceinture";
                case EquipSlot.Legs: return "Jambes";
                case EquipSlot.Feet: return "Pieds";
                case EquipSlot.Weapon: return "Arme";
                case EquipSlot.OffHand: return "Main G.";
                default: return "";
            }
        }

        /// <summary>The soft disc texture used for quest-zone overlays on the maps.</summary>
        private static Texture2D ZoneDisc()
        {
            if (_zoneDisc == null)
            {
                const int S = 64;
                _zoneDisc = new Texture2D(S, S, TextureFormat.RGBA32, false);
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float dx = (x - (S / 2f)) / (S / 2f);
                        float dy = (y - (S / 2f)) / (S / 2f);
                        float d = Mathf.Sqrt((dx * dx) + (dy * dy));
                        float edge = Mathf.Abs(d - 0.88f) < 0.10f ? 0.55f : 0f; // the ring
                        float fill = d < 0.88f ? 0.16f : 0f;                    // the wash
                        _zoneDisc.SetPixel(x, y, new Color(1f, 0.82f, 0.2f, Mathf.Max(edge, fill)));
                    }
                }

                _zoneDisc.Apply();
            }

            return _zoneDisc;
        }

        /// <summary>The quest whose zone the maps highlight: the tracked one, else the active one.</summary>
        private Aetheria.Shared.Data.QuestDefinition? HighlightedQuest()
        {
            if (_client.QuestCatalog == null)
            {
                return null;
            }

            byte want = _trackedQuestId != 0 ? _trackedQuestId : _client.ActiveQuestId;
            if (want == 0)
            {
                return null;
            }

            foreach (Aetheria.Shared.Data.QuestDefinition q in _client.QuestCatalog)
            {
                if (q.Id == want && q.ZoneRadius > 0f)
                {
                    return q;
                }
            }

            return null;
        }

        /// <summary>L — the QUEST LOG: the chain with states, click a quest to track its zone.</summary>
        private void DrawQuestLogWindow()
        {
            if (!_questLogOpen || _client.QuestCatalog == null)
            {
                return;
            }

            var win = new Rect((VirtW / 2f) - 420, (VirtH / 2f) - 240, 380, 480);
            WowUi.Panel(win);
            WowUi.GoldCentered(new Rect(win.x, win.y + 8, win.width, 20), "<b>Carnet de quêtes</b>");
            if (GUI.Button(new Rect(win.xMax - 26, win.y + 5, 21, 21), "X")) { _questLogOpen = false; return; }

            // TABS: the finished quests live in their own tab — the first shows only the hunt.
            if (_questLogTab == 0) { WowUi.Highlight(new Rect(win.x + 12, win.y + 30, 110, 24)); }
            if (WowUi.Button(new Rect(win.x + 12, win.y + 30, 110, 24), "Actives")) { _questLogTab = 0; }
            if (_questLogTab == 1) { WowUi.Highlight(new Rect(win.x + 130, win.y + 30, 110, 24)); }
            if (WowUi.Button(new Rect(win.x + 130, win.y + 30, 110, 24), "Terminées")) { _questLogTab = 1; }

            float y = win.y + 62;
            foreach (Aetheria.Shared.Data.QuestDefinition q in _client.QuestCatalog)
            {
                bool done = q.Id <= _client.QuestCompletedUpTo;
                bool active = q.Id == _client.ActiveQuestId;
                bool available = !done && !active && QuestIsNext(q.Id);
                if (_questLogTab == 0 ? done : !done)
                {
                    continue; // wrong tab
                }

                if (!done && !active && !available)
                {
                    continue; // still locked behind the chain: keep the log clean
                }

                var row = new Rect(win.x + 10, y, win.width - 20, 40);
                if (_trackedQuestId == q.Id) { WowUi.Highlight(row); }

                string state = done ? "<color=#30d040>✔</color>"
                    : active ? (_client.QuestKills >= q.RequiredKills
                        ? "<color=#ffd100>?</color>" : "<color=#ffd100>●</color>")
                    : "<color=#ffd100>!</color>";
                string progress = active
                    ? "  <color=#c8c8c8>" + Mathf.Min(_client.QuestKills, q.RequiredKills) + " / " + q.RequiredKills + "</color>"
                    : "";
                GUI.Label(new Rect(row.x + 6, row.y + 2, row.width - 12, 18),
                    state + " <b>" + q.Name + "</b>" + progress, Rich());
                GUI.Label(new Rect(row.x + 20, row.y + 20, row.width - 26, 16),
                    "<size=10><color=#909090>" + (q.ZoneRadius > 0f
                        ? "Clique pour voir la zone de chasse sur la carte"
                        : "") + "</color></size>", Rich());

                Event evt = Event.current;
                if (evt.type == EventType.MouseDown && row.Contains(evt.mousePosition))
                {
                    _trackedQuestId = _trackedQuestId == q.Id ? (byte)0 : q.Id;
                    evt.Use();
                }

                y += 44f;
                if (y > win.yMax - 60) { break; }
            }

            Aetheria.Shared.Data.QuestDefinition? sel = HighlightedQuest();
            if (sel != null)
            {
                WowUi.Body(new Rect(win.x + 14, win.yMax - 58, win.width - 28, 48),
                    "<size=10><i>" + sel.Description + "</i></size>");
            }
        }

        /// <summary>Is this quest the NEXT offer of the chain (available at the quest giver)?</summary>
        private bool QuestIsNext(byte questId)
        {
            if (_client.ActiveQuestId != 0 || _client.QuestCatalog == null)
            {
                return false;
            }

            // The chain's next quest: the first one beyond CompletedUpTo.
            foreach (Aetheria.Shared.Data.QuestDefinition q in _client.QuestCatalog)
            {
                if (q.Id > _client.QuestCompletedUpTo)
                {
                    return q.Id == questId;
                }
            }

            return false;
        }

        /// <summary>A soft-edged filled disc, tinted with GUI.color at draw time.</summary>
        private static Texture2D _solidDisc;

        private static Texture2D SolidDisc()
        {
            if (_solidDisc == null)
            {
                const int S = 64;
                _solidDisc = new Texture2D(S, S, TextureFormat.RGBA32, false);
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float dx = (x - (S / 2f)) / (S / 2f);
                        float dy = (y - (S / 2f)) / (S / 2f);
                        float d = Mathf.Sqrt((dx * dx) + (dy * dy));
                        float a = Mathf.Clamp01((0.97f - d) * 10f); // soft edge
                        _solidDisc.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }

                _solidDisc.Apply();
            }

            return _solidDisc;
        }

        /// <summary>A tinted disc + centred label — one named zone patch on the parchment.</summary>
        private static void MapZone(Rect map, float wx, float wy, float worldRadius, Color color, string label)
        {
            Vector2 c = WorldMapView.ToMap(map, wx, wy);
            float r = worldRadius / WorldMapView.Extent * (map.width / 2f);
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(c.x - r, c.y - r, r * 2f, r * 2f), SolidDisc(), ScaleMode.StretchToFill);
            GUI.color = prev;
            if (label.Length > 0)
            {
                GUI.Label(new Rect(c.x - 90, c.y - 9, 180, 18),
                    "<size=11><color=#4a3a20><b>" + label + "</b></color></size>", RichCentered());
            }
        }

        /// <summary>
        /// M — the WORLD MAP, WoW-style: a STYLISED parchment chart (not a camera shot — those
        /// drown in the day/night lighting). Named zones, portals, quest markers, and a big gold
        /// arrow for the player, rotated to the facing.
        /// </summary>
        private void DrawWorldMapWindow()
        {
            if (!_worldMapOpen)
            {
                return;
            }

            if (_client.InInstance)
            {
                var small = new Rect((VirtW / 2f) - 160, (VirtH / 2f) - 50, 320, 100);
                WowUi.Panel(small, "Carte du monde");
                WowUi.Body(new Rect(small.x + 16, small.y + 44, small.width - 32, 40),
                    "Pas de carte à l'intérieur d'une instance.");
                return;
            }

            float size = Mathf.Min(VirtH * 0.82f, 620f);
            var win = new Rect((VirtW / 2f) - (size / 2f) - 10, (VirtH / 2f) - (size / 2f) - 22, size + 20, size + 44);
            WowUi.Panel(win);
            WowUi.GoldCentered(new Rect(win.x, win.y + 7, win.width, 18), "<b>Carte du monde</b>");
            if (GUI.Button(new Rect(win.xMax - 26, win.y + 5, 21, 21), "X")) { _worldMapOpen = false; return; }

            var map = new Rect(win.x + 10, win.y + 30, size, size);

            // PARCHMENT base + a darker border, WoW map palette.
            Color prevBg = GUI.color;
            GUI.color = new Color(0.36f, 0.28f, 0.16f);
            GUI.DrawTexture(new Rect(map.x - 3, map.y - 3, map.width + 6, map.height + 6), Texture2D.whiteTexture);
            GUI.color = new Color(0.80f, 0.72f, 0.54f);
            GUI.DrawTexture(map, Texture2D.whiteTexture);
            GUI.color = prevBg;

            // The NAMED ZONES, each a tinted patch with its name in map-ink.
            MapZone(map, 0f, 0f, 20f, new Color(0.62f, 0.62f, 0.66f, 0.85f), "Sanctuaire");
            MapZone(map, 25f, 11f, 14f, new Color(0.55f, 0.48f, 0.30f, 0.85f), "Camp gobelin");
            MapZone(map, -48f, 3f, 18f, new Color(0.78f, 0.68f, 0.34f, 0.9f), "Champ des loups");
            MapZone(map, 80f, 80f, 13f, new Color(0.42f, 0.30f, 0.26f, 0.9f), "Terres brûlées");
            MapZone(map, 41f, 41f, 8f, new Color(0.52f, 0.44f, 0.36f, 0.8f), "Camp du Roi");

            // The instance GATES: blue swirls, labelled.
            MapZone(map, 34f, 26f, 4f, new Color(0.35f, 0.55f, 0.95f, 0.95f), "");
            GUI.Label(new Rect(WorldMapView.ToMap(map, 34f, 26f).x - 70,
                WorldMapView.ToMap(map, 34f, 26f).y + 8, 140, 16),
                "<size=10><color=#20406a><b>Donjon</b></color></size>", RichCentered());
            MapZone(map, 74f, 66f, 4f, new Color(0.55f, 0.35f, 0.95f, 0.95f), "");
            GUI.Label(new Rect(WorldMapView.ToMap(map, 74f, 66f).x - 70,
                WorldMapView.ToMap(map, 74f, 66f).y + 8, 140, 16),
                "<size=10><color=#3a2a6a><b>Raid</b></color></size>", RichCentered());

            // The quest giver: his live « ! » / « ? » stands on the map too (Aldric's plaza).
            string giverMark = QuestGiverMarker();
            if (giverMark.Length > 0)
            {
                Vector2 g = WorldMapView.ToMap(map, 3.5f, 3.5f);
                GUI.Label(new Rect(g.x - 15, g.y - 26, 30, 26), giverMark, RichCentered());
            }

            // Quest zone circle — PULSING while the tracker click asked for attention.
            Aetheria.Shared.Data.QuestDefinition? quest = HighlightedQuest();
            if (quest != null)
            {
                Vector2 c = WorldMapView.ToMap(map, quest.ZoneX, quest.ZoneY);
                float r = quest.ZoneRadius / WorldMapView.Extent * (size / 2f);
                Color prevZone = GUI.color;
                if (Time.time < _mapBlinkUntil)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.55f + (Mathf.Abs(Mathf.Sin(Time.time * 6f)) * 0.45f));
                }

                GUI.DrawTexture(new Rect(c.x - r, c.y - r, r * 2f, r * 2f), ZoneDisc(), ScaleMode.StretchToFill);
                GUI.color = prevZone;
                GUI.Label(new Rect(c.x - 90, c.y - r - 20, 180, 18),
                    "<size=10><color=#ffd100><b>" + quest.Name + "</b></color></size>", RichCentered());

                // Every quest monster currently in sight: a red-gold dot at its live position.
                IReadOnlyList<EntitySnapshot> seen = _client.Visible;
                Color prevDot = GUI.color;
                GUI.color = new Color(1f, 0.35f, 0.2f);
                for (int i = 0; i < seen.Count; i++)
                {
                    if (seen[i].Kind == EntityKind.Monster && seen[i].RaceId == quest.TargetMonsterId)
                    {
                        Vector2 mp = WorldMapView.ToMap(map, seen[i].Position.X, seen[i].Position.Y);
                        GUI.DrawTexture(new Rect(mp.x - 3, mp.y - 3, 6, 6), Texture2D.whiteTexture);
                    }
                }

                GUI.color = prevDot;
            }

            // THE PLAYER: impossible to miss — a pulsing gold ring under a big arrow that
            // points where you're facing, WoW-style.
            EntitySnapshot self;
            if (_client.TryGetSelf(out self))
            {
                Vector2 p = WorldMapView.ToMap(map, self.Position.X, self.Position.Y);

                float pulse = 10f + (Mathf.Sin(Time.time * 3f) * 2.5f);
                Color prev = GUI.color;
                GUI.color = new Color(1f, 0.85f, 0.2f, 0.5f);
                GUI.DrawTexture(new Rect(p.x - pulse, p.y - pulse, pulse * 2f, pulse * 2f),
                    SolidDisc(), ScaleMode.StretchToFill);
                GUI.color = prev;

                float rot = 90f - (_lastFacing * Mathf.Rad2Deg); // ▲ points north; rotate to facing
                Matrix4x4 saved = GUI.matrix;
                GUIUtility.RotateAroundPivot(rot, p);
                GUI.Label(new Rect(p.x - 12, p.y - 15, 24, 26),
                    "<size=19><color=#3a2a10><b>▲</b></color></size>", RichCentered());
                GUI.Label(new Rect(p.x - 12, p.y - 17, 24, 26),
                    "<size=17><color=#ffd100><b>▲</b></color></size>", RichCentered());
                GUI.matrix = saved;
            }
        }

        /// <summary>The WoW micro-bar: tiny buttons for menu, character, quests, map, bags.</summary>
        private void DrawMicroBar()
        {
            (string label, string tip, System.Action toggle)[] entries =
            {
                ("☰", "Menu (Échap)", () => { _menuOpen = !_menuOpen; _optionsTab = -1; }),
                ("P", "Personnage (" + KeyLabel(HudConfig.Bind.CharSheet) + ")", () => _sheetOpen = !_sheetOpen),
                ("Q", "Carnet de quêtes (" + KeyLabel(HudConfig.Bind.QuestLog) + ")", () => _questLogOpen = !_questLogOpen),
                ("C", "Carte du monde (" + KeyLabel(HudConfig.Bind.WorldMap) + ")", () => _worldMapOpen = !_worldMapOpen),
                ("S", "Sacs (" + KeyLabel(HudConfig.Bind.Bags) + ")", () => _bagsOpen = !_bagsOpen),
            };

            const float B = 26f;
            Rect barRect = FrameRect(HudConfig.Frame.MicroBar);
            float x0 = barRect.x;
            float y = barRect.y;
            for (int i = 0; i < entries.Length; i++)
            {
                var r = new Rect(x0 + (i * (B + 3f)), y, B, B);
                WowUi.Slot(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2));
                if (GUI.Button(r, "<size=12><b>" + entries[i].label + "</b></size>",
                    new GUIStyle(GUI.skin.button) { richText = true }))
                {
                    entries[i].toggle();
                }

                if (r.Contains(Event.current.mousePosition))
                {
                    _tooltip = "<b>" + entries[i].tip + "</b>";
                }
            }
        }

        /// <summary>The seated logout countdown, front and centre.</summary>
        private void DrawLogoutCountdown()
        {
            if (_logoutAt <= 0f)
            {
                return;
            }

            int seconds = Mathf.CeilToInt(_logoutAt - Time.time);
            var box = new Rect((VirtW / 2f) - 170, VirtH * 0.24f, 340, 64);
            WowUi.Panel(box);
            WowUi.GoldCentered(new Rect(box.x, box.y + 10, box.width, 22),
                "<size=15><b>Déconnexion dans " + Mathf.Max(seconds, 0) + " s</b></size>");
            WowUi.Body(new Rect(box.x, box.y + 36, box.width, 20),
                "<size=10><color=#a0a0a0>Bouge (ou Échap) pour rester en jeu.</color></size>");
        }

        // --- Bags (WoW-style separate window, bottom right) ---

        private void DrawBagsWindow()
        {
            if (!_bagsOpen) { return; }

            const float Icon = 34f;
            const int Cols = 8;
            int cells = _client.InventoryCapacity;
            Rect win = FrameRect(HudConfig.Frame.Bags);
            float w = win.width;
            WowUi.Panel(win); // opaque WoW backpack, not a see-through box
            WowUi.GoldCentered(new Rect(win.x, win.y + 7, win.width, 18), "<b>Sacs</b>");

            // The WORN BAG's slot, top-left of the window: shows the equipped bag (right-click
            // a bag in the cells to wear it; wearing a bigger one grows the grid live).
            var bagSlot = new Rect(win.x + 8, win.y + 4, 24, 24);
            IReadOnlyList<byte> gearForBag = _client.EquipmentSlots;
            byte wornBag = gearForBag != null && (int)EquipSlot.Bag < gearForBag.Count
                ? gearForBag[(int)EquipSlot.Bag] : (byte)0;
            WowUi.Slot(bagSlot);
            if (wornBag != 0)
            {
                DrawItemIcon(new Rect(bagSlot.x + 2, bagSlot.y + 2, 20, 20), wornBag, 1);
                if (bagSlot.Contains(Event.current.mousePosition))
                {
                    ItemDefinition bagDef = Data.GetItem(wornBag);
                    _tooltip = "<b>" + bagDef.Name + "</b>\n<color=#a0a0a0>+" + bagDef.BagCapacity +
                        " emplacements</color>\n<size=9><color=#808080>Clic droit : ranger le sac</color></size>";
                }

                Event bagEvt = Event.current;
                if (bagEvt.type == EventType.MouseDown && bagEvt.button == 1 && bagSlot.Contains(bagEvt.mousePosition))
                {
                    _client.SendEquipItem(0, (byte)EquipSlot.Bag); // unequip (must fit the base cells)
                    bagEvt.Use();
                }
            }
            else if (bagSlot.Contains(Event.current.mousePosition))
            {
                _tooltip = "<b>Emplacement de sac</b>\n<color=#a0a0a0>Équipe un sac (clic droit dessus) " +
                    "pour agrandir ton inventaire — en vente chez Mira.</color>";
            }

            if (GUI.Button(new Rect(win.x + win.width - 26, win.y + 5, 21, 21), "X"))
            {
                _bagsOpen = false;
                return;
            }

            // FILTER TABS: click = filter the view; right-click = move that filter first
            // (the order is yours, saved with the interface profile).
            float fx = win.x + 10;
            for (int f = 0; f < _bagFilterOrder.Count; f++)
            {
                int filter = _bagFilterOrder[f];
                string label = BagFilterLabel(filter);
                float bw2 = 14 + (label.Length * 7f);
                var tab = new Rect(fx, win.y + 26, bw2, 20);
                bool active = _bagFilter == filter;
                if (active) { WowUi.Highlight(tab); }
                if (GUI.Button(tab, "<size=10>" + (active ? "<color=#ffd100>" + label + "</color>" : label) + "</size>",
                    new GUIStyle(GUI.skin.button) { richText = true }))
                {
                    _bagFilter = filter;
                }

                Event fe = Event.current;
                if (fe.type == EventType.MouseDown && fe.button == 1 && tab.Contains(fe.mousePosition))
                {
                    _bagFilterOrder.RemoveAt(f);
                    _bagFilterOrder.Insert(0, filter);
                    SaveBagFilterOrder();
                    fe.Use();
                }

                fx += bw2 + 4;
            }

            IReadOnlyList<ItemStack> items = _client.InventoryItems;
            bool filtered = _bagFilter != 0;
            int shown = 0;
            for (int i = 0; i < cells; i++)
            {
                // Filtered view: matching items pack into sequential cells (drag-reorder is
                // only meaningful in « Tout », where cells are the REAL bag layout).
                int index = i;
                if (filtered)
                {
                    index = NextMatchingIndex(items, ref shown);
                }

                Rect cell = new Rect(win.x + 12 + ((i % Cols) * (Icon + 4f)),
                    win.y + 50 + ((i / Cols) * (Icon + 4f)), Icon, Icon);
                if (index < 0 || index >= items.Count || items[index].ItemId == 0)
                {
                    WowUi.Slot(cell); // empty bag cell (or a layout hole), WoW-style socket
                    continue;
                }

                ItemStack stack = items[index];
                DrawItemIcon(cell, stack.ItemId, stack.Quantity);

                Event e = Event.current;
                if (e.type == EventType.MouseDown && cell.Contains(e.mousePosition))
                {
                    ItemDefinition def = Data.GetItem(stack.ItemId);
                    if (e.button == 0 && _draggingItem == null && !_client.TradeActive)
                    {
                        // LEFT hold = pick the item up: drop it on another cell to reorder,
                        // on an ally to trade, or on the ground to let it go.
                        _draggingItem = stack;
                        _dragFromIndex = index;
                        e.Use();
                    }
                    else if (e.button == 1 && _client.TradeActive)
                    {
                        // Trade open: RIGHT = put it on the table (once, up to six offers).
                        bool offered = false;
                        for (int k = 0; k < _myOffer.Count; k++)
                        {
                            if (_myOffer[k].ItemId == stack.ItemId) { offered = true; }
                        }

                        if (!offered && _myOffer.Count < TradeSlots && !_client.TradeMyAccepted)
                        {
                            _myOffer.Add(stack);
                            PushOffer();
                        }

                        e.Use();
                    }
                    else if (e.button == 1 && _shopOpen)
                    {
                        // Shop open: RIGHT sells the WHOLE stack (WoW behaviour) — hold Shift
                        // to sell a single unit instead.
                        byte qty = e.shift ? (byte)1 : (byte)Mathf.Clamp(stack.Quantity, 1, 255);
                        _client.SendVendor(sell: true, stack.ItemId, qty);
                        e.Use();
                    }
                    else if (e.button == 1 && def.Type == ItemType.Consumable)
                    {
                        _client.SendUseItem(stack.ItemId); // RIGHT = drink / eat
                        e.Use();
                    }
                    else if (e.button == 1 && def.Slot != EquipSlot.None)
                    {
                        _client.SendEquipItem(stack.ItemId, (byte)def.Slot); // RIGHT = wear it
                        e.Use();
                    }
                }
            }

            GUI.Label(new Rect(win.x + 12, win.yMax - 24, w - 24, 18),
                "<size=9><color=#909090>Clic droit : équiper · Glisser : ranger / échanger / poser</color></size>", Rich());

            // WoW backpack money bar: your coins, bottom-right of the bag.
            GUI.Label(new Rect(win.x + 12, win.yMax - 24, w - 27, 18),
                "<b>" + FormatMoney(_client.Gold) + "</b>",
                new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.MiddleRight });
        }

        /// <summary>One equipment slot tile; click to unequip the piece back into the bags.</summary>
        private void DrawEquipSlot(Rect rect, byte equippedId, EquipSlot slot, string label)
        {
            WowUi.Slot(rect);
            if (equippedId != 0)
            {
                DrawItemIcon(new Rect(rect.x + 3, rect.y + 3, rect.width - 6, rect.height - 6), equippedId, 1);
                Event e = Event.current;
                if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
                {
                    if (e.button == 0 && _draggingItem == null && !_client.TradeActive)
                    {
                        // LEFT hold = take the piece OFF the sheet: drop it on a bag cell to
                        // store it there, on an ally to trade it, or on the ground to drop it.
                        _draggingItem = new ItemStack(equippedId, 1);
                        _dragFromEquip = slot;
                        _dragFromIndex = -1;
                        e.Use();
                    }
                    else if (e.button == 1)
                    {
                        _client.SendEquipItem(0, (byte)slot); // RIGHT = quick unequip
                        e.Use();
                    }
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

        private const int TradeSlots = 6; // WoW-style: six offer rows per side

        private void DrawTradeWindow()
        {
            if (!_client.TradeActive) { return; }

            Rect win = new Rect((VirtW / 2f) - 225, (VirtH / 2f) - 185, 450, 370);
            WowUi.Panel(win);
            WowUi.GoldCentered(new Rect(win.x, win.y + 8, win.width, 20),
                "<size=13><b>Échange — " + _client.TradePartner + "</b></size>");

            float colW = (win.width - 36f) / 2f;
            float leftX = win.x + 12f;
            float rightX = win.x + 24f + colW;
            float topY = win.y + 32f;

            DrawTradeColumn(new Rect(leftX, topY, colW, 250), mine: true);
            DrawTradeColumn(new Rect(rightX, topY, colW, 250), mine: false);

            if (!string.IsNullOrEmpty(_client.TradeMessage))
            {
                GUI.Label(new Rect(win.x + 12, win.y + win.height - 78, win.width - 24, 20),
                    "<color=#f0c040>" + _client.TradeMessage + "</color>", RichCentered());
            }

            GUI.Label(new Rect(win.x + 12, win.y + win.height - 58, win.width - 24, 18),
                "<size=9><color=#909090>Clic droit sur un objet de tes sacs : l'offrir · Clic sur une offre : la retirer</color></size>",
                RichCentered());

            // WoW flow: « Échanger » locks YOUR side; the deal closes when both have locked.
            bool locked = _client.TradeMyAccepted;
            GUI.enabled = !locked;
            if (WowUi.Button(new Rect(win.x + 12, win.y + win.height - 36, 200, 28),
                    locked ? "En attente de " + _client.TradePartner + "…" : "Échanger"))
            {
                _client.SendTradeAccept();
            }

            GUI.enabled = true;
            if (WowUi.Button(new Rect(win.x + win.width - 212, win.y + win.height - 36, 200, 28), "Annuler"))
            {
                _client.SendTradeCancel();
            }
        }

        /// <summary>One trade column: header with accept state, six item rows, and the money line.
        /// The whole column glows green once that side has locked its offer (WoW).</summary>
        private void DrawTradeColumn(Rect area, bool mine)
        {
            bool accepted = mine ? _client.TradeMyAccepted : _client.TradeTheirAccepted;
            WowUi.Highlight(area);
            if (accepted)
            {
                Color prev = GUI.color;
                GUI.color = new Color(0.2f, 0.9f, 0.3f, 0.10f);
                GUI.DrawTexture(area, Texture2D.whiteTexture);
                GUI.color = prev;
            }

            GUI.Label(new Rect(area.x + 8, area.y + 4, area.width - 16, 18),
                "<b>" + (mine ? "Ton offre" : "Offre de " + _client.TradePartner) + "</b>" +
                (accepted ? "  <color=#50e060>✔ verrouillée</color>" : ""), Rich());

            IReadOnlyList<ItemStack> items = mine ? (IReadOnlyList<ItemStack>)_myOffer : _client.TradeTheirItems;
            for (int i = 0; i < TradeSlots; i++)
            {
                var slotRect = new Rect(area.x + 8, area.y + 26 + (i * 33f), 30, 30);
                if (i < items.Count && items[i].ItemId != 0)
                {
                    ItemStack s = items[i];
                    DrawItemIcon(slotRect, s.ItemId, s.Quantity); // real icon + hover tooltip
                    ItemDefinition def = Data.GetItem(s.ItemId);
                    GUI.Label(new Rect(slotRect.xMax + 6, slotRect.y + 5, area.width - 56, 20),
                        "<color=" + QualityHex(def) + ">" + def.Name +
                        (s.Quantity > 1 ? " ×" + s.Quantity : "") + "</color>", Rich());

                    // Click one of MY offered items to take it back off the table.
                    Event e = Event.current;
                    if (mine && e.type == EventType.MouseDown && e.button == 0 &&
                        new Rect(slotRect.x, slotRect.y, area.width - 16, 30).Contains(e.mousePosition))
                    {
                        _myOffer.RemoveAt(i);
                        PushOffer();
                        e.Use();
                    }
                }
                else
                {
                    WowUi.Slot(slotRect); // empty offer socket
                }
            }

            // Money line: mine is THREE WoW fields (gold / silver / copper); theirs shows coins.
            float my = area.y + 26 + (TradeSlots * 33f) + 4f;
            if (mine)
            {
                int gold = _myOfferGold / 10000;
                int silver = _myOfferGold % 10000 / 100;
                int copper = _myOfferGold % 100;

                float x = area.x + 8;
                int newGold = MoneyField(ref x, my, gold, "<color=#ffd700>●</color>");
                int newSilver = MoneyField(ref x, my, silver, "<color=#c8c8d0>●</color>");
                int newCopper = MoneyField(ref x, my, copper, "<color=#c07940>●</color>");

                int total = (newGold * 10000) + (newSilver * 100) + newCopper;
                if (total != _myOfferGold)
                {
                    _myOfferGold = Mathf.Clamp(total, 0, _client.Gold);
                    PushOffer();
                }
            }
            else
            {
                GUI.Label(new Rect(area.x + 8, my + 3, area.width - 16, 18),
                    "<size=10>Argent : " + FormatMoney(_client.TradeTheirGold) + "</size>", Rich());
            }
        }

        /// <summary>One denomination field (number + coin dot); advances the x cursor.</summary>
        private int MoneyField(ref float x, float y, int value, string coin)
        {
            string typed = GUI.TextField(new Rect(x, y, 38, 20), value.ToString());
            x += 40f;
            GUI.Label(new Rect(x, y + 2, 16, 18), coin, Rich());
            x += 18f;
            int parsed;
            return int.TryParse(typed, out parsed) ? Mathf.Max(0, parsed) : 0;
        }

        private void DrawInspectWindow()
        {
            if (!(_client.LastInspect is InspectResult info)) { return; }

            Rect win = new Rect(40, 120, 300, 336);
            WowUi.Panel(win);
            WowUi.GoldCentered(new Rect(win.x, win.y + 7, win.width, 18),
                "<b>Inspection — " + info.Name + "</b>");
            if (GUI.Button(new Rect(win.x + win.width - 26, win.y + 5, 21, 21), "X"))
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

            float y = win.y + 30f;
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 20),
                "<color=#c8c8c8>" + race + " · " + cls + " — <b>niveau " + info.Level + "</b></color>", Rich());
            y += 24f;

            // Stat blocks, mirroring the character sheet.
            float half = (win.width - 30f) / 2f;
            WowUi.Highlight(new Rect(win.x + 12, y, half, 66));
            WowUi.Highlight(new Rect(win.x + 18 + half, y, half, 66));
            GUI.Label(new Rect(win.x + 20, y + 4, half - 16, 18), "<b><color=#ffd100>Attributs</color></b>", Rich());
            GUI.Label(new Rect(win.x + 20, y + 24, half - 16, 18), StatLine("Endurance", info.MaxHealth.ToString()), Rich());
            GUI.Label(new Rect(win.x + 20, y + 43, half - 16, 18), StatLine("Force", info.Attack.ToString()), Rich());
            GUI.Label(new Rect(win.x + 26 + half, y + 4, half - 16, 18), "<b><color=#ffd100>Combat</color></b>", Rich());
            GUI.Label(new Rect(win.x + 26 + half, y + 24, half - 16, 18), StatLine("Puissance", info.Attack.ToString()), Rich());
            GUI.Label(new Rect(win.x + 26 + half, y + 43, half - 16, 18), StatLine("Défense", info.Defense.ToString()), Rich());
            y += 74f;

            // The FULL loadout: two columns of slots with real icons and hover tooltips.
            GUI.Label(new Rect(win.x + 12, y, win.width - 24, 18), "<b><color=#ffd100>Équipement</color></b>", Rich());
            y += 20f;
            const float Cell = 30f;
            EquipSlot[] left = { EquipSlot.Head, EquipSlot.Shoulders, EquipSlot.Back, EquipSlot.Chest, EquipSlot.Hands };
            EquipSlot[] right = { EquipSlot.Waist, EquipSlot.Legs, EquipSlot.Feet, EquipSlot.Weapon, EquipSlot.OffHand };
            for (int i = 0; i < left.Length; i++)
            {
                DrawInspectSlot(new Rect(win.x + 12, y + (i * (Cell + 3f)), Cell, Cell), info, left[i]);
                DrawInspectSlot(new Rect(win.x + 18 + half, y + (i * (Cell + 3f)), Cell, Cell), info, right[i]);
            }
        }

        /// <summary>One inspected equip slot: real icon + tooltip, or a labelled empty socket.</summary>
        private void DrawInspectSlot(Rect rect, InspectResult info, EquipSlot slot)
        {
            byte itemId = (int)slot < info.Equipment.Length ? info.Equipment[(int)slot] : (byte)0;
            if (itemId != 0)
            {
                DrawItemIcon(rect, itemId, 1);
            }
            else
            {
                WowUi.Slot(rect);
                GUI.Label(new Rect(rect.x, rect.y + 8, rect.width, 14),
                    "<size=8><color=#808080>" + SlotLabel(slot) + "</color></size>", RichCentered());
            }

            GUI.Label(new Rect(rect.xMax + 6, rect.y + 6, 100, 18),
                itemId != 0
                    ? "<size=10><color=" + QualityHex(Data.GetItem(itemId)) + ">" + Data.GetItem(itemId).Name + "</color></size>"
                    : "", Rich());
        }

        /// <summary>The item ghost that follows the cursor during a bag drag.</summary>
        private void DrawDragGhost()
        {
            if (_draggingItem == null) { return; }

            ItemStack s = _draggingItem.Value;
            Vector2 m = new Vector2(Input.mousePosition.x / _cfg.UiScale,
                (Screen.height - Input.mousePosition.y) / _cfg.UiScale);
            DrawItemIcon(new Rect(m.x - 17, m.y - 40, 34, 34), s.ItemId, s.Quantity);
            GUI.Label(new Rect(m.x - 120, m.y - 4, 240, 18),
                "<size=10>→ case du sac : ranger · allié : échanger · sol : poser</size>", RichCentered());

            // Release (the drag is held on the LEFT button):
            //   over another bag cell → reorder; over an ally → trade; elsewhere → drop.
            if (Input.GetMouseButtonUp(0))
            {
                int from = _dragFromIndex;
                EquipSlot fromEquip = _dragFromEquip;
                _draggingItem = null;
                _dragFromIndex = -1;
                _dragFromEquip = EquipSlot.None;

                if (_bagsOpen)
                {
                    int target = BagCellAt(m);
                    if (target >= 0)
                    {
                        if (fromEquip != EquipSlot.None)
                        {
                            // From the SHEET onto a bag cell: unequip straight into that cell.
                            _client.SendUnequipTo((byte)fromEquip, (byte)target);
                        }
                        else if (from >= 0 && target != from)
                        {
                            _client.SendMoveItem((byte)from, (byte)target);
                        }

                        return; // dropped inside the bag: never on the ground
                    }
                }

                // Dropped back onto the character sheet: cancel, nothing happens.
                if (_sheetOpen && SheetWindowRect().Contains(m) && fromEquip != EquipSlot.None)
                {
                    return;
                }

                EntitySnapshot self;
                bool haveSelf = _client.TryGetSelf(out self);
                EntitySnapshot? ally = !haveSelf ? null : PickEntityUnderMouse(e =>
                    e.Kind == EntityKind.Player && e.Faction == self.Faction && e.Id != self.Id);

                if (fromEquip != EquipSlot.None)
                {
                    // The piece must leave the body first — then trade it or drop it.
                    _client.SendEquipItem(0, (byte)fromEquip);
                    if (ally != null)
                    {
                        _pendingAutoOffer = s;
                        _client.SendTradeRequest(ally.Value.Id);
                    }
                    else
                    {
                        _client.SendDropItem(s.ItemId, s.Quantity);
                    }

                    return;
                }

                if (ally != null)
                {
                    _pendingAutoOffer = s;
                    _client.SendTradeRequest(ally.Value.Id);
                }
                else
                {
                    _client.SendDropItem(s.ItemId, s.Quantity);
                }
            }
        }

        /// <summary>The bag cell index under a GUI point, or -1 (mirrors DrawBagsWindow's grid).</summary>
        private int BagCellAt(Vector2 guiPoint)
        {
            const float Icon = 34f;
            const int Cols = 8;
            Rect win = FrameRect(HudConfig.Frame.Bags);
            if (!win.Contains(guiPoint))
            {
                return -1;
            }

            int col = Mathf.FloorToInt((guiPoint.x - (win.x + 12)) / (Icon + 4f));
            int row = Mathf.FloorToInt((guiPoint.y - (win.y + 50)) / (Icon + 4f));
            if (col < 0 || col >= Cols || row < 0)
            {
                return -1;
            }

            int index = (row * Cols) + col;
            return index < SimulationConstants.PlayerInventoryCapacity ? index : -1;
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
            if (GUILayout.Button("Se déconnecter", GUILayout.Height(28))) { BeginLogout(); _menuOpen = false; }
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
            GUILayout.Label("Active le mode déplacement puis glisse les cadres\n(joueur, cible, barre d'action, barre d'XP, messages,\ncarte, suivi de quêtes, fiche de personnage, sacs)\noù tu veux. Sauvegardé dans le profil " + _cfg.Profile + ".");
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

            Rect rect = FrameRect(HudConfig.Frame.CastBar);
            string name = self.CastAbilityId == SimulationConstants.HearthstoneCastId
                ? "Pierre de foyer"
                : Data.GetAbility(self.CastAbilityId).Name;
            DrawBar(rect, self.CastProgress / 255f, new Color(0.95f, 0.78f, 0.20f), "");
            GUI.Label(new Rect(rect.x, rect.y + 1, rect.width, 20),
                "<size=11><b>" + name + "</b></size>", RichCentered());
        }

        /// <summary>
        /// The world chat: ONLY what players write — no combat spam, no system noise. Enter opens
        /// the input, Enter sends, Escape cancels.
        /// </summary>
        /// <summary>One rendered chat line, coloured by channel.</summary>
        private static string FormatChatLine(ChatLine l)
        {
            (ChatChannel _, string label, string color) = StyleOf(l.Channel);
            if (l.Channel == ChatChannel.System)
            {
                return "<color=" + color + ">" + l.Text + "</color>";
            }

            if (l.Channel == ChatChannel.Whisper)
            {
                string who = l.To.Length > 0 ? "À " + l.To : "De " + l.From;
                return "<color=" + color + "><b>" + who + " :</b> " + l.Text + "</color>";
            }

            return "<color=" + color + ">[" + label + "] <b>" + l.From + " :</b> " + l.Text + "</color>";
        }

        private void DrawChat()
        {
            EnsureChatTabs();
            if (_chatTabIndex >= _chatTabs.Count) { _chatTabIndex = 0; }
            ChatTab active = _chatTabs[_chatTabIndex];

            const float W = 380f;
            const float LineH = 17f;
            Vector2 chatOff = _cfg.Offset(HudConfig.Frame.Chat);

            // Collect the ACTIVE TAB's last lines.
            var show = new List<ChatLine>();
            for (int i = _chatHistory.Count - 1; i >= 0 && show.Count < 9; i--)
            {
                if ((active.Mask & (1 << (int)_chatHistory[i].Channel)) != 0)
                {
                    show.Add(_chatHistory[i]);
                }
            }

            show.Reverse();

            float historyH = Mathf.Max(show.Count, 1) * LineH;
            float inputH = _chatInputActive ? 24f : 0f;
            float tabsH = 20f;
            float y0 = VirtH - 34f - inputH - historyH + chatOff.y;

            // TAB BAR: click = switch, right-click = configure, « + » = new tab.
            float tx = 8f + chatOff.x;
            for (int t = 0; t < _chatTabs.Count; t++)
            {
                float tw = 16f + (_chatTabs[t].Name.Length * 7f);
                var tabRect = new Rect(tx, y0 - 4f - tabsH, tw, 18f);
                if (t == _chatTabIndex) { WowUi.Highlight(tabRect); }
                if (GUI.Button(tabRect, "<size=10>" + _chatTabs[t].Name + "</size>",
                    new GUIStyle(GUI.skin.button) { richText = true }))
                {
                    _chatTabIndex = t;
                }

                Event te = Event.current;
                if (te.type == EventType.MouseDown && te.button == 1 && tabRect.Contains(te.mousePosition))
                {
                    _chatTabConfig = _chatTabConfig == t ? -1 : t;
                    te.Use();
                }

                tx += tw + 4f;
            }

            if (GUI.Button(new Rect(tx, y0 - 4f - tabsH, 20f, 18f), "+") && _chatTabs.Count < 6)
            {
                _chatTabs.Add(new ChatTab { Name = "Onglet " + (_chatTabs.Count + 1), Mask = 0xFF });
                _chatTabIndex = _chatTabs.Count - 1;
                _chatTabConfig = _chatTabIndex;
                SaveChatTabs();
            }

            // The history box.
            Dim(new Rect(8f + chatOff.x, y0 - 4f, W, historyH + 8f), 0.35f);
            for (int i = 0; i < show.Count; i++)
            {
                GUI.Label(new Rect(14f + chatOff.x, y0 + (i * LineH), W - 12f, LineH + 2f),
                    "<size=11>" + FormatChatLine(show[i]) + "</size>", Rich());
            }

            // INPUT: the outgoing channel's coloured tag sits before the field.
            if (_chatInputActive)
            {
                // THE LOGIN-SCREEN LESSON: a FOCUSED TextField swallows the KeyDown, so Enter
                // and Escape must be captured HERE, before the field draws — not in Update.
                Event kev = Event.current;
                if (kev.type == EventType.KeyDown &&
                    (kev.keyCode == KeyCode.Return || kev.keyCode == KeyCode.KeypadEnter || kev.character == '\n'))
                {
                    string toSend = _chatInput.Trim();
                    if (toSend.Length > 0) { ParseAndSendChat(toSend); }
                    _chatInput = "";
                    _chatInputActive = false;
                    kev.Use();
                    return;
                }

                if (kev.type == EventType.KeyDown && kev.keyCode == KeyCode.Escape)
                {
                    _chatInput = "";
                    _chatInputActive = false;
                    kev.Use();
                    return;
                }

                (ChatChannel _, string chLabel, string chColor) = StyleOf(_chatChannel);
                string tag = _chatChannel == ChatChannel.Whisper && _whisperTarget.Length > 0
                    ? "À " + _whisperTarget : chLabel;
                float tagW = 20f + (tag.Length * 7f);
                GUI.Label(new Rect(8f + chatOff.x, VirtH - 32f + chatOff.y, tagW, 24f),
                    "<size=11><color=" + chColor + ">[" + tag + "]</color></size>", Rich());
                GUI.SetNextControlName("ChatInput");
                _chatInput = GUI.TextField(new Rect(8f + tagW + chatOff.x, VirtH - 32f + chatOff.y, W - tagW, 24f), _chatInput, 200);
                GUI.FocusControl("ChatInput");
            }

            DrawChatTabConfig(y0 - tabsH);
            _ = tabsH; // (part of the frame's height envelope)
        }

        /// <summary>Right-click tab panel: rename, choose visible channels, delete.</summary>
        private void DrawChatTabConfig(float anchorY)
        {
            if (_chatTabConfig < 0 || _chatTabConfig >= _chatTabs.Count)
            {
                return;
            }

            ChatTab tab = _chatTabs[_chatTabConfig];
            var box = new Rect(8f, anchorY - 236f, 240f, 228f);
            WowUi.Panel(box);
            WowUi.GoldCentered(new Rect(box.x, box.y + 6, box.width, 18), "<b>Onglet de discussion</b>");

            GUI.Label(new Rect(box.x + 12, box.y + 28, 50, 20), "<size=11>Nom :</size>", Rich());
            string newName = GUI.TextField(new Rect(box.x + 58, box.y + 28, box.width - 70, 20), tab.Name, 14);
            if (newName != tab.Name) { tab.Name = newName; SaveChatTabs(); }

            float cy = box.y + 54;
            foreach ((ChatChannel ch, string label, string color) in ChannelStyles)
            {
                bool on = (tab.Mask & (1 << (int)ch)) != 0;
                bool now = GUI.Toggle(new Rect(box.x + 14, cy, box.width - 28, 18),
                    on, " <color=" + color + ">" + label + "</color>",
                    new GUIStyle(GUI.skin.toggle) { richText = true });
                if (now != on)
                {
                    tab.Mask ^= 1 << (int)ch;
                    SaveChatTabs();
                }

                cy += 19f;
            }

            if (_chatTabs.Count > 1 &&
                WowUi.Button(new Rect(box.x + 12, box.yMax - 26, 100, 20), "Supprimer"))
            {
                _chatTabs.RemoveAt(_chatTabConfig);
                _chatTabConfig = -1;
                _chatTabIndex = 0;
                SaveChatTabs();
                return;
            }

            if (WowUi.Button(new Rect(box.xMax - 90, box.yMax - 26, 78, 20), "Fermer"))
            {
                _chatTabConfig = -1;
            }
        }

        private void DrawVersionTag()
        {
            GUI.Label(new Rect(VirtW - 150, VirtH - 22, 142, 18),
                "<size=10>v" + SimulationConstants.GameVersion + " · proto " +
                SimulationConstants.ProtocolVersion + "</size>",
                new GUIStyle(GUI.skin.label) { richText = true, alignment = TextAnchor.UpperRight });
        }

        /// <summary>The zone the player currently stands in (for the minimap header).</summary>
        private string ZoneName(EntitySnapshot self)
        {
            if (_client.InInstance) { return "Instance"; }

            float dx = self.Position.X - SimulationConstants.SafeZoneCenterX;
            float dy = self.Position.Y - SimulationConstants.SafeZoneCenterY;
            if ((dx * dx) + (dy * dy) <=
                SimulationConstants.SafeZoneRadius * SimulationConstants.SafeZoneRadius)
            {
                return "Sanctuaire";
            }

            if (self.Position.X < -30f) { return "Champ des loups"; }
            if (self.Position.X > 60f && self.Position.Y > 60f) { return "Terres brûlées"; }
            if (self.Position.X > 15f && self.Position.Y > 2f) { return "Camp gobelin"; }
            return "Plaines d'Aetheria";
        }

        /// <summary>The WoW top-right minimap: zone name header + live top-down view.</summary>
        private void DrawMinimap()
        {
            if (_minimap.Texture == null) { return; }

            Rect panel = FrameRect(HudConfig.Frame.Minimap);
            WowUi.Panel(panel);

            var mapRect = new Rect(panel.x + 6, panel.y + 8, panel.width - 12, panel.width - 12);
            GUI.DrawTexture(mapRect, _minimap.Texture, ScaleMode.ScaleToFit);

            // QUEST ZONE on the minimap: the hunting circle of the highlighted quest, clipped
            // to the map square. Minimap camera: ortho size 26, centred on the player.
            EntitySnapshot selfForZone;
            Aetheria.Shared.Data.QuestDefinition? zone = HighlightedQuest();
            if (zone != null && !_client.InInstance && _client.TryGetSelf(out selfForZone))
            {
                const float Ortho = 26f;
                float pxPerUnit = mapRect.width / (2f * Ortho);
                float cx = mapRect.x + (mapRect.width / 2f) + ((zone.ZoneX - selfForZone.Position.X) * pxPerUnit);
                float cy = mapRect.y + (mapRect.height / 2f) - ((zone.ZoneY - selfForZone.Position.Y) * pxPerUnit);
                float r = zone.ZoneRadius * pxPerUnit;
                GUI.BeginGroup(mapRect);
                Color prevZone = GUI.color;
                if (Time.time < _mapBlinkUntil)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.55f + (Mathf.Abs(Mathf.Sin(Time.time * 6f)) * 0.45f));
                }

                GUI.DrawTexture(new Rect(cx - mapRect.x - r, cy - mapRect.y - r, r * 2f, r * 2f),
                    ZoneDisc(), ScaleMode.StretchToFill);
                GUI.color = prevZone;

                // Quest monsters in sight: red-gold dots, live, clipped to the map square.
                IReadOnlyList<EntitySnapshot> seen = _client.Visible;
                GUI.color = new Color(1f, 0.35f, 0.2f);
                for (int i = 0; i < seen.Count; i++)
                {
                    if (seen[i].Kind == EntityKind.Monster && seen[i].RaceId == zone.TargetMonsterId)
                    {
                        float mx = (mapRect.width / 2f) + ((seen[i].Position.X - selfForZone.Position.X) * pxPerUnit);
                        float my = (mapRect.height / 2f) - ((seen[i].Position.Y - selfForZone.Position.Y) * pxPerUnit);
                        GUI.DrawTexture(new Rect(mx - 3, my - 3, 6, 6), Texture2D.whiteTexture);
                    }
                }

                GUI.color = prevZone;
                GUI.EndGroup();
            }

            // The player is always dead-centre: a golden arrow-dot.
            WowUi.GoldCentered(new Rect(panel.x + (panel.width / 2f) - 8, panel.y + 8 + ((panel.width - 12) / 2f) - 8, 16, 16),
                "<size=12>◆</size>");

            // UNDER the map: zone name, then coordinates + server clock (from the day/night sun).
            EntitySnapshot self;
            if (_client.TryGetSelf(out self))
            {
                float worldSeconds = (_client.LastTick * SimulationConstants.TickDelta) + 150f;
                float frac = DayNight.PhaseFor(worldSeconds);
                int minutes = (int)(frac * 24f * 60f);
                string clock = (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");

                float below = panel.y + 8 + (panel.width - 12);
                WowUi.GoldCentered(new Rect(panel.x, below + 2, panel.width, 16),
                    "<size=11>" + ZoneName(self) + "</size>");
                GUI.Label(new Rect(panel.x, below + 19, panel.width, 15),
                    "<size=10><color=#c8c8c8>" + Mathf.RoundToInt(self.Position.X) + ", " +
                    Mathf.RoundToInt(self.Position.Y) + "   ·   " + clock + "</color></size>",
                    RichCentered());
            }
        }

        /// <summary>
        /// WoW-style money: the stored integer is COPPER; 100 pc = 1 pa (argent), 100 pa = 1 po
        /// (or). Shows only the coins you actually have: "3po 12pa 45pc", "8pa 20pc", "35pc".
        /// </summary>
        private static string FormatMoney(int copper)
        {
            // WoW-style: number + coloured coin dot per denomination, only what you have.
            if (copper < 0) { copper = 0; }
            int gold = copper / 10000;
            int silver = copper % 10000 / 100;
            int cents = copper % 100;

            if (gold > 0)
            {
                return gold + "<color=#ffd700>●</color> " + silver + "<color=#c8c8d0>●</color> " +
                       cents + "<color=#c07940>●</color>";
            }

            if (silver > 0)
            {
                return silver + "<color=#c8c8d0>●</color> " + cents + "<color=#c07940>●</color>";
            }

            return cents + "<color=#c07940>●</color>";
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
