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
        Cooldown
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

    int animIDSpeed, animIDMotionSpeed, animIDGrounded, animIDJump, animIDFreeFall;

    struct DetectionResult
    {
        public ParkourKind kind;
        public Vector3 ledgeTop;
        public Vector3 wallNormalXZ;
    }

    void Start()
    {
        cc = GetComponent<CharacterController>();
        tpc = GetComponent<ThirdPersonController>();
        inputs = GetComponent<StarterAssetsInputs>();
        animator = GetComponentInChildren<Animator>();
        if (Camera.main != null) cam = Camera.main.transform;

        animIDSpeed = Animator.StringToHash("Speed");
        animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
        animIDGrounded = Animator.StringToHash("Grounded");
        animIDJump = Animator.StringToHash("Jump");
        animIDFreeFall = Animator.StringToHash("FreeFall");
    }

    void Update()
    {
        switch (state)
        {
            case State.Idle:
                TryTrigger();
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
        }
    }

    void TryTrigger()
    {
        if (cam == null && Camera.main != null) cam = Camera.main.transform;
        if (cam == null) return;

        if (!tpc.Grounded) return;
        if (!inputs.jump) return;

        Vector3 vel = cc.velocity; vel.y = 0f;
        if (vel.magnitude < minTriggerSpeed) return;

        Vector3 camFwd = cam.forward; camFwd.y = 0f; camFwd.Normalize();
        Vector3 bodyFwd = transform.forward; bodyFwd.y = 0f; bodyFwd.Normalize();
        if (Vector3.Dot(camFwd, bodyFwd) < cameraAlignmentMin) return;

        if (!TryDetect(out DetectionResult hit)) return;

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

        Vector3 vel = cc.velocity; vel.y = 0f;
        if (vel.magnitude < minTriggerSpeed * 0.5f)
        {
            state = State.Idle;
            stateTimer = 0f;
            return;
        }

        Vector3 feet = transform.position;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) return;
        fwd.Normalize();

        Vector3 origin = feet + Vector3.up * lowProbeHeight;
        if (Physics.Raycast(origin, fwd, out RaycastHit hit, contactDistance, climbableLayer, QueryTriggerInteraction.Ignore))
        {
            Vector3 wallNormal = hit.normal; wallNormal.y = 0f;
            if (wallNormal.sqrMagnitude < 0.0001f) return;
            wallNormal.Normalize();

            Vector3 snapPos = new Vector3(hit.point.x, feet.y, hit.point.z) + wallNormal * cc.radius;
            transform.position = snapPos;

            queuedHit.wallNormalXZ = wallNormal;
            queuedHit.ledgeTop = new Vector3(hit.point.x, queuedHit.ledgeTop.y, hit.point.z);

            EnterExecute(queuedHit);
        }
    }

    bool TryDetect(out DetectionResult result)
    {
        result = default;

        Vector3 feet = transform.position;
        Vector3 fwd = transform.forward; fwd.y = 0f; fwd.Normalize();

        bool low = Probe(feet, fwd, lowProbeHeight, out RaycastHit lowHit);
        bool waist = Probe(feet, fwd, waistProbeHeight, out _);
        bool chest = Probe(feet, fwd, chestProbeHeight, out _);
        bool head = Probe(feet, fwd, headProbeHeight, out _);
        bool over = Probe(feet, fwd, overProbeHeight, out _);

        if (over) return false;
        if (!low) return false;

        RaycastHit anchor = lowHit;

        Vector3 wallNormal = anchor.normal; wallNormal.y = 0f;
        if (wallNormal.sqrMagnitude < 0.0001f) return false;
        wallNormal.Normalize();

        Vector3 ledgeProbeOrigin = anchor.point + fwd * ledgeInset + Vector3.up * 3.0f;
        if (!Physics.Raycast(ledgeProbeOrigin, Vector3.down, out RaycastHit ledgeHit, 4.0f, climbableLayer, QueryTriggerInteraction.Ignore))
            return false;

        if (Vector3.Angle(ledgeHit.normal, Vector3.up) > ledgeMaxSlope) return false;

        float ledgeH = ledgeHit.point.y - feet.y;
        ParkourKind kind;
        if (ledgeH >= 0.3f && ledgeH < mantleMax) kind = ParkourKind.Mantle;
        else if (ledgeH < mediumMax) kind = ParkourKind.MediumClimb;
        else if (ledgeH <= highMax) kind = ParkourKind.HighClimb;
        else return false;

        if (!HasHeadroom(ledgeHit.point, wallNormal)) return false;

        result.kind = kind;
        result.ledgeTop = ledgeHit.point;
        result.wallNormalXZ = wallNormal;

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
        if (animator == null) return;
        animator.SetFloat(animIDSpeed, 0f);
        animator.SetFloat(animIDMotionSpeed, 0f);
        animator.SetBool(animIDGrounded, true);
        animator.SetBool(animIDJump, false);
        animator.SetBool(animIDFreeFall, false);
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
            if (!pushed) break;
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

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Vector3 feet = transform.position;
        Vector3 fwd = transform.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) return;
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

        if (state == State.Executing)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(startPos, risePoint);
            Gizmos.DrawLine(risePoint, landingPos);
            Gizmos.DrawWireSphere(landingPos, 0.2f);
        }
    }
}
