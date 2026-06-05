using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;

public class VirtualSensors : MonoBehaviour
{
    [Header("Точки привязки (Anchors)")]
    [Tooltip("Точка старта луча ультразвукового датчика (вперед)")]
    public Transform CenterPoint;

    [Tooltip("Левая точка ИК-датчика (направлена влево)")]
    public Transform LeftIRPoint;

    [Tooltip("Правая точка ИК-датчика (направлена вправо)")]
    public Transform RightIRPoint;

    [Tooltip("Точка ИК-датчика в клешне (направлена внутрь захвата)")]
    public Transform GripperIRPoint;

    [Header("Данные сенсоров (финальные значения)")]
    [Range(0f, 1f)]
    [Tooltip("Нормализованная дистанция (1 = нет препятствий, 0 = вплотную)")]
    public float ultrasonicDist = 1f;

    [Tooltip("Левый ИК: 1 - препятствие, 0 - свободно")]
    public int leftIR = 0;

    [Tooltip("Правый ИК: 1 - препятствие, 0 - свободно")]
    public int rightIR = 0;

    [Tooltip("ИК в клешне: 1 - мяч внутри, 0 - пусто")]
    public int gripperIR = 0;

    [Header("Настройки")]
    [Tooltip("Максимальная дистанция УЗ датчика (м). По спецификации XiaoR Geek = 5м")]
    public float maxUltrasonicRange = 5f;

    [Tooltip("Дальность ИК датчика (м) - 15см по спецификации")]
    public float irRange = 0.15f;

    [Tooltip("Дальность ИК датчика в клешне (м) - обычно 5-10см для точного захвата")]
    public float gripperIRRange = 0.07f;

    [Header("Пороги стопа (Sim-to-Real)")]
    [Tooltip("Порог УЗ для стопа (нормализованный). 0.10 = 50см при maxRange 5м. ⚠️ ВАЖНО: Проверь это значение в Inspector!")]
    public float ultrasonicStopThreshold = 0.10f; // 0.5м / 5м = 0.10

    [Tooltip("Игнорировать виртуальные рейкасты, если есть данные от ROS? Если включено — робот не будет стопиться из-за десинхрона с цифровым двойником.")]
    public bool ignoreVirtualInInference = false;

    [Header("ROS State")]
    public bool useRealSensors = false; // Автоматически включается при получении ROS данных
    private ROSConnection ros;

    // --- Реальные значения (хранение между SensorCallback и FixedUpdate) ---
    private float _realUltrasonicDist = 1f;
    private int _realLeftIR = 0;
    private int _realRightIR = 0;
    private int _realGripperIR = 0;

