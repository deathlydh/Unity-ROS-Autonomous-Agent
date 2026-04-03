using System.Collections.Generic;
using System.IO;
using UnityEngine;

// ============================================================
//  ЦИФРОВОЙ ДВОЙНИК — танковое управление
//
//  Rigidbody настройки:
//    ✔ Freeze Rotation X, Y, Z  — ВСЕ ТРИ включить
//    ✔ Freeze Position Y         — включить (если не нужна физика падения)
//    ✔ Collision Detection: Continuous
//    ✔ Interpolate: Interpolate
//
//  Collider: только BoxCollider на корпусе
// ============================================================

[System.Serializable]
public class SavedCommand
{
    public float timeFromStart;
    public float velocityLinear;  // m/s
    public float velocityAngular; // rad/s (ROS convention)
}

[System.Serializable]
public class TrajectoryLog
{
    public List<SavedCommand> commands = new List<SavedCommand>();
}

public class DigitalTwinController : MonoBehaviour
{
    // ── Движение ──────────────────────────────────────────────
    [Header("Движение")]
    [Tooltip("Скорость вперёд / назад (м/с). Макс робота ~0.57")]
    public float moveSpeed = 0.57f;

    [Tooltip("Скорость разворота (градусов/с). Макс робота ~202.5")]
    public float turnSpeed = 120f;

    [Tooltip("Инерция разгона — плавность (0 = мгновенно)")]
    [Range(0f, 0.95f)]
    public float smoothing = 0.05f;

    // ── Ультразвуковой датчик ──────────────────────────────
    [Header("Ультразвуковой датчик")]
    [Tooltip("Максимальная дальность (м)")]
    public float ultrasonicRange = 5f;

    [Tooltip("Дистанция срабатывания защиты (м)")]
    public float safetyDistance = 0.3f;

    [Tooltip("Смещение датчика относительно центра")]
    public Vector3 sensorOffset = new Vector3(0f, 0.1f, 0f);

    // ── Запись ───────────────────────────────────────────────
    [Header("Запись маршрута")]
    public float recordFrequency = 20f;

    // ── Меши гусениц / катков (опционально) ──────────────────
    [Header("Меши колёс (опционально)")]
    public Transform[] leftWheelMeshes;
    public Transform[] rightWheelMeshes;
    public float wheelRadius = 0.05f;

    // ── Приватные ─────────────────────────────────────────────
    [Header("Фильтр нажатий (Защита от микрокликов)")]
    [Tooltip("Минимальное время удержания кнопки для срабатывания (сек)")]
    public float inputDeadzoneTime = 0.08f;

    private float holdTimerLinear = 0f;
    private float holdTimerAngular = 0f;

    private Rigidbody rb;
    private float smoothLinear = 0f;
    private float smoothAngular = 0f;
    private float leftAngle = 0f;
    private float rightAngle = 0f;

    private bool isRecording = false;
    private float recordStartTime = 0f;
    private float recordTimer = 0f;
    private TrajectoryLog currentLog = new TrajectoryLog();

    public static string SavePath =>
        Application.dataPath + "/robot_trajectory.json";

    // ─────────────────────────────────────────────────────────
    void Start()
    {
        rb = GetComponent<Rigidbody>();

        // Все вращения заморожены — мы управляем ими сами через MoveRotation
        rb.constraints =
            RigidbodyConstraints.FreezeRotationX |
            RigidbodyConstraints.FreezeRotationY |
            RigidbodyConstraints.FreezeRotationZ |
            RigidbodyConstraints.FreezePositionY;   // робот не летит вниз/вверх

        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        rb.linearDamping = 5f;   // гасим остаточное скольжение
        rb.angularDamping = 10f;
        rb.sleepThreshold = 0f;

        Debug.Log("[DigitalTwin] Готов. WASD — движение, R — запись, F — сохранить.");
    }

