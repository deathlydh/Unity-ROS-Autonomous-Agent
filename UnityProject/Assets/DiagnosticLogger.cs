using UnityEngine;
using System.IO;
using System.Text;
using System.Globalization;

/// <summary>
/// v18: Расширенный диагностический логгер.
/// Записывает observations + actions + внутреннее состояние RobotBrain в CSV.
/// Повесь на тот же объект где RobotBrain. Включи enableLogging = true.
/// Файл: C:\Users\Admin\Unity\ROS_test\diagnostic_log.csv
/// </summary>
public class DiagnosticLogger : MonoBehaviour
{
    [Header("Настройки")]
    public bool enableLogging = false;
    [Tooltip("Логировать каждые N решений (1 = каждое)")]
    public int logEveryN = 1;
    [Tooltip("Макс строк (потом остановится)")]
    public int maxRows = 2000;

    private RobotBrain brain;
    private VirtualSensors sensors;
    private StreamWriter writer;
    private int decisionCount = 0;
    private int rowsWritten = 0;
    private float startTime;

    // Публичные поля — RobotBrain заполнит их
    [HideInInspector] public float lastGas;
    [HideInInspector] public float lastSteering;
    [HideInInspector] public float lastCameraYaw;
    [HideInInspector] public bool lastBallSeen;
    [HideInInspector] public float lastBallAngle;
    [HideInInspector] public float lastBallDist;

    void Start()
    {
        brain = GetComponent<RobotBrain>();
        sensors = GetComponent<VirtualSensors>();
        
        if (enableLogging)
        {
            string path = Path.Combine(Application.dataPath, "..", "diagnostic_log.csv");
            writer = new StreamWriter(path, false, Encoding.UTF8);
            // v18: расширенный заголовок с внутренним состоянием
            writer.WriteLine("time,step,ballSeen,ballAngle,ballDist,uz,irL,irR,gripIR,camYaw,gas,steering,cameraAction,hasBall,holdTicks,isRetrying,wasClose,blindTicks,ballRecentlySeen,holdWithoutIR,displacementX,displacementZ,heading,speed");
            startTime = Time.time;
            Debug.Log($"[DiagnosticLogger] v18 пишу в: {path}");
        }
    }

    /// <summary>
    /// v18: Расширенный вызов из RobotBrain.OnActionReceived.
    /// Добавлены: hasBall, holdTicks, isRetrying, wasClose, blindTicks,
    /// ballRecentlySeen, holdWithoutIR, displacement, heading, speed.
    /// </summary>
    public void LogStep(
        int step, bool ballSeen, float ballAngle, float ballDist,
        float uz, int irL, int irR, int gripIR, float camYaw,
        float gas, float steering, float cameraAction,
        bool hasBall, int holdTicks, bool isRetrying, bool wasClose,
        int blindTicks, bool ballRecentlySeen, int holdWithoutIR,
        float displacementX, float displacementZ, float heading, float speed)
    {
        if (!enableLogging || writer == null || rowsWritten >= maxRows) return;

        decisionCount++;
        if (decisionCount % logEveryN != 0) return;

        float elapsed = Time.time - startTime;
        // CRITICAL: Используем InvariantCulture! Русская локаль пишет "0,040" (запятая)
        // вместо "0.040" (точка), что ломает CSV (разделитель полей тоже запятая).
        string line = string.Format(CultureInfo.InvariantCulture,
            "{0:F3},{1},{2},{3:F4},{4:F4},{5:F4},{6},{7},{8},{9:F4},{10:F4},{11:F4},{12:F4},{13},{14},{15},{16},{17},{18},{19},{20:F4},{21:F4},{22:F4},{23:F4}",
            elapsed, step, ballSeen?1:0, ballAngle, ballDist, uz, irL, irR, gripIR, camYaw,
            gas, steering, cameraAction, hasBall?1:0, holdTicks, isRetrying?1:0, wasClose?1:0,
            blindTicks, ballRecentlySeen?1:0, holdWithoutIR, displacementX, displacementZ, heading, speed);
        writer.WriteLine(line);
        writer.Flush();
        rowsWritten++;

        if (rowsWritten >= maxRows)
        {
            Debug.Log($"[DiagnosticLogger] Достигнут лимит {maxRows} строк. Файл готов.");
        }
    }

    void OnDestroy()
    {
        writer?.Close();
    }
}
