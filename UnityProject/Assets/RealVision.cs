using UnityEngine;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using UnityEngine.UI;

[Serializable]
public class YoloDataPacket
{
    public float angle;
    public float distance;
    public float sees;
}

/// <summary>
/// RealVision.cs - Unity script for streaming MJPEG and detecting an orange ball.
/// Reads continuous frame data in a background thread and updates variables on the main thread.
/// </summary>
public class RealVision : MonoBehaviour
{
    [Header("Stream Settings")]
    [Tooltip("MJPEG stream URL (e.g., http://192.168.1.100:8080/?action=stream)")]
    public string streamUrl = "http://192.168.1.100:8080/?action=stream";
    public int bufferSize = 8192; // Read chunk size
    public int maxFrameSize = 1024 * 1024; // 1MB for single JPEG

    [Header("UDP Settings (YOLO PC)")]
    public int udpPort = 5005;

    [Header("Color Thresholds (HSV)")]
    [Range(0f, 1f)] public float hMin = 0.05f; // Orange start (approx 18 degrees)
    [Range(0f, 1f)] public float hMax = 0.15f; // Orange end (approx 54 degrees)
    [Range(0f, 1f)] public float sMin = 0.40f; 
    [Range(0f, 1f)] public float sMax = 1.00f;
    [Range(0f, 1f)] public float vMin = 0.40f; 
    [Range(0f, 1f)] public float vMax = 1.00f;

    [Header("Detection Settings")]
    [Tooltip("Skip N pixels for performance (e.g., 4 means step=4)")]
    [Range(1, 10)] public int step = 4;
    [Tooltip("Min pixels found to set seesBall=true")]
    public int minOrangeCount = 20; 
    [Tooltip("Estimated area for ball close to camera (distance=0)")]
    public int maxOrangeCount = 2000; 

    [Tooltip("Минимальная площадь пятна в % от площади кадра")]
    public float minBallAreaPercentage = 1.0f; 

    private Rect _ballRect; // Для отрисовки рамки

    [Header("Output Data")]
    public float normalizedAngle;    // -1 (left) to 1 (right)
    public float normalizedDistance; // 0 (close) to 1 (far)
    public float maxViewDistance = 2f;   // Было 5м, теперь 2м
    public float maxViewAngle = 20f;    // FOV 40° (±20°) — ОТКАЛИБРОВАНО
    public bool seesBall;
    public float lastKnownBallDirection; // -1 (left), 1 (right), 0 (unknown)

    [Header("YOLO UDP Format Setting")]
    [Tooltip("Включите, если YOLO отправляет угол от 0 до 1 (0.5 = центр). Выключите, если YOLO уже шлет от -1 до 1.")]
    public bool yoloAngleIsZeroToOne = true; 
    [Tooltip("Инверсия камеры (если мяч физически слева, а рисуется справа).")]
    public bool reverseYoloAngle = false;

    [Header("UI Display (Optional)")]
    public RawImage displayImage; // Сюда можно перетащить UI картинку из Unity

    [Header("ROS / YOLO Settings")]
    public bool useYOLO = false; // Автоматически переключится при получении топика
    private ROSConnection ros;

    [Header("Debug")]
    public bool showDebugTexture = true;
    public Transform debugSphere;

    [Header("Calibration (Sim-to-Real)")]
    [Tooltip("Реальный угол обзора камеры (FOV). Нужен для перевода пикселей X в точный градусный угол для нейросети.")]
    public float realCameraFov = 40f; // ±20 градусов (откалибровано пользователем)

    [Tooltip("Кривая перевода высоты пикселя Y (0..1) в расстояние до мяча в метрах, где 1.0 — это максимум VirtualCamera.maxViewDistance. Настройте на глаз, замеряя дистанции.")]
    public AnimationCurve pixelYToVirtualDistance = AnimationCurve.Linear(0f, 0f, 1f, 1f);

