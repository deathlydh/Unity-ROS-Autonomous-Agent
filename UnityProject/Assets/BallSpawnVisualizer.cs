using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Тестировщик зоны спавна мяча. Полностью копирует логику из RobotBrain.cs.
/// ВАЖНО: Учитывает случайный поворот робота, который происходит в OnEpisodeBegin!
/// </summary>
public class BallSpawnVisualizer : MonoBehaviour
{
    [Header("Параметры (эмуляция Curriculum)")]
    public float maxDistance = 3.0f;
    public float maxOffset = 1.2f;
    public float minDistance = 0.5f;

    [Header("Управление (только в Play Mode)")]
    public KeyCode spawnKey = KeyCode.T;
    public KeyCode clearKey = KeyCode.C;
    public int bulkSpawnCount = 100;
    
    private List<GameObject> markers = new List<GameObject>();
    private Quaternion startRotation;

    void Start()
    {
        startRotation = transform.rotation;
    }

    void Update()
    {
        if (Input.GetKeyDown(spawnKey))
        {
            for (int i = 0; i < bulkSpawnCount; i++)
            {
                TestSpawnLogic();
            }
        }
        
        if (Input.GetKeyDown(clearKey))
        {
            foreach (var m in markers) if (m != null) Destroy(m);
            markers.Clear();
        }
    }

    void TestSpawnLogic()
    {
        // 1. КОПИРУЕМ ЛОГИКУ ИЗ RobotBrain.cs (OnEpisodeBegin):
        // Робот СНАЧАЛА случайно поворачивается, а ТОЛЬКО ПОТОМ спавнит мяч!
        float randomYaw = Random.Range(-180f, 180f);
        Quaternion simulatedRotation = startRotation * Quaternion.Euler(0f, randomYaw, 0f);
        
        // Симулируем векторы forward и right для этого случайного поворота
        Vector3 simForward = simulatedRotation * Vector3.forward;
        Vector3 simRight = simulatedRotation * Vector3.right;

        Vector3 randomPos = transform.position;
        bool validPos = false;
        int attempts = 0;

        // 2. Логика поиска позиции (ResetBall)
        while (!validPos && attempts < 30)
        {
            float randomZ = Random.Range(minDistance, maxDistance);
            float randomX = Random.Range(-maxOffset, maxOffset);

            // Используем симулированные векторы, а не текущий transform.forward
            randomPos = transform.position + simForward * randomZ + simRight * randomX;
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

        bool usedFallback = false;
        if (!validPos)
        {
            // Fallback тоже использует симулированный forward
            randomPos = transform.position + simForward * 0.7f;
            randomPos.y = transform.position.y + 0.2f;
            usedFallback = true;
        }

        SpawnMarker(randomPos, usedFallback);
    }

    void SpawnMarker(Vector3 pos, bool isFallback)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.transform.position = pos;
        marker.transform.localScale = Vector3.one * 0.15f;
        
        Destroy(marker.GetComponent<Collider>());

        Renderer rend = marker.GetComponent<Renderer>();
        if (rend != null)
        {
            rend.material.color = isFallback ? Color.red : Color.green;
        }

        markers.Add(marker);
    }
}
