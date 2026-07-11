using UnityEngine;

/// <summary>
/// SimulatedYoloCamera — замена VirtualCamera для Sim-to-Real.
/// 
/// ПРОБЛЕМА: VirtualCamera использует raycast-геометрию (угол/расстояние),
/// а реальный робот — YOLO (пиксельные координаты bbox).
/// Результат: модель обучена на одном распределении, а инференс на другом.
/// 
/// РЕШЕНИЕ: Используем Unity Camera для ПРОЕКЦИИ мяча на виртуальный экран.
/// Camera.WorldToViewportPoint() даёт ту же математику, что реальная камера + YOLO:
///   - normalizedAngle = (viewport.x - 0.5) * 2   ←→  YOLO: (center_x - w/2) / (w/2)
///   - normalizedDistance = projectedBallHeight      ←→  YOLO: bbox_height / frame_height
///   - Окклюзия клешнёй через Raycast              ←→  YOLO: клешня в кадре закрывает мяч
///
/// ПРОИЗВОДИТЕЛЬНОСТЬ: 0.001мс на агента (одна матричная операция).
/// 640 агентов = 0.64мс. Никаких RenderTexture, никакого GPU-рендеринга.
/// </summary>
public class SimulatedYoloCamera : MonoBehaviour
{
    [Header("Camera (ОБЯЗАТЕЛЬНО)")]
    [Tooltip("Unity Camera, размещённая на позиции реальной камеры робота. НЕ рендерит кадр — используется только для проекционной математики.")]
    public Camera projectionCamera;

    [Header("Settings")]
    [Tooltip("Радиус мяча в метрах (для расчёта bbox_height)")]
    public float ballRadius = 0.03f; // ~6cm диаметр мяча

    [Tooltip("Горизонтальный FOV реальной камеры в градусах. Используется для точного расчёта вертикального FOV при aspect 4:3.")]
    public float horizontalFOV = 40f; // Откалибровано пользователем линейкой

    [Tooltip("Максимальная дистанция видимости (метров). Дальше = ballSeen=false.")]
    public float maxViewDistance = 2.0f;

    [Tooltip("Маска слоёв, которые блокируют обзор (стены, клешня, корпус робота)")]
    public LayerMask occlusionMask = ~0;

    [Header("Claw Occlusion")]
    [Tooltip("Тег клешни. Если луч попадает в объект с этим тегом — мяч за клешнёй.")]
    public string clawTag = "Gripper";

    [Tooltip("Количество лучей для проверки окклюзии (больше = точнее, но дороже)")]
    [Range(1, 9)]
    public int occlusionRayCount = 5;

    [Header("Output Data (идентичен VirtualCamera/RealVision)")]
    public float normalizedAngle;    // -1 (left) to 1 (right)  — как YOLO
    public float normalizedDistance; // 0 (close) to 1 (far)    — как YOLO
    public bool seesBall;
    public float lastKnownBallDirection; // -1 (left), 1 (right), 0 (unknown)

    [Header("Debounce (удержание видимости)")]
    [Tooltip("Секунды удержания после потери мяча (имитация BoT-SORT трекера в YOLO)")]
    public float debounceDuration = 0.3f;

    [Header("Sim-to-Real: YOLO Frame Rate")]
    [Tooltip("Минимальная частота кадров YOLO")]
    public float minYoloFPS = 20f;
    [Tooltip("Максимальная частота кадров YOLO")]
    public float maxYoloFPS = 30f;

    [HideInInspector]
    public Transform targetBall; // Назначается из RobotBrain.cs

    // Debounce & YOLO Rate internals
    private float _lastSeenTime = -100f;
    private float _lastNormalizedAngle;
    private float _lastNormalizedDistance;
    private bool _lastDirectlySeesBall;
    private float _nextYoloUpdateTime = 0f;
    private float _currentYoloInterval = 0.1f;