    [HideInInspector]
    public GameObject lastGripperHitObj = null;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        // Основные датчики: УЗ + боковые ИК
        ros.Subscribe<QuaternionMsg>("/sensor/data", SensorCallback);
        // Отдельный топик для ИК датчика клешни (unity_gripper_ir.py)
        ros.Subscribe<Int32Msg>("/sensor/gripper_ir", GripperIRCallback);
    }

    void SensorCallback(QuaternionMsg msg)
    {
        useRealSensors = true;
        
        // Сохраняем реальные данные в приватные переменные
        _realUltrasonicDist = Mathf.Clamp01((float)msg.x / maxUltrasonicRange);
        _realLeftIR = (int)msg.y;
        _realRightIR = (int)msg.z;
        // msg.w намеренно НЕ читаем — gripperIR приходит через /sensor/gripper_ir

        Debug.Log($"[VirtualSensors] ROS данные: UZ={msg.x:F2}м, IR_L={_realLeftIR}, IR_R={_realRightIR}");
    }

    void GripperIRCallback(Int32Msg msg)
    {
        // Отдельный топик от unity_gripper_ir.py (IR_M, pin 22)
        // Устанавливаем useRealSensors=true чтобы Unity знала о реальных данных
        useRealSensors = true;
        _realGripperIR = msg.data;
        if (_realGripperIR == 1)
            Debug.Log("[VirtualSensors] 🎯 GRIPPER IR = 1 — мяч в клешне!");
    }

    void FixedUpdate()
    {
        // 1. ВСЕГДА считаем виртуальные рейкасты (даже если реальные активны)
        float v_ultrasonicDist = CalculateVirtualUltrasonic();
        int v_leftIR = CalculateVirtualIR(LeftIRPoint, irRange);
        int v_rightIR = CalculateVirtualIR(RightIRPoint, irRange);
        // Для клешни используем фильтр по тэгу "TargetBall", чтобы не хватать стены/пол
        int v_gripperIR = CalculateVirtualIR(GripperIRPoint, gripperIRRange, "TargetBall", true);

        if (useRealSensors)
        {
            if (ignoreVirtualInInference)
            {
                // 2. РЕЖИМ ПОРЯДОЧНОСТИ: Доверяем только реальным датчикам (используем при десинхроне)
                // ИСПРАВЛЕНО: gripperIR не присваивался — автозахват не работал в этом режиме.
                ultrasonicDist = _realUltrasonicDist;
                leftIR = _realLeftIR;
                rightIR = _realRightIR;
                gripperIR = _realGripperIR; // BUGFIX: было пропущено, теперь захват работает
            }
            else
            {
                ultrasonicDist = Mathf.Min(_realUltrasonicDist, v_ultrasonicDist);
                leftIR = Mathf.Max(_realLeftIR, v_leftIR);
                rightIR = Mathf.Max(_realRightIR, v_rightIR);
                gripperIR = Mathf.Max(_realGripperIR, v_gripperIR);
            }
        }
        else
        {
            // 3. Чистая симуляция
            ultrasonicDist = v_ultrasonicDist;
            leftIR = v_leftIR;
            rightIR = v_rightIR;
            gripperIR = v_gripperIR;
        }

        // 4. Отрисовка ВСЕГДА
        DrawAllRays(v_ultrasonicDist, v_leftIR, v_rightIR, v_gripperIR);
    }

    // ====================================================
    // ВИРТУАЛЬНЫЕ СЕНСОРЫ (Рейкасты)
    // ====================================================

    /// <summary>
    /// Считает виртуальный УЗ датчик КОНУСОМ из 5 лучей (0°, ±7°, ±15°).
    /// HC-SR04 имеет реальный конус ~30°. Один луч строго вперёд не видит стены под углом.
    /// Возвращает нормализованное значение 0..1 (НЕ пишет в поле!).
    /// </summary>
    private static readonly float[] ultrasonicConeAngles = { 0f, -7f, 7f, -15f, 15f };

    float CalculateVirtualUltrasonic()
    {
        if (CenterPoint == null) return 1f;

        float closestDist = maxUltrasonicRange;

        foreach (float angle in ultrasonicConeAngles)
        {
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * CenterPoint.forward;
            // RaycastAll + фильтр: ультразвук видит стены/препятствия, но НЕ мяч!
            // На реальном роботе мячик слишком маленький/мягкий для ультразвука.
            RaycastHit[] hits = Physics.RaycastAll(CenterPoint.position, dir, maxUltrasonicRange);
            foreach (var hit in hits)
            {
                if (hit.collider.CompareTag("TargetBall")) continue;
                if (hit.distance < closestDist)
                {
                    closestDist = hit.distance;
                }
            }
        }
        return closestDist / maxUltrasonicRange;
    }

    /// <summary>
    /// Считает виртуальный ИК датчик рейкастом в направлении point.forward.
    /// tagFilter - если задан, возвращает 1 только если попали в объект с этим тэгом.
    /// </summary>
    int CalculateVirtualIR(Transform point, float range, string tagFilter = null, bool isGripper = false)
    {
        if (point == null) return 0;
        if (isGripper) lastGripperHitObj = null;

        Vector3 dir = point.forward;
        // Используем QueryTriggerInteraction.Collide, так как мяч может быть триггером
        if (Physics.Raycast(point.position, dir, out RaycastHit hit, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
        {
            if (string.IsNullOrEmpty(tagFilter) || hit.collider.CompareTag(tagFilter))
            {
                if (isGripper) lastGripperHitObj = hit.collider.gameObject;
                return 1; // Препятствие в пределах range (соответствует фильтру)
            }
            else if (!string.IsNullOrEmpty(tagFilter))
            {
                // Игнорируем попадание не по тэгу. Лог удален, чтобы не спамил консоль при обучении (ML-Agents x100 speed).
            }
        }
        return 0; // Чисто или не тот тэг
    }

    // ====================================================
    // ВИЗУАЛИЗАЦИЯ (Debug.DrawRay)
    // ====================================================

    void DrawAllRays(float virtualUZ, int virtualIR_L, int virtualIR_R, int virtualIR_M)
    {
        // --- УЛЬТРАЗВУК ---
        if (CenterPoint != null)
        {
            float displayDist = ultrasonicDist * maxUltrasonicRange;
            
            // Цвет по финальному значению (красный = стоп, жёлтый = осторожно, зелёный = чисто)
            Color uzColor;
            if (ultrasonicDist < ultrasonicStopThreshold)
                uzColor = Color.red;     // СТОП! ≤60см
            else if (ultrasonicDist < 0.2f)
                uzColor = Color.yellow;  // Осторожно (≤1м)
            else
                uzColor = Color.green;   // Чисто

            Debug.DrawRay(CenterPoint.position, CenterPoint.forward * displayDist, uzColor);

            // Если есть реальные данные — дополнительно рисуем виртуальный луч белым для сравнения
            if (useRealSensors)
            {
                float vDist = virtualUZ * maxUltrasonicRange;
                Debug.DrawRay(CenterPoint.position + Vector3.up * 0.02f, CenterPoint.forward * vDist, Color.white);
            }
        }

        // --- ИК ЛЕВЫЙ ---
        if (LeftIRPoint != null)
        {
            Color irLColor = leftIR == 1 ? Color.red : Color.blue;
            Debug.DrawRay(LeftIRPoint.position, LeftIRPoint.forward * irRange, irLColor);

            if (useRealSensors)
            {
                Color vColor = virtualIR_L == 1 ? new Color(1f, 0.5f, 0.5f) : Color.white;
                Debug.DrawRay(LeftIRPoint.position + Vector3.up * 0.01f, LeftIRPoint.forward * irRange, vColor);
            }
        }

        // --- ИК ПРАВЫЙ ---
        if (RightIRPoint != null)
        {
            Color irRColor = rightIR == 1 ? Color.red : Color.magenta;
            Debug.DrawRay(RightIRPoint.position, RightIRPoint.forward * irRange, irRColor);

            if (useRealSensors)
            {
                Color vColor = virtualIR_R == 1 ? new Color(1f, 0.5f, 0.5f) : Color.white;
                Debug.DrawRay(RightIRPoint.position + Vector3.up * 0.01f, RightIRPoint.forward * irRange, vColor);
            }
        }

        // --- ИК КЛЕШНИ ---
        if (GripperIRPoint != null)
        {
            Color irMColor = gripperIR == 1 ? Color.red : Color.cyan;
            Debug.DrawRay(GripperIRPoint.position, GripperIRPoint.forward * gripperIRRange, irMColor);

            if (useRealSensors)
            {
                Color vColor = virtualIR_M == 1 ? new Color(1f, 0.5f, 0.5f) : Color.white;
                Debug.DrawRay(GripperIRPoint.position + Vector3.up * 0.01f, GripperIRPoint.forward * gripperIRRange, vColor);
            }
        }
    }
}
