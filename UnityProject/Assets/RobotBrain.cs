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
    public int minActionLatency = 8;
    [Tooltip("Максимальная задержка действий (рандомизируется каждый эпизод).")]
    public int maxActionLatency = 13;
    [Tooltip("Задержка сенсоров (шаги). Имитирует запаздывание ROS-топиков.")]
    public int sensorLatency = 2;
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
    public SimulatedYoloCamera simulatedYolo; // ЗАМЕНА virtualCamera для обучения (YOLO-идентичные наблюдения)
    public ROSBridge rosBridge;

    [Header("Stage 6 Components")]
    public Transform cameraPivot; // Пустышка, на которой висит камера для вращения
    private float currentCameraYaw = 0f;

    [Header("Stage 3 Components")]
    public GripperController gripper;
    private DiagnosticLogger diagLogger;

    [Header("Parallel Training Setup")]
    public GameObject ballPrefab; // Префаб мяча
    private GameObject spawnedBall; // Ссылка на мяч (созданный или найденный)

    private Vector3 startPosition;
    private Quaternion startRotation;

    // --- Пенальти и Награды ---
    private float lastDistance = 1f;
    private bool wasSeeingBallLastStep = false;
    private int holdTicks = 0;

    // --- Action Rate Penalty (NVIDIA best practice) ---
    private float prevGas = 0f;
    private float prevSteering = 0f;
    private float prevCameraYaw = 0f;

    // --- Фаза 3: Слепой захват ---
    private bool wasCloseToBall = false;
    private float lastCloseAngle = 0f;
    private int blindApproachTicks = 0;
    private const int BLIND_APPROACH_MAX = 80;

    // --- Retry: Отъехать назад и попробовать снова ---
    private bool isRetrying = false;
    private int retryBackupTicks = 0;
    private int retryCount = 0;
    private const int MAX_RETRIES = 2;
    private const int RETRY_BACKUP_DURATION = 80;

    // --- Анти-застревание (Stuck Detection) ---
    private Vector3 lastPosition;
    private int stuckTimer = 0;

    // --- Burst Dropout (YOLO теряет мяч пачками, не покадрово) ---
    private int burstDropoutRemaining = 0;

    // --- Одноразовый бонус за остановку ---
    private float lastCamDelta = 0f;

    // --- Клешня: трекинг физического состояния серво на реальном роботе ---
    private bool physicalServoCloseCmd = false;
    private int failedGrabTicks = 0;
    private const int FAILED_GRAB_THRESHOLD = 25;

    // --- Принудительная пауза после закрытия клешни (v15) ---
    private int gripperHoldTicks = 0;
    private const int GRIPPER_HOLD_DURATION = 25; // 25 тиков × 20мс = 500мс пауза

    // --- v17: Таймер удержания клешни при потере ИК (анти-дребезг) ---
    private int holdWithoutIR = 0;
    private const int HOLD_WITHOUT_IR_MAX = 100; // 100 × 20мс = 2 сек удержания

    // --- v17: Клешня закрывается только если мяч был виден недавно ---
    private int lastBallSeenStep = -999;
    private const int BALL_SEEN_WINDOW = 50; // 50 тиков = 1 сек

    // --- Camera Step Limit: реальное серво MAX_CAMERA_STEP = 15°/тик ---
    private const float MAX_CAMERA_STEP_NORMALIZED = 15f / 90f; // 15° из 90° диапазона

    public override void Initialize()
    {
        track = GetComponent<TrackController>();
        sensors = GetComponent<VirtualSensors>();
        rb = GetComponent<Rigidbody>();
        diagLogger = GetComponent<DiagnosticLogger>();

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
        if (spawnedBall != null && simulatedYolo != null)
        {
            simulatedYolo.targetBall = spawnedBall.transform;
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            isMovementEnabled = !isMovementEnabled;
            Debug.Log("Движение " + (isMovementEnabled ? "разрешено!" : "запрещено!"));
            
            // v27: Автоматический сброс LSTM и одометрии при старте движения
            if (isMovementEnabled)
            {
                ResetInferenceEpisode();
            }
        }
        
        // Ручной сброс кнопкой R (полезно при тестах)
        if (Input.GetKeyDown(KeyCode.R))
        {
            ResetInferenceEpisode();
        }
    }

    public void ResetInferenceEpisode()
    {
        Debug.Log("[RobotBrain] СБРОС: Обнуление LSTM-памяти и одометрии для чистого старта");
        EndEpisode(); // ML-Agents сбросит LSTM память и вызовет OnEpisodeBegin()
    }

    public override void OnEpisodeBegin()
    {
        // v27: Всегда сбрасываем позицию и ротацию виртуального робота (для тренировки и инференса!)
        // Это обнуляет displacementX и displacementZ
        transform.position = startPosition;
        transform.rotation = startRotation;

        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        if (isTraining)
        {
            // Рандомизация стартовой ротации — агент учится искать мяч в любом направлении
            float randomYaw = UnityEngine.Random.Range(-180f, 180f);
            transform.rotation = startRotation * Quaternion.Euler(0f, randomYaw, 0f);

            // --- Сброс мяча (Stage 3) ---
            ResetBall();
        }

         // --- Domain Randomization (Физика) — расширенные диапазоны для Sim-to-Real ---
        if (isTraining && track != null)
        {
            track.moveSpeed = UnityEngine.Random.Range(0.3f, 0.7f);   // ±40% (реальный макс 0.5 м/с)
            track.turnSpeed = UnityEngine.Random.Range(80f, 160f);    // ±33%
            track.smoothing = UnityEngine.Random.Range(0.01f, 0.25f); // Разная "отзывчивость" моторов
        }
        if (isTraining && rb != null)
        {
            rb.mass = UnityEngine.Random.Range(1.0f, 4.0f); // Реальный ~2.5кг ± батарея/груз
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
        physicalServoCloseCmd = false;
        failedGrabTicks = 0;
        holdWithoutIR = 0;
        prevGas = 0f;
        prevSteering = 0f;
        prevCameraYaw = 0f;
        burstDropoutRemaining = 0;
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
        // РАЗДЕЛЬНЫЙ шум: YOLO angle точный (bbox center), distance шумный (bbox height)
        float noiseAmp = isTraining ? Academy.Instance.EnvironmentParameters.GetWithDefault("vision_noise", 0.02f) : 0f;
        float noiseVisAngle = isTraining ? UnityEngine.Random.Range(-noiseAmp, noiseAmp) : 0f;
        float noiseVisDist = isTraining ? UnityEngine.Random.Range(-noiseAmp * 3f, noiseAmp * 3f) : 0f; // Дистанция в 3× шумнее угла
        // BURST DROPOUT: YOLO теряет мяч пачками (3-8 кадров), не покадрово
        float dropoutRate = isTraining ? Unity.MLAgents.Academy.Instance.EnvironmentParameters.GetWithDefault("vision_dropout", 0f) : 0f;
        float bodyRotDropout = (rb != null && rb.angularVelocity.magnitude > 0.5f) ? 0.12f : 0f;
        float effectiveDropout = dropoutRate + (lastCamDelta > 0.3f ? 0.15f : 0f) + bodyRotDropout;
        // Пачечный dropout: если сработал — теряем на 3-8 кадров подряд
        if (burstDropoutRemaining > 0)
        {
            burstDropoutRemaining--;
        }
        else if (isTraining && UnityEngine.Random.value < effectiveDropout)
        {
            burstDropoutRemaining = UnityEngine.Random.Range(5, 16); // 5-15 кадров потери (реальный YOLO: до 48 шагов)
        }
        bool yoloDropout = burstDropoutRemaining > 0;

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
            // ИСПРАВЛЕНО: Neural Network обучалась на том, что когда мяча нет, угол строго = 0f.
            // Передача lastKnownBallDirection в слот угла вызывала неадекватное поведение.
            bool ballVisible = realVision.seesBall;
            if (ballVisible) lastBallSeenStep = StepCount;
            sensor.AddObservation(ballVisible ? Mathf.Clamp(realVision.normalizedAngle, -1f, 1f) : 0f);
            sensor.AddObservation(ballVisible ? Mathf.Clamp01(realVision.normalizedDistance) : 1f);
            sensor.AddObservation(realVision.lastKnownBallDirection);
            sensor.AddObservation(ballVisible ? 1f : 0f);
            sensor.AddObservation(currentCameraYaw);
        }
        else if (simulatedYolo != null)
        {
            // SimulatedYoloCamera: YOLO-идентичные наблюдения через проекцию камеры
            bool ballVisible = simulatedYolo.seesBall && !yoloDropout;
            if (ballVisible) lastBallSeenStep = StepCount;
            
            sensor.AddObservation(ballVisible ? Mathf.Clamp(simulatedYolo.normalizedAngle + noiseVisAngle, -1f, 1f) : 0f);
            sensor.AddObservation(ballVisible ? Mathf.Clamp01(simulatedYolo.normalizedDistance + noiseVisDist) : 1f);
            sensor.AddObservation(simulatedYolo.lastKnownBallDirection);
            sensor.AddObservation(ballVisible ? 1f : 0f);
            sensor.AddObservation(currentCameraYaw);
        }
        else if (virtualCamera != null)
        {
            // Legacy fallback: VirtualCamera (raycast-based)
            bool ballVisible = virtualCamera.seesBall && !yoloDropout;
            
            sensor.AddObservation(ballVisible ? Mathf.Clamp(virtualCamera.normalizedAngle + noiseVisAngle, -1f, 1f) : 0f);
            sensor.AddObservation(ballVisible ? Mathf.Clamp01(virtualCamera.normalizedDistance + noiseVisDist) : 1f);
            sensor.AddObservation(virtualCamera.lastKnownBallDirection);
            sensor.AddObservation(ballVisible ? 1f : 0f);
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

        // === v16: EGOCENTRIC FEATURES (Self-Localization) ===
        // Дают агенту понимание "где я" и "куда двигаюсь" без полной карты местности.
        // Это позволяет LSTM строить внутреннюю модель пространства.

        // 1. Смещение от стартовой позиции (эгоцентрическое, нормализованное)
        Vector3 displacement = transform.position - startPosition;
        sensor.AddObservation(Mathf.Clamp(displacement.x / 3f, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp(displacement.z / 3f, -1f, 1f));

        // 2. Heading (направление взгляда робота, 0-1)
        float heading = transform.eulerAngles.y / 360f;
        sensor.AddObservation(heading);

        // 3. Собственная скорость (по сути одометрия)
        if (rb != null)
        {
            float speed = rb.linearVelocity.magnitude;
            sensor.AddObservation(Mathf.Clamp01(speed / 0.5f)); // 0=стоит, 1=макс
        }
        else
        {
            sensor.AddObservation(0f);
        }

        // 4. Время с момента последнего видения мяча (0=только что видел, 1=давно не видел)
        float timeSinceSeen = 1f;
        if (simulatedYolo != null && simulatedYolo.seesBall)
            timeSinceSeen = 0f;
        else if (realVision != null && realVision.seesBall)
            timeSinceSeen = 0f;
        else
            timeSinceSeen = Mathf.Clamp01((float)blindApproachTicks / 100f);
        sensor.AddObservation(timeSinceSeen);
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

        if (isTraining && currentActionLatency > 0)
        {
            // --- LATENCY: Кладем текущее действие нейросети в буфер ---
            float[] newContinuous = new float[] {
                actions.ContinuousActions[0],
                actions.ContinuousActions[1],
                actions.ContinuousActions.Length > 2 ? actions.ContinuousActions[2] : 0f
            };
            actionBuffer.Enqueue(newContinuous);

            // Достаем УСТАРЕВШЕЕ действие из головы очереди
            float[] delayed = actionBuffer.Dequeue();
            gas = delayed[0];
            steering = delayed[1];
            cameraYawInput = delayed[2];
        }
        else
        {
            // Без задержки (инференс на реальном роботе)
            gas = actions.ContinuousActions[0];
            steering = actions.ContinuousActions[1];
            cameraYawInput = actions.ContinuousActions.Length > 2 ? actions.ContinuousActions[2] : 0f;
        }

        if (enableVerboseLogging)
        {
            Debug.Log($"[RobotBrain] Inputs -> BallSeen: {realVision?.seesBall}, Angle: {realVision?.normalizedAngle:F2}, Dist: {realVision?.normalizedDistance:F2}");
            Debug.Log($"[RobotBrain] Sensors -> UZ: {sensors?.ultrasonicDist:F2}, IR_L: {sensors?.leftIR}, IR_R: {sensors?.rightIR}, RealSensors: {sensors?.useRealSensors}");
            Debug.Log($"[RobotBrain] FINAL Output -> Gas: {gas:F2}, Steer: {steering:F2} (Latency: {currentActionLatency} steps)");
            // Диагностика задержек для сравнения sim vs real
            Debug.Log($"[RobotBrain] TIMING -> Time.time: {Time.time:F3}, deltaTime: {Time.deltaTime:F4}, fixedDelta: {Time.fixedDeltaTime:F4}, Step: {StepCount}");
        }

        // ============================================================
        // ФАЗА 0: МЯЧ СХВАЧЕН — ПРОВЕРЯЕМ САМЫМ ПЕРВЫМ!
        // Ничто не должно убивать эпизод, пока мяч в клешне.
        // ============================================================
        if (gripper != null && gripper.hasBall)
        {
            holdTicks++;
            stuckTimer = 0;
            AddReward(0.02f);

            if (track != null) track.Move(0f, 0f);
            if (rosBridge != null && !isTraining)
            {
                rosBridge.PublishCommand(0f, 0f);
                rosBridge.PublishGripperCmd(2);
            }

            if (isTraining && holdTicks >= 50)
            {
                AddReward(5.0f);
                Unity.MLAgents.Academy.Instance.StatsRecorder.Add("Custom/GrabSuccess", 1.0f);
                EndEpisode();
                return;
            }
            return;
        }
        holdTicks = 0;

        // === АНТИ-ЗАСТРЕВАНИЕ И ЛИМИТ ЭПИЗОДА ===
        int episodeLimit = Mathf.RoundToInt(
            Academy.Instance.EnvironmentParameters.GetWithDefault("episode_length", 800f)
        );
        if (isTraining && StepCount >= episodeLimit)
        {
            AddReward(-0.05f); // Лёгкий штраф — не наказываем за сложное расположение мяча
            Academy.Instance.StatsRecorder.Add("Custom/GrabSuccess", 0.0f);
            EndEpisode();
            return;
        }

        if (Mathf.Abs(gas) > 0.1f || Mathf.Abs(steering) > 0.1f)
        {
            stuckTimer++;
            if (stuckTimer >= 200)
            {
                float distanceTravelled = Vector3.Distance(transform.position, lastPosition);
                if (distanceTravelled < 0.5f)
                {
                    AddReward(-0.5f);
                    if (isTraining) Unity.MLAgents.Academy.Instance.StatsRecorder.Add("Custom/GrabSuccess", 0.0f);
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

        // --- Camera Step Limit: реальное серво двигается макс 15°/тик ---
        float targetCamYaw = Mathf.Clamp(cameraYawInput, -1f, 1f);
        float camDelta = targetCamYaw - currentCameraYaw;
        if (Mathf.Abs(camDelta) > MAX_CAMERA_STEP_NORMALIZED)
        {
            targetCamYaw = currentCameraYaw + Mathf.Sign(camDelta) * MAX_CAMERA_STEP_NORMALIZED;
        }
        lastCamDelta = Mathf.Abs(targetCamYaw - currentCameraYaw);
        currentCameraYaw = targetCamYaw;
        if (cameraPivot != null)
        {
            cameraPivot.localRotation = Quaternion.Euler(0f, currentCameraYaw * 90f, 0f);
        }

        // === ПРИНУДИТЕЛЬНАЯ ОСТАНОВКА ПРИ ЗАКРЫТИИ КЛЕШНИ (v15) ===
        // Когда клешня закрывается, робот должен стоять и ждать подтверждения gripperIR.
        // Без этого робот проезжает мимо мяча после закрытия клешни.
        if (gripperHoldTicks > 0)
        {
            gas = 0f;
            steering = 0f;
            gripperHoldTicks--;
        }

        if (track != null)
        {
            // Убрали EndEpisode при столкновении по сонару, так как это учило агента вообще не ехать к стенам
            // Штраф за физические столкновения обрабатывается в СЕНСОРНЫЕ ШТРАФЫ

            track.Move(gas, steering);

            if (rosBridge != null && !isTraining)
            {
                rosBridge.PublishCommand(gas, steering);
                rosBridge.PublishCameraCmd(currentCameraYaw);
            }
        }

        // --- Логика Клешни (АВТОМАТИЧЕСКАЯ) ---
        // Клешня ВСЕГДА открыта. Закрывается ТОЛЬКО по ИК-датчику.
        // Нейросеть НЕ управляет клешнёй — она управляет только движением и камерой.
        // Это упрощает action space и соответствует реальному роботу (авто-захват по ИК).
        bool gripperSensorActive = sensors != null && sensors.gripperIR == 1;

        // В симуляции мяч теряет коллайдер при захвате, ИК перестает его видеть (bug).
        // Симулируем, что ИК все еще видит мяч, если он уже в клешне.
        if (sensors != null && !sensors.useRealSensors && gripper != null && gripper.hasBall)
        {
            gripperSensorActive = true;
        }

        if (gripper != null)
        {
            // v17: Клешня закрывается только если ИК сработал И мяч был виден камерой недавно.
            // Это отсекает ложные срабатывания от ножек стульев, пола и т.д.
            bool ballRecentlySeen = (StepCount - lastBallSeenStep) < BALL_SEEN_WINDOW;
            // В тренировке всегда разрешаем (мяч гарантированно есть), на реале — только если YOLO видел мяч
            bool allowGrip = isTraining || ballRecentlySeen;

            if (!gripper.hasBall && gripperSensorActive && allowGrip)
            {
                gripper.CloseGripper();
                gripperHoldTicks = GRIPPER_HOLD_DURATION;
                if (rosBridge != null && !isTraining)
                {
                    rosBridge.PublishGripperCmd(2);
                    physicalServoCloseCmd = true;
                    failedGrabTicks = 0;
                    Debug.Log($"[RobotBrain] Захват: gripperIR=1, ballRecentlySeen={ballRecentlySeen}, step={StepCount}");
                }
            }
            else if (!gripper.hasBall && gripperSensorActive && !allowGrip)
            {
                // ИК сработал, но мяч не был виден — ложное срабатывание, игнорируем
                if (rosBridge != null && !isTraining)
                {
                    rosBridge.PublishGripperCmd(4); // Открыть клешню назад
                    Debug.Log($"[RobotBrain] Ложное срабатывание ИК: мяч не был виден {StepCount - lastBallSeenStep} тиков назад");
                }
            }
            else if (gripper.hasBall && !gripperSensorActive)
            {
                // v17: НЕ разжимаем мгновенно! ИК может дребезжать при сдвиге мяча.
                // Ждём 2 секунды без ИК-сигнала перед разжатием.
                holdWithoutIR++;
                if (holdWithoutIR >= HOLD_WITHOUT_IR_MAX)
                {
                    gripper.OpenGripper();
                    holdWithoutIR = 0;
                    if (rosBridge != null && !isTraining)
                    {
                        rosBridge.PublishGripperCmd(4);
                        physicalServoCloseCmd = false;
                        failedGrabTicks = 0;
                    }
                }
            }
            else if (gripper.hasBall && gripperSensorActive)
            {
                // ИК снова видит мяч — сбрасываем таймер разжатия
                holdWithoutIR = 0;
            }
            else if (!isTraining && !gripper.hasBall && !gripperSensorActive && physicalServoCloseCmd)
            {
                failedGrabTicks++;
                if (failedGrabTicks >= FAILED_GRAB_THRESHOLD)
                {
                    rosBridge?.PublishGripperCmd(4);
                    physicalServoCloseCmd = false;
                    failedGrabTicks = 0;
                }
            }
            else if (!gripperSensorActive)
            {
                failedGrabTicks = 0;
            }
        }

        // (ФАЗА 0 перенесена выше — до проверок EndEpisode)

        // === REWARD #4: SENSOR PROXIMITY (заменяет OnCollisionEnter) ===
        // Штраф по ДАТЧИКАМ, а не по коллизиям — на реальном роботе коллизий нет
        if (isTraining && sensors != null)
        {
            // УЗ: градиентный штраф (чем ближе стена — тем больше)
            if (sensors.ultrasonicDist < 0.12f) // < 60см при maxRange 5м
            {
                float sonarProx = 1f - (sensors.ultrasonicDist / 0.12f);
                AddReward(-0.03f * sonarProx);
            }

            // ИК: бинарный штраф (датчики бинарные — 0 или 1)
            if (sensors.leftIR == 1 || sensors.rightIR == 1)
            {
                AddReward(-0.01f);
            }
        }

        // === REWARD #5: ACTION RATE PENALTY (NVIDIA Isaac Lab стандарт) ===
        if (isTraining)
        {
            float actionRate = Mathf.Pow(gas - prevGas, 2)
                             + Mathf.Pow(steering - prevSteering, 2)
                             + Mathf.Pow(cameraYawInput - prevCameraYaw, 2);
            AddReward(-0.05f * actionRate);

            // === REWARD #6: MILD REVERSE PENALTY (v15) ===
            // v12 удалил агрессивный -0.02 (был воркараунд для бага мотора).
            // Мотор исправлен, но без ЛЮБОГО штрафа reverse=41% — слишком много.
            // Мягкий -0.005 + исключения для retry и стен.
            if (gas < -0.1f && !isRetrying)
            {
                bool nearWall = sensors != null && sensors.ultrasonicDist < 0.12f;
                bool nearSideWall = sensors != null && (sensors.leftIR == 1 || sensors.rightIR == 1);
                if (!nearWall && !nearSideWall)
                {
                    AddReward(-0.005f);
                }
            }
        }
        prevGas = gas;
        prevSteering = steering;
        prevCameraYaw = cameraYawInput;

        // === ДИАГНОСТИКА (TensorBoard) ===
        if (isTraining)
        {
            Academy.Instance.StatsRecorder.Add("Custom/Gas", gas);
            Academy.Instance.StatsRecorder.Add("Custom/IsReverse", gas < -0.1f ? 1f : 0f);
            Academy.Instance.StatsRecorder.Add("Custom/BlindTicks", blindApproachTicks);
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
        else if (simulatedYolo != null && simulatedYolo.seesBall)
        {
            hasSeenBall = true;
            currentAngle = simulatedYolo.normalizedAngle;
            currentDist = simulatedYolo.normalizedDistance;
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
            isRetrying = false;
            // Убраны отдельные бонусы/штрафы — action rate penalty уже учит плавности
        }

        // === ФАЗА RETRY: Отъезд назад после промаха ===
        // КРИТИЧЕСКИЙ ФИКС: Если мы сдаем назад, и вдруг мяч появился снова - НЕМЕДЛЕННО обрываем Retry!
        if (isRetrying && hasSeenBall)
        {
            isRetrying = false;
            wasCloseToBall = false;
            retryBackupTicks = 0;
            blindApproachTicks = 0;
        }

        if (isRetrying)
        {
            retryBackupTicks++;

            // Retry — аварийный режим. Без отдельных штрафов.
            // Action rate penalty автоматически штрафует резкие переключения.

            if (retryBackupTicks >= RETRY_BACKUP_DURATION)
            {
                isRetrying = false;
                wasCloseToBall = false;
                retryBackupTicks = 0;
                blindApproachTicks = 0;
                lastDistance = 1f;
            }
            return;
        }

        if (hasSeenBall)
        {
            // Убираем спайк-награду: если мяч только появился, дельта-награды не будет
            if (!wasSeeingBallLastStep)
            {
                lastDistance = currentDist;
            }

            // Мяч видим камерой — retry-сброс при повторном обнаружении
            blindApproachTicks = 0;
            isRetrying = false;

            // === REWARD #1: DISTANCE DELTA (proximity-scaled) ===
            // Чем ближе мяч — тем ВАЖНЕЕ точность приближения (множитель растёт).
            // dist=0.8 → 2.8x, dist=0.3 → 4.8x, dist=0.1 → 5.6x
            if (wasSeeingBallLastStep)
            {
                float distanceDelta = lastDistance - currentDist;
                if (Mathf.Abs(distanceDelta) < 0.5f) // Фильтр спайков
                {
                    float proximityMultiplier = 2.0f + 4.0f * (1.0f - Mathf.Clamp01(currentDist));
                    AddReward(distanceDelta * proximityMultiplier);
                }
            }

            // === REWARD #7: PROXIMITY SLOW-DOWN BONUS (v15, усилен) ===
            if (currentDist < 0.3f && gas > 0.01f && gas < 0.3f)
            {
                AddReward(0.005f);
            }

            // === REWARD #8: SPEED PENALTY NEAR BALL (v26) ===
            // Штраф за высокую скорость вблизи мяча — не таранить!
            if (currentDist < 0.25f && Mathf.Abs(gas) > 0.4f)
            {
                AddReward(-0.01f);
            }

            // === REWARD #9: ALIGNMENT BONUS (v26) ===
            // Бонус за центрированный мяч при близком подъезде.
            // Учит робота выравниваться перед захватом.
            if (currentDist < 0.4f && Mathf.Abs(currentAngle) < 0.15f)
            {
                AddReward(0.005f);
            }

            // Обновляем фазу для слепого подъезда
            if (currentDist <= 0.35f)
            {
                wasCloseToBall = true;
                lastCloseAngle = currentAngle;
            }
            else
            {
                wasCloseToBall = false;
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
                // ФАЗА 3: МЯЧ ПРОПАЛ (был рядом)
                // Мяч в слепой зоне камеры (под роботом/в клешне)
                // БЕЗ этого бонуса агент едет назад ("safe" action)
                // =======================================
                blindApproachTicks++;

                // Бонус за медленное движение вперёд (ползи к мячу, не отъезжай)
                if (gas > 0.01f && gas < 0.3f)
                {
                    AddReward(0.003f);
                }

                if (blindApproachTicks >= BLIND_APPROACH_MAX)
                {
                    if (retryCount < MAX_RETRIES)
                    {
                        isRetrying = true;
                        retryBackupTicks = 0;
                        retryCount++;
                        blindApproachTicks = 0;
                    }
                    else
                    {
                        wasCloseToBall = false;
                        blindApproachTicks = 0;
                    }
                }
            }
            // Фаза поиска: без отдельных наград/штрафов
            // Distance delta + action rate penalty достаточно

            lastDistance = 1f;
            wasSeeingBallLastStep = false;
        }

        // === CSV-ЛОГГЕР (для диагностики sim vs real) ===
        if (diagLogger != null)
        {
            // v15: Добавлен simulatedYolo в логгер (раньше BallSeen=0 в 100% sim-строк!)
            bool bs = (realVision != null && realVision.seesBall) 
                   || (simulatedYolo != null && simulatedYolo.seesBall) 
                   || (virtualCamera != null && virtualCamera.seesBall);
            float ba = realVision?.seesBall == true ? realVision.normalizedAngle 
                     : (simulatedYolo?.seesBall == true ? simulatedYolo.normalizedAngle 
                     : (virtualCamera?.seesBall == true ? virtualCamera.normalizedAngle : 0f));
            float bd = realVision?.seesBall == true ? realVision.normalizedDistance 
                     : (simulatedYolo?.seesBall == true ? simulatedYolo.normalizedDistance 
                     : (virtualCamera?.seesBall == true ? virtualCamera.normalizedDistance : 0f));
            
            // v18: Расширенные данные для диагностики
            bool ballRecent = (StepCount - lastBallSeenStep) < BALL_SEEN_WINDOW;
            Vector3 disp = transform.position - startPosition;
            float spd = rb != null ? rb.linearVelocity.magnitude : 0f;
            float hdg = transform.eulerAngles.y / 360f;
            
            diagLogger.LogStep(StepCount, bs, ba, bd,
                sensors?.ultrasonicDist ?? 1f, sensors?.leftIR ?? 0, sensors?.rightIR ?? 0, sensors?.gripperIR ?? 0,
                currentCameraYaw, gas, steering, cameraYawInput,
                gripper != null && gripper.hasBall, holdTicks, isRetrying, wasCloseToBall,
                blindApproachTicks, ballRecent, holdWithoutIR,
                Mathf.Clamp(disp.x / 3f, -1f, 1f), Mathf.Clamp(disp.z / 3f, -1f, 1f), hdg, spd,
                transform.position.x, transform.position.z,
                realVision != null ? realVision.yoloConfidence : 0f,
                realVision != null ? realVision.bboxWidth : 0f,
                realVision != null ? realVision.bboxHeight : 0f,
                sensors != null ? sensors.realPwmLeft : 0f,
                sensors != null ? sensors.realPwmRight : 0f);
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
                float maxDist = Academy.Instance.EnvironmentParameters.GetWithDefault("ball_max_distance", 1.5f);

                // === 360° SPAWN: мяч появляется в ЛЮБОМ направлении вокруг робота ===
                float spawnAngle = Random.Range(0f, 360f);
                float spawnDist = Random.Range(0.5f, maxDist);
                Vector3 direction = Quaternion.Euler(0f, spawnAngle, 0f) * Vector3.forward;
                randomPos = transform.position + direction * spawnDist;
                randomPos.y = transform.position.y + 0.2f;

                // --- Проверка: не попал ли мяч в объект или за пределы арены ---
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

                // Дополнительно: Raycast вниз — проверяем что под мячом есть пол (не за границей арены)
                if (!hasObstacle)
                {
                    bool hasFloor = Physics.Raycast(randomPos + Vector3.up * 0.5f, Vector3.down, 2f);
                    if (!hasFloor) hasObstacle = true;
                }

                if (!hasObstacle) validPos = true;
                attempts++;
            }

            if (!validPos)
            {
                // Fallback: прямо перед роботом (гарантированно валидная позиция)
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

            // Включаем коллайдер мяча обратно (мог быть отключен при захвате)
            Collider ballCollider = spawnedBall.GetComponent<Collider>();
            if (ballCollider != null) ballCollider.enabled = true;
        }

        if (gripper != null)
        {
            gripper.hasBall = false;
        }
    }

    // OnCollisionEnter УБРАН — штраф теперь по ДАТЧИКАМ (Sensor Proximity)
    // На реальном роботе нет OnCollisionEnter — есть только УЗ и ИК

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var continuousActions = actionsOut.ContinuousActions;

        // По умолчанию всё стоит
        float gas = 0f;
        float steer = 0f;
        float camYaw = 0f;

        // Считываем реальное зрение (YOLO)
        bool sees = realVision != null && realVision.seesBall;
        float angle = realVision != null ? realVision.normalizedAngle : 0f;
        float dist = realVision != null ? realVision.normalizedDistance : 1f;

        if (!sees)
        {
            steer = 0.5f;
            camYaw = Mathf.Sin(Time.time * 1.5f) * 0.6f; 
        }
        else
        {
            camYaw = Mathf.Lerp(currentCameraYaw, angle, Time.deltaTime * 5f); 

            if (sensors != null && sensors.gripperIR == 1)
            {
                // Клешня сработает автоматически по ИК — просто стоим
                gas = 0f;
                steer = 0f;
                if (enableVerboseLogging) Debug.Log("[Heuristic] Мяч в клешне! Авто-захват.");
            }
            else if (dist > 0.16f)
            {
                gas = 0.45f;
                if (Mathf.Abs(angle) > 0.15f)
                {
                    steer = angle * 0.7f; 
                }
            }
            else
            {
                gas = 0.2f;
                steer = angle * 0.5f;
            }
        }

        // Записываем результат автопилота (только continuous, клешня автоматическая)
        continuousActions[0] = gas;
        continuousActions[1] = steer;
        if (continuousActions.Length > 2) continuousActions[2] = camYaw;

        // 🚨 АВАРИЙНЫЙ ПЕРЕХВАТ 🚨
        // Зажмите LEFT SHIFT чтобы управлять WASD напрямую
        if (Input.GetKey(KeyCode.LeftShift))
        {
            continuousActions[0] = Input.GetAxis("Vertical");
            continuousActions[1] = Input.GetAxis("Horizontal");
            
            if (Input.GetKey(KeyCode.Q)) continuousActions[2] = -1f;
            else if (Input.GetKey(KeyCode.E)) continuousActions[2] = 1f;
        }
    }
}
