using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.Law;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.PlayerScripts; // + player type
using Il2CppScheduleOne.DevUtilities; // + PlayerSingleton<>
using Il2CppScheduleOne.ItemFramework; // + ItemSlot, ItemInstance, ELegalStatus
using Il2CppScheduleOne.Product; // + ProductItemInstance
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.AI; // + officer agent control
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

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

        public static int MaxUnitCount => UnitCount.Value;

        internal static void Init()
        {
            Cat = MelonPreferences.CreateCategory("K9Unit");
            UnitCount = Cat.CreateEntry("UnitCount", 2);
            SniffRadius = Cat.CreateEntry("SniffRadius", 10f);
            SearchRadius = Cat.CreateEntry("SearchRadius", 6f);
            RecheckInterval = Cat.CreateEntry("RecheckInterval", 0.2f);
            SearchCooldown = Cat.CreateEntry("SearchCooldown", 30f);
            DebugLogging = Cat.CreateEntry("DebugLogging", true);
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
        public override void OnInitializeMelon()
        {
            K9Config.Init();

            // Register types once at startup
            ClassInjector.RegisterTypeInIl2Cpp<K9Manager>();
            ClassInjector.RegisterTypeInIl2Cpp<K9UnitController>();
            ClassInjector.RegisterTypeInIl2Cpp<K9NPC>();

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
        private readonly List<K9UnitController> _units = [];
        private float _checkTimer = 0f;
        private const float CHECK_INTERVAL = 5f;

        private static void Start()
        {
            Logger.Debug("K9 Manager started.");
        }

        private void Update()
        {
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
                    _units.RemoveAt(i);
                }
            }
        }

        private static PoliceOfficer FindClosestOfficer(Vector3 pos, float maxDist)
        {
            var all = Object.FindObjectsOfType<PoliceOfficer>(true);
            float best = maxDist;
            PoliceOfficer pick = null;
            foreach (var o in all)
            {
                if (o == null || o.transform == null) continue;
                float d = Vector3.Distance(pos, o.transform.position);
                if (d < best)
                {
                    best = d;
                    pick = o;
                }
            }
            return pick;
        }

        private void PeriodicOfficerCheck()
        {
            int currentK9OfficerCount = _units.Count;

            if (currentK9OfficerCount < K9Config.MaxUnitCount && Map.Instance != null && Map.Instance.Regions != null)
            {
                int officersToSpawn = K9Config.MaxUnitCount - currentK9OfficerCount;
                SpawnOfficersWithK9InRandomRegions(officersToSpawn);
            }
        }

        private void SpawnOfficersWithK9InRandomRegions(int count)
        {
            Logger.Debug($"Spawning up to {count} K9 officer(s)...");
            int spawnedCount = 0;
            int attempts = 0;

            var stations = Object.FindObjectsOfType<PoliceStation>();
            if (stations == null || stations.Length == 0)
            {
                Logger.Warning("No police stations found – cannot spawn K9 officers.");
                return;
            }

            while (spawnedCount < count && attempts < count * 3)
            {
                attempts++;

                int stationIndex = Random.Range(0, stations.Length);
                PoliceStation selectedStation = stations[stationIndex];
                if (selectedStation == null) continue;

                Vector3 spawnPosition = selectedStation.transform.position;

                try
                {
                    // Routes
                    var routes = Object.FindObjectsOfType<FootPatrolRoute>();
                    FootPatrolRoute selectedRoute = null;

                    if (routes != null && routes.Length > 0)
                    {
                        var validRoutes = new List<FootPatrolRoute>();
                        foreach (var route in routes)
                        {
                            if (route == null || route.Waypoints == null) continue;
                            if (route.StartWaypointIndex < 0 || route.StartWaypointIndex >= route.Waypoints.Length) continue;
                            validRoutes.Add(route);
                        }

                        if (validRoutes.Count > 0)
                        {
                            int randomRouteIndex = Random.Range(0, validRoutes.Count);
                            selectedRoute = validRoutes[randomRouteIndex];
                        }
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
                                if (asOfficer != null)
                                {
                                    officer = asOfficer;
                                    break;
                                }
                            }
                        }
                    }

                    // Fallback: find nearby existing officer
                    officer ??= FindClosestOfficer(spawnPosition, 50f);
                    if (officer == null)
                    {
                        Logger.Warning("Failed to acquire officer for K9 unit – retrying.");
                        continue;
                    }

                    // Avoid duplicate K9 for the same officer
                    bool hasK9 = false;
                    foreach (var k9 in _units)
                    {
                        if (k9 != null && k9.Officer != null && k9.Officer.GetInstanceID() == officer.GetInstanceID())
                        {
                            hasK9 = true;
                            break;
                        }
                    }
                    if (hasK9) continue;

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
        private readonly float _lastCheck;
        private bool _ready;
        private float _setupTimer = 0f;

        // Detection state
        private Player _pursuitTarget;
        private float _detectionTick;
        private bool _isSearchListenerActive;

        public void Initialize(PoliceOfficer officer)
        {
            Officer = officer;
            name = $"K9_ofc_{officer.GetInstanceID()}";
            CreateDog();

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

                K9DogNPC = Dog.AddComponent<K9NPC>();
                K9DogNPC.Initialize(Officer, this);

                SetLayerRecursively(Dog, 0);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error creating K9 dog: {ex.Message}");
            }
        }

        private static void SetLayerRecursively(GameObject obj, int layer)
        {
            obj.layer = layer;
            foreach (Transform child in obj.transform)
            {
                if (child == null) continue;
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        private void OnDestroy()
        {
            if (Dog != null)
            {
                try { Destroy(Dog); } catch { /* ignore */ }
                Dog = null;
                K9DogNPC = null;
            }
            // Ensure listeners are removed if the object is destroyed
            RemoveSearchListeners();
        }

        // ---------------------- Detection & Actions ----------------------

        private void TryDetectAndAct()
        {
            if (Officer == null || Officer.Health == null || Officer.Health.IsDead || _pursuitTarget != null) return;

            float sniffRadius = K9Config.SniffRadius.Value;
            float bestDist = sniffRadius;
            Player best = null;

            var players = Object.FindObjectsOfType<Player>(true);
            foreach (var p in players)
            {
                if (p == null || p.transform == null) continue;

                float d = Vector3.Distance(Officer.transform.position, p.transform.position);
                if (d <= sniffRadius)
                    Logger.Debug($"Sniff: candidate player {p.GetInstanceID()} at {d:F1}m (owner={p.IsOwner}, local={(p == Player.Local)})");

                if (d > bestDist) continue;

                bestDist = d;
                best = p;
            }

            if (best == null)
            {
                Logger.Debug("Sniff: no candidates within radius.");
                return;
            }

            if (!HasDrugsInToolbelt(best))
            {
                Logger.Debug($"Sniff: player {best.GetInstanceID()} has no items of interest (or inventory not visible).");
                return;
            }

            int pid = best.GetInstanceID();
            float now = Time.time;

            if (_lastSearchAt.TryGetValue(pid, out var lastSearch) && (now - lastSearch) < K9Config.SearchCooldown.Value)
            {
                Logger.Debug($"Sniff: pursuit on player {pid} suppressed by cooldown ({now - lastSearch:F1}s).");
                return;
            }

            _pursuitTarget = best;
            Logger.Debug($"Sniff: flagged player {pid} at {bestDist:F1}m. Officer pursuing.");

            EnsureOfficerAgentAndSetDestination(best.transform.position);
        }

        private void MaintainPursuit()
        {
            if (_pursuitTarget == null || _pursuitTarget.transform == null)
            {
                Logger.Debug("Pursuit: target lost, stopping.");
                _pursuitTarget = null;
                RemoveSearchListeners();
                return;
            }

            Vector3 targetPos = _pursuitTarget.transform.position;
            var agent = Officer.gameObject.GetComponent<NavMeshAgent>();
            if (agent != null && agent.enabled)
            {
                if (agent.isOnNavMesh)
                {
                    agent.SetDestination(targetPos);
                    Logger.Debug($"Pursuit: updating destination to {targetPos}.");
                }
                else
                {
                    Logger.Debug("Pursuit: officer agent not on NavMesh.");
                }
            }
            else
            {
                Logger.Debug("Pursuit: officer NavMeshAgent missing or disabled.");
            }

            float dist = Vector3.Distance(Officer.transform.position, targetPos);
            if (dist <= K9Config.SearchRadius.Value)
            {
                int pid = _pursuitTarget.GetInstanceID();
                if (!_lastSearchAt.TryGetValue(pid, out var last) || (Time.time - last) >= K9Config.SearchCooldown.Value)
                {
                    Logger.Debug($"Search: within radius ({dist:F1}m). Initiating StartBodySearchInvestigation for player {pid}.");
                    Officer.BeginBodySearch(_pursuitTarget.NetworkObject);
                    AddSearchListeners();
                }
                else
                {
                    Logger.Debug($"Search: cooldown active for player {pid} ({Time.time - last:F1}s).");
                    _pursuitTarget = null; // Cooldown is active, so stop pursuing.
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
            if (_pursuitTarget == null) return;

            int pid = _pursuitTarget.GetInstanceID();
            _lastSearchAt[pid] = Time.time;
            Logger.Debug($"Search complete. Cooldown timer set for player {pid}.");

            RemoveSearchListeners();
            _pursuitTarget = null;
        }

        private void EnsureOfficerAgentAndSetDestination(Vector3 pos)
        {
            var agent = Officer.gameObject.GetComponent<NavMeshAgent>() ?? Officer.gameObject.AddComponent<NavMeshAgent>();
            if (!agent.enabled)
            {
                Logger.Debug("Nav: officer agent not enabled.");
            }

            if (agent.enabled && agent.isOnNavMesh)
            {
                agent.SetDestination(pos);
                Logger.Debug($"Nav: set initial destination {pos}.");
            }
            else
            {
                Logger.Debug("Nav: could not set destination (not on NavMesh).");
            }
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
                    Logger.Debug($"Sniff: player {player.GetInstanceID()} inventory not visible (not local owner).");
                    return false;
                }

                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv == null || inv.hotbarSlots == null)
                {
                    Logger.Debug("Sniff: PlayerInventory or hotbarSlots is null.");
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
                        Logger.Debug($"Sniff: found ProductItemInstance in slot {slotIndex}.");
                        return true;
                    }

                    // Any illegal item by definition
                    var def = item.Definition;
                    if (def != null && def.legalStatus != ELegalStatus.Legal)
                    {
                        Logger.Debug($"Sniff: found illegal item in slot {slotIndex} (status={def.legalStatus}).");
                        return true;
                    }
                }

                Logger.Debug($"Sniff: no items of interest in hotbar for player {player.GetInstanceID()}.");
            }
            catch (Exception ex)
            {
                Logger.Warning($"HasDrugsInToolbelt failed: {ex.Message}");
            }
            return false;
        }
    }
}
