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
    [Tooltip("Minimum horizontal speed (m/s) required to trigger parkour.")]
    [SerializeField] float minTriggerSpeed = 6.0f;
    [Tooltip("Detection reach. Jump press while a wall is within this distance queues the parkour.")]
    [SerializeField] float triggerDistance = 0.7f;
    [Tooltip("Dot product threshold between camera forward (XZ) and player forward (XZ).")]
    [SerializeField] float cameraAlignmentMin = 0.7f;

    [Header("Approach")]
    [Tooltip("Once queued, wait until the wall is within this distance before snapping to it and starting the climb.")]
    [SerializeField] float contactDistance = 0.5f;
    [Tooltip("Cancel a queued parkour if the player hasn't reached the wall within this many seconds.")]
    [SerializeField] float queueTimeout = 1.0f;

    [Header("Layers")]
    [Tooltip("Layer for climbable walls/obstacles. Set up in Tags and Layers; assign to wall GameObjects.")]
    [SerializeField] LayerMask climbableLayer;
    [Tooltip("Layers that block headroom at the ledge top (typically same as ThirdPersonController.GroundLayers).")]
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
    [Tooltip("How far back from the ledge edge the feet should land.")]
    [SerializeField] float ledgeInset = 0.35f;
    [Tooltip("Reject ledges sloped steeper than this (degrees).")]
    [SerializeField] float ledgeMaxSlope = 35f;
    [SerializeField] float headroomHeight = 1.80f;
    [SerializeField] float headroomRadius = 0.35f;

    [Header("Motion Durations (s)")]
    [SerializeField] float mantleDuration = 0.45f;
    [SerializeField] float mediumDuration = 0.80f;
    [SerializeField] float highDuration = 1.20f;
    [SerializeField] float recoveryDuration = 0.15f;
    [SerializeField] float cooldownDuration = 0.25f;

    [Header("Animator Triggers (optional — leave empty to skip)")]
    [SerializeField] string mantleTrigger = "Mantle";
    [SerializeField] string mediumTrigger = "ClimbMedium";
    [SerializeField] string highTrigger = "ClimbHigh";
    [SerializeField] string wallRunTrigger = "WallRun";
    [SerializeField] string wallJumpTrigger = "WallJump";

    [Header("Wall Run — Detection")]
    [Tooltip("Minimum horizontal speed (m/s) required for an airborne magnet. Grounded starts bypass this.")]
    [SerializeField] float wallrunMinSpeed = 4.0f;
    [Tooltip("Magnet range — sideways distance a wall must be within to snap and start wallrunning.")]
    [SerializeField] float wallMagnetDistance = 2.0f;
    [Tooltip("Forward ray length for the grounded-start 'nothing in front' check.")]
    [SerializeField] float wallMagnetForwardClearance = 1.0f;
    [Tooltip("Height used for side wall probe raycasts.")]
    [SerializeField] float wallSideProbeHeight = 1.00f;
    [Tooltip("Second probe height — wall must exist at both probe heights to count as a runnable wall.")]
    [SerializeField] float wallSideProbeHeightUpper = 1.50f;
    [Tooltip("If airborne and the wall is within this sideways distance when Space is pressed, do an instant wall-jump instead of a wallrun. Keep this close to cc.radius (~0.4) so only true contact triggers the kick-off.")]
    [SerializeField] float wallJumpContactDistance = 0.55f;
    [Tooltip("How much of the player's horizontal velocity must be directed toward the wall (dot product, 0..1) to allow the airborne contact wall-jump. Prevents accidental 'double jumps' when pressing Space while near geometry without approaching it.")]
    [SerializeField] float airJumpApproachDot = 0.3f;

    [Header("Wall Run — Motion")]
    [Tooltip("Max seconds on a wall before gravity pulls the player off.")]
    [SerializeField] float wallrunMaxDuration = 1.5f;
    [Tooltip("Downward acceleration while wallrunning (m/s^2, negative).")]
    [SerializeField] float wallrunGravity = -3.0f;
    [Tooltip("Forward speed along the wall (m/s).")]
    [SerializeField] float wallrunSpeed = 7.0f;
    [Tooltip("Initial upward velocity when entering wallrun — lifts the player off the ground for standing starts.")]
    [SerializeField] float wallrunInitialUpVelocity = 2.0f;
    [Tooltip("Seconds after entering wallrun during which cc.isGrounded is ignored (prevents immediate detach on ground start).")]
    [SerializeField] float wallrunGroundedGrace = 0.25f;
    [Tooltip("Seconds after entering wallrun during which a jump press is ignored (prevents accidental wall-jump right after chain/magnet).")]
    [SerializeField] float wallrunJumpGrace = 0.15f;
    [Tooltip("Auto-chain: when airborne AFTER a wallrun, how well the horizontal velocity must align with the side-probe direction (0..1). Set higher to make 'not aiming at next wall' exit the chain more reliably.")]
    [SerializeField] float chainVelocityAlignment = 0.25f;

    [Header("Wall Run — Wall Jump")]
    [Tooltip("Outward launch speed away from the wall on wall jump.")]
    [SerializeField] float wallJumpAwaySpeed = 6.0f;
    [Tooltip("Upward launch speed on wall jump.")]
    [SerializeField] float wallJumpUpSpeed = 5.0f;
    [Tooltip("Grace window after a wall jump during which magnet detection can attach to another wall.")]
    [SerializeField] float wallJumpAirTime = 1.0f;
    [Tooltip("Gravity applied during the wall-jump/airborne-between-walls arc (m/s^2, negative).")]
    [SerializeField] float wallJumpGravity = -12.0f;

    [Header("Debug")]
    [SerializeField] bool drawGizmos = true;

    CharacterController cc;
    ThirdPersonController tpc;
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
        tpc = GetComponent<ThirdPersonController>();
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
        jumpPressed = inputs.jump && !lastJumpHeld;

        switch (state)
        {
            case State.Idle:
                if (tpc.Grounded)
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

        if (!tpc.Grounded)
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
        if (Physics.Raycast(origin, fwd, out RaycastHit hit, contactDistance, climbableLayer, QueryTriggerInteraction.Ignore))
        {
            Vector3 n = hit.normal;
            n.y = 0f;
            if (n.sqrMagnitude < 0.0001f)
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

        tpc.enabled = false;
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
        tpc.enabled = true;
        inputs.jump = false;
        activeKind = ParkourKind.None;
    }

    static float SmoothStep01(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    // --------------------------------------------------------------
    // Wall Run
    // --------------------------------------------------------------

    void TryWallRunTrigger()
    {
        if (tpc.Grounded)
        {
            // Grounded start requires a FRESH press so a held jump doesn't retrigger every frame.
            if (!jumpPressed) return;

            if (inputs.move.y < 0.5f) return;
            if (IsForwardBlocked()) return;

            if (!TryFindSideWall(excluded: lastWallCollider, requireVelocityToward: false, out WallRunCandidate c))
                return;

            inputs.jump = false;
            EnterWallRun(c, isGroundedEntry: true);
        }
        else
        {
            // Airborne magnet accepts HELD Space (not just a fresh press) — this is the main
            // "I'm already in the air with Space held, let me grip the wall on approach" entry.
            if (!inputs.jump) return;

            Vector3 vel = cc.velocity;
            vel.y = 0f;
            if (vel.magnitude < 0.5f) return;

            // Without an active chain, don't re-attach to the wall we just left.
            Collider excluded = lastWallCollider;

            if (!TryFindSideWall(excluded: excluded, requireVelocityToward: false, out WallRunCandidate c))
                return;

            Vector3 towardWall = -c.normal;
            float approach = (vel.sqrMagnitude > 0.01f)
                ? Vector3.Dot(vel.normalized, towardWall)
                : 0f;

            inputs.jump = false;

            // Contact wall-jump (the one with the +5 upward launch) stays gated on a FRESH press.
            // A held jump leaking from a grounded TPC jump no longer triggers the boost — it
            // magnets to a wallrun instead, eliminating the "double jump" feel.
            bool canAirWallJump = jumpPressed
                                  && c.distance <= wallJumpContactDistance
                                  && approach >= airJumpApproachDot;

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

        tpc.enabled = false;
        BeginWallJumpArc(launch, fromWallrun: false);
    }

    bool IsForwardBlocked()
    {
        Vector3 feet = transform.position;
        Vector3 fwd = transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f)
            return true;
        fwd.Normalize();
        Vector3 origin = feet + Vector3.up * wallSideProbeHeight;
        return Physics.SphereCast(origin, probeSphereRadius, fwd, out _,
            wallMagnetForwardClearance, climbableLayer | obstructionLayers,
            QueryTriggerInteraction.Ignore);
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
        // Try both heights; EITHER hit counts as "wall is there".
        // Requiring both caused ~80% of airborne attempts to fail because short walls (1.8m)
        // with the player near jump-peak put the upper probe above the wall top.
        Vector3 o1 = feet + Vector3.up * wallSideProbeHeight;
        Vector3 o2 = feet + Vector3.up * wallSideProbeHeightUpper;

        bool h1 = Physics.Raycast(o1, sideDir, out RaycastHit lowerHit, wallMagnetDistance, climbableLayer, QueryTriggerInteraction.Ignore);
        bool h2 = Physics.Raycast(o2, sideDir, out RaycastHit upperHit, wallMagnetDistance, climbableLayer, QueryTriggerInteraction.Ignore);

        if (!h1 && !h2)
        {
            hit = default;
            return false;
        }

        // Prefer the lower probe (more likely to be within wall geometry). Fall back to upper.
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
        Vector3 originalPos = transform.position;
        Quaternion originalRot = transform.rotation;

        wallNormal = c.normal;
        wallTangent = c.tangent;

        Vector3 feet = originalPos;
        Vector3 snap = new Vector3(c.contactPoint.x, feet.y, c.contactPoint.z) + wallNormal * cc.radius;
        transform.position = snap;
        transform.rotation = Quaternion.LookRotation(wallTangent, Vector3.up);

        // Sanity: make sure the wall is actually detectable at one of the verify heights from the snapped position.
        if (!ProbeWallAtAnyHeight(snap, -wallNormal, c.collider))
        {
            transform.position = originalPos;
            transform.rotation = originalRot;
            return;
        }

        lastWallCollider = c.collider;
        wallVerticalVelocity = isGroundedEntry ? wallrunInitialUpVelocity : 0f;

        tpc.enabled = false;

        if (animator != null && !string.IsNullOrEmpty(wallRunTrigger))
            animator.SetTrigger(wallRunTrigger);

        stateTimer = 0f;
        state = State.WallRunning;
    }

    // True if the wall is detected from the given feet-level origin at any of the three verify heights.
    bool ProbeWallAtAnyHeight(Vector3 feet, Vector3 sideDir, Collider expected)
    {
        float verifyDistance = cc.radius + 0.6f;
        float[] heights = { wallSideProbeHeight - 0.6f, wallSideProbeHeight, wallSideProbeHeightUpper };
        foreach (float h in heights)
        {
            Vector3 origin = feet + Vector3.up * h;
            if (Physics.Raycast(origin, sideDir, out RaycastHit hit, verifyDistance, climbableLayer, QueryTriggerInteraction.Ignore))
            {
                if (expected == null || hit.collider == expected)
                    return true;
            }
        }
        return false;
    }

    void TickWallRun()
    {
        stateTimer += Time.deltaTime;

        if (stateTimer >= wallrunMaxDuration)
        {
            DetachFromWall(jumpOff: false);
            return;
        }

        // Verify wall is still there — try three probe heights (below feet, hip, chest). Any hit keeps us attached.
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

        // Refresh normal smoothly so curves aren't a problem
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

        // Wall jump — fresh Space press only (edge) and grace-gated so a chain-entry press
        // doesn't immediately fire.
        if (stateTimer > wallrunJumpGrace && jumpPressed)
        {
            inputs.jump = false;
            Vector3 launch = wallNormal * wallJumpAwaySpeed
                           + Vector3.up * wallJumpUpSpeed
                           + wallTangent * (wallrunSpeed * 0.5f);
            BeginWallJumpArc(launch, fromWallrun: true);
            return;
        }

        // Keep the player pinned to the wall horizontally
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

        if (stateTimer > wallrunGroundedGrace && cc.isGrounded)
        {
            DetachFromWall(jumpOff: false);
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
        tpc.enabled = true;
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

    // --------------------------------------------------------------
    // Gizmos
    // --------------------------------------------------------------

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

        // Side wall probes (wallrun detection): green line + end sphere, red when hit.
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
