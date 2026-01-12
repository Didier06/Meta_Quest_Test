using UnityEngine;
using System;

// [RequireComponent(typeof(MqttManager))] // Removd to avoid accidental duplication if added to Pendulum
public class PendulumReporter : MonoBehaviour
{
    public enum RotationAxis { X, Y, Z }
    public enum MeasurementMode { LocalRotation, PositionVector }

    [Header("Configuration")]
    public string targetObjectName = "Pendule";
    public string pivotObjectName = "Pivot"; // New: Name of the Pivot
    public string overrideMqttTopic = "FABLAB_21_22/Unity/Pendule/out";
    public float publishRate = 0.1f; 
    
    [Header("Measurement Settings")]
    public MeasurementMode mode = MeasurementMode.PositionVector; // Default to Vector now
    public RotationAxis axisToMonitor = RotationAxis.Z; // Pivot usually implies Z rotation in 2D
    public Transform pivotTransform; // Can be assigned manually

    [Header("Debugging")]
    public float currentAngle;
    public float debugAngleX;
    public float debugAngleY;
    public float debugAngleZ;
    public bool isConnected;

    private MqttManager mqttManager;
    private GameObject pendulumObj;
    private float nextPublishTime;

    void Start()
    {
        mqttManager = FindFirstObjectByType<MqttManager>();
        
        // Find Pendulum
        if (pendulumObj == null) pendulumObj = GameObject.Find(targetObjectName);
        if (pendulumObj == null) pendulumObj = GameObject.Find(targetObjectName.ToLower());

        // Find Pivot if needed
        if (pivotTransform == null)
        {
            GameObject piv = GameObject.Find(pivotObjectName);
            if (piv != null) pivotTransform = piv.transform;
            else 
            {
                // Try to find parent as fallback
                if (pendulumObj != null && pendulumObj.transform.parent != null)
                {
                   pivotTransform = pendulumObj.transform.parent;
                   Debug.Log($"[PendulumReporter] Pivot not found by name '{pivotObjectName}', using parent '{pivotTransform.name}'");
                }
            }
        }

        if (pendulumObj != null)
            Debug.Log($"[PendulumReporter] Monitoring '{pendulumObj.name}'. Pivot: {(pivotTransform!=null?pivotTransform.name:"None")}");
    }

    void Update()
    {
        if (Time.time < nextPublishTime) return;
        if (mqttManager == null) return;
        if (pendulumObj == null) return;

        nextPublishTime = Time.time + publishRate;

        float finalAngle = 0f;

        if (mode == MeasurementMode.PositionVector && pivotTransform != null)
        {
            // Calculate Vector from Pivot to Pendulum (Global)
            Vector3 dir = pendulumObj.transform.position - pivotTransform.position;
            
            // Calculate Angle based on Axis relative to "Down" (-Y)
            switch (axisToMonitor)
            {
                case RotationAxis.Z:
                    // Z-Axis Rotation -> Movement in X-Y Plane
                    finalAngle = Mathf.Atan2(dir.x, -dir.y) * Mathf.Rad2Deg;
                    break;
                case RotationAxis.X:
                    // X-Axis Rotation -> Movement in Z-Y Plane
                    // FIXED: -dir.z to match Unity Left-Handed Coordinate System
                    finalAngle = Mathf.Atan2(-dir.z, -dir.y) * Mathf.Rad2Deg;
                    break;
                case RotationAxis.Y:
                    // Y-Axis Rotation -> Movement in X-Z Plane
                    finalAngle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                    break;
            }
        }
        else 
        {
             // Fallback to Rotation
             Vector3 euler = pendulumObj.transform.rotation.eulerAngles;
             float raw = 0f;
             switch (axisToMonitor)
             {
                 case RotationAxis.X: raw = euler.x; break;
                 case RotationAxis.Y: raw = euler.y; break;
                 case RotationAxis.Z: raw = euler.z; break;
             }
             finalAngle = WrapAngle(raw);
        }

        currentAngle = finalAngle;

        // JSON
        string json = $"{{\"temps\": {System.Math.Round(Time.time, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"angle\": {System.Math.Round(finalAngle, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

        mqttManager.PublishCustom(overrideMqttTopic, json);
        isConnected = true; 
    }

    float WrapAngle(float a)
    {
        if (a > 180) return a - 360;
        return a;
    }

    /// <summary>
    /// Resets the pendulum physics and places it at the specified angle (in degrees).
    /// </summary>
    public void SetInitialAngle(float angleDeg)
    {
        StartCoroutine(ResetPhysicsRoutine(angleDeg));
    }


