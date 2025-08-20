using Il2CppFishNet.Object;
using Il2CppFishNet.Managing;
using Il2CppFishNet.Component.Transforming;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.DevUtilities; // + PlayerSingleton<>
using Il2CppScheduleOne.ItemFramework; // + ItemSlot, ItemInstance, ELegalStatus
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.NPCs; // + NPCMovement
using Il2CppScheduleOne.PlayerScripts; // + player type
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.Product; // + ProductItemInstance
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;
using System; // For Exception

[assembly: MelonInfo(typeof(K9_Patrol.Core), "K9 Patrol", "1.0.0", "DropDaDeuce")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace K9_Patrol
{
    // ---------------------- CONFIG ----------------------
    internal static class K9Config
    {
        internal static MelonPreferences_Category Cat;
        internal static MelonPreferences_Entry<int> UnitCount;
        internal static MelonPreferences_Entry<float> SniffRadius;
        internal static MelonPreferences_Entry<float> SearchRadius;
        internal static MelonPreferences_Entry<float> RecheckInterval;
        internal static MelonPreferences_Entry<float> SearchCooldown;
        internal static MelonPreferences_Entry<bool> DebugLogging;
        internal static MelonPreferences_Entry<float> PursuitSpeedMultiplier;

        public static int MaxUnitCount => UnitCount.Value;

        internal static void Init()
        {
            Cat = MelonPreferences.CreateCategory("K9Unit");
            UnitCount = Cat.CreateEntry("UnitCount", 2);
            SniffRadius = Cat.CreateEntry("SniffRadius", 10f);
            SearchRadius = Cat.CreateEntry("SearchRadius", 6f);
            RecheckInterval = Cat.CreateEntry("RecheckInterval", 0.2f);
            SearchCooldown = Cat.CreateEntry("SearchCooldown", 30f);
            DebugLogging = Cat.CreateEntry("DebugLogging", false);
            PursuitSpeedMultiplier = Cat.CreateEntry("PursuitSpeedMultiplier", 1.25f); // mild, temporary boost while tracking

            MelonPreferences.Save();
        }
    }

    public static class Logger
    {
        internal static void Debug(string msg)
        {
            if (K9Config.DebugLogging.Value)
                MelonLogger.Msg($"[K9] {msg}");
        }
        internal static void Warning(string msg)
        {
            MelonLogger.Warning($"[K9] {msg}");
        }
        internal static void Error(string msg)
        {
            MelonLogger.Error($"[K9] {msg}");
        }
        internal static void Msg(string msg)
        {
            MelonLogger.Msg($"[K9] {msg}");
        }
    }

    // ---------------------- ENTRY ----------------------
    public sealed class Core : MelonMod
    {
        public static string cfgDir;
        public static Il2CppAssetBundle bundle;

        public override void OnInitializeMelon()
        {
            K9Config.Init();

            // Register types once at startup
            ClassInjector.RegisterTypeInIl2Cpp<K9Manager>();
            ClassInjector.RegisterTypeInIl2Cpp<K9UnitController>();
            ClassInjector.RegisterTypeInIl2Cpp<K9NPC>();

            try
            {
                // Use the utility class for consistent loading
                bundle = TemplateUtils.AssetBundleUtils.LoadAssetBundle("K9 Patrol SLN.Assets.k9model.bundle");
                
                if (bundle != null)
                {
                    Logger.Msg("AssetBundle loaded successfully");
                    // Optionally log available assets for debugging
                    string[] assetNames = bundle.GetAllAssetNames();
                    Logger.Debug($"Assets in bundle: {string.Join(", ", assetNames)}");

                    var meshes = bundle.LoadAllAssets(Il2CppType.Of<Mesh>());
                    MelonLoader.MelonLogger.Msg($"[K9] Mesh count in bundle: {meshes.Length}");
                    for (int i = 0; i < meshes.Length; i++) Logger.Msg($"  mesh[{i}]={(meshes[i].TryCast<Mesh>()?.name ?? "<null>")}");
                }
                else
                {
                    Logger.Error("Failed to load AssetBundle - returned null");
                }
            }
            catch (Exception e)
            {
                Logger.Error($"Failed to load AssetBundle: {e}");
            }

            Logger.Msg("K9 Patrol mod initialized.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Logger.Debug($"Scene initialized: {sceneName} (Build Index: {buildIndex})");
            if (Object.FindObjectOfType<K9Manager>() == null)
            {
                var go = new GameObject("K9UnitManager");
                Object.DontDestroyOnLoad(go);
                go.AddComponent<K9Manager>();
                Logger.Debug("K9Manager created and added to the scene.");
            }
        }
    }

    // ---------------------- MANAGER ----------------------
    public sealed class K9Manager(IntPtr ptr) : MonoBehaviour(ptr)
    {
        private static readonly List<K9UnitController> _units = [];
        private float _checkTimer = 0f;
        private const float CHECK_INTERVAL = 5f;
        private const float maxDistance = 100f;

        // --- Player Cache ---
        public static readonly List<Player> PlayerCache = [];
        private float _playerCacheTimer = 0f;
        private const float PLAYER_CACHE_INTERVAL = 1.0f; // Refresh player list once per second

        private static void Start()
        {
            Logger.Debug("K9 Manager started.");
        }

        private void Update()
        {
            // Update the player cache periodically
            _playerCacheTimer += Time.deltaTime;
            if (_playerCacheTimer >= PLAYER_CACHE_INTERVAL)
            {
                _playerCacheTimer = 0f;
                PlayerCache.Clear();
                PlayerCache.AddRange(Object.FindObjectsOfType<Player>(true));
            }

            // Remove disposed/null/dead units before counting or spawning
            PruneUnits();

            _checkTimer += Time.deltaTime;
            if (_checkTimer >= CHECK_INTERVAL)
            {
                PeriodicOfficerCheck();
                _checkTimer = 0f;
            }
        }

        private void PruneUnits()
        {
            for (int i = _units.Count - 1; i >= 0; i--)
            {
                var unit = _units[i];
                if (unit == null || unit.Officer == null || unit.Officer.Health == null || unit.Officer.Health.IsDead)
                {
                    if (unit != null)
                    {
                        // Destroy the GameObject to prevent memory leaks.
                        // This will trigger OnDestroy on the K9UnitController, cleaning up the dog.
                        Object.Destroy(unit.gameObject);
                    }
                    _units.RemoveAt(i);
                }
            }
        }

        public static PoliceOfficer FindClosestOfficer(Vector3 pos)
        {
            //Logger.Debug($"Finding closest officer to position {pos} using static list.");
            float bestDistSqr = maxDistance * maxDistance; // Use squared distance for efficiency
            PoliceOfficer pick = null;

            // Create a set of assigned officers for a fast lookup.
            var assignedOfficers = new HashSet<PoliceOfficer>(_units.Select(u => u.Officer));

            // Use the game's own static list of officers for high performance.
            foreach (var o in PoliceOfficer.Officers)
            {
                if (o == null || o.transform == null || !o.isActiveAndEnabled || assignedOfficers.Contains(o) || IsOfficerInStation(o)) continue;

                float dSqr = (pos - o.transform.position).sqrMagnitude;
                if (dSqr < bestDistSqr)
                {
                    bestDistSqr = dSqr;
                    pick = o;
                }
            }
            return pick;
        }

        private void PeriodicOfficerCheck()
        {
            // Only the server (or offline/SP) should spawn K9 units
            var net = Object.FindObjectOfType<NetworkManager>(true);
            bool isServer = (net == null) || net.IsServer;
            if (!isServer) return;

            int currentK9OfficerCount = _units.Count;
            if (currentK9OfficerCount < K9Config.MaxUnitCount && Map.Instance != null && Map.Instance.Regions != null)
            {
                int officersToSpawn = K9Config.MaxUnitCount - currentK9OfficerCount;
                SpawnOfficersWithK9InRandomRegions(officersToSpawn);
            }
        }

        public static bool IsOfficerInStation(PoliceOfficer officer)
        {
            if (officer == null) return false;
            
            // CRITICAL FIX: Use the game's static list instead of FindObjectsOfType.
            foreach (var station in PoliceStation.PoliceStations)
            {
                if (station != null && station.OfficerPool.Contains(officer))
                {
                    return true;
                }
            }
            return false;
        }

        private void SpawnOfficersWithK9InRandomRegions(int count)
        {
            Logger.Debug($"Spawning up to {count} K9 officer(s)...");
            int spawnedCount = 0;
            int attempts = 0;

            // OPTIMIZATION: Use the static list here as well.
            var stations = PoliceStation.PoliceStations;
            if (stations == null || stations.Count == 0)
            {
                Logger.Warning("No police stations found – cannot spawn K9 officers.");
                return;
            }

            // OPTIMIZATION: Find routes only once outside the loop.
            var routes = Object.FindObjectsOfType<FootPatrolRoute>();
            var validRoutes = new List<FootPatrolRoute>();
            if (routes != null)
            {
                foreach (var route in routes)
                {
                    if (route != null && route.Waypoints != null && route.StartWaypointIndex >= 0 && route.StartWaypointIndex < route.Waypoints.Length)
                    {
                        validRoutes.Add(route);
                    }
                }
            }

            while (spawnedCount < count && attempts < count * 3)
            {
                attempts++;

                int stationIndex = Random.Range(0, stations.Count);
                PoliceStation selectedStation = stations[stationIndex];
                if (selectedStation == null) continue;

                Vector3 spawnPosition = selectedStation.transform.position;

                try
                {
                    // Routes
                    FootPatrolRoute selectedRoute = null;
                    if (validRoutes.Count > 0)
                    {
                        int randomRouteIndex = Random.Range(0, validRoutes.Count);
                        selectedRoute = validRoutes[randomRouteIndex];
                    }

                    // Try to get an officer via patrol
                    PoliceOfficer officer = null;
                    if (selectedRoute != null)
                    {
                        var group = LawManager.Instance.StartFootpatrol(selectedRoute, 1);
                        if (group?.Members != null)
                        {
                            foreach (var m in group.Members)
                            {
                                var asOfficer = m as PoliceOfficer;

                                if (asOfficer != null && !IsOfficerInStation(asOfficer))
                                {
                                    officer = asOfficer;
                                    break;
                                }
                            }
                        }
                    }

                    // Fallback: find nearby existing officer
                    officer ??= FindClosestOfficer(spawnPosition);
                    if (officer == null)
                    {
                        Logger.Warning("Failed to acquire officer for K9 unit – retrying.");
                        continue;
                    }

                    // Create and initialize K9 unit
                    var unitGo = new GameObject($"K9Unit_{officer.GetInstanceID()}");
                    unitGo.transform.position = officer.transform.position;
                    var unit = unitGo.AddComponent<K9UnitController>();
                    unit.Initialize(officer);
                    _units.Add(unit);

                    spawnedCount++;
                    Logger.Debug($"Spawned K9 unit for officer {officer.GetInstanceID()} at station {selectedStation.name}");
                }
                catch (Exception ex)
                {
                    Logger.Error($"SpawnOfficersWithK9InRandomRegions exception: {ex}");
                }
            }

            if (spawnedCount < count)
            {
                Logger.Warning($"Only {spawnedCount} of {count} K9 officer(s) were spawned.");
            }
        }
    }

    // ---------------------- UNIT ----------------------
    public sealed class K9UnitController(IntPtr ptr) : MonoBehaviour(ptr)
    {
        public PoliceOfficer Officer { get; private set; }
        public GameObject Dog { get; private set; }
        public K9NPC K9DogNPC { get; private set; }

        private readonly Dictionary<int, float> _lastSearchAt = [];
        private bool _ready;
        private float _setupTimer = 0f;

        // Detection state
        internal Player _pursuitTarget;
        private float _detectionTick;
        private bool _isSearchListenerActive;

        // NACops-inspired: temporary movement boost while tracking
        private bool _speedBoostApplied;
        private float _origWalkSpeed;
        private float _origRunSpeed;

        public void Initialize(PoliceOfficer officer)
        {
            Officer = officer;
            name = $"K9_ofc_{officer.GetInstanceID()}";
            CreateDog();

            // No need to add NetworkedK9 or NetworkObject to the unit root
            _setupTimer = 0.25f;
        }

        private void Update()
        {
            if (!_ready && _setupTimer > 0f)
            {
                _setupTimer -= Time.deltaTime;
                if (_setupTimer <= 0f)
                {
                    FinalizeSetup();
                }
            }

            if (!_ready || Officer == null) return;

            // If the officer has entered a station, despawn this entire K9 unit.
            // The K9Manager will then spawn a new one to replace it.
            if (K9Manager.IsOfficerInStation(Officer))
            {
                Logger.Debug($"Officer {Officer.GetInstanceID()} entered station. Despawning K9 unit.");
                Object.Destroy(gameObject); // This destroys the K9UnitController and its child K9NPC.
                return;
            }

            // Periodic detection
            _detectionTick += Time.deltaTime;
            if (_detectionTick >= K9Config.RecheckInterval.Value)
            {
                _detectionTick = 0f;
                TryDetectAndAct();
            }

            // Maintain pursuit destination if chasing
            if (_pursuitTarget != null)
            {
                MaintainPursuit();

                // Check if search is complete by monitoring the BodySearchBehaviour active state
                if (_isSearchListenerActive && Officer != null && Officer.BodySearchBehaviour != null)
                {
                    // If body search was active but is now inactive, the search is complete
                    if (!Officer.BodySearchBehaviour.Active)
                    {
                        Logger.Debug("Search complete detected via behavior state change");
                        HandleSearchComplete();
                    }
                }
            }
        }

        internal void FinalizeSetup()
        {
            if (K9DogNPC != null) K9DogNPC.Officer = Officer;
            _ready = true;
            Logger.Debug("K9 unit setup finalized");
        }

        private void CreateDog()
        {
            try
            {
                Dog = new GameObject("K9_Dog_NPC");
                Dog.transform.SetParent(transform);

                Vector3 spawnPosition = Officer.transform.position + (Officer.transform.right * 0.9f);
                spawnPosition.y = Officer.transform.position.y;
                Dog.transform.position = spawnPosition;

                Logger.Debug($"K9 NPC created at position: {Dog.transform.position}");

                // Add networking to the dog itself
                var nob = Dog.GetComponent<NetworkObject>() ?? Dog.AddComponent<NetworkObject>();
                var nt = Dog.GetComponent<NetworkTransform>() ?? Dog.AddComponent<NetworkTransform>();
                // Defaults are fine; server authoritative transform

                // Spawn on the server
                var net = Object.FindObjectOfType<NetworkManager>(true);
                if (net == null || net.IsServer)
                {
                    if (net != null && !nob.IsSpawned)
                        net.ServerManager.Spawn(nob);
                }

                K9DogNPC = Dog.AddComponent<K9NPC>();
                K9DogNPC.Initialize(Officer, this);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error creating K9 dog: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            if (Dog != null)
            {
                // The dog is a networked object. Its destruction is managed by the server.
                // When this K9UnitController's GameObject is destroyed on the server,
                // the child Dog object is also destroyed, and FishNet handles the despawn.
                // No explicit despawn call is needed here, but we ensure it's null.
                Dog = null;
                K9DogNPC = null;
            }
            // Ensure listeners are removed if the object is destroyed
            RemoveSearchListeners();

            // Restore any temporary movement changes
            if (_speedBoostApplied && Officer != null)
            {
                try
                {
                    Officer.Movement.WalkSpeed = _origWalkSpeed;
                    Officer.Movement.RunSpeed = _origRunSpeed;
                }
                catch { /* ignore */ }
                _speedBoostApplied = false;
            }
        }

        // ---------------------- Detection & Actions ----------------------

        private void TryDetectAndAct()
        {
            if (Officer == null || Officer.Health == null || Officer.Health.IsDead || _pursuitTarget != null) return;

            float sniffRadius = K9Config.SniffRadius.Value;
            float rSqr = sniffRadius * sniffRadius;

            float bestDistSqr = rSqr;   // only accept candidates within radius
            Player best = null;

            _pursuitTarget = null;
            if (K9DogNPC != null) K9DogNPC.PursuitTarget = null;

            foreach (var p in K9Manager.PlayerCache)
            {
                if (p == null || p.transform == null) continue;


                float dSqr = (Officer.transform.position - p.transform.position).sqrMagnitude;

                // HARD FILTER: ignore anyone outside sniff radius
                if (dSqr > rSqr) continue;

                if (dSqr >= bestDistSqr) continue;

                bestDistSqr = dSqr;
                best = p;
            }

            if (best == null)
            {
                //Logger.Debug("Sniff: no candidates within radius.");
                SetTracking(false);
                return;
            }

            if (!HasDrugsInToolbelt(best))
            {
                SetTracking(false);
                return;
            }

            SetTracking(true);

            int pid = best.GetInstanceID();
            float now = Time.time;
            if (_lastSearchAt.TryGetValue(pid, out var last) && (now - last) < K9Config.SearchCooldown.Value)
                return;

            // Avoid starting anything if a search is already pending for the player
            if (best.CrimeData != null && best.CrimeData.BodySearchPending)
                return;

            if (bestDistSqr > rSqr) return;
            _pursuitTarget = best;
            if (K9DogNPC != null) K9DogNPC.PursuitTarget = _pursuitTarget.transform; // Set the dog's target
            
            EnsureOfficerAgentAndSetDestination(best.transform.position);
        }

        public void SetTracking(bool tracking)
        {
            if (K9DogNPC != null) K9DogNPC.isTracking = tracking;

            // Temporary speed boost while tracking (restored when tracking ends)
            if (Officer != null && Officer.Movement != null)
            {
                try
                {
                    if (tracking && !_speedBoostApplied)
                    {
                        _origWalkSpeed = Officer.Movement.WalkSpeed;
                        _origRunSpeed = Officer.Movement.RunSpeed;

                        float mult = Mathf.Max(1f, K9Config.PursuitSpeedMultiplier.Value);
                        Officer.Movement.WalkSpeed = _origWalkSpeed * mult;
                        Officer.Movement.RunSpeed = _origRunSpeed * mult;
                        _speedBoostApplied = true;
                    }
                    else if (!tracking && _speedBoostApplied)
                    {
                        Officer.Movement.WalkSpeed = _origWalkSpeed;
                        Officer.Movement.RunSpeed = _origRunSpeed;
                        _speedBoostApplied = false;
                    }
                }
                catch { /* ignore */ }
            }
        }

        private void EnsureOfficerAgentAndSetDestination(Vector3 pos)
        {
            try
            {
                if (Officer?.Movement != null && Officer.Movement.CanMove())
                {
                    if (Officer.Movement.GetClosestReachablePoint(pos, out var reachable) && reachable != Vector3.zero)
                    {
                        Officer.Movement.SetAgentType(NPCMovement.EAgentType.IgnoreCosts);
                        Officer.Movement.SetDestination(reachable);
                        Logger.Debug($"Nav: set destination via NPCMovement {reachable}.");
                    }
                    else
                    {
                        Logger.Debug("Nav: no reachable point via NPCMovement.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"EnsureOfficerAgentAndSetDestination failed: {ex.Message}");
            }
        }

        private void MaintainPursuit()
        {
            if (_pursuitTarget == null || _pursuitTarget.transform == null)
            {
                Logger.Debug("Pursuit: target lost, stopping.");
                _pursuitTarget = null;
                if (K9DogNPC != null) K9DogNPC.PursuitTarget = null;
                SetTracking(false);
                RemoveSearchListeners();
                return;
            }

            if (_pursuitTarget.CrimeData != null && _pursuitTarget.CrimeData.BodySearchPending)
            {
                Logger.Debug("Pursuit: body search already pending, stopping.");
                _pursuitTarget = null;
                if (K9DogNPC != null) K9DogNPC.PursuitTarget = null;
                SetTracking(false);
                RemoveSearchListeners();
                return;
            }

            float rSqr = K9Config.SniffRadius.Value * K9Config.SniffRadius.Value;
            float dSqr = (Officer.transform.position - _pursuitTarget.transform.position).sqrMagnitude;
            if (dSqr > rSqr) { _pursuitTarget = null; SetTracking(false); return; }

            // Server-authoritative movement using Movement API
            try
            {
                if (Officer.Movement != null && Officer.Movement.CanMove())
                {
                    var targetPos = _pursuitTarget.transform.position;
                    if (Officer.Movement.GetClosestReachablePoint(targetPos, out var reachable) && reachable != Vector3.zero)
                    {
                        Officer.Movement.SetAgentType(NPCMovement.EAgentType.IgnoreCosts);
                        Officer.Movement.SetDestination(reachable);
                    }
                }
            }
            catch { /* ignore */ }

            float dist = Vector3.Distance(Officer.transform.position, _pursuitTarget.transform.position);
            if (dist <= K9Config.SearchRadius.Value)
            {
                int pid = _pursuitTarget.GetInstanceID();

                if (!_lastSearchAt.TryGetValue(pid, out var last) || (Time.time - last) >= K9Config.SearchCooldown.Value)
                {
                    if (Officer.BodySearchBehaviour != null && Officer.BodySearchBehaviour.Active)
                        return;

                    Logger.Debug($"Search: within radius ({dist:F1}m). Initiating body search for player {pid}.");

                    // Game-provided networked action
                    Officer.BeginBodySearch_Networked(_pursuitTarget.NetworkObject);

                    AddSearchListeners();
                }
                else
                {
                    _pursuitTarget = null;
                    if (K9DogNPC != null) K9DogNPC.PursuitTarget = null;
                    SetTracking(false);
                }
            }
        }

        // Modified AddSearchListeners method
        private void AddSearchListeners()
        {
            if (_isSearchListenerActive) return;
            var bsb = Officer.BodySearchBehaviour;
            if (bsb != null)
            {
                // Don't use event listeners directly as they cause IL2CPP issues
                // Instead, we'll patch our own methods later in the search
                _isSearchListenerActive = true;
                Logger.Debug("Search listeners activated (using method patching)");
            }
        }

        // Modified RemoveSearchListeners method
        private void RemoveSearchListeners()
        {
            if (!_isSearchListenerActive) return;
            _isSearchListenerActive = false;
            Logger.Debug("Search listeners deactivated");
        }

        // Common implementation 
        private void HandleSearchComplete()
        {
            if (_pursuitTarget == null)
            {
                // Restore movement even if target disappeared
                SetTracking(false);
                return;
            }

            int pid = _pursuitTarget.GetInstanceID();
            _lastSearchAt[pid] = Time.time;
            Logger.Debug($"Search complete. Cooldown timer set for player {pid}.");

            RemoveSearchListeners();
            _pursuitTarget = null;

            // Stop tracking and restore speeds
            SetTracking(false);
        }

        // Inventory scan mirroring BodySearchBehaviour (without StealthLevel check).
        private static bool HasDrugsInToolbelt(Player player)
        {
            try
            {
                if (player == null) return false;

                bool invVisible = (player.IsOwner || player == Player.Local);
                if (!invVisible)
                {
                    //Logger.Debug($"Sniff: player {player.GetInstanceID()} inventory not visible (not local owner).");
                    return false;
                }

                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv == null || inv.hotbarSlots == null)
                {
                    //Logger.Debug("Sniff: PlayerInventory or hotbarSlots is null.");
                    return false;
                }

                int slotIndex = -1;
                foreach (ItemSlot hotbarSlot in inv.hotbarSlots)
                {
                    slotIndex++;
                    var item = hotbarSlot?.ItemInstance;
                    if (item == null) continue;

                    // Any product item counts (ignore packaging stealth)
                    if (item is ProductItemInstance)
                    {
                        //Logger.Debug($"Sniff: found ProductItemInstance in slot {slotIndex}.");
                        return true;
                    }

                    // Any illegal item by definition
                    var def = item.Definition;
                    if (def != null && def.legalStatus != ELegalStatus.Legal)
                    {
                        //Logger.Debug($"Sniff: found illegal item in slot {slotIndex} (status={def.legalStatus}).");
                        return true;
                    }
                }

                //Logger.Debug($"Sniff: no items of interest in hotbar for player {player.GetInstanceID()}.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"HasDrugsInToolbelt failed: {ex.Message}");
            }
            return false;
        }
    }
}
