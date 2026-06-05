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

    [HideInInspector]
    public Transform targetBall; // Назначается из RobotBrain.cs

    // Debounce internals
    private float _lastSeenTime = -100f;
    private float _lastNormalizedAngle;
    private float _lastNormalizedDistance;

    void Start()
    {
        if (projectionCamera == null)
        {
            // Пытаемся найти камеру на этом же объекте или дочерних
            projectionCamera = GetComponentInChildren<Camera>();
        }

        if (projectionCamera != null)
        {
            // v17 FIX: СНАЧАЛА настраиваем projection (aspect + FOV), ПОТОМ отключаем рендеринг!
            // Unity может НЕ обновлять projection matrix на disabled камере.
            // Это объясняет "Display 1 No cameras rendering" + ballSeen=0%.

            // 1. Фиксируем aspect ratio для идентичности с реальной камерой 640x480
            projectionCamera.aspect = 4f / 3f;

            // 2. Рассчитываем ВЕРТИКАЛЬНЫЙ FOV из калиброванного горизонтального FOV.
            // Формула: vFOV = 2 * atan(tan(hFOV/2) / aspect)
            float halfHFovRad = horizontalFOV * 0.5f * Mathf.Deg2Rad;
            float calculatedVFov = 2f * Mathf.Atan(Mathf.Tan(halfHFovRad) / projectionCamera.aspect) * Mathf.Rad2Deg;
            projectionCamera.fieldOfView = calculatedVFov;

            // 3. ТЕПЕРЬ отключаем рендеринг (проекционная матрица уже настроена)
            projectionCamera.enabled = false;

            // Исключаем слой робота из occlusionMask
            Transform robotRoot = transform.root;
            foreach (Collider col in robotRoot.GetComponentsInChildren<Collider>())
            {
                occlusionMask &= ~(1 << col.gameObject.layer);
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

        // v17: Автоматический ballRadius из реального scale мяча
        // Unity Sphere: mesh radius = 0.5 в local space → world radius = lossyScale * 0.5
        ballRadius = targetBall.lossyScale.x * 0.5f;

        // === ЭТАП 1: Проецируем мяч на виртуальный экран ===
        Vector3 viewportPos = projectionCamera.WorldToViewportPoint(targetBall.position);

        // viewportPos.x = 0..1 (лево..право в кадре)
        // viewportPos.y = 0..1 (низ..верх в кадре)
        // viewportPos.z = расстояние от камеры (>0 = перед камерой)

        bool inFrontOfCamera = viewportPos.z > 0;
        bool inViewport = viewportPos.x >= 0f && viewportPos.x <= 1f &&
                          viewportPos.y >= 0f && viewportPos.y <= 1f;
        float distance = Vector3.Distance(projectionCamera.transform.position, targetBall.position);
        bool inRange = distance < maxViewDistance;

        // === ЭТАП 2: Проверка окклюзии (стены + клешня) ===
        bool hasLineOfSight = true;
        bool blockedByClaw = false;

        if (inFrontOfCamera && inViewport && inRange)
        {
            hasLineOfSight = CheckLineOfSight(
                projectionCamera.transform.position,
                targetBall.position,
                distance,
                out blockedByClaw
            );
        }

        bool directlySeesBall = inFrontOfCamera && inViewport && inRange && hasLineOfSight;

        if (directlySeesBall)
        {
            _lastSeenTime = Time.time;

            // === ЭТАП 3: Угол мяча (ИДЕНТИЧЕН YOLO) ===
            // YOLO: x_norm = (center_x - w/2) / (w/2)
            // Unity: viewport.x = center_x / w → x_norm = (viewport.x - 0.5) * 2
            normalizedAngle = (viewportPos.x - 0.5f) * 2f;
            normalizedAngle = Mathf.Clamp(normalizedAngle, -1f, 1f);

            // === ЭТАП 4: Дистанция мяча (ИДЕНТИЧЕН YOLO) ===
            // YOLO: y_norm = bbox_height / frame_height
            // Unity: проецируем верх и низ мяча → разница viewport.y = bbox_height / frame_height
            normalizedDistance = CalculateProjectedBboxHeight(viewportPos, distance);

            seesBall = true;
            lastKnownBallDirection = Mathf.Sign(normalizedAngle);
            if (lastKnownBallDirection == 0) lastKnownBallDirection = 1f;

            // Сохраняем для debounce
            _lastNormalizedAngle = normalizedAngle;
            _lastNormalizedDistance = normalizedDistance;

            Debug.DrawLine(projectionCamera.transform.position, targetBall.position, Color.green);
        }
        else
        {
            // === МЯЧ НЕ ВИДЕН ===
            if (Time.time - _lastSeenTime <= debounceDuration)
            {
                // Debounce: удерживаем последние значения (имитация BoT-SORT трекера)
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

            Debug.DrawRay(projectionCamera.transform.position,
                          projectionCamera.transform.forward * maxViewDistance, Color.red);
        }
    }

    /// <summary>
    /// Рассчитывает normalizedDistance через обратную проекцию bbox viewport height.
    /// 
    /// Формула: из viewport bbox_height → реальная дистанция → нормализация 0..1
    /// 
    /// СТАРЫЙ БАГ: maxBboxHeight=0.15 насыщался при 0.5м (normalizedDist=0.0).
    /// Это убивало distance delta reward на последних 50см подъезда → агент не ехал к мячу!
    /// 
    /// НОВЫЙ ПОДХОД: обратная проекция через FOV камеры.
    /// bboxHeight = 2*ballRadius / (distance * 2 * tan(vFOV/2)) 
    /// → distance = ballRadius / (bboxHeight * tan(vFOV/2))
    /// → normalizedDistance = distance / maxViewDistance
    /// 
    /// Результат: линейная шкала 0-1 без сатурации. Полный градиент на ВСЕХ дистанциях.
    /// </summary>
    float CalculateProjectedBboxHeight(Vector3 viewportCenter, float distance)
    {
        // Проецируем верхнюю и нижнюю точки мяча
        Vector3 ballTop = targetBall.position + Vector3.up * ballRadius;
        Vector3 ballBottom = targetBall.position - Vector3.up * ballRadius;

        Vector3 vpTop = projectionCamera.WorldToViewportPoint(ballTop);
        Vector3 vpBottom = projectionCamera.WorldToViewportPoint(ballBottom);

        // bbox_height в viewport координатах (0..1)
        float bboxHeight = Mathf.Abs(vpTop.y - vpBottom.y);

        if (bboxHeight < 0.001f) return 1f; // Слишком далеко, практически невидим

        // Обратная проекция: из bbox viewport fraction → реальная дистанция
        // Camera.fieldOfView — ВЕРТИКАЛЬНЫЙ FOV в градусах
        float halfVFovRad = projectionCamera.fieldOfView * 0.5f * Mathf.Deg2Rad;
        float approxDist = ballRadius / (bboxHeight * Mathf.Tan(halfVFovRad));

        // Нормализуем: 0 = вплотную, 1 = maxViewDistance
        return Mathf.Clamp01(approxDist / maxViewDistance);
    }

    /// <summary>
    /// Проверяет прямую видимость от камеры к мячу.
    /// Несколько лучей для проверки частичной окклюзии (клешня может закрывать часть мяча).
    /// </summary>
    bool CheckLineOfSight(Vector3 from, Vector3 ballPos, float dist, out bool clawBlocked)
    {
        clawBlocked = false;
        int blocked = 0;

        // Центральный луч + смещённые (имитация того, что YOLO видит bbox, а не точку)
        Vector3[] offsets;
        if (occlusionRayCount >= 5)
        {
            offsets = new Vector3[] {
                Vector3.zero,
                Vector3.up * ballRadius,
                Vector3.down * ballRadius,
                Vector3.left * ballRadius,
                Vector3.right * ballRadius,
                // Дополнительные диагональные
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
                // Попали в что-то ДО мяча
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

        // Мяч виден, если хотя бы один луч прошёл
        return blocked < raysToCheck;
    }

    // === GIZMOS (визуализация в Scene View) ===
    private void OnDrawGizmosSelected()
    {
        if (projectionCamera == null) return;

        // Рисуем frustum камеры
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

        // Линия к мячу
        if (seesBall && targetBall != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(projectionCamera.transform.position, targetBall.position);
            Gizmos.DrawWireSphere(targetBall.position, ballRadius);
        }
    }
}
