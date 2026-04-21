using UnityEngine;
using StarterAssets;

public class ParkourController : MonoBehaviour
{
    enum ParkourKind
    {
        None,
        Mantle,
        MediumClimb,
        HighClimb
    }

    enum State
    {
        Idle,
        Queued,
        Executing,
        Recovery,
        Cooldown,
        WallRunning,
        WallJumping
    }

    [Header("Trigger Gates")]
    [Tooltip("Min horizontal speed (m/s) to trigger a climb.")]
    [SerializeField] float minTriggerSpeed = 6.0f;
    [Tooltip("Forward detection reach (m).")]
    [SerializeField] float triggerDistance = 0.7f;
    [Tooltip("Dot(cam.fwd, body.fwd), both XZ.")]
    [SerializeField] float cameraAlignmentMin = 0.7f;

    [Header("Approach")]
    [Tooltip("Wall proximity to start the climb motion.")]
    [SerializeField] float contactDistance = 0.5f;
    [Tooltip("Seconds to reach the wall before cancelling.")]
    [SerializeField] float queueTimeout = 1.0f;

    [Header("Layers")]
    [Tooltip("Walls and ledges the player can interact with.")]
    [SerializeField] LayerMask climbableLayer;
    [Tooltip("Layers that block head clearance above a ledge.")]
    [SerializeField] LayerMask obstructionLayers;

    [Header("Probe Heights (m, above feet)")]
    [SerializeField] float lowProbeHeight = 0.30f;
    [SerializeField] float waistProbeHeight = 1.00f;
    [SerializeField] float chestProbeHeight = 1.60f;
    [SerializeField] float headProbeHeight = 2.10f;
    [SerializeField] float overProbeHeight = 2.90f;
    [SerializeField] float probeSphereRadius = 0.15f;

    [Header("Classification (ledge height from feet)")]
    [SerializeField] float mantleMax = 1.20f;
    [SerializeField] float mediumMax = 2.10f;
    [SerializeField] float highMax = 2.90f;

    [Header("Target")]
    [Tooltip("Step back from ledge edge when landing.")]
    [SerializeField] float ledgeInset = 0.35f;
    [Tooltip("Reject ledges steeper than this (deg).")]
    [SerializeField] float ledgeMaxSlope = 35f;
    [SerializeField] float headroomHeight = 1.80f;
    [SerializeField] float headroomRadius = 0.35f;

    [Header("Motion Durations (s)")]
    [SerializeField] float mantleDuration = 0.45f;
    [SerializeField] float mediumDuration = 0.80f;
    [SerializeField] float highDuration = 1.20f;
    [SerializeField] float recoveryDuration = 0.15f;
    [SerializeField] float cooldownDuration = 0.25f;

    [Header("Animator Triggers (empty = skip)")]
    [SerializeField] string mantleTrigger = "Mantle";
    [SerializeField] string mediumTrigger = "ClimbMedium";
    [SerializeField] string highTrigger = "ClimbHigh";
    [SerializeField] string wallRunTrigger = "WallRun";
    [SerializeField] string wallJumpTrigger = "WallJump";

    [Header("Wall Run: Detection")]
    [Tooltip("Min horizontal speed (m/s) for airborne magnet.")]
    [SerializeField] float wallrunMinSpeed = 4.0f;
    [Tooltip("Sideways magnet range (m).")]
    [SerializeField] float wallMagnetDistance = 2.0f;
    [Tooltip("Two side probe heights above feet.")]
    [SerializeField] float wallSideProbeHeight = 1.00f;
    [SerializeField] float wallSideProbeHeightUpper = 1.50f;
    [Tooltip("Fresh press within this distance does a kick-off wall-jump.")]
    [SerializeField] float wallJumpContactDistance = 0.55f;

    [Header("Wall Run: Motion")]
    [Tooltip("Max seconds on a wall.")]
    [SerializeField] float wallrunMaxDuration = 1.5f;
    [Tooltip("Vertical accel while on wall (m/s^2).")]
    [SerializeField] float wallrunGravity = -3.0f;
    [Tooltip("Forward speed along tangent (m/s).")]
    [SerializeField] float wallrunSpeed = 7.0f;
    [Tooltip("Upward velocity on grounded entry (lifts off the floor).")]
    [SerializeField] float wallrunInitialUpVelocity = 2.0f;
    [Tooltip("Grace after entry before cc.isGrounded detach is allowed.")]
    [SerializeField] float wallrunGroundedGrace = 0.25f;
    [Tooltip("Chain alignment (0..1). Higher = must aim more at next wall.")]
    [SerializeField] float chainVelocityAlignment = 0.25f;

