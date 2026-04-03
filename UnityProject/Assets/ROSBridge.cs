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

    /// <summary>
    /// Publishes cmd_vel Twist message.
    /// linear.x = gas * maxLinearSpeed
    /// angular.z = steering * maxAngularSpeed
    /// </summary>
    public void PublishCommand(float gas, float steering)
    {
        TwistMsg cmd = new TwistMsg();
        
        // Drive controls
        cmd.linear.x = gas * maxLinearSpeed;
        cmd.angular.z = steering * maxAngularSpeed;

        Debug.Log($"[ROSBridge] Sending Twist -> linear.x={cmd.linear.x:F3}, angular.z={cmd.angular.z:F3}");

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