    [Header("Debounce (Память зрения)")]
    [Tooltip("Сколько секунд робот будет 'помнить' мяч, если YOLO потеряет его из-за блика. Предотвращает дергание камеры и гусениц.")]
    public float debounceDuration = 1.0f; // 1 секунда памяти

    private System.DateTime _lastSeenTime = System.DateTime.MinValue;
    private Vector3 _lastWorldPosition; // ЗАПОМИНАЕМ ГЛОБАЛЬНУЮ ТОЧКУ (Абсолютную!)
    private float _lastRawY = 1f; // "Сырой" Y для HUD для калибровки
    
    // --- HUD Для удобной настройки ---
    public bool showCalibrationHUD = true;

    private Texture2D _videoTexture;
    private CancellationTokenSource _cts;
    private byte[] _latestFrameBytes;
    private readonly object _frameLock = new object();
    private bool _hasNewFrame = false;

    // Thread-safe queue: UDP background -> Unity main thread
    private readonly ConcurrentQueue<YoloDataPacket> _udpQueue = new ConcurrentQueue<YoloDataPacket>();

    private void Start()
    {
        _videoTexture = new Texture2D(2, 2); // Auto-resized by LoadImage
        _cts = new CancellationTokenSource();
        
        // --- 1. Стандартная подписка на ROS ---
        ros = ROSConnection.GetOrCreateInstance();
        ros.Subscribe<Vector3Msg>("/vision/ball", YoloCallback);

        // --- 2. Добавляем UDP Слушателя для Windows-версии ---
        Debug.Log($"[RealVision] Запуск UDP слушателя на порту {udpPort}...");
        Task.Run(() => UdpListenerLoop(_cts.Token));

        // Start streaming background task (HSV-fallback)
        Task.Run(() => StreamLoop(_cts.Token));
    }

