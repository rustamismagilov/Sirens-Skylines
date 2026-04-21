using UnityEngine;
using UnityEngine.UI;
using StarterAssets;

[RequireComponent(typeof(ThirdPersonController))]
[RequireComponent(typeof(StarterAssetsInputs))]
public class StaminaController : MonoBehaviour
{
    [Header("Stamina")]
    [SerializeField] float maxStamina = 100f;
    [SerializeField] float sprintDurationSeconds = 30f;
    [SerializeField] float fullRegenSeconds = 15f;
    [SerializeField, Range(0f, 1f)] float unlockThreshold = 0.25f;

    [Header("UI")]
    [SerializeField] Slider staminaSlider;

    ThirdPersonController thirdPersonController;
    StarterAssetsInputs inputs;

    float stamina;
    float originalSprintSpeed;
    bool sprintLocked;

    public float StaminaNormalized => stamina / maxStamina;

    void Start()
    {
        thirdPersonController = GetComponent<ThirdPersonController>();
        inputs = GetComponent<StarterAssetsInputs>();

        originalSprintSpeed = thirdPersonController.SprintSpeed;
        stamina = maxStamina;

        if (staminaSlider != null)
        {
            staminaSlider.minValue = 0f;
            staminaSlider.maxValue = 1f;
            staminaSlider.value = 1f;
        }
    }

    void Update()
    {
        float drainPerSecond = maxStamina / sprintDurationSeconds;
        float regenPerSecond = maxStamina / fullRegenSeconds;

        bool isSprintingNow = inputs.sprint && !sprintLocked && stamina > 0f;

        if (isSprintingNow)
        {
            stamina -= drainPerSecond * Time.deltaTime;
        }
        else if (!inputs.sprint)
        {
            stamina += regenPerSecond * Time.deltaTime;
        }

        stamina = Mathf.Clamp(stamina, 0f, maxStamina);

        if (!sprintLocked && stamina <= 0f)
        {
            thirdPersonController.SprintSpeed = thirdPersonController.MoveSpeed;
            sprintLocked = true;
        }
        else if (sprintLocked && stamina >= maxStamina * unlockThreshold)
        {
            thirdPersonController.SprintSpeed = originalSprintSpeed;
            sprintLocked = false;
        }

        if (staminaSlider != null)
        {
            staminaSlider.value = StaminaNormalized;
        }
    }

    void OnDisable()
    {
        if (thirdPersonController != null && sprintLocked)
        {
            thirdPersonController.SprintSpeed = originalSprintSpeed;
            sprintLocked = false;
        }
    }
}
