using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
public class CalibrationHelper : MonoBehaviour
{
    private ROSConnection ros;
    public string topicName = "/cmd_vel";
    
    [Header("Test Settings")]
    public float testLinearVelocity = 0.5f;
    [Tooltip("angular.z value to send (1.0 = 100% PWM on robot)")]
    public float testAngularVelocity = 1.0f;
    public float testDuration = 1.75f;

    public enum TestMode { None, Linear, Angular }

    private TestMode currentTest = TestMode.None;
    private float timer = 0f;
    private Vector3 startPos;
    private Quaternion startRot;
    private float totalDistance = 0f;
    private float totalAngleDegrees = 0f;

    private DigitalTwinController twin;
    private Rigidbody rb;

    void Start()
    {
        ros = ROSConnection.GetOrCreateInstance();
        ros.RegisterPublisher<TwistMsg>(topicName);
        twin = GetComponentInParent<DigitalTwinController>();
        rb = GetComponentInParent<Rigidbody>();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.T) && currentTest == TestMode.None)
        {
            currentTest = TestMode.Linear;
            timer = 0f;
            totalDistance = 0f;
            startPos = transform.position;
            if (twin != null) twin.enabled = false;
            Debug.Log($"[Calibration] LINEAR START: {testLinearVelocity} m/s за {testDuration}с");
        }
        
        if (Input.GetKeyDown(KeyCode.Y) && currentTest == TestMode.None)
        {
            currentTest = TestMode.Angular;
            timer = 0f;
            totalAngleDegrees = 0f;
            startRot = transform.rotation;
            if (twin != null) twin.enabled = false;
            Debug.Log($"[Calibration] ROTATION START: {testAngularVelocity} rad/s за {testDuration}с");
        }

        if (currentTest != TestMode.None)
        {
            timer += Time.deltaTime;
            if (timer < testDuration)
            {
                if (currentTest == TestMode.Linear)
                {
                    float d = testLinearVelocity * Time.deltaTime;
                    if (rb != null)
                        rb.MovePosition(rb.position + transform.forward * d);
                    else
                        transform.Translate(Vector3.forward * d);

                    totalDistance += d;
                    SendTwist(testLinearVelocity, 0f);
                }
                else
                {
                    float angDeg = testAngularVelocity * Mathf.Rad2Deg * Time.deltaTime;
                    if (rb != null)
                        rb.MoveRotation(rb.rotation * Quaternion.Euler(0, angDeg, 0));
                    else
                        transform.Rotate(0, angDeg, 0);

                    totalAngleDegrees += angDeg;
                    SendTwist(0f, testAngularVelocity);
                }
            }
            else
            {
                currentTest = TestMode.None;
                SendTwist(0f, 0f);
                if (twin != null) twin.enabled = true;

                Debug.Log($"[Calibration] ✅ Конец теста.\n" +
                          $"ДВОЙНИК (Идеал): Дистанция = {totalDistance:F2}м | Угол = {totalAngleDegrees:F1}°");
            }
        }
    }

    void SendTwist(float lin, float ang)
    {
        var msg = new TwistMsg();
        msg.linear.x = lin;
        msg.angular.z = ang;
        ros.Publish(topicName, msg);
    }
    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 250, 350, 150));
        if (GUILayout.Button("LINEAR TEST (T) - measure distance")) 
        {
            currentTest = TestMode.Linear;
            timer = 0f;
        }
        if (GUILayout.Button("ROTATION TEST (Y) - measure degrees"))
        {
            currentTest = TestMode.Angular;
            timer = 0f;
        }
        GUILayout.EndArea();
    }
}