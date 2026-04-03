using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Sensors;
using Unity.MLAgents.Actuators;
using System.Collections.Generic;

[RequireComponent(typeof(TrackController))]
[RequireComponent(typeof(VirtualSensors))]
public class RobotBrain : Agent
{
    private TrackController track;
    private VirtualSensors sensors;
    private Rigidbody rb;

    [Header("General Settings")]
    public bool isTraining = false;
    public bool isMovementEnabled = false; // "Ручник" для калибровки
    public bool enableVerboseLogging = true;

    [Header("Latency Simulation (Sim-to-Real)")]
    [Tooltip("Минимальная задержка действий (в шагах FixedUpdate). 1 шаг ≈ 20мс при 50Hz.")]
    public int minActionLatency = 2;
    [Tooltip("Максимальная задержка действий (рандомизируется каждый эпизод).")]
    public int maxActionLatency = 5;
    [Tooltip("Задержка сенсоров (шаги). Имитирует запаздывание ROS-топиков.")]
    public int sensorLatency = 1;
    private int currentActionLatency = 3;

    // Буфер задержки действий (Circular Queue)
    private Queue<float[]> actionBuffer = new Queue<float[]>();
    private Queue<int[]> discreteActionBuffer = new Queue<int[]>();
    // Буфер задержки сенсоров
    private Queue<float[]> sensorBuffer = new Queue<float[]>();
    private float[] delayedSensors = new float[4]; // UZ, L_IR, R_IR, CLAW_IR

    [Header("Stage 4 Components")]
    public RealVision realVision;
    public VirtualCamera virtualCamera;
    public ROSBridge rosBridge;

    [Header("Stage 6 Components")]
    public Transform cameraPivot; // Пустышка, на которой висит камера для вращения
    private float currentCameraYaw = 0f;

    [Header("Stage 3 Components")]
    public GripperController gripper;

    [Header("Parallel Training Setup")]
    public GameObject ballPrefab; // Префаб мяча
    private GameObject spawnedBall; // Ссылка на мяч (созданный или найденный)

    private Vector3 startPosition;
    private Quaternion startRotation;

    // --- Пенальти и Награды ---
    private float lastDistance = 1f;
    private bool wasSeeingBallLastStep = false;
    private int holdTicks = 0;

    // --- Фаза 3: Слепой захват ---
    private bool wasCloseToBall = false;   // Мяч был близко перед потерей из виду
    private float lastCloseAngle = 0f;     // Последний угол перед потерей
    private int blindApproachTicks = 0;    // Сколько шагов едем в слепую
    private const int BLIND_APPROACH_MAX = 40;  // Было 80, теперь короче

    // --- Retry: Отъехать назад и попробовать снова ---
    private bool isRetrying = false;       // Сейчас сдаём назад
    private int retryBackupTicks = 0;      // Счётчик шагов отъезда
    private int retryCount = 0;            // Сколько retry уже сделали
    private const int MAX_RETRIES = 2;     // Макс попыток
    private const int RETRY_BACKUP_DURATION = 30; // Шагов назад (~0.6 сек)

    // --- Анти-застревание (Stuck Detection) ---
    private Vector3 lastPosition;
    private int stuckTimer = 0;

    // --- ФАЗА 1: Эвристическое Автономное Управление (Автопилот) ---
    private int autoState = 0; // 0=Search, 1=Approach, 2=Grab
    private float autoTimer = 0f;

    public override void Initialize()
    {
        track = GetComponent<TrackController>();
        sensors = GetComponent<VirtualSensors>();
        rb = GetComponent<Rigidbody>();

        startPosition = transform.position;
        startRotation = transform.rotation;

        // --- Инициализация мяча для параллельных зон ---
        if (ballPrefab != null)
        {
            // Создаем из префаба как потомка нашей тренировочной лаборатории
            spawnedBall = Instantiate(ballPrefab, transform.parent);
            spawnedBall.name = "TargetBall_Instance";
        }
        else
        {
            // Если префаб не задан, ищем локально среди соседей в иерархии
            spawnedBall = FindLocalBall();
        }

        // КРИТИЧЕСКИ ВАЖНО: Привязываем СВОЙ мяч к СВОЕЙ камере!
        // Без этого VirtualCamera.FindTarget() берет ПЕРВЫЙ мяч из ВСЕЙ сцены (чужой зоны)
        if (spawnedBall != null && virtualCamera != null)
        {
            virtualCamera.targetBall = spawnedBall.transform;
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            isMovementEnabled = !isMovementEnabled;
            Debug.Log("Движение " + (isMovementEnabled ? "разрешено!" : "запрещено!"));
        }
    }

