using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
using RosMessageTypes.Geometry;
using RosMessageTypes.Std;

/// <summary>
/// ROSBridge.cs - Connects Unity to ROS 1 for HIL Simulation.
/// Sends Twist messages to controlling linear and angular velocities.
/// </summary>
public class ROSBridge : MonoBehaviour
{
    [Header("ROS Settings")]
    public string topicName = "/cmd_vel";
    public float maxLinearSpeed = 0.5f;
    public float maxAngularSpeed = 1.0f;

    private ROSConnection ros;

    void Start()
    {
        // Get or Create instance connecting to ROS TCP Endpoint
        ros = ROSConnection.GetOrCreateInstance();
        
        // Register Publishers
        ros.RegisterPublisher<TwistMsg>(topicName);
        ros.RegisterPublisher<Int32Msg>("/cmd_gripper");
        ros.RegisterPublisher<Float32Msg>("/cmd_camera_pan");

        // Send reset sequence after connection is established
        Invoke("InitializeRobotPosture", 1.5f);
    }

    private void InitializeRobotPosture()
    {
        if (ros == null) return;
        Debug.Log("[ROSBridge] Sending initial posture commands (Arm Up, Camera Center)");
        PublishGripperCmd(3); // cmd 3 = init_arm() in unity_gripper.py
        PublishCameraCmd(0f); // Center the camera
    }

    public void PublishGripperCmd(int cmd)
    {
        Int32Msg msg = new Int32Msg();
        msg.data = cmd;
        ros.Publish("/cmd_gripper", msg);
    }

    [Header("Sim-to-Real: EMA Smoothing")]
    [Tooltip("Сглаживание команд перед отправкой на реального робота. 0.3=плавно, 1.0=без фильтра. v16: повышен с 0.4 до 0.8 — alpha=0.4 гасил gas<0.35 до нуля через motor deadzone!")]
    [Range(0.1f, 1f)]
    public float emaAlpha = 0.8f;
    private float smoothGas = 0f;
    private float smoothSteering = 0f;

    /// <summary>
    /// Publishes cmd_vel Twist message with EMA smoothing.
    /// linear.x = gas * maxLinearSpeed
    /// angular.z = steering * maxAngularSpeed
    /// </summary>
    public void PublishCommand(float gas, float steering)
    {
        // v18 FIX: HARD STOP — если команда "стой", обнуляем EMA мгновенно.
        // Без этого EMA-остатки (0.06) бустились MIN_MOTOR_PWM до 35 → робот крутился после захвата.
        if (Mathf.Approximately(gas, 0f) && Mathf.Approximately(steering, 0f))
        {
            smoothGas = 0f;
            smoothSteering = 0f;
        }
        else
        {
            // EMA: output = α × new + (1-α) × prev — убирает тряску нейросети
            smoothGas = emaAlpha * gas + (1f - emaAlpha) * smoothGas;
            smoothSteering = emaAlpha * steering + (1f - emaAlpha) * smoothSteering;
        }

        TwistMsg cmd = new TwistMsg();
        
        // Drive controls (сглаженные)
        cmd.linear.x = smoothGas * maxLinearSpeed;
        cmd.angular.z = smoothSteering * maxAngularSpeed;

        Debug.Log($"[ROSBridge] Sending Twist -> linear.x={cmd.linear.x:F3}, angular.z={cmd.angular.z:F3} (raw: gas={gas:F2}, steer={steering:F2})");

        // Publish to topic
        ros.Publish(topicName, cmd);
    }

    public void PublishCameraCmd(float yaw)
    {
        if (ros == null) return;
        Float32Msg msg = new Float32Msg(yaw);
        ros.Publish("/cmd_camera_pan", msg);
    }
}