    [Header("Wall Run: Wall Jump")]
    [Tooltip("Outward launch speed.")]
    [SerializeField] float wallJumpAwaySpeed = 6.0f;
    [Tooltip("Upward launch speed.")]
    [SerializeField] float wallJumpUpSpeed = 5.0f;
    [Tooltip("Airtime window for chain detection.")]
    [SerializeField] float wallJumpAirTime = 1.0f;
    [Tooltip("Gravity during wall-jump arc (m/s^2).")]
    [SerializeField] float wallJumpGravity = -12.0f;

    [Header("Debug")]
    [SerializeField] bool drawGizmos = true;

    CharacterController cc;
    ThirdPersonController thirdPersonController;
    StarterAssetsInputs inputs;
    Animator animator;
    Transform cam;

    State state = State.Idle;
    ParkourKind activeKind = ParkourKind.None;

    Vector3 startPos, landingPos, risePoint;
    Quaternion startRot, faceWallRot;
    float stateTimer;
    float activeDuration;

    DetectionResult queuedHit;

    Vector3 wallNormal;
    Vector3 wallTangent;
    float wallVerticalVelocity;
    Collider lastWallCollider;
    bool wallrunChainActive;

    Vector3 airVelocity;

    bool lastJumpHeld;
    bool jumpPressed;

    int animIDSpeed, animIDMotionSpeed, animIDGrounded, animIDJump, animIDFreeFall;

    struct DetectionResult
    {
        public ParkourKind kind;
        public Vector3 ledgeTop;
        public Vector3 wallNormalXZ;
    }

    struct WallRunCandidate
    {
        public Vector3 normal;
        public Vector3 tangent;
        public Vector3 contactPoint;
        public Collider collider;
        public float distance;
    }

    void Start()
    {
        cc = GetComponent<CharacterController>();
        thirdPersonController = GetComponent<ThirdPersonController>();
        inputs = GetComponent<StarterAssetsInputs>();
        animator = GetComponentInChildren<Animator>();
        if (Camera.main != null)
            cam = Camera.main.transform;

        animIDSpeed = Animator.StringToHash("Speed");
        animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
        animIDGrounded = Animator.StringToHash("Grounded");
        animIDJump = Animator.StringToHash("Jump");
        animIDFreeFall = Animator.StringToHash("FreeFall");
    }

    void Update()
    {
        // Edge-detect jump so held Space does not retrigger.
        jumpPressed = inputs.jump && !lastJumpHeld;

        switch (state)
        {
            case State.Idle:
                if (thirdPersonController.Grounded)
                {
                    lastWallCollider = null;
                    wallrunChainActive = false;
                }
                TryTrigger();
                if (state == State.Idle)
                    TryWallRunTrigger();
                break;
            case State.Queued:
                TickQueued();
                break;
            case State.Executing:
                TickExecute();
                break;
            case State.Recovery:
                TickTimer(recoveryDuration, State.Cooldown, onEnter: ReenableTpc);
                break;
            case State.Cooldown:
                TickTimer(cooldownDuration, State.Idle);
                break;
            case State.WallRunning:
                TickWallRun();
                break;
            case State.WallJumping:
                TickWallJump();
                break;
        }

        lastJumpHeld = inputs.jump;
    }

    void TryTrigger()
    {
        if (cam == null && Camera.main != null)
            cam = Camera.main.transform;
        if (cam == null)
            return;

        if (!thirdPersonController.Grounded)
            return;
        if (!jumpPressed)
            return;

        Vector3 vel = cc.velocity;
        vel.y = 0f;
        if (vel.magnitude < minTriggerSpeed)
            return;

        Vector3 camFwd = cam.forward;
        camFwd.y = 0f;
        camFwd.Normalize();
        Vector3 bodyFwd = transform.forward;
        bodyFwd.y = 0f;
        bodyFwd.Normalize();
        if (Vector3.Dot(camFwd, bodyFwd) < cameraAlignmentMin)
            return;

        if (!TryDetect(out DetectionResult hit))
            return;

        inputs.jump = false;
        queuedHit = hit;
        stateTimer = 0f;
        state = State.Queued;
    }