    public override void OnEpisodeBegin()
    {
        if (isTraining)
        {
            transform.position = startPosition;
            transform.rotation = startRotation;

            if (rb != null)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            // --- Сброс мяча (Stage 3) ---
            ResetBall();
        }

        // --- Domain Randomization (Физика) ---
        if (isTraining && track != null)
        {
            // Случайная мощность моторов и проскальзывание треков
            track.moveSpeed = UnityEngine.Random.Range(0.45f, 0.65f); // ±~20%
            track.turnSpeed = UnityEngine.Random.Range(100f, 140f);
            track.smoothing = UnityEngine.Random.Range(0.01f, 0.15f);
        }
        if (isTraining && rb != null)
        {
            rb.mass = UnityEngine.Random.Range(1.5f, 3.5f); // Реальный вес может слегка плавать из-за батареи
        }

        // Сброс таймеров
        holdTicks = 0;
        stuckTimer = 0;
        lastPosition = transform.position;
        lastDistance = 1f; 
        wasSeeingBallLastStep = false;
        wasCloseToBall = false;
        lastCloseAngle = 0f;
        blindApproachTicks = 0;
        isRetrying = false;
        retryBackupTicks = 0;
        retryCount = 0;
        currentCameraYaw = 0f;
        if (cameraPivot != null) cameraPivot.localRotation = Quaternion.Euler(0, 0, 0);

        // --- Latency: рандомизация задержки каждый эпизод ---
        if (isTraining)
        {
            currentActionLatency = UnityEngine.Random.Range(minActionLatency, maxActionLatency + 1);
            actionBuffer.Clear();
            discreteActionBuffer.Clear();
            sensorBuffer.Clear();
            delayedSensors = new float[] { 1f, 0f, 0f, 0f };
            // Заполняем буфер "нулевыми" действиями
            for (int i = 0; i < currentActionLatency; i++)
            {
                actionBuffer.Enqueue(new float[] { 0f, 0f, 0f });
                discreteActionBuffer.Enqueue(new int[] { 0 });
            }
            for (int i = 0; i < sensorLatency; i++)
            {
                sensorBuffer.Enqueue(new float[] { 1f, 0f, 0f, 0f });
            }
        }
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // --- Domain Randomization (Шум сенсоров) ---
        float noiseUS = isTraining ? UnityEngine.Random.Range(-0.05f, 0.05f) : 0f;
        float noiseVisAngle = isTraining ? UnityEngine.Random.Range(-0.02f, 0.02f) : 0f;
        float noiseVisDist = isTraining ? UnityEngine.Random.Range(-0.02f, 0.02f) : 0f;

        // --- Latency: Задержка сенсоров (имитация запаздывания ROS-топиков) ---
        if (isTraining && sensorLatency > 0 && sensors != null)
        {
            // Текущие данные кладем в буфер
            sensorBuffer.Enqueue(new float[] {
                Mathf.Clamp01(sensors.ultrasonicDist + noiseUS),
                (float)sensors.leftIR,
                (float)sensors.rightIR,
                (float)sensors.gripperIR
            });
            // Достаем устаревшие данные
            if (sensorBuffer.Count > sensorLatency)
            {
                delayedSensors = sensorBuffer.Dequeue();
            }
            sensor.AddObservation(delayedSensors[0]);
            sensor.AddObservation((int)delayedSensors[1]);
            sensor.AddObservation((int)delayedSensors[2]);
            sensor.AddObservation((int)delayedSensors[3]);
        }
        else if (sensors != null)
        {
            sensor.AddObservation(Mathf.Clamp01(sensors.ultrasonicDist + noiseUS));
            sensor.AddObservation(sensors.leftIR);
            sensor.AddObservation(sensors.rightIR);
            sensor.AddObservation(sensors.gripperIR);
        }
        else
        {
            sensor.AddObservation(1f);
            sensor.AddObservation(0);
            sensor.AddObservation(0);
            sensor.AddObservation(0);
        }

        // --- Наблюдения Stage 4 (Зрение и память) ---
        if (realVision != null)
        {
            sensor.AddObservation(realVision.normalizedAngle);
            sensor.AddObservation(realVision.normalizedDistance);
            sensor.AddObservation(realVision.lastKnownBallDirection);
            sensor.AddObservation(realVision.seesBall ? 1f : 0f);
            sensor.AddObservation(currentCameraYaw);
        }
        else if (virtualCamera != null)
        {
            sensor.AddObservation(Mathf.Clamp(virtualCamera.normalizedAngle + noiseVisAngle, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp01(virtualCamera.normalizedDistance + noiseVisDist));
            sensor.AddObservation(virtualCamera.lastKnownBallDirection);
            sensor.AddObservation(virtualCamera.seesBall ? 1f : 0f);
            sensor.AddObservation(currentCameraYaw);
        }
        else
        {
            sensor.AddObservation(0f);
            sensor.AddObservation(1f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
            sensor.AddObservation(0f);
        }

        if (gripper != null)
        {
            sensor.AddObservation(gripper.hasBall ? 1f : 0f);
        }
        else
        {
            sensor.AddObservation(0f);
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        // --- "Ручник" (принудительный стоп) ---
        if (!isMovementEnabled)
        {
            if (track != null)
            {
                track.Move(0f, 0f);
                if (rosBridge != null)
                {
                    rosBridge.PublishCommand(0f, 0f);
                }
            }
            return;
        }

        float gas, steering, cameraYawInput;
        int gripperAction;

        if (isTraining && currentActionLatency > 0)
        {
            // --- LATENCY: Кладем текущее действие нейросети в буфер ---
            float[] newContinuous = new float[] {
                actions.ContinuousActions[0],
                actions.ContinuousActions[1],
                actions.ContinuousActions.Length > 2 ? actions.ContinuousActions[2] : 0f
            };
            int[] newDiscrete = new int[] {
                actions.DiscreteActions.Length > 0 ? actions.DiscreteActions[0] : 0
            };
            actionBuffer.Enqueue(newContinuous);
            discreteActionBuffer.Enqueue(newDiscrete);

            // Достаем УСТАРЕВШЕЕ действие из головы очереди
            float[] delayed = actionBuffer.Dequeue();
            int[] delayedDiscrete = discreteActionBuffer.Dequeue();
            gas = delayed[0];
            steering = delayed[1];
            cameraYawInput = delayed[2];
            gripperAction = delayedDiscrete[0];
        }
        else
        {
            // Без задержки (инференс на реальном роботе)
            gas = actions.ContinuousActions[0];
            steering = actions.ContinuousActions[1];
            cameraYawInput = actions.ContinuousActions.Length > 2 ? actions.ContinuousActions[2] : 0f;
            gripperAction = actions.DiscreteActions.Length > 0 ? actions.DiscreteActions[0] : 0;
        }

        if (enableVerboseLogging)
        {
            Debug.Log($"[RobotBrain] Inputs -> BallSeen: {realVision?.seesBall}, Angle: {realVision?.normalizedAngle:F2}, Dist: {realVision?.normalizedDistance:F2}");
            Debug.Log($"[RobotBrain] Sensors -> UZ: {sensors?.ultrasonicDist:F2}, IR_L: {sensors?.leftIR}, IR_R: {sensors?.rightIR}, RealSensors: {sensors?.useRealSensors}");
            Debug.Log($"[RobotBrain] FINAL Output -> Gas: {gas:F2}, Steer: {steering:F2} (Latency: {currentActionLatency} steps)");
        }

        // --- Штраф за резкость камеры (Анти-Motion Blur для YOLO) ---
        float camDelta = Mathf.Abs(currentCameraYaw - cameraYawInput);
        if (isTraining && camDelta > 0.05f)
        {
            AddReward(-0.02f * camDelta);
        }

        currentCameraYaw = Mathf.Clamp(cameraYawInput, -1f, 1f);
        if (cameraPivot != null)
        {
            cameraPivot.localRotation = Quaternion.Euler(0f, currentCameraYaw * 90f, 0f);
        }

        if (track != null)
        {
            track.Move(gas, steering);

            if (rosBridge != null && !isTraining)
            {
                rosBridge.PublishCommand(gas, steering);
                rosBridge.PublishCameraCmd(currentCameraYaw);
            }
        }

        // --- Логика Клешни (Discrete Action) ---
        if (enableVerboseLogging && gripperAction != 0)
        {
            Debug.Log($"[RobotBrain] NN Output -> GRIPPER ACTION: {gripperAction}");
        }

        // Клешня РАЗРЕШЕНА ВСЕГДА (убран блок на слепую зону — мяч может быть прямо в клешне, но не виден камерой!)
        if (gripperAction == 1)
        {
            if (rosBridge != null && !isTraining) rosBridge.PublishGripperCmd(1);
            if (gripper != null) gripper.OpenGripper();
        }
        else if (gripperAction == 2)
        {
            if (rosBridge != null && !isTraining) rosBridge.PublishGripperCmd(2);
            if (gripper != null) gripper.CloseGripper();
        }

        // ============================================================
        // ТРЁХФАЗНАЯ СИСТЕМА НАГРАД
        // ============================================================

        // === ФАЗА 0: МЯЧ СХВАЧЕН ===
        if (gripper != null && gripper.hasBall)
        {
            holdTicks++;
            AddReward(0.02f); // Микро-награда за удержание
            if (isTraining && holdTicks >= 50)
            {
                AddReward(5.0f); // ГЛАВНЫЙ ПРИЗ
                EndEpisode();
                return;
            }
            // Мяч схвачен — не нужны другие награды
            return;
        }
        holdTicks = 0;

        // === СЕНСОРНЫЕ ШТРАФЫ (всегда активны) ===
        if (isTraining && sensors != null)
        {
            if (sensors.ultrasonicDist < sensors.ultrasonicStopThreshold) AddReward(-0.005f);
            if (sensors.leftIR == 1 || sensors.rightIR == 1) AddReward(-0.005f);

            // Штраф за езду задом: сзади НЕТ сенсоров, робот слепой!
            // НО: во время retry отъезд назад РАЗРЕШЁН и даже поощряется
            if (gas < -0.05f && !isRetrying) AddReward(-0.003f);
        }

        // === СЧИТЫВАЕМ ЗРЕНИЕ ===
        bool hasSeenBall = false;
        float currentAngle = 0f;
        float currentDist = 0f;

        if (realVision != null && realVision.seesBall)
        {
            hasSeenBall = true;
            currentAngle = realVision.normalizedAngle;
            currentDist = realVision.normalizedDistance;
        }
        else if (virtualCamera != null && virtualCamera.seesBall)
        {
            hasSeenBall = true;
            currentAngle = virtualCamera.normalizedAngle;
            currentDist = virtualCamera.normalizedDistance;
        }

        // === ПРОВЕРКА ИК-ДАТЧИКА КЛЕШНИ ===
        bool gripperSeesBall = sensors != null && sensors.gripperIR == 1;

        if (gripperSeesBall)
        {
            // !!! МЯЧ ПРЯМО В КЛЕШНЕ !!!
            isRetrying = false; // Отменяем retry — мяч уже тут!
            AddReward(0.1f); // ОГРОМНЫЙ бонус каждый шаг!
            // Штраф за любое движение (не столкни мяч!) — стой и хватай!
            if (Mathf.Abs(gas) > 0.15f) AddReward(-0.03f);
        }

        // === ФАЗА RETRY: Отъезд назад после промаха ===
        if (isRetrying)
        {
            retryBackupTicks++;

            // Бонус за медленный отъезд назад
            if (gas < -0.05f && gas > -0.35f)
            {
                AddReward(0.002f);
            }
            // Штраф за резкий отъезд или стояние на месте
            if (gas > 0.05f) AddReward(-0.005f); // Не езжай вперёд!

            if (retryBackupTicks >= RETRY_BACKUP_DURATION)
            {
                // Отъехали достаточно — сброс, мяч должен вернуться в FOV
                isRetrying = false;
                wasCloseToBall = false;
                retryBackupTicks = 0;
                blindApproachTicks = 0;
                lastDistance = 1f;
            }
            // Во время retry-фазы другие награды не начисляются
            return;
        }

        if (hasSeenBall)
        {
            // Мяч видим камерой — retry-сброс при повторном обнаружении
            blindApproachTicks = 0;
            isRetrying = false;

            if (currentDist <= 0.15f)
            {
                // =======================================
                // ФАЗА 2.5: ФИНАЛЬНЫЙ НАЕЗД
                // Мяч ОЧЕНЬ близко — ползком, строго прямо
                // =======================================
                wasCloseToBall = true;
                lastCloseAngle = currentAngle;

                // Бонус за идеальную центровку (|angle| < 0.1)
                float alignmentQuality = 1f - Mathf.Abs(currentAngle);
                AddReward(0.015f * alignmentQuality);

                // ЖЁСТКИЙ штраф за скорость — НЕ СНОСИ МЯЧ!
                if (Mathf.Abs(gas) > 0.2f)
                {
                    AddReward(-0.025f);
                }

                // Бонус за ползком + точно
                if (gas > 0.01f && gas < 0.2f && Mathf.Abs(currentAngle) < 0.15f)
                {
                    AddReward(0.008f);
                }
            }
            else if (currentDist <= 0.25f)
            {
                // =======================================
                // ФАЗА 2: ПОДКРАДЫВАНИЕ (НОВОЕ!)
                // Мяч в зоне 0.15-0.25 — замедляемся и выравниваемся
                // =======================================
                wasCloseToBall = true;
                lastCloseAngle = currentAngle;

                // Бонус за выравнивание корпуса
                float alignmentQuality = 1f - Mathf.Abs(currentAngle);
                AddReward(0.005f * alignmentQuality);

                // Награда за медленное сближение (gas 0.05-0.25)
                if (gas > 0.05f && gas < 0.25f)
                {
                    AddReward(0.004f);
                }

                // Штраф за полный газ — тормози!
                if (gas > 0.35f)
                {
                    AddReward(-0.015f);
                }

                // Награда за сближение (delta)
                if (wasSeeingBallLastStep)
                {
                    float distanceDelta = lastDistance - currentDist;
                    if (distanceDelta > 0f && distanceDelta < 0.3f)
                    {
                        AddReward(distanceDelta * 0.8f); // Умеренная delta-награда
                    }
                }
            }
            else
            {
                // =======================================
                // ФАЗА 1.5: ДАЛЬНИЙ ПОДЪЕЗД
                // Мяч виден и далеко (dist > 0.25) — приближайся
                // =======================================
                wasCloseToBall = false;

                // Награда за сближение (Delta)
                if (wasSeeingBallLastStep)
                {
                    float distanceDelta = lastDistance - currentDist;
                    if (distanceDelta > 0f && distanceDelta < 0.5f)
                    {
                        AddReward(distanceDelta * 1.5f);
                    }
                }

                // Награда за выравнивание корпуса
                float bodyAlignment = 1f - Mathf.Abs(currentAngle);
                if (bodyAlignment > 0f)
                {
                    AddReward(0.001f * bodyAlignment);
                }
            }

            lastDistance = currentDist;
            wasSeeingBallLastStep = true;
        }
        else
        {
            // === МЯЧ НЕ ВИДЕН ===

            if (wasCloseToBall && !gripperSeesBall)
            {
                // =======================================
                // ФАЗА 3: СЛЕПОЙ ЗАХВАТ
                // Мяч был рядом, но вышел из FOV
                // =======================================
                blindApproachTicks++;

                if (blindApproachTicks < BLIND_APPROACH_MAX)
                {
                    // Ползём вперёд вслепую
                    if (gas > 0.01f && gas < 0.25f)
                    {
                        AddReward(0.003f);
                    }
                    if (Mathf.Abs(gas) > 0.3f) AddReward(-0.01f);
                }
                else
                {
                    // Слепой подъезд провален → RETRY или сдаёмся
                    if (retryCount < MAX_RETRIES)
                    {
                        // Запускаем retry: отъезжаем назад
                        isRetrying = true;
                        retryBackupTicks = 0;
                        retryCount++;
                        blindApproachTicks = 0;
                        AddReward(-0.05f); // Небольшой штраф за промах
                    }
                    else
                    {
                        // Все retry исчерпаны
                        wasCloseToBall = false;
                        blindApproachTicks = 0;
                        AddReward(-0.15f); // Ощутимый штраф: 2 раза промахнулся
                    }
                }
            }
            else if (!wasCloseToBall)
            {
                // =======================================
                // ФАЗА 1: ПОИСК
                // =======================================
                AddReward(-0.0003f); // Налог на жизнь
            }

            lastDistance = 1f;
            wasSeeingBallLastStep = false;
        }

        // === АНТИ-ЗАСТРЕВАНИЕ ===
        if (Mathf.Abs(gas) > 0.1f || Mathf.Abs(steering) > 0.1f)
        {
            stuckTimer++;
            if (stuckTimer >= 200)
            {
                float distanceTravelled = Vector3.Distance(transform.position, lastPosition);
                if (distanceTravelled < 0.5f)
                {
                    AddReward(-0.5f);
                    EndEpisode();
                    return;
                }
                stuckTimer = 0;
                lastPosition = transform.position;
            }
        }
        else
        {
            stuckTimer = 0;
            lastPosition = transform.position;
        }

        // === ЛИМИТ ЭПИЗОДА ===
        if (isTraining && StepCount >= 700)
        {
            AddReward(-0.3f); // Слишком долго блуждал
            EndEpisode();
        }
    }

    private GameObject FindLocalBall()
    {
        if (transform.parent != null)
        {
            foreach (Transform t in transform.parent)
            {
                if (t.CompareTag("TargetBall")) return t.gameObject;
            }
        }
        return null;
    }

    private void ResetBall()
    {
        if (spawnedBall == null)
        {
            spawnedBall = FindLocalBall();
        }

        if (spawnedBall != null)
        {
            // Возвращаем в тренировочную зону, если он был в клешне
            if (spawnedBall.transform.parent != transform.parent)
            {
                spawnedBall.transform.SetParent(transform.parent);
            }

            Rigidbody ballRb = spawnedBall.GetComponent<Rigidbody>();
            if (ballRb != null)
            {
                ballRb.isKinematic = false;
                ballRb.linearVelocity = Vector3.zero;
                ballRb.angularVelocity = Vector3.zero;
            }

            Vector3 randomPos = transform.position;
            bool validPos = false;
            int attempts = 0;

            while (!validPos && attempts < 30)
            {
                // --- CURRICULUM LEARNING (Встроенный ML-Agents) ---
                // Параметры читаются из config.yaml → environment_parameters.
                // Сложность повышается ТОЛЬКО когда средняя награда агента достигает порога.
                float maxDist = Academy.Instance.EnvironmentParameters.GetWithDefault("ball_max_distance", 1.5f);
                float maxOffset = Academy.Instance.EnvironmentParameters.GetWithDefault("ball_max_offset", 0.3f);

                // Дистанция: от 0.5м до maxDist (контролируется Curriculum)
                float randomZ = Random.Range(0.5f, maxDist);
                
                // Боковой разброс (контролируется Curriculum)
                float randomX = Random.Range(-maxOffset, maxOffset);

                randomPos = transform.position + transform.forward * randomZ + transform.right * randomX;
                randomPos.y = transform.position.y + 0.2f;

                Collider[] colliders = Physics.OverlapSphere(randomPos, 0.15f);
                bool hasObstacle = false;
                foreach (var c in colliders)
                {
                    if (c.CompareTag("Wall") || c.CompareTag("Obstacle"))
                    {
                        hasObstacle = true;
                        break;
                    }
                }

                if (!hasObstacle) validPos = true;
                attempts++;
            }

            if (!validPos)
            {
                // Fallback: если Physics.OverlapSphere отверг все 30 попыток (например, maxDist больше размера комнаты),
                // ставим мяч безопасно перед роботом (0.7м), чтобы он не оказался внутри текстур стен.
                randomPos = transform.position + transform.forward * 0.7f;
                randomPos.y = transform.position.y + 0.2f;
            }

            spawnedBall.transform.position = randomPos;

            // --- Domain Randomization мяча (паттерн Ball3DAgent) ---
            Rigidbody bRb = spawnedBall.GetComponent<Rigidbody>();
            if (bRb != null)
            {
                bRb.mass = Academy.Instance.EnvironmentParameters.GetWithDefault("ball_mass", 0.1f);
                bRb.mass *= UnityEngine.Random.Range(0.5f, 2.0f); // ±100% разброс
            }
            float ballScale = Academy.Instance.EnvironmentParameters.GetWithDefault("ball_scale", 0.12f);
            ballScale *= UnityEngine.Random.Range(0.8f, 1.2f); // ±20% разброс
            spawnedBall.transform.localScale = Vector3.one * ballScale;
        }

        if (gripper != null)
        {
            gripper.hasBall = false;
        }
    }

    // OnCollisionEnter: ОДИН штраф за УДАР, а не непрерывное наказание каждый физический кадр.
    // OnCollisionStay давал -0.02 × 50fps = -1.0/сек за касание стены — это было убийственно.
    private void OnCollisionEnter(Collision collision)
    {
        if (collision.collider.CompareTag("Wall") || collision.collider.CompareTag("Obstacle"))
        {
            AddReward(-0.1f); // Чёткий одноразовый сигнал: "Ты врезался"
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActions = actionsOut.ContinuousActions;
        var discreteActions = actionsOut.DiscreteActions;

        // По умолчанию всё стоит
        float gas = 0f;
        float steer = 0f;
        float camYaw = 0f;
        int grip = 0;

        // Считываем реальное зрение (YOLO)
        bool sees = realVision != null && realVision.seesBall;
        float angle = realVision != null ? realVision.normalizedAngle : 0f;
        float dist = realVision != null ? realVision.normalizedDistance : 1f;

        if (!sees)
        {
            // --------------------------------------------------------
            // 🕵️ СОСТОЯНИЕ 0: ПОИСК МЯЧА
            // --------------------------------------------------------
            steer = 0.5f; // Медленно крутимся на месте
            
            // Сканируем визором влево-вправо (синусоида во времени)
            camYaw = Mathf.Sin(Time.time * 1.5f) * 0.6f; 
        }
        else
        {
            // --------------------------------------------------------
            // 🎯 СОСТОЯНИЕ 1: НАВЕДЕНИЕ И ПОДЪЕЗД
            // --------------------------------------------------------
            // Плавное слежение камеры за мячом
            camYaw = Mathf.Lerp(currentCameraYaw, angle, Time.deltaTime * 5f); 

            if (sensors != null && sensors.gripperIR == 1)
            {
                // --------------------------------------------------------
                // 🦾 СОСТОЯНИЕ 2: ЗАХВАТ КЛЕШНЕЙ (по ИК-датчику)
                // --------------------------------------------------------
                gas = 0f;
                steer = 0f;
                grip = 2; // Команда "СХВАТИТЬ"
                if (enableVerboseLogging) Debug.Log("[Heuristic] Мяч в клешне! Авто-захват.");
            }
            else if (dist > 0.16f) // Мяч далеко (FOV откалиброван, можно доверять)
            {
                gas = 0.45f; // Едем вперёд
                
                // Эластичное подруливание пропорционально углу (центруем робота на мяч)
                if (Mathf.Abs(angle) > 0.15f)
                {
                    steer = angle * 0.7f; 
                }
            }
            else
            {
                // Мяч близко, но датчик в клешне еще не сработал — медленно подползаем
                gas = 0.2f;
                steer = angle * 0.5f;
            }
        }

        // Записываем результат автопилота
        continuousActions[0] = gas;
        continuousActions[1] = steer;
        if (continuousActions.Length > 2) continuousActions[2] = camYaw;
        if (discreteActions.Length > 0) discreteActions[0] = grip;

        // 🚨 АВАРИЙНЫЙ ПЕРЕХВАТ 🚨
        // Зажмите LEFT SHIFT чтобы управлять WASD напрямую
        if (Input.GetKey(KeyCode.LeftShift))
        {
            continuousActions[0] = Input.GetAxis("Vertical");
            continuousActions[1] = Input.GetAxis("Horizontal");
            
            if (Input.GetKey(KeyCode.Q)) continuousActions[2] = -1f;
            else if (Input.GetKey(KeyCode.E)) continuousActions[2] = 1f;
            
            if (Input.GetKey(KeyCode.Z)) discreteActions[0] = 2;      // Close
            else if (Input.GetKey(KeyCode.X)) discreteActions[0] = 1; // Open
        }
    }
}