    System.Collections.IEnumerator ResetPhysicsRoutine(float targetAngleDeg)
    {
        if (pendulumObj == null || pivotTransform == null) yield break;

        Rigidbody rb = pendulumObj.GetComponent<Rigidbody>();
        if (rb == null) yield break;

        // Capture current physics settings before stopping
        float savedMass = rb.mass;
        float savedAlpha = rb.angularDamping;
        float savedLinear = rb.linearDamping;

        // 1. Lock Physics FIRST (Stop everything)
        // Correct order: Zero velocity BEFORE making it kinematic to avoid Unity error
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = true;
        
        // Temporarily zero out damping for the move (clean slate)
        rb.linearDamping = 0f;
        rb.angularDamping = 0f;

        // Wait for physics to catch up
        yield return new WaitForFixedUpdate();

        // 2. Measure Start Angle (Static)
        Vector3 dir = pendulumObj.transform.position - pivotTransform.position;
        float startAngle = 0f;
        Vector3 rotAxis = Vector3.right; 
        
        // ... (Measurement Switch Case omitted for brevity, logic remains same) ...
        switch (axisToMonitor)
        {
            case RotationAxis.Z: startAngle = Mathf.Atan2(dir.x, -dir.y) * Mathf.Rad2Deg; rotAxis = Vector3.forward; break;
            case RotationAxis.X: startAngle = Mathf.Atan2(-dir.z, -dir.y) * Mathf.Rad2Deg; rotAxis = Vector3.right; break;
            case RotationAxis.Y: startAngle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg; rotAxis = Vector3.up; break;    
        }
        
        // 3. Animation Loop (Smooth Transition)
        float duration = 2.0f; // 2 seconds transition
        float elapsed = 0f;
        float lastAngle = startAngle;

        Debug.Log($"[PendulumReporter] Starting smooth reset: {startAngle:F1}° -> {targetAngleDeg:F1}°");

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            t = Mathf.SmoothStep(0f, 1f, t);
            float intendedAngle = Mathf.LerpAngle(startAngle, targetAngleDeg, t);
            float step = Mathf.DeltaAngle(lastAngle, intendedAngle);
            pendulumObj.transform.RotateAround(pivotTransform.position, rotAxis, step);
            lastAngle = intendedAngle;
            yield return null; 
        }

        // 4. Final Adjustment
        dir = pendulumObj.transform.position - pivotTransform.position;
        float finalCurrent = 0f;
        switch (axisToMonitor)
        {
            case RotationAxis.Z: finalCurrent = Mathf.Atan2(dir.x, -dir.y) * Mathf.Rad2Deg; break;
            case RotationAxis.X: finalCurrent = Mathf.Atan2(-dir.z, -dir.y) * Mathf.Rad2Deg; break;
            case RotationAxis.Y: finalCurrent = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg; break;
        }

        float finalDelta = targetAngleDeg - finalCurrent;
        if (finalDelta > 180) finalDelta -= 360;
        if (finalDelta < -180) finalDelta += 360;
        pendulumObj.transform.RotateAround(pivotTransform.position, rotAxis, finalDelta);

        // 5. Release and Restore Physics
        rb.isKinematic = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        
        // RESTORE saved settings
        rb.mass = savedMass;
        rb.angularDamping = savedAlpha;
        rb.linearDamping = savedLinear;
        
        yield return new WaitForFixedUpdate();
        rb.WakeUp();
        
        Debug.Log($"[PendulumReporter] Smooth reset complete. Released at {targetAngleDeg:F1}° with alpha={savedAlpha}");
    }

    /// <summary>
    /// Updates physics parameters from MQTT.
    /// Pas null arguments mean "do not change".
    /// </summary>
    public void UpdatePhysics(float? mass, float? alpha, float? fs)
    {
        // 1. Pendulum Rigidbody Settings (Mass, Alpha)
        if (pendulumObj != null)
        {
            Rigidbody rb = pendulumObj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Unlock speed limit to allow natural high-speed swings and proper damping at speed
                rb.maxAngularVelocity = 100f; 

                if (mass.HasValue) rb.mass = mass.Value;
                
                // USER REQUEST: Map "alpha" (MQTT) to Linear Damping (Unity) for better braking effect
                if (alpha.HasValue) 
                {
                    rb.linearDamping = alpha.Value;
                    // rb.angularDamping remains unchanged (or default)
                }
            }
        }

        // 2. Solid Friction via HingeJoint Motor (on Pivot)
        if (pivotTransform != null && fs.HasValue)
        {
            HingeJoint joint = pivotTransform.GetComponent<HingeJoint>();
            if (joint != null)
            {
                if (fs.Value > 0.0001f) // Treat negligible values as 0
                {
                    joint.useMotor = true;
                    JointMotor motor = joint.motor;
                    motor.targetVelocity = 0f;       // Brake
                    motor.force = fs.Value;          // Maximum Friction Torque
                    joint.motor = motor;             // Apply back
                }
                else
                {
                    // If fs is 0, disable the motor (no friction)
                    joint.useMotor = false;
                }
            }
            else
            {
                Debug.LogWarning("[PendulumReporter] 'fs' received but no HingeJoint found on Pivot!");
            }
        }

        Debug.Log($"[PendulumReporter] Physics Update: m={mass}, alpha={alpha}, fs={fs}");
    }
}
