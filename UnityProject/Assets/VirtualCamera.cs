using UnityEngine;

public class VirtualCamera : MonoBehaviour
{
    [Header("Settings")]
    public string targetTag = "TargetBall";
    public float maxViewDistance = 2f;   // Было 5м, теперь 2м (предел видимости YOLO)
    public float maxViewAngle = 20f;    // FOV 40° (±20°) — ОТКАЛИБРОВАНО (W=71.5см при D=100см)

    [Header("Occlusion")]
    [Tooltip("Слои, которые блокируют обзор (стены, мебель). Убедись, что стены на слое Default или Wall")]
    public LayerMask occlusionMask = ~0; // По умолчанию: все слои блокируют обзор

    [Header("Debounce (удержание видимости)")]
    [Tooltip("Секунды удержания после потери мяча из виду — как инерция реальной камеры")]
    public float debounceDuration = 0.3f;

    [Header("Output Data")]
    public float normalizedAngle;    // От -1 до 1
    public float normalizedDistance; // От 0 до 1
    public bool seesBall;
    public float lastKnownBallDirection; // -1 (left), 1 (right), 0 (unknown)

    [HideInInspector]
    public Transform targetBall; // Назначается автоматически из RobotBrain.cs

    // Debounce internals
    private float _lastSeenTime = -100f;
    private float _lastNormalizedAngle;
    private float _lastNormalizedDistance;

    void Start()
    {
        if (targetBall == null)
        {
            FindTarget();
        }
    }

    void Update()
    {
        if (targetBall == null)
        {
            FindTarget();
            if (targetBall == null)
            {
                normalizedAngle = 0f;
                normalizedDistance = 1f;
                seesBall = false;
                return;
            }
        }

        Vector3 directionToBall = targetBall.position - transform.position;
        float distance = directionToBall.magnitude;

        // Расчет угла (в горизонтальной плоскости)
        Vector3 forward = transform.forward;
        forward.y = 0;
        forward.Normalize();

        Vector3 dirHorizontal = directionToBall;
        dirHorizontal.y = 0;
        dirHorizontal.Normalize();

        float angle = Vector3.SignedAngle(forward, dirHorizontal, Vector3.up);
        
        // --- ПРОВЕРКА ВИДИМОСТИ (3 условия) ---
        bool inFOV = (Mathf.Abs(angle) < maxViewAngle);
        bool inRange = (distance < maxViewDistance);
        
        // Fix #2: Raycast — камера НЕ видит сквозь стены!
        // Но ИГНОРИРУЕМ пол — луч от камеры идет вниз к мячу и попал бы в пол раньше мяча.
        bool hasLineOfSight = true;
        if (inFOV && inRange)
        {
            // Стреляем лучом от камеры к мячу
            RaycastHit[] hits = Physics.RaycastAll(transform.position, directionToBall.normalized, distance);
            foreach (var hit in hits)
            {
                // Если луч попал в стену или препятствие РАНЬШЕ мяча — обзор заблокирован
                if (hit.collider.CompareTag("Wall") || hit.collider.CompareTag("Obstacle"))
                {
                    hasLineOfSight = false;
                    break;
                }
            }
        }

        bool directlySeesBall = inFOV && inRange && hasLineOfSight;

        if (directlySeesBall)
        {
            // --- Мяч ВИДЕН прямо сейчас ---
            _lastSeenTime = Time.time;

            // Нормализация угла от -1 до 1
            normalizedAngle = Mathf.Clamp(angle / maxViewAngle, -1f, 1f);
            normalizedDistance = Mathf.Clamp01(distance / maxViewDistance);

            seesBall = true;
            lastKnownBallDirection = Mathf.Sign(normalizedAngle);
            if (lastKnownBallDirection == 0) lastKnownBallDirection = 1f;

            // Сохраняем для Debounce
            _lastNormalizedAngle = normalizedAngle;
            _lastNormalizedDistance = normalizedDistance;

            Debug.DrawLine(transform.position, targetBall.position, Color.green);
        }
        else
        {
            // --- Мяч НЕ виден ---
            // Fix #3: Debounce — удерживаем последние значения, чтобы LSTM получал плавный fade-out
            if (Time.time - _lastSeenTime <= debounceDuration)
            {
                seesBall = true; // Удерживаем видимость
                normalizedAngle = _lastNormalizedAngle;
                normalizedDistance = _lastNormalizedDistance;
            }
            else
            {
                seesBall = false;
                normalizedAngle = lastKnownBallDirection; // Остаточный хинт: ±1 = "последний раз мяч был ТАМ"
                normalizedDistance = 1f; // Далеко = 1.0 (а не 0!)
            }

            Debug.DrawLine(transform.position, transform.position + transform.forward * maxViewDistance, Color.red);
        }
    }