    void TickQueued()
    {
        stateTimer += Time.deltaTime;
        inputs.jump = false;

        if (stateTimer >= queueTimeout)
        {
            state = State.Idle;
            stateTimer = 0f;
            return;
        }

        Vector3 vel = cc.velocity;
        vel.y = 0f;
        if (vel.magnitude < minTriggerSpeed * 0.5f)
        {
            state = State.Idle;
            stateTimer = 0f;
            return;
        }

        Vector3 feet = transform.position;
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f)
            return;
        fwd.Normalize();

        Vector3 origin = feet + Vector3.up * lowProbeHeight;
        if (Physics.Raycast(origin, fwd, out RaycastHit hit, contactDistance, climbableLayer))
        {
            Vector3 n = hit.normal;
            n.y = 0f;
            if (n.sqrMagnitude < Mathf.Epsilon)
                return;
            n.Normalize();

            Vector3 snapPos = new Vector3(hit.point.x, feet.y, hit.point.z) + n * cc.radius;
            transform.position = snapPos;

            queuedHit.wallNormalXZ = n;
            queuedHit.ledgeTop = new Vector3(hit.point.x, queuedHit.ledgeTop.y, hit.point.z);

            EnterExecute(queuedHit);
        }
    }

    bool TryDetect(out DetectionResult result)
    {
        result = default;

        Vector3 feet = transform.position;
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        fwd.Normalize();

        bool low = Probe(feet, fwd, lowProbeHeight, out RaycastHit lowHit);
        bool waist = Probe(feet, fwd, waistProbeHeight, out _);
        bool chest = Probe(feet, fwd, chestProbeHeight, out _);
        bool head = Probe(feet, fwd, headProbeHeight, out _);
        bool over = Probe(feet, fwd, overProbeHeight, out _);

        if (over)
            return false;
        if (!low)
            return false;

        RaycastHit anchor = lowHit;

        Vector3 n = anchor.normal;
        n.y = 0f;
        if (n.sqrMagnitude < 0.0001f)
            return false;
        n.Normalize();

        Vector3 ledgeProbeOrigin = anchor.point + fwd * ledgeInset + Vector3.up * 3.0f;
        if (!Physics.Raycast(ledgeProbeOrigin, Vector3.down, out RaycastHit ledgeHit, 4.0f, climbableLayer, QueryTriggerInteraction.Ignore))
            return false;

        if (Vector3.Angle(ledgeHit.normal, Vector3.up) > ledgeMaxSlope)
            return false;

        float ledgeH = ledgeHit.point.y - feet.y;
        ParkourKind kind;
        if (ledgeH >= 0.3f && ledgeH < mantleMax)
            kind = ParkourKind.Mantle;
        else if (ledgeH < mediumMax)
            kind = ParkourKind.MediumClimb;
        else if (ledgeH <= highMax)
            kind = ParkourKind.HighClimb;
        else
            return false;

        if (!HasHeadroom(ledgeHit.point, n))
            return false;

        result.kind = kind;
        result.ledgeTop = ledgeHit.point;
        result.wallNormalXZ = n;

        return true;
    }

    bool Probe(Vector3 feet, Vector3 fwd, float height, out RaycastHit hit)
    {
        Vector3 origin = feet + Vector3.up * height;
        return Physics.SphereCast(origin, probeSphereRadius, fwd, out hit, triggerDistance, climbableLayer, QueryTriggerInteraction.Ignore);
    }

    bool HasHeadroom(Vector3 ledgePoint, Vector3 wallNormalXZ)
    {
        Vector3 standPos = ledgePoint + (-wallNormalXZ) * ledgeInset;
        Vector3 p1 = standPos + Vector3.up * headroomRadius;
        Vector3 p2 = standPos + Vector3.up * (headroomHeight - headroomRadius);
        return !Physics.CheckCapsule(p1, p2, headroomRadius, obstructionLayers, QueryTriggerInteraction.Ignore);
    }

    void EnterExecute(DetectionResult hit)
    {
        activeKind = hit.kind;
        activeDuration = hit.kind switch
        {
            ParkourKind.Mantle => mantleDuration,
            ParkourKind.MediumClimb => mediumDuration,
            ParkourKind.HighClimb => highDuration,
            _ => mantleDuration
        };

        startPos = transform.position;
        startRot = transform.rotation;
        landingPos = hit.ledgeTop + (-hit.wallNormalXZ) * ledgeInset + Vector3.up * 0.02f;
        risePoint = new Vector3(startPos.x, hit.ledgeTop.y + 0.15f, startPos.z);
        faceWallRot = Quaternion.LookRotation(-hit.wallNormalXZ, Vector3.up);

        thirdPersonController.enabled = false;
        cc.enabled = false;

        string trigger = hit.kind switch
        {
            ParkourKind.Mantle => mantleTrigger,
            ParkourKind.MediumClimb => mediumTrigger,
            ParkourKind.HighClimb => highTrigger,
            _ => null
        };
        if (animator != null && !string.IsNullOrEmpty(trigger))
            animator.SetTrigger(trigger);

        stateTimer = 0f;
        state = State.Executing;
    }

    void TickExecute()
    {
        stateTimer += Time.deltaTime;
        float t = Mathf.Clamp01(stateTimer / activeDuration);

        Vector3 pos;
        if (activeKind == ParkourKind.Mantle)
        {
            float s = SmoothStep01(t);
            pos = Vector3.Lerp(startPos, landingPos, s);
            pos.y += Mathf.Sin(t * Mathf.PI) * 0.15f;
        }
        else
        {
            const float phase1 = 0.55f;
            if (t < phase1)
                pos = Vector3.Lerp(startPos, risePoint, t / phase1);
            else
                pos = Vector3.Lerp(risePoint, landingPos, (t - phase1) / (1f - phase1));
        }

        transform.position = pos;
        transform.rotation = Quaternion.Slerp(startRot, faceWallRot, Mathf.Clamp01(t / 0.35f));

        PinAnimatorIdle();

        if (t >= 1f)
        {
            transform.position = landingPos;
            transform.rotation = faceWallRot;
            ResolvePenetration();
            cc.enabled = true;
            inputs.jump = false;
            inputs.move = Vector2.zero;
            stateTimer = 0f;
            state = State.Recovery;
        }
    }

    void PinAnimatorIdle()
    {
        if (animator == null)
            return;
        animator.SetFloat(animIDSpeed, 0f);
        animator.SetFloat(animIDMotionSpeed, 0f);
        animator.SetBool(animIDGrounded, true);
        animator.SetBool(animIDJump, false);
        animator.SetBool(animIDFreeFall, false);
    }

    void PinAnimatorAirborne()
    {
        if (animator == null)
            return;
        animator.SetFloat(animIDSpeed, 0f);
        animator.SetFloat(animIDMotionSpeed, 0f);
        animator.SetBool(animIDGrounded, false);
        animator.SetBool(animIDJump, false);
        animator.SetBool(animIDFreeFall, true);
    }

    void ResolvePenetration()
    {
        Collider[] nearby = Physics.OverlapCapsule(
            transform.position + Vector3.up * cc.radius,
            transform.position + Vector3.up * (cc.height - cc.radius),
            cc.radius,
            obstructionLayers,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < 3 && nearby.Length > 0; i++)
        {
            bool pushed = false;
            foreach (var col in nearby)
            {
                if (Physics.ComputePenetration(
                    cc, transform.position, transform.rotation,
                    col, col.transform.position, col.transform.rotation,
                    out Vector3 dir, out float dist))
                {
                    transform.position += dir * (dist + 0.01f);
                    pushed = true;
                }
            }
            if (!pushed)
                break;
            nearby = Physics.OverlapCapsule(
                transform.position + Vector3.up * cc.radius,
                transform.position + Vector3.up * (cc.height - cc.radius),
                cc.radius, obstructionLayers, QueryTriggerInteraction.Ignore);
        }
    }

    void TickTimer(float duration, State next, System.Action onEnter = null)
    {
        stateTimer += Time.deltaTime;
        if (stateTimer >= duration)
        {
            stateTimer = 0f;
            state = next;
            onEnter?.Invoke();
        }
    }

    void ReenableTpc()
    {
        thirdPersonController.enabled = true;
        inputs.jump = false;
        activeKind = ParkourKind.None;
    }

    static float SmoothStep01(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    void TryWallRunTrigger()
    {
        if (thirdPersonController.Grounded)
        {
            // Held Space + side wall. Side probes filter by orientation.
            if (!inputs.jump)
                return;

            if (!TryFindSideWall(excluded: lastWallCollider, requireVelocityToward: false, out WallRunCandidate c))
                return;

            inputs.jump = false;
            EnterWallRun(c, isGroundedEntry: true);
        }
        else
        {
            // Held Space is enough; fresh press is required only for the kick-off wall-jump.
            if (!inputs.jump)
                return;

            Vector3 vel = cc.velocity;
            vel.y = 0f;
            if (vel.magnitude < 0.5f)
                return;

            if (!TryFindSideWall(excluded: lastWallCollider, requireVelocityToward: false, out WallRunCandidate c))
                return;

            Vector3 towardWall = -c.normal;
            float approach = (vel.sqrMagnitude > 0.01f)
                ? Vector3.Dot(vel.normalized, towardWall)
                : 0f;

            inputs.jump = false;

            const float AirJumpApproachDot = 0.3f;
            bool canAirWallJump = jumpPressed
                                  && c.distance <= wallJumpContactDistance
                                  && approach >= AirJumpApproachDot;

            if (canAirWallJump)
                PerformAirWallJump(c);
            else
                EnterWallRun(c, isGroundedEntry: false);
        }
    }

    void PerformAirWallJump(WallRunCandidate c)
    {
        lastWallCollider = c.collider;
        wallNormal = c.normal;
        wallTangent = c.tangent;

        Vector3 launch = wallNormal * wallJumpAwaySpeed
                       + Vector3.up * wallJumpUpSpeed
                       + wallTangent * (wallrunSpeed * 0.5f);

        thirdPersonController.enabled = false;
        BeginWallJumpArc(launch, fromWallrun: false);
    }

    bool TryFindSideWall(Collider excluded, bool requireVelocityToward, out WallRunCandidate result)
    {
        result = default;

        Vector3 feet = transform.position;
        Vector3 leftDir = -transform.right;
        Vector3 rightDir = transform.right;

        bool left = TrySideProbe(feet, leftDir, excluded, requireVelocityToward, out RaycastHit leftHit);
        bool right = TrySideProbe(feet, rightDir, excluded, requireVelocityToward, out RaycastHit rightHit);

        if (!left && !right)
            return false;

        RaycastHit chosen;
        if (left && right)
            chosen = leftHit.distance <= rightHit.distance ? leftHit : rightHit;
        else
            chosen = left ? leftHit : rightHit;

        FillCandidate(chosen, ref result);
        return true;
    }

    bool TrySideProbe(Vector3 feet, Vector3 sideDir, Collider excluded, bool requireVelocityToward, out RaycastHit hit)
    {
        // Two heights; either hit is enough.
        Vector3 o1 = feet + Vector3.up * wallSideProbeHeight;
        Vector3 o2 = feet + Vector3.up * wallSideProbeHeightUpper;

        bool h1 = Physics.Raycast(o1, sideDir, out RaycastHit lowerHit, wallMagnetDistance, climbableLayer, QueryTriggerInteraction.Ignore);
        bool h2 = Physics.Raycast(o2, sideDir, out RaycastHit upperHit, wallMagnetDistance, climbableLayer, QueryTriggerInteraction.Ignore);

        if (!h1 && !h2)
        {
            hit = default;
            return false;
        }

        hit = h1 ? lowerHit : upperHit;

        if (excluded != null && hit.collider == excluded)
            return false;

        if (requireVelocityToward)
        {
            Vector3 vel = cc.velocity;
            vel.y = 0f;
            if (vel.sqrMagnitude > 0.01f)
            {
                if (Vector3.Dot(vel.normalized, sideDir) < chainVelocityAlignment)
                    return false;
            }
        }

        return true;
    }

    void FillCandidate(RaycastHit hit, ref WallRunCandidate c)
    {
        Vector3 n = hit.normal;
        n.y = 0f;
        if (n.sqrMagnitude < 0.0001f)
            n = -transform.forward;
        n.Normalize();

        Vector3 tangent = Vector3.Cross(Vector3.up, n);
        Vector3 vel = cc.velocity;
        vel.y = 0f;
        if (vel.sqrMagnitude > 0.01f && Vector3.Dot(tangent, vel) < 0f)
            tangent = -tangent;
        else if (vel.sqrMagnitude <= 0.01f && Vector3.Dot(tangent, transform.forward) < 0f)
            tangent = -tangent;
        tangent.Normalize();

        c.normal = n;
        c.tangent = tangent;
        c.contactPoint = hit.point;
        c.collider = hit.collider;
        c.distance = hit.distance;
    }

    void EnterWallRun(WallRunCandidate c, bool isGroundedEntry)
    {
        wallNormal = c.normal;
        wallTangent = c.tangent;

        Vector3 feet = transform.position;
        Vector3 snap = new Vector3(c.contactPoint.x, feet.y, c.contactPoint.z) + wallNormal * cc.radius;
        transform.position = snap;
        transform.rotation = Quaternion.LookRotation(wallTangent, Vector3.up);

        lastWallCollider = c.collider;
        // Ground start gets a lift; airborne entry preserves any upward momentum.
        wallVerticalVelocity = isGroundedEntry
            ? wallrunInitialUpVelocity
            : Mathf.Max(0f, cc.velocity.y);

        thirdPersonController.enabled = false;

        if (animator != null && !string.IsNullOrEmpty(wallRunTrigger))
            animator.SetTrigger(wallRunTrigger);

        stateTimer = 0f;
        state = State.WallRunning;
    }

    void TickWallRun()
    {
        stateTimer += Time.deltaTime;

        if (stateTimer >= wallrunMaxDuration)
        {
            DetachFromWall(jumpOff: false);
            return;
        }

        // Re-check wall presence at three heights.
        Vector3 feet = transform.position;
        Vector3 sideDir = -wallNormal;
        float verifyDistance = cc.radius + 0.6f;
        float[] verifyHeights = { wallSideProbeHeight - 0.6f, wallSideProbeHeight, wallSideProbeHeightUpper };
        RaycastHit contactHit = default;
        bool anyHit = false;
        foreach (float h in verifyHeights)
        {
            Vector3 origin = feet + Vector3.up * h;
            if (Physics.Raycast(origin, sideDir, out RaycastHit tryHit, verifyDistance, climbableLayer, QueryTriggerInteraction.Ignore))
            {
                contactHit = tryHit;
                anyHit = true;
                break;
            }
        }
        if (!anyHit)
        {
            DetachFromWall(jumpOff: false);
            return;
        }

        // Ease normal toward the current hit (handles curved walls).
        Vector3 freshNormal = contactHit.normal;
        freshNormal.y = 0f;
        if (freshNormal.sqrMagnitude > 0.0001f)
        {
            freshNormal.Normalize();
            wallNormal = Vector3.Slerp(wallNormal, freshNormal, 0.25f).normalized;
            Vector3 newTangent = Vector3.Cross(Vector3.up, wallNormal);
            if (Vector3.Dot(newTangent, wallTangent) < 0f)
                newTangent = -newTangent;
            wallTangent = newTangent.normalized;
        }

        // Wall jump on fresh press.
        if (jumpPressed)
        {
            inputs.jump = false;
            Vector3 launch = wallNormal * wallJumpAwaySpeed
                           + Vector3.up * wallJumpUpSpeed
                           + wallTangent * (wallrunSpeed * 0.5f);
            BeginWallJumpArc(launch, fromWallrun: true);
            return;
        }

        // Pin horizontal position to wall surface.
        Vector3 pinTarget = new Vector3(contactHit.point.x, feet.y, contactHit.point.z) + wallNormal * cc.radius;
        Vector3 pinDelta = pinTarget - feet;
        pinDelta.y = 0f;

        wallVerticalVelocity += wallrunGravity * Time.deltaTime;

        Vector3 motion = wallTangent * wallrunSpeed + Vector3.up * wallVerticalVelocity;
        motion += pinDelta / Mathf.Max(Time.deltaTime, 0.0001f) * 0.5f;

        cc.Move(motion * Time.deltaTime);

        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(wallTangent, Vector3.up), 12f * Time.deltaTime);

        PinAnimatorIdle();

        // Hitting ground ends the run cleanly; hand back to TPC.
        if (stateTimer > wallrunGroundedGrace && cc.isGrounded)
        {
            thirdPersonController.enabled = true;
            inputs.jump = false;
            lastWallCollider = null;
            wallrunChainActive = false;
            stateTimer = 0f;
            state = State.Idle;
        }
    }

    void DetachFromWall(bool jumpOff)
    {
        Vector3 launch = wallTangent * wallrunSpeed * 0.6f
                       + wallNormal * (jumpOff ? wallJumpAwaySpeed : 1.5f)
                       + Vector3.up * (jumpOff ? wallJumpUpSpeed : 0f);
        BeginWallJumpArc(launch, fromWallrun: true);
    }

    void BeginWallJumpArc(Vector3 launchVelocity, bool fromWallrun)
    {
        airVelocity = launchVelocity;
        if (fromWallrun)
            wallrunChainActive = true;

        if (animator != null && !string.IsNullOrEmpty(wallJumpTrigger))
            animator.SetTrigger(wallJumpTrigger);

        stateTimer = 0f;
        state = State.WallJumping;
    }

    void TickWallJump()
    {
        stateTimer += Time.deltaTime;

        airVelocity.y += wallJumpGravity * Time.deltaTime;
        cc.Move(airVelocity * Time.deltaTime);

        PinAnimatorAirborne();

        bool autoChain = wallrunChainActive;
        bool pressChain = jumpPressed;
        if (autoChain || pressChain)
        {
            Vector3 horizVel = airVelocity;
            horizVel.y = 0f;
            if (horizVel.magnitude >= wallrunMinSpeed * 0.4f &&
                TryFindSideWall(excluded: lastWallCollider,
                                requireVelocityToward: autoChain && !pressChain,
                                out WallRunCandidate c))
            {
                inputs.jump = false;
                EnterWallRun(c, isGroundedEntry: false);
                return;
            }
        }

        if (cc.isGrounded)
        {
            ExitToIdle(reachedGround: true);
            return;
        }

        if (stateTimer >= wallJumpAirTime)
        {
            ExitToIdle(reachedGround: false);
        }
    }

    void ExitToIdle(bool reachedGround)
    {
        thirdPersonController.enabled = true;
        inputs.jump = false;
        activeKind = ParkourKind.None;
        if (reachedGround)
        {
            lastWallCollider = null;
            wallrunChainActive = false;
        }
        stateTimer = 0f;
        state = State.Idle;
    }

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos)
            return;

        Vector3 feet = transform.position;
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f)
            return;
        fwd.Normalize();

        float[] heights = { lowProbeHeight, waistProbeHeight, chestProbeHeight, headProbeHeight, overProbeHeight };
        foreach (float h in heights)
        {
            Vector3 origin = feet + Vector3.up * h;
            bool hit = Application.isPlaying &&
                       Physics.SphereCast(origin, probeSphereRadius, fwd, out _, triggerDistance, climbableLayer, QueryTriggerInteraction.Ignore);
            Gizmos.color = hit ? Color.red : Color.green;
            Gizmos.DrawLine(origin, origin + fwd * triggerDistance);
            Gizmos.DrawWireSphere(origin + fwd * triggerDistance, probeSphereRadius);
        }

        Gizmos.color = new Color(1f, 0.8f, 0f, 0.6f);
        Vector3 contactOrigin = feet + Vector3.up * lowProbeHeight;
        Gizmos.DrawWireSphere(contactOrigin + fwd * contactDistance, 0.08f);

        // Side probes for wallrun (green = clear, red = hit).
        Vector3 right = transform.right;
        float[] sideHeights = { wallSideProbeHeight, wallSideProbeHeightUpper };
        foreach (float h in sideHeights)
        {
            Vector3 o = feet + Vector3.up * h;

            bool hitLeft = Application.isPlaying &&
                Physics.Raycast(o, -right, out _, wallMagnetDistance, climbableLayer, QueryTriggerInteraction.Ignore);
            Gizmos.color = hitLeft ? Color.red : Color.green;
            Gizmos.DrawLine(o, o - right * wallMagnetDistance);
            Gizmos.DrawWireSphere(o - right * wallMagnetDistance, probeSphereRadius);

            bool hitRight = Application.isPlaying &&
                Physics.Raycast(o, right, out _, wallMagnetDistance, climbableLayer, QueryTriggerInteraction.Ignore);
            Gizmos.color = hitRight ? Color.red : Color.green;
            Gizmos.DrawLine(o, o + right * wallMagnetDistance);
            Gizmos.DrawWireSphere(o + right * wallMagnetDistance, probeSphereRadius);
        }

        if (state == State.Executing)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(startPos, risePoint);
            Gizmos.DrawLine(risePoint, landingPos);
            Gizmos.DrawWireSphere(landingPos, 0.2f);
        }

        if (state == State.WallRunning)
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawLine(transform.position, transform.position + wallTangent * 2f);
            Gizmos.DrawLine(transform.position, transform.position + wallNormal * 0.7f);
        }
    }
}
