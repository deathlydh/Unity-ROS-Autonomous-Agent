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
        if (hasBall && grabbedBall != null)
        {
            grabbedBall.transform.SetParent(null);
            Rigidbody rb = grabbedBall.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = false;
            
            grabbedBall = null;
            hasBall = false;
            Debug.Log("Мяч отпущен.");
        }
    }

    public bool CloseGripper()
    {
        if (hasBall) return false;

        // Вместо триггера используем данные ИК-датчика из VirtualSensors
        if (sensors != null && sensors.gripperIR == 1)
        {
            // Находим сам объект мяча (так как IR дает только факт наличия)
            // Ищем ближайший мяч в радиусе захвата
            Collider[] hits = Physics.OverlapSphere(holdPoint.position, 0.2f);
            foreach (var hit in hits)
            {
                if (hit.CompareTag(targetTag))
                {
                    grabbedBall = hit.gameObject;
                    hasBall = true;
                    
                    grabbedBall.transform.SetParent(holdPoint);
                    grabbedBall.transform.localPosition = Vector3.zero;

                    Rigidbody ballRb = grabbedBall.GetComponent<Rigidbody>();
                    if (ballRb != null)
                    {
                        ballRb.isKinematic = true;
                        ballRb.linearVelocity = Vector3.zero;
                        ballRb.angularVelocity = Vector3.zero;
                    }

                    Debug.Log($"Мяч {grabbedBall.name} захвачен (по данным ИК-датчика)!");
                    return true;
                }
            }
        }
        
        return false;
    }
}
