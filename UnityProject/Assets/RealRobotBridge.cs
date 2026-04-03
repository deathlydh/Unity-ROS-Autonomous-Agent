using System.Collections;
using System.IO;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;

// ============================================================
//  МОСТ К РЕАЛЬНОМУ РОБОТУ — только ROS и воспроизведение
//  Не двигает объекты в Unity. Только отправляет команды.
//  Кидай на любой пустой GameObject в сцене.
// ============================================================

public class RealRobotBridge : MonoBehaviour
{
    // ── ROS настройки ─────────────────────────────────────────
    [Header("ROS настройки")]
    [Tooltip("Топик команд скорости (должен совпадать с unity.py на Raspberry Pi)")]
    public string topicName = "/cmd_vel";

[Tooltip("Линейный: cmd 1.0 = 0.617 м/с реальных (по замеру 54см/1.75с)")]
    public float linearCalibrationFactor = 0.617f;

    [Tooltip("Угловой: cmd 1.0 = 231°/с = 4.032 рад/с реальных (по замеру 405°/1.75с)")]
    public float angularCalibrationFactor = 4.032f;

    // ── Прямое управление (опционально) ──────────────────────
    [Header("Прямое управление реальным роботом (кнопка H)")]
    [Tooltip("Если включено — WASD сразу едет реальный робот (без записи)")]
    public bool enableDirectControl = false;

    [Tooltip("Частота отправки команд при прямом управлении (Гц)")]
    public float directControlHz = 20f;

    // ── Внутренние переменные ─────────────────────────────────
    private ROSConnection ros;
    private bool isPlaying = false;
    private float directTimer = 0f;

    // ─────────────────────────────────────────────────────────
    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<TwistMsg>(topicName);
        Debug.Log("[RealRobotBridge] Подключён к ROS. Нажми P — воспроизвести маршрут.");
    }

    // ─────────────────────────────────────────────────────────
    void Update()
    {
        // Переключение прямого управления
        if (Input.GetKeyDown(KeyCode.H))
        {
            enableDirectControl = !enableDirectControl;
            if (!enableDirectControl) SendStop(); // стоп при выключении
            Debug.Log("[RealRobotBridge] Прямое управление: " +
                      (enableDirectControl ? "ВКЛЮЧЕНО (H)" : "ВЫКЛЮЧЕНО"));
        }

        // Воспроизведение
        if (Input.GetKeyDown(KeyCode.P) && !isPlaying)
            StartPlayback();

        // Прямое управление реальным роботом
        if (enableDirectControl && !isPlaying)
        {
            directTimer += Time.deltaTime;
            if (directTimer >= 1f / directControlHz)
            {
                directTimer = 0f;
                float fwd = Input.GetAxis("Vertical");
                float turn = -Input.GetAxis("Horizontal"); // минус = ROS-конвенция

                // Конвертируем raw input в физические единицы (как при записи)
                var twin = FindFirstObjectByType<DigitalTwinController>();
                float linVel = fwd * (twin != null ? twin.moveSpeed : 0.57f);
                float angVel = turn * (twin != null ? twin.turnSpeed : 120f) * Mathf.Deg2Rad;
                SendTwist(linVel, angVel);
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  ВОСПРОИЗВЕДЕНИЕ МАРШРУТА
    // ══════════════════════════════════════════════════════════

    void StartPlayback()
    {
        string path = DigitalTwinController.SavePath;
        if (!File.Exists(path))
        {
            Debug.LogWarning("[RealRobotBridge] Файл маршрута не найден: " + path +
                             "\nСначала запиши маршрут (R → F) в цифровом двойнике.");
            return;
        }

        string json = File.ReadAllText(path);
        TrajectoryLog log = JsonUtility.FromJson<TrajectoryLog>(json);

        if (log == null || log.commands.Count == 0)
        {
            Debug.LogWarning("[RealRobotBridge] Файл пустой или повреждён.");
            return;
        }

        StartCoroutine(PlaybackCoroutine(log));
    }

    IEnumerator PlaybackCoroutine(TrajectoryLog log)
    {
        isPlaying = true;
        Debug.Log($"[RealRobotBridge] ▶ Воспроизведение {log.commands.Count} команд на реальном роботе...");

        float startTime = Time.time;

        foreach (var cmd in log.commands)
        {
            // Ждём нужного момента времени
            while (Time.time - startTime < cmd.timeFromStart)
                yield return null;

            SendTwist(cmd.velocityLinear, cmd.velocityAngular);
        }

        // Остановка в конце маршрута
        SendStop();
        isPlaying = false;
        Debug.Log("[RealRobotBridge] ■ Воспроизведение завершено.");
    }

    // ══════════════════════════════════════════════════════════
    //  ОТПРАВКА КОМАНД В ROS
    // ══════════════════════════════════════════════════════════

    void SendTwist(float linVel, float angVel)
    {
        var msg = new TwistMsg();
        // Unity Velocity (m/s) → ROS cmd value
        msg.linear.x = linVel / linearCalibrationFactor;
        // Unity Angular (rad/s) → ROS cmd value  
        msg.angular.z = angVel / angularCalibrationFactor;
        ros.Publish(topicName, msg);
    }

    void SendStop() => SendTwist(0f, 0f);

    // ══════════════════════════════════════════════════════════
    //  GUI
    // ══════════════════════════════════════════════════════════
    void OnGUI()
    {
        GUI.color = Color.cyan;
        GUILayout.BeginArea(new Rect(10, 140, 280, 120));
        GUILayout.Label("── РЕАЛЬНЫЙ РОБОТ (ROS) ──");
        GUILayout.Label("P — воспроизвести записанный маршрут");
        GUILayout.Label("H — прямое управление реальным роботом");

        if (isPlaying)
            GUILayout.Label("<color=yellow>▶ Воспроизведение...</color>");
        else if (enableDirectControl)
            GUILayout.Label("<color=orange>⚡ Прямое управление активно</color>");
        else
            GUILayout.Label("○ Ожидание");

        GUILayout.EndArea();
    }
}