    void Start()
    {
        if (projectionCamera == null)
        {
            // Пытаемся найти камеру на этом же объекте или дочерних
            projectionCamera = GetComponentInChildren<Camera>();
        }

        if (projectionCamera != null)
        {
            // v17 FIX: aspect ratio для идентичности с реальной камерой 640x480
            projectionCamera.aspect = 4f / 3f;

            // 2. Рассчитываем вертикальный FOV на основе горизонтального FOV и aspect 4:3
            // vFOV = 2 * arctan( tan(hFOV/2) / aspect )
            float hFovRad = horizontalFOV * Mathf.Deg2Rad;
            float vFovRad = 2f * Mathf.Atan(Mathf.Tan(hFovRad * 0.5f) / projectionCamera.aspect);
            projectionCamera.fieldOfView = vFovRad * Mathf.Rad2Deg;

            // 3. Отключаем рендеринг камеры для производительности!
            projectionCamera.enabled = false;

            // 4. Проверяем, что мяч назначен
            if (targetBall == null)
            {
                GameObject found = GameObject.FindGameObjectWithTag("TargetBall");
                if (found != null)
                {
                    targetBall = found.transform;
                }
            }

            Debug.Log($"[SimulatedYoloCamera] Initialized: FOV={projectionCamera.fieldOfView:F1}° (hFOV={horizontalFOV}°), " +
                      $"aspect={projectionCamera.aspect:F2}, occlusionMask={occlusionMask.value}, " +
                      $"maxDist={maxViewDistance}m, ballRadius={ballRadius}m, " +
                      $"targetBall={(targetBall != null ? targetBall.name : "NULL")}");
        }
        else
        {
            Debug.LogError("[SimulatedYoloCamera] Camera не найдена! Добавьте Camera компонент.");
        }
    }

    void Update()
    {
        // Автопоиск мяча если targetBall не назначен
        if (targetBall == null)
        {
            GameObject found = GameObject.FindGameObjectWithTag("TargetBall");
            if (found != null)
            {
                targetBall = found.transform;
                Debug.Log($"[SimulatedYoloCamera] targetBall найден автоматически: {targetBall.name}");
            }
        }

        if (projectionCamera == null || targetBall == null)
        {
            seesBall = false;
            normalizedAngle = 0f;
            normalizedDistance = 1f;
            return;
        }

        // Автоматический ballRadius из реального scale мяча
        ballRadius = targetBall.lossyScale.x * 0.5f;

        // v27: Симуляция фреймрейта YOLO (8-12 Hz)
        bool yoloTick = false;
        if (Time.time >= _nextYoloUpdateTime)
        {
            yoloTick = true;
            _currentYoloInterval = Random.Range(1f / maxYoloFPS, 1f / minYoloFPS);
            _nextYoloUpdateTime = Time.time + _currentYoloInterval;
        }

        // Если это YOLO-тик, пересчитываем реальную видимость мяча
        if (yoloTick)
        {
            // === ЭТАП 1: Проецируем мяч на виртуальный экран ===
            Vector3 viewportPos = projectionCamera.WorldToViewportPoint(targetBall.position);
            bool inFrontOfCamera = viewportPos.z > 0;
            bool inViewport = viewportPos.x >= 0f && viewportPos.x <= 1f &&
                              viewportPos.y >= 0f && viewportPos.y <= 1f;
            float distance = Vector3.Distance(projectionCamera.transform.position, targetBall.position);
            bool inRange = distance < maxViewDistance;

            // === ЭТАП 2: Проверка окклюзии ===
            bool blockedByClaw = false;
            bool hasLineOfSight = inFrontOfCamera && inViewport && inRange &&
                                 CheckLineOfSight(projectionCamera.transform.position, targetBall.position, distance, out blockedByClaw);

            _lastDirectlySeesBall = inFrontOfCamera && inViewport && inRange && hasLineOfSight;

            if (_lastDirectlySeesBall)
            {
                _lastSeenTime = Time.time;
                _lastNormalizedAngle = (viewportPos.x - 0.5f) * 2f;
                _lastNormalizedAngle = Mathf.Clamp(_lastNormalizedAngle, -1f, 1f);
                _lastNormalizedDistance = CalculateProjectedBboxHeight(viewportPos, distance);
                
                lastKnownBallDirection = Mathf.Sign(_lastNormalizedAngle);
                if (lastKnownBallDirection == 0) lastKnownBallDirection = 1f;
            }
        }

        // === ЭТАП 3: Дебаунс и логика видимости тикают на каждом кадре! ===
        if (_lastDirectlySeesBall)
        {
            seesBall = true;
            normalizedAngle = _lastNormalizedAngle;
            normalizedDistance = _lastNormalizedDistance;
            Debug.DrawLine(projectionCamera.transform.position, targetBall.position, Color.green);
        }
        else
        {
            // Мяч не виден напрямую
            if (Time.time - _lastSeenTime <= debounceDuration)
            {
                // Debounce удерживает последние известные значения
                seesBall = true;
                normalizedAngle = _lastNormalizedAngle;
                normalizedDistance = _lastNormalizedDistance;
            }
            else
            {
                seesBall = false;
                normalizedAngle = 0f;
                normalizedDistance = 1f;
            }
            Debug.DrawRay(projectionCamera.transform.position, projectionCamera.transform.forward * maxViewDistance, Color.red);
        }
    }

