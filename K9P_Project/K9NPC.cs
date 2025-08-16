using System;
using Il2CppScheduleOne.Police;
using MelonLoader;
using UnityEngine;
using UnityEngine.AI;

namespace K9_Patrol
{
    /// <summary>
    /// K9 dog controller that follows an assigned officer (composition, not inheriting NPC).
    /// </summary>
    public sealed class K9NPC(IntPtr ptr) : MonoBehaviour(ptr)
    {
        public PoliceOfficer Officer { get; set; }
        public K9UnitController Controller { get; private set; }

        private Il2CppScheduleOne.NPCs.NPC _s1Npc;
        private NavMeshAgent _agent;

        // Tunables
        private readonly float followDistance = 1.5f;
        private readonly float moveSpeed = 4.2f;
        private readonly float maxDistance = 10f;
        private readonly float updatePathInterval = 0.35f;
        private readonly float stuckCheckTime = 4.0f;
        private const float BehaviorTickInterval = 0.1f;
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

        public void Initialize(PoliceOfficer officer, K9UnitController controller)
        {
            Officer = officer;
            Controller = controller;

            _followDistanceSqr = followDistance * followDistance;
            _maxDistanceSqr = maxDistance * maxDistance;

            // Ensure agent
            _agent = gameObject.GetComponent<NavMeshAgent>() ?? gameObject.AddComponent<NavMeshAgent>();
            SetupAgentTuning();

            // Place on NavMesh near officer (heel target)
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
            EnsureVisual();

            lastPosition = transform.position;
            _initialized = true;

            Logger.Debug($"K9 agent ready. isOnNavMesh={_agent.isOnNavMesh}, layer={gameObject.layer}");
            StartBehaviorUpdate();
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

        private void EnsureVisual()
        {
            if (GetComponentInChildren<Renderer>() != null) return;

            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.name = "Visual";
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localScale = new Vector3(0.3f, 0.35f, 0.8f);
            // Lay the capsule “horizontally” a bit to look less like a pillar
            visual.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);

            var col = visual.GetComponent<Collider>();
            if (col != null) Destroy(col);

            try
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader != null)
                {
                    var mat = new Material(shader) { color = new Color(0.35f, 0.25f, 0.15f, 1f) };
                    var mr = visual.GetComponent<Renderer>();
                    mr.sharedMaterial = mat;
                }
            }
            catch { /* ignore */ }
        }

        private void StartBehaviorUpdate()
        {
            if (_started) return;
            _started = true;
            CancelInvoke(nameof(UpdateBehavior));
            InvokeRepeating(nameof(UpdateBehavior), 1f, BehaviorTickInterval);
        }

        private void UpdateBehavior()
        {
            if (!_initialized || Officer == null || Officer.Health == null || Officer.Health.IsDead)
                return;

            pathUpdateTimer += BehaviorTickInterval;
            if (pathUpdateTimer >= updatePathInterval)
            {
                UpdatePath();
                pathUpdateTimer = 0f;
            }

            CheckIfStuck();
        }

        private void UpdatePath()
        {
            if (Officer == null || isWarping || _agent == null || !_agent.enabled) return;

            Vector3 myPos = transform.position;
            Vector3 heel = GetHeelTarget();

            // Keep a “heel” formation: target a sampled point near heel
            Vector3 target = heel;
            if (NavMesh.SamplePosition(heel, out var navHit, 1.5f, NavMesh.AllAreas))
                target = navHit.position;

            float distSqr = (heel - myPos).sqrMagnitude;

            if (_agent.isOnNavMesh)
            {
                _agent.speed = Officer.movement.Agent.speed;
                _agent.SetDestination(target);
            }

            // Face officer when close
            if (distSqr <= _followDistanceSqr)
            {
                Vector3 toOfficer = Officer.transform.position - myPos;
                toOfficer.y = 0f;
                if (toOfficer.sqrMagnitude > 0.0001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(toOfficer.normalized);
                    transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * 6f);
                }
            }
        }

        private void CheckIfStuck()
        {
            if (Officer == null) return;

            float movedSqr = (transform.position - lastPosition).sqrMagnitude;
            float toOfficerSqr = (transform.position - Officer.transform.position).sqrMagnitude;

            if (movedSqr < 0.01f && toOfficerSqr > (_followDistanceSqr * 4f))
            {
                stuckTimer += BehaviorTickInterval;
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

        private Vector3 GetHeelTarget()
        {
            // Right-and-behind the officer
            var t = Officer.transform;
            var worldOffset = t.TransformVector(HeelLocalOffset);
            return t.position + worldOffset;
        }

        private void OnDisable() => CancelInvoke(nameof(UpdateBehavior));
        private void OnDestroy() => CancelInvoke(nameof(UpdateBehavior));
    }
}