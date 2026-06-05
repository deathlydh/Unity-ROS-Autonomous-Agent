using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class TrackController : MonoBehaviour
{
    [Header("Настройки движения")]
    [Tooltip("Скорость вперед/назад (м/с)")]
    public float moveSpeed = 0.57f;

    [Tooltip("Скорость поворота (градусов/с)")]
    public float turnSpeed = 120f;

    [Header("Плавность")]
    [Range(0f, 0.95f)]
    [Tooltip("Инерция разгона (0 = мгновенно, 1 = бесконечно)")]
    public float smoothing = 0.05f;

    [Header("Sim-to-Real: Motor Model")]
    [Tooltip("Мёртвая зона мотора (реальный MIN_MOTOR_PWM=35 → 0.35). Мотор не крутится при PWM ниже этого порога.")]
    [Range(0f, 0.5f)]
    public float motorDeadzone = 0.35f;

    [Tooltip("Макс изменение скорости за шаг (реальный MAX_PWM_STEP=15/100=0.15). Ограничивает ускорение.")]
    [Range(0.01f, 1f)]
    public float maxAccelPerStep = 0.15f;

    private Rigidbody rb;
    private float targetLinear = 0f;
    private float targetAngular = 0f;
    private float smoothLinear = 0f;
    private float smoothAngular = 0f;
    private float prevSmooth = 0f; // Для рампы ускорения

    void Start()
    {
        rb = GetComponent<Rigidbody>();

        // 1. Заморозка вращений по X и Z (оставляем Y для поворотов)
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        
        // Рекомендуемые настройки для стабильности
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        // linearDamping гасит инерцию вместо обнуления velocity
        rb.linearDamping = 8f;
        rb.angularDamping = 10f;
    }

    /// <summary>
    /// Задает целевую скорость движения (от -1 до 1).
    /// </summary>
    public void Move(float linearInput, float angularInput)
    {
        targetLinear = linearInput;
        targetAngular = angularInput;
    }

    void FixedUpdate()
    {
        // 1. Сглаживание входов (инерция)
        float t = 1f - smoothing;
        smoothLinear = Mathf.Lerp(smoothLinear, targetLinear, t);
        smoothAngular = Mathf.Lerp(smoothAngular, targetAngular, t);

        // 2. МЁРТВАЯ ЗОНА: реальный мотор не крутится при PWM < MIN_MOTOR_PWM (35%)
        // Агент должен выучить что слабый газ = стоять на месте
        float effectiveLinear = smoothLinear;
        if (Mathf.Abs(effectiveLinear) < motorDeadzone)
            effectiveLinear = 0f;

        // v18 FIX: Angular deadzone — без неё smoothAngular=0.05 × turnSpeed=120 = 6°/сек = заметное кручение.
        // ВАЖНО: порог 0.15 (мягче чем linear=0.35), т.к. модель обучена БЕЗ angular deadzone
        // и даёт мелкие коррекции (0.2-0.3). Deadzone 0.35 убивает всё рулевое управление!
        if (Mathf.Abs(smoothAngular) < 0.15f)
            smoothAngular = 0f;

        // 3. РАМПА УСКОРЕНИЯ: реальный MAX_PWM_STEP = 15 из 100 за тик
        // Ограничиваем скорость нарастания чтобы агент учился плавному управлению
        float delta = effectiveLinear - prevSmooth;
        if (Mathf.Abs(delta) > maxAccelPerStep)
        {
            effectiveLinear = prevSmooth + Mathf.Sign(delta) * maxAccelPerStep;
        }
        prevSmooth = effectiveLinear;

        // 4. Поворот (Y-вращение)
        float yawDelta = smoothAngular * turnSpeed * Time.fixedDeltaTime;
        Quaternion newRot = rb.rotation * Quaternion.Euler(0f, yawDelta, 0f);
        rb.MoveRotation(newRot);

        // 5. Перемещение (Вперед/Назад)
        Vector3 move = transform.forward * (effectiveLinear * moveSpeed);
        rb.MovePosition(rb.position + move * Time.fixedDeltaTime);

        // 6. НЕ обнуляем velocity — пусть linearDamping = 8 гасит инерцию естественно
        // Это даёт реалистичное проскальзывание при торможении (реальный робот 2.5 кг)
    }
}
