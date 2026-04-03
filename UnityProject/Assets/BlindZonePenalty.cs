using UnityEngine;
using Unity.MLAgents;

/// <summary>
/// Вешается на пустой объект с Trigger Collider (слепая зона робота).
/// Когда мяч попадает внутрь — штрафует агента.
/// </summary>
public class BlindZonePenalty : MonoBehaviour
{
    [Tooltip("Перетащи сюда робота с RobotBrain")]
    public Agent agent;

    [Tooltip("Штраф за ОДИН тик нахождения мяча в слепой зоне (раз в 0.2 сек)")]
    public float penalty = -0.003f;

    private float penaltyTimer = 0f;
    private const float PENALTY_INTERVAL = 0.2f; // Раз в 0.2 сек, НЕ каждый кадр!

    private void OnTriggerStay(Collider other)
    {
        if (agent == null) return;
        if (!other.CompareTag("TargetBall")) return;

        // Штрафуем НЕ каждый физический кадр, а раз в 0.2 сек
        penaltyTimer += Time.fixedDeltaTime;
        if (penaltyTimer >= PENALTY_INTERVAL)
        {
            penaltyTimer = 0f;
            agent.AddReward(penalty);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("TargetBall"))
        {
            penaltyTimer = 0f; // Сброс при выходе
        }
    }
}