    // ─────────────────────────────────────────────────────────
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.R)) StartRecording();
        if (Input.GetKeyDown(KeyCode.F)) StopAndSave();
    }

    // ─────────────────────────────────────────────────────────
    void FixedUpdate()
    {
        float inputForward = Input.GetAxis("Vertical");
        float inputTurn = Input.GetAxis("Horizontal");

        // ── Мертвая зона (Игнорирование микрокликов) ────────
        if (Mathf.Abs(inputForward) > 0.01f) holdTimerLinear += Time.fixedDeltaTime;
        else holdTimerLinear = 0f;

        if (Mathf.Abs(inputTurn) > 0.01f) holdTimerAngular += Time.fixedDeltaTime;
        else holdTimerAngular = 0f;

        if (holdTimerLinear < inputDeadzoneTime) inputForward = 0f;
        if (holdTimerAngular < inputDeadzoneTime) inputTurn = 0f;

        // ── Ультразвуковой датчик / Безопасность ───────────
        Vector3 rayOrigin = transform.position + transform.rotation * sensorOffset;
        bool hitObstacle = Physics.Raycast(rayOrigin, transform.forward, out RaycastHit hit, ultrasonicRange);
        float distance = hitObstacle ? hit.distance : ultrasonicRange;

        if (hitObstacle && distance < safetyDistance)
        {
            if (inputForward > 0f) 
            {
                inputForward = 0f; // Запрещаем движение вперед
            }
        }

        #if UNITY_EDITOR
        Color rayColor = hitObstacle ? Color.red : Color.green;
        Debug.DrawRay(rayOrigin, transform.forward * distance, rayColor);
        #endif

        // ── Сглаживание (инерция гусениц) ────────────────────
        float t = 1f - smoothing;
        smoothLinear = Mathf.Lerp(smoothLinear, inputForward, t);
        smoothAngular = Mathf.Lerp(smoothAngular, inputTurn, t);

        // ── ПОВОРОТ — через MoveRotation, не физику ───────────
        // Работает и на месте (танковый разворот) и в движении
        float yawDelta = smoothAngular * turnSpeed * Time.fixedDeltaTime;
        Quaternion newRot = rb.rotation * Quaternion.Euler(0f, yawDelta, 0f);
        rb.MoveRotation(newRot);

        // ── ДВИЖЕНИЕ — вдоль текущего forward ─────────────────
        Vector3 move = transform.forward * (smoothLinear * moveSpeed);
        rb.MovePosition(rb.position + move * Time.fixedDeltaTime);

        // Обнуляем любую остаточную физическую скорость
        // (иначе коллайдер добавляет дрейф после столкновений)
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // ── Запись ────────────────────────────────────────────
        if (isRecording)
        {
            recordTimer -= Time.fixedDeltaTime;
            if (recordTimer <= 0f)
            {
                recordTimer = 1f / recordFrequency;
                currentLog.commands.Add(new SavedCommand
                {
                    timeFromStart = Time.time - recordStartTime,
                    velocityLinear = smoothLinear * moveSpeed,
                    velocityAngular = -(smoothAngular * turnSpeed * Mathf.Deg2Rad)
                });
            }
        }

        // ── Анимация катков ───────────────────────────────────
        AnimateWheels(inputForward, inputTurn);
    }

    // ══════════════════════════════════════════════════════════
    //  АНИМАЦИЯ КАТКОВ
    // ══════════════════════════════════════════════════════════
    void AnimateWheels(float fwd, float turn)
    {
        if (wheelRadius <= 0f) return;

        // Левая гусеница быстрее при повороте вправо, и наоборот
        float leftSpeed = (fwd + turn) * moveSpeed;
        float rightSpeed = (fwd - turn) * moveSpeed;

        float leftDeg = (leftSpeed / (2f * Mathf.PI * wheelRadius)) * 360f * Time.fixedDeltaTime;
        float rightDeg = (rightSpeed / (2f * Mathf.PI * wheelRadius)) * 360f * Time.fixedDeltaTime;

        leftAngle += leftDeg;
        rightAngle += rightDeg;

        foreach (var t in leftWheelMeshes)
            if (t != null) t.localRotation = Quaternion.Euler(leftAngle, 0f, 0f);

        foreach (var t in rightWheelMeshes)
            if (t != null) t.localRotation = Quaternion.Euler(rightAngle, 0f, 0f);
    }

    // ══════════════════════════════════════════════════════════
    //  ЗАПИСЬ
    // ══════════════════════════════════════════════════════════
    public void StartRecording()
    {
        currentLog.commands.Clear();
        recordStartTime = Time.time;
        recordTimer = 0f;
        isRecording = true;
        Debug.Log("[DigitalTwin] ▶ Запись начата. F — сохранить.");
    }

    public void StopAndSave()
    {
        if (!isRecording) return;
        isRecording = false;
        string json = JsonUtility.ToJson(currentLog, true);
        File.WriteAllText(SavePath, json);
        Debug.Log($"[DigitalTwin] ■ Сохранено {currentLog.commands.Count} команд -> {SavePath}");
    }

    // ══════════════════════════════════════════════════════════
    //  GUI
    // ══════════════════════════════════════════════════════════
    void OnGUI()
    {
        GUI.color = Color.white;
        GUILayout.BeginArea(new Rect(10, 10, 300, 110));
        GUILayout.Label("── ЦИФРОВОЙ ДВОЙНИК ──");
        GUILayout.Label("WASD — движение  |  A/D на месте = разворот");
        GUILayout.Label("R — начать запись  |  F — сохранить");
        GUILayout.Label(isRecording
            ? $"<color=red>● REC  {(Time.time - recordStartTime):0.0}с  | {currentLog.commands.Count} команд</color>"
            : "○ Ожидание записи");
        GUILayout.EndArea();
    }
}