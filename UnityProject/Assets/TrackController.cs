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

    private Rigidbody rb;
    private float targetLinear = 0f;
    private float targetAngular = 0f;
    private float smoothLinear = 0f;
    private float smoothAngular = 0f;

    void Start()
    {
        rb = GetComponent<Rigidbody>();

        // 1. Заморозка вращений по X и Z (оставляем Y для поворотов)
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        
        // Рекомендуемые настройки для стабильности
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.linearDamping = 5f;   // Гасим дрейф
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

        // 2. Поворот (Y-вращение)
        float yawDelta = smoothAngular * turnSpeed * Time.fixedDeltaTime;
        Quaternion newRot = rb.rotation * Quaternion.Euler(0f, yawDelta, 0f);
        rb.MoveRotation(newRot);

        // 3. Перемещение (Вперед/Назад)
        Vector3 move = transform.forward * (smoothLinear * moveSpeed);
        rb.MovePosition(rb.position + move * Time.fixedDeltaTime);

        // 4. Обнуляем остаточную скорость от физики во избежание дрейфа
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }
}
