using Il2CppFishNet.Object;
using Il2CppFishNet.Component.Transforming;
using Il2CppPathfinding.RVO.Sampled;
using Il2CppScheduleOne.Police;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace K9_Patrol
{
    /// <summary>
    /// K9 dog controller that follows an assigned officer (composition, not inheriting NPC).
    /// </summary>
    public sealed class K9NPC(IntPtr ptr) : MonoBehaviour(ptr)
    {
        public PoliceOfficer Officer { get; set; }
        public K9UnitController Controller { get; private set; }
        public Transform PursuitTarget { get; set; }

        private Il2CppScheduleOne.NPCs.NPC _s1Npc;
        private NavMeshAgent _agent;

        // Tunables
        private readonly float followDistance = 1f;
        private readonly float moveSpeed = 4.2f;
        private readonly float maxDistance = 10f;
        private readonly float updatePathInterval = 0.35f;
        private readonly float stuckCheckTime = 4.0f;
        private const float TickInterval = 0.1f;
        // Heel offset (right 0.9m, back 0.6m)
        private static readonly Vector3 HeelLocalOffset = new(0.9f, 0f, -0.6f);

        private float _followDistanceSqr;
        private float _maxDistanceSqr;

        private Vector3 lastPosition;
        private float stuckTimer;
        private float pathUpdateTimer;
        private bool isWarping;
        private bool _initialized;
        private bool _started;
        public bool isTracking = false;

        public Animator animator;
        public GameObject dogModel;

        private int _frameCounter;
        // Cache animator parameter hashes to avoid string hashing every frame
        private static readonly string AnimSpeed = "Speed";
        private static readonly string AnimIsWalking = "isWalking";
        private static readonly string AnimIsTracking = "isTracking";

        private float _yawVel; // degrees/sec

        enum K9State
        {
            Idle,
            Following,
            Tracking,
            TrackingIdle,
        }

        K9State state = K9State.Idle;
        private K9State previousState = K9State.Idle;

        private NetworkObject _netObj;
        private Vector3 _lastAnimPos;

        public void Initialize(PoliceOfficer officer, K9UnitController controller)
        {
            Officer = officer;
            Controller = controller;

            _followDistanceSqr = followDistance * followDistance;
            _maxDistanceSqr = maxDistance * maxDistance;

            _netObj = gameObject.GetComponent<NetworkObject>();

            _agent = gameObject.GetComponent<NavMeshAgent>() ?? gameObject.AddComponent<NavMeshAgent>();
            SetupAgentTuning();

            // On clients, don't let the agent write transform; the NetworkTransform will.
            if (_netObj != null && !_netObj.IsServer)
            {
                _agent.updatePosition = false;
                _agent.updateRotation = false;
            }

            var desired = GetHeelTarget();
            if (!NavMesh.SamplePosition(desired, out var hit, 2f, NavMesh.AllAreas))
                hit.position = officer.transform.position + (officer.transform.right * 0.9f);
            transform.position = hit.position;

            // Optional cooperation with game's NPC if present
            _s1Npc = gameObject.GetComponent<Il2CppScheduleOne.NPCs.NPC>();
            if (_s1Npc != null)
            {
                try { if (_s1Npc.awareness != null) _s1Npc.awareness.enabled = false; }
                catch (Exception ex) { Logger.Warning($"K9NPC: optional S1NPC cooperation failed: {ex.Message}"); }
            }

            // Ensure a visible renderer exists (placeholder if no prefab/model)
            LoadBundle();

            lastPosition = transform.position;
            _lastAnimPos = transform.position;
            _initialized = true;

            Logger.Debug($"K9 agent ready. isOnNavMesh={_agent.isOnNavMesh}, layer={gameObject.layer}");
            StartBehaviorUpdate();
        }

        private void Update()
        {
            // Run the state update logic every other frame for efficiency.
            _frameCounter++;
            if (_frameCounter % 2 == 0)
            {
                UpdateState();
            }

            if (animator == null || !_initialized) return;

            // Use server agent velocity on server, Transform delta on clients
            float movement;
            if (_netObj != null && _netObj.IsServer && _agent != null && _agent.enabled)
                movement = _agent.velocity.magnitude;
            else
            {
                movement = (transform.position - _lastAnimPos).magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
                _lastAnimPos = transform.position;
            }

            float speedParam;
            if (movement <= 1.664f)
                speedParam = Remap(movement, 0f, 1.664f, 0f, 0.3f);
            else
                speedParam = Remap(movement, 1.664f, 4.352f, 0.3f, 1f);

            animator.SetFloat(AnimSpeed, Mathf.Clamp01(speedParam));

            // Per-frame smooth facing when idle and close to officer
            AlignFacingWhenIdle();
        }

        private void SetupAgentTuning()
        {
            // Small animal footprint
            _agent.radius = 0.18f;
            _agent.height = 0.45f;
            _agent.baseOffset = 0.02f;

            _agent.speed = moveSpeed;
            _agent.stoppingDistance = followDistance;
            _agent.angularSpeed = 720f;     // snappier turns
            _agent.acceleration = 16f;
            _agent.autoBraking = true;
            _agent.autoRepath = true;
            _agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            _agent.avoidancePriority = 60;  // yield to officers (Unity: lower value = higher priority)
        }

        private void LoadBundle()
        {
            if (transform.childCount > 0) return;

            try
            {
                if (Core.bundle == null) { Logger.Error("Dog model bundle not loaded"); return; }

                var prefab = Core.bundle.LoadAsset<GameObject>(
                    "Assets/Bublisher/3D Stylized Animated Dogs Kit/Prefabs/K9Model.prefab");
                Logger.Debug($"Loading dog model from bundle: {prefab?.name ?? "null"}");
                if (!prefab)
                {
                    Logger.Error("Failed to load dog model from asset bundle");
                    Logger.Debug($"Available assets: {string.Join(", ", Core.bundle.GetAllAssetNames())}");
                    return;
                }

                // textures (normal should be imported as NormalMap in the bundle project)
                var albedo = Core.bundle.LoadAsset<Texture2D>("Assets/Bublisher/3D Stylized Animated Dogs Kit/Textures/3D Stylized Animated Dogs Kit - BaseColor_Custom.png");
                var normal = Core.bundle.LoadAsset<Texture2D>("Assets/Bublisher/3D Stylized Animated Dogs Kit/Textures/3D Stylized Animated Dogs Kit - Normal.png");

                var mat = MakeMaterial(albedo, normal);

                dogModel = Instantiate(prefab);
                dogModel.transform.SetParent(transform, false);
                dogModel.transform.localScale = new Vector3(1.3f, 1.3f, 1.3f);

                // apply material to all renderers (just in case)
                foreach (var r in dogModel.GetComponentsInChildren<Renderer>(true))
                {
                    r.sharedMaterial = mat;
                    // kill probe reflections that can add gloss even with low smoothness
                    r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                }

                animator = dogModel.GetComponent<Animator>();
                SetLayerRecursively(dogModel.transform, LayerMask.NameToLayer("NPC"));

                Logger.Debug("Dog model loaded successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading dog model: {ex.Message}");
            }
        }

        private static Material MakeMaterial(Texture2D albedo, Texture2D normal)
        {
            var shader = FindShader();
            if (!shader || !albedo) return null;

            var m = new Material(shader) { name = "K9_Matte" };

            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", albedo);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", albedo);

            if (normal && m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", normal);
                // Unity will enable keyword automatically when a normal map is present, but belt + suspenders:
                m.EnableKeyword("_NORMALMAP");
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 1f);
            }

            // Ensure no metallic/smoothness texture is driving gloss
            if (m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", null);

            // Metallic / Smoothness (URP uses _Smoothness; Standard uses _Glossiness)
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.1f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.1f);

            // URP-only toggles (guarded)
            if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 0f);
            if (m.HasProperty("_EnvironmentReflections")) m.SetFloat("_EnvironmentReflections", 0f);
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 0f); // 0=Opaque

            // Don't pull smoothness from albedo alpha
            if (m.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A"))
                m.DisableKeyword("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");

            return m;
        }

        private static Shader FindShader()
        {
            // Prefer URP/Lit, fall back sanely
            var s = Shader.Find("Universal Render Pipeline/Lit");
            if (s) return s;
            s = Shader.Find("Standard");
            if (s) return s;
            s = Shader.Find("Unlit/Texture");
            if (s) return s;
            return Shader.Find("Diffuse");
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null) return;

            root.gameObject.layer = layer;

            // Avoid foreach over root to dodge IL2CPP’s non-generic enumerator issues
            int count = root.childCount;
            for (int i = 0; i < count; i++)
            {
                var child = root.GetChild(i);
                SetLayerRecursively(child, layer);
            }
        }

        private void StartBehaviorUpdate()
        {
            if (_started) return;
            _started = true;
            CancelInvoke(nameof(UpdateBehavior));
            InvokeRepeating(nameof(UpdateBehavior), 1f, TickInterval);
        }

        private void UpdateBehavior()
        {
            // Server-only movement/pathing
            if (_netObj != null && !_netObj.IsServer) return;

            // Stop all behavior if the officer is invalid or dead. The K9Manager will handle cleanup.
            if (!_initialized || Officer == null || Officer.Health == null || Officer.Health.IsDead)
            {
                if (_agent != null && _agent.hasPath) _agent.ResetPath();
                return;
            }

            // If the officer is in a station, the K9 should stop and wait for the unit to be despawned.
            if (K9Manager.IsOfficerInStation(Officer))
            {
                if (_agent != null && _agent.hasPath) _agent.ResetPath();
                return;
            }

            pathUpdateTimer += TickInterval;
            if (pathUpdateTimer >= updatePathInterval)
            {
                UpdatePath();
                pathUpdateTimer = 0f;
            }

            CheckIfStuck();
        }

        private void UpdateState()
        {
            if (!_initialized || Officer == null || Officer.Health == null || Officer.Health.IsDead || animator == null || _agent == null)
                return;

            // Use movement magnitude as a fallback across server/clients
            float moveMag = (_netObj != null && _netObj.IsServer) ? _agent.velocity.magnitude
                             : (transform.position - _lastAnimPos).magnitude / Mathf.Max(Time.deltaTime, 0.0001f);

            var newState =
                isTracking
                    ? (moveMag > 0.05f ? K9State.Tracking : K9State.TrackingIdle)
                    : (moveMag > 0.05f ? K9State.Following : K9State.Idle);

            // Only record and log on actual state changes
            if (newState != state)
            {
                previousState = state;
                state = newState;
                Logger.Debug($"K9NPC state changed from {previousState} to {state}.");
            }

            // Update animator based on current state
            switch (state)
            {
                case K9State.Idle:
                    HandleIdle();
                    break;
                case K9State.Following:
                    HandleFollowing();
                    break;
                case K9State.Tracking:
                    HandleTracking();
                    break;
                case K9State.TrackingIdle:
                    HandleIdleTracking();
                    break;
            }
        }

        private void UpdatePath()
        {
            // server-only by UpdateBehavior gate
            if (Officer == null || isWarping || _agent == null || !_agent.enabled) return;

            Vector3 targetPosition = (isTracking && PursuitTarget != null) ? PursuitTarget.position : GetHeelTarget();

            Vector3 target = targetPosition;
            if (NavMesh.SamplePosition(targetPosition, out var navHit, 1.5f, NavMesh.AllAreas))
                target = navHit.position;

            if (_agent.isOnNavMesh)
            {
                float dist = Vector2.Distance(target, transform.position);
                float distSqr1 = dist * dist;

                float officerSpeed = moveSpeed;
                var officerMove = Officer?.movement;
                var officerAgent = officerMove != null ? officerMove.Agent : null;
                if (officerAgent != null)
                    officerSpeed = officerAgent.speed;

                _agent.speed = (distSqr1 > _maxDistanceSqr) ? officerSpeed * 1.2f : officerSpeed;
                _agent.SetDestination(target);
            }
        }

        private void HandleFollowing()
        {
            if (animator == null) return;
            animator.SetBool(AnimIsWalking, true);
            animator.SetBool(AnimIsTracking, false);
        }

        private void HandleIdleTracking()
        {
            if (animator == null) return;
            if (previousState != K9State.Tracking) { Logger.Debug("Should be idle tracking now"); }
            animator.SetBool(AnimIsWalking, false);
            animator.SetBool(AnimIsTracking, true);
        }

        private void HandleTracking()
        {
            if (animator == null) return;
            if (previousState != K9State.Tracking) { Logger.Debug("Should be tracking now"); }
            animator.SetBool(AnimIsWalking, true);
            animator.SetBool(AnimIsTracking, true);
        }

        private void HandleIdle()
        {
            if (animator == null) return;
            animator.SetBool(AnimIsWalking, false);
            animator.SetBool(AnimIsTracking, false);
        }

        private void CheckIfStuck()
        {
            if (Officer == null) return;

            float movedSqr = (transform.position - lastPosition).sqrMagnitude;
            float toOfficerSqr = (transform.position - Officer.transform.position).sqrMagnitude;

            if (movedSqr < 0.01f && toOfficerSqr > (_followDistanceSqr * 4f))
            {
                stuckTimer += TickInterval;
                if (stuckTimer >= stuckCheckTime && !isWarping) WarpToOfficer();
            }
            else
            {
                stuckTimer = 0f;
            }

            lastPosition = transform.position;
        }

        private void WarpToOfficer()
        {
            if (Officer == null || _agent == null) return;

            isWarping = true;
            Vector3 target = GetHeelTarget();
            if (!NavMesh.SamplePosition(target, out var navHit, 2.5f, NavMesh.AllAreas))
                navHit.position = Officer.transform.position;

            try
            {
                if (_agent.enabled)
                {
                    if (!_agent.Warp(navHit.position))
                    {
                        _agent.enabled = false;
                        transform.position = navHit.position;
                        _agent.enabled = true;
                    }
                    // Keep agent and transform in sync after warping
                    _agent.nextPosition = transform.position;
                    _agent.ResetPath();
                    _agent.SetDestination(navHit.position);
                }
                else
                {
                    transform.position = navHit.position;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"K9NPC WarpToOfficer exception: {ex.Message}");
                transform.position = navHit.position;
            }

            Logger.Debug($"K9 dog warped to: {navHit.position}");
            stuckTimer = 0f;
            isWarping = false;
        }

        private void AlignFacingWhenIdle()
        {
            if (Officer == null || _agent == null || !_agent.enabled) return;

            // Only align facing when close to heel and not really moving
            float distSqr = (GetHeelTarget() - transform.position).sqrMagnitude;
            if (distSqr > _followDistanceSqr * 1.25f) return;

            bool hasGoal = _agent.hasPath && _agent.remainingDistance > _agent.stoppingDistance + 0.05f;
            bool moving = _agent.velocity.sqrMagnitude > 0.02f || hasGoal;
            if (moving) return;

            Vector3 toOfficer = Officer.transform.position - transform.position;
            toOfficer.y = 0f;
            if (toOfficer.sqrMagnitude < 0.0001f) return;

            float targetYaw = Mathf.Atan2(toOfficer.x, toOfficer.z) * Mathf.Rad2Deg;
            float currentYaw = transform.eulerAngles.y;

            // SmoothDampAngle for stable, non-snappy rotation
            float newYaw = Mathf.SmoothDampAngle(currentYaw, targetYaw, ref _yawVel, 0.15f, 540f, Time.deltaTime);
            transform.rotation = Quaternion.Euler(0f, newYaw, 0f);

            // Snap to exact when close enough to avoid micro jiggle
            float angleDelta = Mathf.DeltaAngle(newYaw, targetYaw);
            if (Mathf.Abs(angleDelta) < 0.5f && Mathf.Abs(_yawVel) < 1f)
            {
                transform.rotation = Quaternion.Euler(0f, targetYaw, 0f);
                _yawVel = 0f;
            }
        }

        private Vector3 GetHeelTarget()
        {
            // Right-and-behind the officer
            var t = Officer.transform;
            var worldOffset = t.TransformVector(HeelLocalOffset);
            return t.position + worldOffset;
        }

        private static float Remap(float value, float fromMin, float fromMax, float toMin, float toMax)
        {
            if (Mathf.Approximately(fromMax, fromMin)) return toMin;
            float t = Mathf.InverseLerp(fromMin, fromMax, value);
            return Mathf.Lerp(toMin, toMax, t);
        }

        private void OnDisable() => CancelInvoke(nameof(UpdateBehavior));
        private void OnDestroy() => CancelInvoke(nameof(UpdateBehavior));
    }
}