    private void FindTarget()
    {
        GameObject ball = GameObject.FindWithTag(targetTag);
        if (ball != null)
        {
            targetBall = ball.transform;
        }
    }

    // --- Fix #4: Gizmos — FOV конус видимости в Scene View ---
    private void OnDrawGizmosSelected()
    {
        // Цвет: Зелёный если видит мяч, красный если нет
        Gizmos.color = seesBall ? new Color(0f, 1f, 0f, 0.15f) : new Color(1f, 0f, 0f, 0.15f);

        // Рисуем FOV конус
        Vector3 origin = transform.position;
        float dist = maxViewDistance;

        // Левая граница FOV
        Vector3 leftDir = Quaternion.Euler(0, -maxViewAngle, 0) * transform.forward;
        // Правая граница FOV
        Vector3 rightDir = Quaternion.Euler(0, maxViewAngle, 0) * transform.forward;

        // Линии границ
        Gizmos.color = seesBall ? Color.green : Color.red;
        Gizmos.DrawRay(origin, leftDir * dist);
        Gizmos.DrawRay(origin, rightDir * dist);

        // Дуга (сегменты по 5 градусов)
        int segments = (int)(maxViewAngle * 2 / 5);
        Vector3 prevPoint = origin + leftDir * dist;
        for (int i = 1; i <= segments; i++)
        {
            float t = (float)i / segments;
            float currentAngle = Mathf.Lerp(-maxViewAngle, maxViewAngle, t);
            Vector3 dir = Quaternion.Euler(0, currentAngle, 0) * transform.forward;
            Vector3 point = origin + dir * dist;
            Gizmos.DrawLine(prevPoint, point);
            prevPoint = point;
        }

        // Заполненный треугольник (полупрозрачный)
        Gizmos.color = seesBall ? new Color(0f, 1f, 0f, 0.08f) : new Color(1f, 0f, 0f, 0.08f);
        for (int i = 1; i <= segments; i++)
        {
            float t0 = (float)(i - 1) / segments;
            float t1 = (float)i / segments;
            float a0 = Mathf.Lerp(-maxViewAngle, maxViewAngle, t0);
            float a1 = Mathf.Lerp(-maxViewAngle, maxViewAngle, t1);
            Vector3 d0 = Quaternion.Euler(0, a0, 0) * transform.forward * dist;
            Vector3 d1 = Quaternion.Euler(0, a1, 0) * transform.forward * dist;
            
            // Mesh triangle via lines (Gizmos don't support filled triangles, but the lines + color give a good visual)
            Gizmos.DrawLine(origin, origin + d0);
            Gizmos.DrawLine(origin, origin + d1);
        }

        // Центральная линия (куда смотрит камера)
        Gizmos.color = Color.yellow;
        Gizmos.DrawRay(origin, transform.forward * dist);

        // Если мяч виден — рисуем линию к нему
        if (seesBall && targetBall != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(origin, targetBall.position);
            Gizmos.DrawWireSphere(targetBall.position, 0.15f);
        }
    }
}