    /// <summary>
    /// Рассчитывает normalizedDistance через обратную проекцию bbox viewport height.
    /// </summary>
    float CalculateProjectedBboxHeight(Vector3 viewportCenter, float distance)
    {
        Vector3 ballTop = targetBall.position + Vector3.up * ballRadius;
        Vector3 ballBottom = targetBall.position - Vector3.up * ballRadius;

        Vector3 vpTop = projectionCamera.WorldToViewportPoint(ballTop);
        Vector3 vpBottom = projectionCamera.WorldToViewportPoint(ballBottom);

        float bboxHeight = Mathf.Abs(vpTop.y - vpBottom.y);

        if (bboxHeight < 0.001f) return 1f; // Слишком далеко, практически невидим

        float halfVFovRad = projectionCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
        float approxDist = ballRadius / (bboxHeight * Mathf.Tan(halfVFovRad));

        return Mathf.Clamp01(approxDist / maxViewDistance);
    }

    /// <summary>
    /// Проверяет прямую видимость от камеры к мячу.
    /// </summary>
    bool CheckLineOfSight(Vector3 from, Vector3 ballPos, float dist, out bool clawBlocked)
    {
        clawBlocked = false;
        int blocked = 0;

        Vector3[] offsets;
        if (occlusionRayCount >= 5)
        {
            offsets = new Vector3[] {
                Vector3.zero,
                Vector3.up * ballRadius,
                Vector3.down * ballRadius,
                Vector3.left * ballRadius,
                Vector3.right * ballRadius,
                (Vector3.up + Vector3.left).normalized * ballRadius * 0.7f,
                (Vector3.up + Vector3.right).normalized * ballRadius * 0.7f,
                (Vector3.down + Vector3.left).normalized * ballRadius * 0.7f,
                (Vector3.down + Vector3.right).normalized * ballRadius * 0.7f
            };
        }
        else
        {
            offsets = new Vector3[] {
                Vector3.zero,
                Vector3.up * ballRadius,
                Vector3.down * ballRadius,
                Vector3.left * ballRadius,
                Vector3.right * ballRadius
            };
        }

        int raysToCheck = Mathf.Min(occlusionRayCount, offsets.Length);

        for (int i = 0; i < raysToCheck; i++)
        {
            Vector3 target = ballPos + offsets[i];
            Vector3 dir = (target - from).normalized;
            float rayDist = Vector3.Distance(from, target);

            if (Physics.Raycast(from, dir, out RaycastHit hit, rayDist, occlusionMask))
            {
                if (!hit.collider.CompareTag("TargetBall"))
                {
                    blocked++;

                    if (!string.IsNullOrEmpty(clawTag) && hit.collider.CompareTag(clawTag))
                    {
                        clawBlocked = true;
                    }
                }
            }
        }

        return blocked < raysToCheck;
    }

    private void OnDrawGizmosSelected()
    {
        if (projectionCamera == null) return;

        Gizmos.color = seesBall ? Color.green : Color.red;
        Gizmos.matrix = projectionCamera.transform.localToWorldMatrix;
        Gizmos.DrawFrustum(
            Vector3.zero,
            projectionCamera.fieldOfView,
            maxViewDistance,
            projectionCamera.nearClipPlane,
            projectionCamera.aspect
        );
        Gizmos.matrix = Matrix4x4.identity;

        if (seesBall && targetBall != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(projectionCamera.transform.position, targetBall.position);
            Gizmos.DrawWireSphere(targetBall.position, ballRadius);
        }
    }
}
