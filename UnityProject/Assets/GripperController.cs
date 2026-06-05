using UnityEngine;

 [RequireComponent(typeof(Collider))] 
public class GripperController : MonoBehaviour
{
    [Header("Settings")]
    public string targetTag = "TargetBall";
    public Transform holdPoint; // Точка крепления мяча
    public VirtualSensors sensors; // Ссылка на сенсоры

    [Header("Status")]
    public bool hasBall = false;
    private GameObject grabbedBall = null;

    private void Start()
    {
        if (sensors == null) sensors = GetComponentInParent<VirtualSensors>();
    }

    private void Update()
    {
        // Автоматически закрываем цифровую клешню, если сработал реальный ИК-датчик.
        // Реальный робот (в unity_master.py) смыкает клешню аппаратно.
        // Мы зеркалим это здесь, чтобы нейросеть получила observation "hasBall = 1".
        if (!hasBall && sensors != null && sensors.gripperIR == 1)
        {
            CloseGripper();
        }
    }

    public void OpenGripper()
    {
        if (!hasBall) return;

        if (grabbedBall != null)
        {
            // Симуляция: отпускаем Unity-мяч
            grabbedBall.transform.SetParent(null);
            
            Collider ballCollider = grabbedBall.GetComponent<Collider>();
            if (ballCollider != null) ballCollider.enabled = true;
            
            Rigidbody rb = grabbedBall.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = false;
            
            grabbedBall = null;
        }

        hasBall = false;
        Debug.Log("[GripperController] Мяч отпущен.");
    }

    public bool CloseGripper()
    {
        if (hasBall) return false;

        // Вместо триггера используем данные ИК-датчика из VirtualSensors
        if (sensors != null && sensors.gripperIR == 1)
        {
            // Пытаемся найти Unity-объект мяча (работает в симуляции)
            if (sensors.lastGripperHitObj != null && sensors.lastGripperHitObj.CompareTag(targetTag))
            {
                grabbedBall = sensors.lastGripperHitObj;
            }
            else
            {
                Collider[] hits = Physics.OverlapSphere(holdPoint.position, 0.05f);
                foreach (var hit in hits)
                {
                    if (hit.CompareTag(targetTag))
                    {
                        grabbedBall = hit.gameObject;
                        break;
                    }
                }
            }

            if (grabbedBall != null)
            {
                // Симуляция: прикрепляем Unity-мяч к клешне
                hasBall = true;
                
                Rigidbody ballRb = grabbedBall.GetComponent<Rigidbody>();
                if (ballRb != null)
                {
                    ballRb.linearVelocity = Vector3.zero;
                    ballRb.angularVelocity = Vector3.zero;
                    ballRb.isKinematic = true;
                }

                Collider ballCollider = grabbedBall.GetComponent<Collider>();
                if (ballCollider != null) ballCollider.enabled = false;

                grabbedBall.transform.SetParent(holdPoint);
                grabbedBall.transform.localPosition = Vector3.zero;

                Debug.Log($"Мяч {grabbedBall.name} захвачен (строго по mesh)!");
                return true;
            }
            else if (sensors.useRealSensors)
            {
                // v17 FIX: На РЕАЛЬНОМ роботе Unity-мяча нет!
                // gripperIR=1 означает что реальный ИК видит реальный мяч → hasBall=true
                // Без этого фикса hasBall ВСЕГДА false → робот не останавливался после захвата → кружился.
                hasBall = true;
                Debug.Log("[GripperController] Реальный захват: gripperIR=1, Unity-мяч не найден → hasBall=true по ИК-датчику");
                return true;
            }
        }
        
        return false;
    }
}