    private async Task UdpListenerLoop(CancellationToken token)
    {
        using (var udpClient = new UdpClient(udpPort))
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var result = await udpClient.ReceiveAsync();
                    string json = System.Text.Encoding.UTF8.GetString(result.Buffer);
                    
                    YoloDataPacket data = JsonUtility.FromJson<YoloDataPacket>(json);
                    if (data != null)
                    {
                        Debug.Log($"[RealVision] UDP пакет получен! sees={data.sees}, angle={data.angle:F2}, dist={data.distance:F2}");
                        _udpQueue.Enqueue(data); // Safe: enqueue from background thread
                    }
                }
            }
            catch (Exception e)
            {
                 // Ignore cancellation errors
                 if (!token.IsCancellationRequested)
                    Debug.LogWarning("UDP Listener Error: " + e.Message);
            }
        }
    }

    void YoloCallback(Vector3Msg msg)
    {
        ProcessYoloInput((float)msg.x, (float)msg.y, msg.z > 0.5f);
    }

    void ProcessYoloInput(float x, float y, bool ballDetected)
    {
        useYOLO = true;

        if (ballDetected)
        {
            seesBall = true;
            
            // 1. УГОЛ: Универсальный парсер YOLO с настройкой из Inspector.
            if (yoloAngleIsZeroToOne) {
                normalizedAngle = (x - 0.5f) * 2f; // Перевод из 0..1 в -1..1
            } else {
                normalizedAngle = x; // YOLO уже присылает -1..1
            }
            if (reverseYoloAngle) normalizedAngle = -normalizedAngle;
            
            // Жестко обрезаем угол, чтобы он никогда не превышал 1.0 (границы FOV)
            normalizedAngle = Mathf.Clamp(normalizedAngle, -1f, 1f);
            
            // 2. ДИСТАНЦИЯ: Теперь не хардкодим гиперболу, а используем кривую ИЗ ИНСПЕКТОРА!
            // Нажмите на кривую pixelYToVirtualDistance в Unity и настройте как хочется (y=0..1, val=0..1)
            _lastRawY = Mathf.Clamp01(y); 
            
            // Получаем нормализованную дистанцию по кривой
            normalizedDistance = pixelYToVirtualDistance.Evaluate(_lastRawY);
            
            lastKnownBallDirection = Mathf.Sign(normalizedAngle);
            if (lastKnownBallDirection == 0) lastKnownBallDirection = 1f;
            
            // Save for debounce
            _lastSeenTime = System.DateTime.Now;
            // НЕ запоминаем здесь локальные угол и дистанцию, так как позиция сферы рассчитывается в UpdateDebugSphere
            // Сфера сама вычислит и запомнит свою мировую позицию (_lastWorldPosition).
        }
        else
        {
            // Debounce: hold last values for duration
            if ((System.DateTime.Now - _lastSeenTime).TotalSeconds <= debounceDuration)
            {
                seesBall = true;
                // Сфера останется на _lastWorldPosition (обрабатывается в UpdateDebugSphere)
                // _lastRawY сохраняем старый для HUD
            }
            else
            {
                seesBall = false;
                normalizedAngle = lastKnownBallDirection;
                normalizedDistance = 1f;
                _lastRawY = 1f;
            }
        }
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
    }

    private void Update()
    {
        // === 1. СНАЧАЛА обрабатываем видеокадры (HSV-fallback) ===
        // Если useYOLO == true, ProcessFrame() мгновенно выходит.
        // Это гарантирует, что HSV не перезапишет данные YOLO.
        byte[] frameBytes = null;
        lock (_frameLock)
        {
            if (_hasNewFrame)
            {
                frameBytes = _latestFrameBytes;
                _hasNewFrame = false;
            }
        }

        if (frameBytes != null)
        {
            if (_videoTexture.LoadImage(frameBytes))
            {
                ProcessFrame(_videoTexture);
                
                // Автоматически выводим в UI, если назначен RawImage
                if (displayImage != null)
                {
                    displayImage.texture = _videoTexture;
                }
            }
        }

        // === 2. ПОТОМ читаем UDP от YOLO ===
        // Его данные имеют ВЫСШИЙ приоритет и перезаписывают HSV.
        while (_udpQueue.TryDequeue(out var packet))
        {
            ProcessYoloInput(packet.angle, packet.distance, packet.sees > 0.5f);
            Debug.Log($"[RealVision] YOLO -> seesBall={seesBall}, angle={normalizedAngle:F2}, dist={normalizedDistance:F2}");
        }

        UpdateDebugSphere();
    }

    private void UpdateDebugSphere()
    {
        if (debugSphere != null)
        {
            if (seesBall)
            {
                if (!debugSphere.gameObject.activeSelf) debugSphere.gameObject.SetActive(true);
                
                // Если мы в тайминге debounce (мяч не видим прямо сейчас, но помним)
                // То просто используем сохраненную МИРОВУЮ позицию (чтобы мяч не вращался за камерой)
                bool isDebouncing = (System.DateTime.Now - _lastSeenTime).TotalSeconds <= debounceDuration && !useYOLO; // грубая проверка
                // Замечание: в ProcessYoloInput мы сразу ставим seesBall = true.
                // Чтобы разделить "видим сейчас" и "помним", мы доверим обновление мировой позиции
                // только моменту, когда приходят свежие координаты (или симуляции)
                
                // Но так как YOLO срабатывает не каждый кадр Update, камера может чуть повернуть.
                // Рассчитываем всегда новую координату
                float z = Mathf.Lerp(0.3f, maxViewDistance, normalizedDistance);
                
                float halfAngleRad = (realCameraFov / 2f) * Mathf.Deg2Rad;
                float maxTan = Mathf.Tan(halfAngleRad);
                float clampedTan = Mathf.Clamp(Mathf.Tan(normalizedAngle * halfAngleRad), -maxTan, maxTan);
                
                float localX = z * clampedTan; 
                
                // Рассчитываем новую абсолютную мировую позицию
                Vector3 worldPos = transform.TransformPoint(new Vector3(localX, 0, z));
                
                // ПРОБЛЕМА 1: Мяч в воздухе! Привязываем жестко к полу. Высота радиуса мяча = 0.05м
                worldPos.y = 0.05f; 
                
                // Обновляем память ТОЛЬКО если обновление не из-за залипания debounce
                // (условно считаем, что если дата свежая, значит видели только что)
                if ((System.DateTime.Now - _lastSeenTime).TotalSeconds < 0.2f) {
                    _lastWorldPosition = worldPos;
                } else {
                    // Используем историческую позицию, чтобы отвязаться от вращения пустой камеры
                    worldPos = _lastWorldPosition;
                }
                
                debugSphere.position = worldPos;
            }
            else
            {
                if (debugSphere.gameObject.activeSelf) debugSphere.gameObject.SetActive(false);
            }
        }
    }

    private async Task StreamLoop(CancellationToken token)
    {
        using (var client = new HttpClient())
        {
            try
            {
                // HttpClient allows reading stream directly
                var response = await client.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, token);
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    byte[] readBuffer = new byte[bufferSize];
                    byte[] jpegBuffer = new byte[maxFrameSize];
                    int jpegIndex = 0;
                    bool reading = false;
                    byte prevByte = 0;

                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, token)) > 0)
                    {
                        if (token.IsCancellationRequested) break;

                        for (int i = 0; i < bytesRead; i++)
                        {
                            byte curr = readBuffer[i];

                            // Detection of JPEG Start of Image (0xFF, 0xD8)
                            if (prevByte == 0xFF && curr == 0xD8)
                            {
                                reading = true;
                                jpegIndex = 0;
                                jpegBuffer[jpegIndex++] = 0xFF;
                                jpegBuffer[jpegIndex++] = 0xD8;
                            }
                            else if (reading)
                            {
                                if (jpegIndex < maxFrameSize)
                                {
                                    jpegBuffer[jpegIndex++] = curr;

                                    // Detection of JPEG End of Image (0xFF, 0xD9)
                                    if (jpegIndex >= 2 && jpegBuffer[jpegIndex - 2] == 0xFF && jpegBuffer[jpegIndex - 1] == 0xD9)
                                    {
                                        reading = false;
                                        
                                        // Save latest frame bytes
                                        byte[] completeFrame = new byte[jpegIndex];
                                        Array.Copy(jpegBuffer, completeFrame, jpegIndex);

                                        lock (_frameLock)
                                        {
                                            _latestFrameBytes = completeFrame;
                                            _hasNewFrame = true;
                                        }
                                    }
                                }
                                else
                                {
                                    // Frame too big, reset buffer
                                    reading = false;
                                }
                            }
                            prevByte = curr;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("RealVision stream error: " + e.Message);
                // Can initiate a retry if needed
            }
        }
    }

    private void ProcessFrame(Texture2D tex)
    {
        if (useYOLO) return; // YOLO присылает координаты отдельно, не тратим CPU на поиск пикселей.

        int width = tex.width;
        int height = tex.height;
        Color32[] pixels = tex.GetPixels32();

        int gridW = width / step;
        int gridH = height / step;
        bool[,] orangeGrid = new bool[gridH, gridW];

        // 1. Заполняем сетку оранжевых пикселей
        for (int y = 0; y < height; y += step)
        {
            for (int x = 0; x < width; x += step)
            {
                int index = y * width + x;
                Color32 c = pixels[index];

                if (c.r < 80 || c.r <= c.g || c.g <= c.b) continue;

                Color.RGBToHSV(c, out float h, out float s, out float v);

                if (h >= hMin && h <= hMax && s >= sMin && s <= sMax && v >= vMin && v <= vMax)
                {
                    int gy = y / step;
                    int gx = x / step;
                    if (gy < gridH && gx < gridW)
                    {
                        orangeGrid[gy, gx] = true;
                    }
                }
            }
        }

        // 2. Поиск компонент связности (BFS)
        bool[,] visited = new bool[gridH, gridW];
        int maxArea = 0;
        System.Collections.Generic.List<Vector2Int> largestComponent = null;

        for (int gy = 0; gy < gridH; gy++)
        {
            for (int gx = 0; gx < gridW; gx++)
            {
                if (orangeGrid[gy, gx] && !visited[gy, gx])
                {
                    var comp = new System.Collections.Generic.List<Vector2Int>();
                    var q = new System.Collections.Generic.Queue<Vector2Int>();
                    q.Enqueue(new Vector2Int(gx, gy));
                    visited[gy, gx] = true;

                    while (q.Count > 0)
                    {
                        var curr = q.Dequeue();
                        comp.Add(curr);

                        int[] dx = { 0, 0, -1, 1 };
                        int[] dy = { -1, 1, 0, 0 };

                        for (int i = 0; i < 4; i++)
                        {
                            int ny = curr.y + dy[i];
                            int nx = curr.x + dx[i];

                            if (ny >= 0 && ny < gridH && nx >= 0 && nx < gridW)
                            {
                                if (orangeGrid[ny, nx] && !visited[ny, nx])
                                {
                                    visited[ny, nx] = true;
                                    q.Enqueue(new Vector2Int(nx, ny));
                                }
                            }
                        }
                    }

                    if (comp.Count > maxArea)
                    {
                        maxArea = comp.Count;
                        largestComponent = comp;
                    }
                }
            }
        }

        // 3. Проверка площади
        float minAreaPixels = (gridW * gridH) * (minBallAreaPercentage / 100f);

        if (maxArea >= minAreaPixels && largestComponent != null && maxArea >= minOrangeCount)
        {
            // Время обновляется в конце функции, тут ничего не пишем!
            seesBall = true;

            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            long sumX = 0;

            foreach (var p in largestComponent)
            {
                int realX = p.x * step;
                int realY = p.y * step;

                minX = Math.Min(minX, realX);
                maxX = Math.Max(maxX, realX);
                minY = Math.Min(minY, realY);
                maxY = Math.Max(maxY, realY);

                sumX += realX;
            }

            float centerX = (float)sumX / largestComponent.Count;
            // Перевод пиксельного X в точный угол:
            // centerX - (width/2) дает сдвиг от центра.
            // Делим на (width/2) -> получаем от -1 (левый край) до +1 (правый край).
            float pixelRatioX = (centerX - (width / 2f)) / (width / 2f);
            
            // Если камера 60 градусов (±30), край кадра = 30 градусов.
            // Значит реальный угол в градусах = pixelRatioX * (realCameraFov / 2).
            float realAngleDegrees = pixelRatioX * (realCameraFov / 2f);

            // Теперь переводим в понятный нейросети масштаб.
            // Модель училась на VirtualCamera, где "1.0" = maxViewAngle (например, 15 градусов).
            // Допустим, realAngleDegrees = 30. Значит для сети это 30 / 15 = 2.0 (за пределами её видимости!)
            // Чтобы всё работало без переобучения, VirtualCamera.maxViewAngle должна быть равна realCameraFov/2!
            // Но пока что оставим как есть:
            normalizedAngle = pixelRatioX; 
            // ^ Выше мы просто оставили -1..1, но теперь мы знаем, что эта цифра означает. При переобучении это учтётся!

            // -----------------------------------------------------
            // 2. ДИСТАНЦИЯ: Перерасчет дистанции через РАЗМЕР МЯЧА (Height)
            // -----------------------------------------------------
            // Считаем высоту мяча в пикселях и нормализуем к высоте кадра
            _lastRawY = Mathf.Max(0.01f, (float)(maxY - minY) / height);

            // ФИЗИЧЕСКИ ПРАВИЛЬНАЯ ИНВЕРСИЯ (Гипербола)
            normalizedDistance = Mathf.Clamp01(0.05f / _lastRawY);

            _ballRect = new Rect(minX, minY, maxX - minX, maxY - minY);
            
            // Записываем время успешного кадра
            _lastSeenTime = System.DateTime.Now;
            seesBall = true;
        }
        else
        {
            // Мяч не найден в текущем кадре
            if ((System.DateTime.Now - _lastSeenTime).TotalSeconds <= debounceDuration)
            {
                seesBall = true; // Удерживаем видимость
                // Не обновляем координаты, сфера останется на _lastWorldPosition
            }
            else
            {
                seesBall = false;
                normalizedAngle = lastKnownBallDirection; // Остаточное направление
                normalizedDistance = 1f;                  // Считаем, что он далеко
            }
        }
    }

    // Optional: Draw debug target center in Gizmos or a visual hook
    private void OnGUI()
    {
        if (showDebugTexture && seesBall && _videoTexture != null)
        {
            int texW = _videoTexture.width;
            int texH = _videoTexture.height;

            // Масштабируем до экрана с сохранением пропорций
            float aspect = (float)texW / texH;
            float guiH = Screen.height;
            float guiW = guiH * aspect;
            float xOffset = (Screen.width - guiW) / 2f;

            float rx = _ballRect.x / texW * guiW + xOffset;
            float rw = _ballRect.width / texW * guiW;
            
            // Texture2D Y=0 снизу. OnGUI Y=0 сверху.
            float ry = (texH - (_ballRect.y + _ballRect.height)) / texH * guiH;
            float rh = _ballRect.height / texH * guiH;

            GUI.color = Color.green;
            GUI.Box(new Rect(rx, ry, rw, rh), "");

            // Отрисовка перекрестия
            float centerX = rx + rw / 2f;
            float centerY = ry + rh / 2f;
            GUI.color = Color.red;
            GUI.Box(new Rect(centerX - 5, centerY - 5, 10, 10), "+");
        }

        // Draw HUD 
        if (showCalibrationHUD)
        {
            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.fontSize = 14; // Меньше шрифт
            style.normal.textColor = Color.yellow;
            style.fontStyle = FontStyle.Bold;

            // Сдвинуто в самый низ слева, компактный размер
            GUILayout.BeginArea(new Rect(10, Screen.height - 140, 450, 130), GUI.skin.box);
            
            GUILayout.Label("=== Сalibration HUD ===", style);
            
            if (seesBall)
            {
                style.normal.textColor = Color.green;
                GUILayout.Label($"СТАТУС: МЯЧ НАЙДЕН", style);
                
                style.normal.textColor = Color.white;
                float actualAngle = normalizedAngle * (realCameraFov / 2f);
                GUILayout.Label($"Угол по горизонту: {actualAngle:F1}° (В сеть: {normalizedAngle:F2})", style);
                
                // Показываем "сырой" РАЗМЕР 0..1 для калибровки кривой
                GUILayout.Label($"Сырой РАЗМЕР (0..1): {_lastRawY:F2}", style);
                GUILayout.Label($"Дист в сеть (кривая): {normalizedDistance:F2}", style);
            }
            else
            {
                style.normal.textColor = Color.red;
                GUILayout.Label($"СТАТУС: МЯЧ ПОТЕРЯН (Поиск...)", style);
            }

            GUILayout.EndArea();
        }
    }

    // --- Добавляем синие лучи FOV для Scene View ---
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.blue;
        Vector3 origin = transform.position;
        float dist = 2f; 

        // Левая граница FOV
        Vector3 leftDir = Quaternion.Euler(0, -realCameraFov / 2f, 0) * transform.forward;
        // Правая граница FOV
        Vector3 rightDir = Quaternion.Euler(0, realCameraFov / 2f, 0) * transform.forward;

        Gizmos.DrawRay(origin, leftDir * dist);
        Gizmos.DrawRay(origin, rightDir * dist);

        // Соединительная дуга
        Gizmos.color = new Color(0, 0, 1, 0.2f);
        Gizmos.DrawLine(origin + leftDir * dist, origin + rightDir * dist);

        if (seesBall && debugSphere != null)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(origin, debugSphere.position);
        }
    }
}
