using UnityEngine;
using System;
using System.Text;
using System.Collections.Concurrent;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using System.Net;
using System.Collections.Generic;
using TMPro;

public class MqttManager : MonoBehaviour
{
    [Header("MQTT Configuration")]
    public string mqttBroker = "mqtt.univ-cotedazur.fr";
    public int mqttPort = 1883;
    public bool useEncrypted = false; // Set to true if using SSL (usually port 8883)
    public string mqttTopic = "FABLAB_21_22/Unity/metaquest/in";
    public string mqttTopicOut = "FABLAB_21_22/Unity/metaquest/out";
    
    [Header("Credentials")]
    public string username = "";
    public string password = "";

    [Header("Settings")]
    public bool processMessages = true;
    public float minUpdateInterval = 0.05f; // Faster updates for smooth movement
    public float reconnectDelay = 5.0f;

    private MqttClient client;
    private ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
    private float lastUpdateTime = 0f;
    
    // Store objects that need continuous rotation: GameObject -> RotationSpeed (Euler/sec)
    private Dictionary<GameObject, Vector3> rotatingObjects = new Dictionary<GameObject, Vector3>();
    // Store objects that need smooth transition to a target rotation: Transform -> TargetLocalRotation
    private Dictionary<Transform, Quaternion> smoothTargets = new Dictionary<Transform, Quaternion>();
    public float smoothingSpeed = 5.0f;

    [Serializable]
    public class ObjectTransformData
    {
        public string targetName;
        public Vector3 position;
        public Vector3 rotation; // Absolute rotation
        public Vector3 scale;    // Zoom/Scale (default 0,0,0 if omitted)
        public Vector3 rotationSpeed; // Continuous rotation speed (degrees/sec)
        public float gauge_value; // Unified generic value
    }

    public enum GaugeType
    {
        Gauge,           // Standard Gauge with Pointer & Calibration
        RotationSpeed,   // Continuous Rotation (Motor)
        RotationAbsolute // Absolute Rotation of the object
    }

    [Serializable]
    public class TopicBinding
    {
        public string topic;       // Specific topic
        public string targetName;  // Target Object Name
        public GaugeType type;     // How to interpret the value

        [Header("Calibration (Gauge Only)")]
        public float minValue = 0f;
        public float maxValue = 100f;
        public float maxAngle = 180f;

        [Header("UI Display (Gauge Only)")]
        public string valueChild = "Value"; // Name of child with TMP for Value
    }

    [Header("Gauge Bindings")]
    public List<TopicBinding> gaugeBindings = new List<TopicBinding>();

    [Header("Coupled Pendulums")]
    public GameObject pendule1;
    public GameObject pendule2;
    public string pendulumCouplesTopic = "FABLAB_21_22/Unity/PendulesCouples/out";
    public string pendulumCouplesInputTopic = "FABLAB_21_22/Unity/PendulesCouples/in";
    public AngularCoupling couplingScript;
    public float coupledPublishRate = 0.1f;

    public float offset1 = 0f; // Calibration offset for Pendule1
    public float offset2 = 0f; // Calibration offset for Pendule2
    private float nextCoupledPublishTime;

    void Start()
    {
        // Ensure secrets are loaded
        SecretsLoader.LoadSecrets();

        if (SecretsLoader.Data != null)
        {
            username = SecretsLoader.Data.mqttUser;
            password = SecretsLoader.Data.mqttPassword;
            mqttPort = SecretsLoader.Data.mqttPort;
            
            // If port suggests SSL (8883 or 8443), enable encryption automatically, or respect the boolean
            // For now, let's keep the user's boolean preference or auto-switch if port is typical SSL
            if (mqttPort == 8883 || mqttPort == 8443) useEncrypted = true;
            
            Debug.Log($"Loaded credentials from secrets. User: {username}, Port: {mqttPort}, Encrypted: {useEncrypted}");
        }
        else
        {
            Debug.LogWarning("No secrets loaded. Using Inspector values.");
        }
        
        StartCoroutine(ConnectionRoutine());
    }

    System.Collections.IEnumerator ConnectionRoutine()
    {
        while (true)
        {
            if (client == null || !client.IsConnected)
            {
                Debug.Log("Connecting to MQTT broker...");
                Connect();
            }
            yield return new WaitForSeconds(reconnectDelay);
        }
    }

    void Connect()
    {
        try
        {
            if (client != null && client.IsConnected) return;

            // Use specified encryption setting
            if (useEncrypted)
            {
                // Recommended constructor: brokerHostName, brokerPort, secure, caCert (null or yours), clientCert (null or yours), sslProtocol
                client = new MqttClient(mqttBroker, mqttPort, true, null, null, MqttSslProtocols.TLSv1_2);
            }
            else
            {
                // Recommended constructor without SSL
                client = new MqttClient(mqttBroker, mqttPort, false, null, null, MqttSslProtocols.None);
            }

            string clientId = Guid.NewGuid().ToString();
            
            // Connect with or without credentials
            if (!string.IsNullOrEmpty(username))
                client.Connect(clientId, username, password);
            else
                client.Connect(clientId);

            if (client.IsConnected)
            {
                Debug.Log($"Connected to MQTT Broker: {mqttBroker}:{mqttPort}");
                client.MqttMsgPublishReceived += Client_MqttMsgPublishReceived;
                client.ConnectionClosed += Client_ConnectionClosed;
                
                // Subscribe to Main JSON Topic
                List<string> topics = new List<string>();
                List<byte> qos = new List<byte>();

                topics.Add(mqttTopic);
                qos.Add(MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE);

                // Subscribe to Pendulum Input
                topics.Add(pendulumInputTopic);
                qos.Add(MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE);

                // Subscribe to all Gauge Bindings
                foreach (var binding in gaugeBindings)
                {
                    if (!string.IsNullOrEmpty(binding.topic) && !topics.Contains(binding.topic))
                    {
                        topics.Add(binding.topic);
                        qos.Add(MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE);
                    }
                }



                // Subscribe to Coupled Pendulum Input
                if (!topics.Contains(pendulumCouplesInputTopic))
                {
                    topics.Add(pendulumCouplesInputTopic);
                    qos.Add(MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE);
                }

                client.Subscribe(topics.ToArray(), qos.ToArray());

                // Send Hello Message
                string helloMsg = "Hello from meta Quest 3";
                client.Publish(mqttTopicOut, Encoding.UTF8.GetBytes(helloMsg), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, false);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"MQTT Connection Failed: {e.Message}");
        }
    }

    private void Client_ConnectionClosed(object sender, EventArgs e)
    {
        Debug.LogWarning("MQTT Connection Closed.");
    }

    private void Client_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
        string msg = Encoding.UTF8.GetString(e.Message);
        string topic = e.Topic;

        // Dispatch based on topic
        if (topic == mqttTopic || topic == pendulumInputTopic)
        {
            // Main JSON Channel OR Pendulum Input
            messageQueue.Enqueue(msg);
        }
        else if (topic == pendulumCouplesInputTopic)
        {
            // Coupled Pendulum Input
            messageQueue.Enqueue($"COUPLED|{msg}");
        }
        else
        {
        // Check bindings
            foreach (var binding in gaugeBindings)
            {
                if (binding.topic == topic)
                {
                    // Enqueue a special "BindingCommand" or handle on main thread via a wrapper
                    // To keep it simple, we wrap it in a pseudo-JSON or struct, 
                    // BUT simplest is to just Enqueue a specially formatted string we can parse later
                    // Or better: Use a thread-safe action queue. 
                    // For now, let's prefix the message with "BINDING|Index|" so Update knows.
                    int index = gaugeBindings.IndexOf(binding); // Warning: O(N) but reliable
                    messageQueue.Enqueue($"BINDING|{index}|{msg}");
                }
            }
        }
    }

    [Header("Input Topics")]
    public string pendulumInputTopic = "FABLAB_21_22/Unity/Pendule/in";

    void Update()
    {
        if (processMessages && !messageQueue.IsEmpty)
        {
            if (Time.time - lastUpdateTime < minUpdateInterval)
                return;

            lastUpdateTime = Time.time;

            string msg;
            while (messageQueue.TryDequeue(out msg))
            {
                if (msg.StartsWith("BINDING|"))
                {
                    ProcessBindingMessage(msg);
                }
                else if (msg.StartsWith("COUPLED|"))
                {
                    ProcessCoupledInput(msg.Substring(8));
                }
                else if (msg.Contains("angle_init")) // Quick check for pendulum message if topic matching is complex in queue
                {
                     //Ideally we shoud pass topic in queue tuple, but for now lets check content
                     ProcessPendulumMessage(msg);
                     ProcessMessage(msg); // Also standard process
                }
                else
                {
                    ProcessMessage(msg);
                }
            }
        }

        // Test sending positions (New Input System)
        if (UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.sKey.wasPressedThisFrame)
        {
            PublishExampleMessage();
        }

        // Apply continuous rotations
        if (rotatingObjects.Count > 0)
        {
            List<GameObject> toRemove = new List<GameObject>();
            foreach (var kvp in rotatingObjects)
            {
                if (kvp.Key != null)
                {
                    kvp.Key.transform.Rotate(kvp.Value * Time.deltaTime);
                }
                else
                {
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (var dead in toRemove) rotatingObjects.Remove(dead);
        }

        // Apply smooth target rotations (e.g. Thermometer)
        if (smoothTargets.Count > 0)
        {
            List<Transform> finished = new List<Transform>();
            foreach (var kvp in smoothTargets)
            {
                Transform t = kvp.Key;
                if (t != null)
                {
                    t.localRotation = Quaternion.Slerp(t.localRotation, kvp.Value, Time.deltaTime * smoothingSpeed);
                    
                    // Stop if close enough to save performance
                    if (Quaternion.Angle(t.localRotation, kvp.Value) < 0.1f) finished.Add(t);
                }
                else
                {
                    finished.Add(t);
                }
            }
            foreach (var t in finished) smoothTargets.Remove(t);
        }

        // Publish Coupled Pendulum Data
        if (Time.time >= nextCoupledPublishTime)
        {
            PublishCoupledData();
            nextCoupledPublishTime = Time.time + coupledPublishRate;
        }
    }

    void ProcessBindingMessage(string rawMsg)
    {
        try
        {
            // Format: BINDING|Index|Value
            string[] parts = rawMsg.Split(new char[] { '|' }, 3);
            if (parts.Length < 3) return;

            int index = int.Parse(parts[1]);
            string valueStr = parts[2];
            
            if (index >= 0 && index < gaugeBindings.Count)
            {
                var binding = gaugeBindings[index];
                if (float.TryParse(valueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float value))
                {
                    ApplyBindingValue(binding, value);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error processing binding: {e.Message}");
        }
    }

    void ApplyBindingValue(TopicBinding binding, float value)
    {
        GameObject target = GameObject.Find(binding.targetName);
        if (target == null) return;

        switch (binding.type)
        {
            case GaugeType.Gauge: 
                // Unified Logic: Uses Min/Max/Angle from binding
                
                // 1. Rotate Pointer "Pointer"
                Transform pointer = FindDeepChild(target.transform, "Pointer");
                if (pointer != null)
                {
                    // Clamp value to ensure it stays within range
                    float clampedValue = Mathf.Clamp(value, binding.minValue, binding.maxValue);

                    // Direct Formula (Simple Rule of Three)
                    // angle = -val * (maxAngle / maxValue)
                    float max = binding.maxValue - binding.minValue;
                    float ratio = (max != 0) ? (binding.maxAngle / max) : 0f;
                    float angle = -clampedValue * ratio;
                    
                    Quaternion targetRot = Quaternion.Euler(0f, angle, 0f);
                    if (!smoothTargets.ContainsKey(pointer)) smoothTargets.Add(pointer, targetRot);
                    else smoothTargets[pointer] = targetRot;
                }

                // 2. Update Value Text (Optional)
                if (!string.IsNullOrEmpty(binding.valueChild))
                {
                    Transform valueT = FindDeepChild(target.transform, binding.valueChild);
                    if (valueT != null)
                    {
                        var tmp = valueT.GetComponent<TMP_Text>();
                        if (tmp != null) tmp.text = value.ToString("F1");
                    }
                }
                break;

            case GaugeType.RotationSpeed:
                // Apply continuous rotation on Y axis by default
                Vector3 speed = new Vector3(0, value, 0); 
                if (value == 0)
                {
                    if (rotatingObjects.ContainsKey(target)) rotatingObjects.Remove(target);
                }
                else
                {
                    if (!rotatingObjects.ContainsKey(target)) rotatingObjects.Add(target, speed);
                    else rotatingObjects[target] = speed;
                }
                break;
                
             case GaugeType.RotationAbsolute:
                // Rotation on Y axis
                Quaternion absRot = Quaternion.Euler(0, value, 0);
                 if (!smoothTargets.ContainsKey(target.transform)) smoothTargets.Add(target.transform, absRot);
                 else smoothTargets[target.transform] = absRot;
                 break;
        }
    }

    void ProcessMessage(string json)
    {
        try
        {
            // Parse JSON into data object
            ObjectTransformData data = JsonUtility.FromJson<ObjectTransformData>(json);

            if (data != null && !string.IsNullOrEmpty(data.targetName))
            {
                GameObject target = GameObject.Find(data.targetName);

                if (target != null)
                {
                    Rigidbody rb = target.GetComponent<Rigidbody>();
                    bool hasRb = (rb != null);

                    // --- POSITION ---
                    // Only update if key provided or value is non-zero (fallback)
                    // (Simple "Contains" check helps distinguish "missing" from "explicit 0")
                    if (json.Contains("\"position\""))
                    {
                        if (hasRb)
                        {
                            rb.position = data.position; // Teleport physics
                            // If kinematic, transform update is implicit, but for dynamic this is safer
                        }
                        else
                        {
                            target.transform.position = data.position;
                        }
                    }

                    // --- ROTATION ---
                    // Fix: Check if JSON contains "rotation" key to allow setting 0,0,0
                    if (json.Contains("\"rotation\""))
                    {
                        Quaternion newRot = Quaternion.Euler(data.rotation);
                        if (hasRb)
                        {
                            rb.rotation = newRot;
                        }
                        else
                        {
                            target.transform.rotation = newRot;
                        }
                    }

                    // --- SCALE ---
                    if (json.Contains("\"scale\"") && data.scale != Vector3.zero)
                    {
                        target.transform.localScale = data.scale;
                    }

                    // --- CONTINUOUS ROTATION ---
                    if (json.Contains("\"rotationSpeed\""))
                    {
                        if (data.rotationSpeed != Vector3.zero)
                        {
                            if (!rotatingObjects.ContainsKey(target))
                                rotatingObjects.Add(target, data.rotationSpeed);
                            else
                                rotatingObjects[target] = data.rotationSpeed;
                        }
                        else
                        {
                            // Explicitly set to 0 -> Stop
                            if (rotatingObjects.ContainsKey(target))
                                rotatingObjects.Remove(target);
                        }
                    }

                    // --- GAUGE VALUE (Universal) ---
                    if (json.Contains("\"gauge_value\""))
                    {
                        // 1. Try to find custom calibration
                        var binding = gaugeBindings.Find(x => x.targetName == data.targetName && x.type == GaugeType.Gauge);
                        
                        // 2. Defaults (Standard 0-100 if no binding found)
                        float min = (binding != null) ? binding.minValue : 0f;
                        float max = (binding != null) ? binding.maxValue : 100f;
                        float maxAng = (binding != null) ? binding.maxAngle : 180f;

                        // 3. Clamp (Safety)
                        float val = Mathf.Clamp(data.gauge_value, min, max);

                        Transform pointer = FindDeepChild(target.transform, "Pointer");
                        if (pointer != null)
                        {
                            // 4. Direct Formula
                            // angle = -val * (maxAngle / (max - min))
                            float range = max - min;
                            float ratio = (range != 0) ? (maxAng / range) : 0f;
                            float angle = -val * ratio;

                            Quaternion targetRot = Quaternion.Euler(0f, angle, 0f);
                            
                            if (!smoothTargets.ContainsKey(pointer)) smoothTargets.Add(pointer, targetRot);
                            else smoothTargets[pointer] = targetRot;

                            Debug.Log($"Updated Gauge '{data.targetName}': {val} -> Angle: {angle}");
                        }
                        else
                        {
                            Debug.LogWarning($"Target '{data.targetName}' received gauge_value but has no child named 'Pointer'.");
                        }
                    }

                    // Physics WakeUp ensures changes are registered immediately
                    if (hasRb && !rb.isKinematic) rb.WakeUp();

                    Debug.Log($"MQTT Update '{data.targetName}': Pos={(json.Contains("\"position\"") ? "Set" : "Skip")} Gauge={(json.Contains("\"gauge_value\"") ? data.gauge_value.ToString() : "N/A")}");
                }
                else
                {
                    Debug.LogWarning($"Target object '{data.targetName}' not found in scene.");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Error parsing/applying JSON update: {e.Message} \nJSON: {json}");
        }
    }

    // Helper to find a child recursively
    Transform FindDeepChild(Transform aParent, string aName)
    {
        foreach (Transform child in aParent)
        {
            if (child.name == aName) return child;
            var result = FindDeepChild(child, aName);
            if (result != null) return result;
        }
        return null;
    }

    void ProcessPendulumMessage(string json)
    {
        var reporter = FindFirstObjectByType<PendulumReporter>();
        if (reporter == null) return;

        // 1. Physics (m, alpha, fs) - UPDATE FIRST
        float? m = null, alpha = null, fs = null;
        
        // Check "m" (Mass)
        if (ExtractFloat(json, "\"m\"", out float massVal)) m = massVal;
        else if (ExtractFloat(json, "m", out massVal)) m = massVal; 

        // Check "alpha" (Angular Damping)
        if (ExtractFloat(json, "alpha", out float aVal)) alpha = aVal;

        // Check "fs" (Solid Friction)
        if (ExtractFloat(json, "fs", out float fVal)) fs = fVal;

        // Apply Physics immediately so they are captured correctly by the Reset Routine if it runs
        if (m.HasValue || alpha.HasValue || fs.HasValue)
        {
            reporter.UpdatePhysics(m, alpha, fs);
        }

        // 2. Angle Init (Check "angle_init") - RESET AFTER PHYSICS
        if (ExtractFloat(json, "angle_init", out float angle))
        {
            reporter.SetInitialAngle(angle);
        }
    }

    /// <summary>
    /// Simple JSON value extractor to avoid overhead of full struct parsing for partial updates.
    /// Looks for "key": value
    /// </summary>
    bool ExtractFloat(string json, string key, out float result)
    {
        result = 0f;
        int index = json.IndexOf(key);
        if (index == -1) return false;

        // Find colon after key
        int colon = json.IndexOf(':', index);
        if (colon == -1) return false;

        // Find end of value (comma or brace)
        int comma = json.IndexOf(',', colon);
        int brace = json.IndexOf('}', colon);
        
        int end = -1;
        if (comma != -1 && brace != -1) end = System.Math.Min(comma, brace);
        else if (comma != -1) end = comma;
        else end = brace;

        if (end == -1) return false;

        string valStr = json.Substring(colon + 1, end - colon - 1).Trim();
        return float.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    public void PublishExampleMessage()
    {
        if (client != null && client.IsConnected)
        {
            // Example with generic gauge value
            string msg = "{\"targetName\":\"Cube\", \"position\":{\"x\":0,\"y\":1,\"z\":2}, \"gauge_value\": 50.0}";
            client.Publish(mqttTopicOut, Encoding.UTF8.GetBytes(msg), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, false);
            Debug.Log("Sent example message: " + msg);
        }
    }

    /// <summary>
    /// Publishes a custom message to a specific topic using the existing client.
    /// </summary>
    public void PublishCustom(string topic, string message)
    {
        if (client != null && client.IsConnected)
        {
            client.Publish(topic, Encoding.UTF8.GetBytes(message), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, false);
            // Debug.Log($"[MqttManager] Published to {topic}: {message}");
        }
        else
        {
            // Debug.LogWarning($"[MqttManager] Cannot publish to {topic}, client not connected.");
        }
    }

    void OnDestroy()
    {
        if (client != null && client.IsConnected)
        {
            client.Disconnect();
        }
    }
    public enum Axis { X, Y, Z }
    public Axis coupledRotationAxis = Axis.X;
    private bool _hasWarnedCoupled = false;

    public bool invert1 = false; // Invert sign for Pendule1
    public bool invert2 = false; // Invert sign for Pendule2
    public bool showDebugLogs = false; // Check this to see raw values in Console

    void PublishCoupledData()
    {
        if (client == null || !client.IsConnected) return;
        
        if (pendule1 == null || pendule2 == null) 
        {
            if (!_hasWarnedCoupled)
            {
                Debug.LogWarning("[MqttManager] Coupled Pendulums (Pendule1 or Pendule2) are NOT assigned in the Inspector! Coupled data cannot be published.");
                _hasWarnedCoupled = true;
            }
            return;
        }

        // Try to get HingeJoint for more accurate physics-based angle
        HingeJoint h1 = pendule1.GetComponent<HingeJoint>();
        HingeJoint h2 = pendule2.GetComponent<HingeJoint>();

        float theta1, theta2;

        if (h1 != null) theta1 = GetHingeAngle(pendule1.transform, h1);
        else theta1 = GetAxisAngle(pendule1.transform, coupledRotationAxis);

        if (h2 != null) theta2 = GetHingeAngle(pendule2.transform, h2);
        else theta2 = GetAxisAngle(pendule2.transform, coupledRotationAxis);

        // Apply offsets
        theta1 = WrapAngle(theta1 + offset1);
        theta2 = WrapAngle(theta2 + offset2);

        if (invert1) theta1 = -theta1;
        if (invert2) theta2 = -theta2;

        // Create JSON
        string json = $"{{\"temps\": {System.Math.Round(Time.time, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"theta1\": {System.Math.Round(theta1, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"theta2\": {System.Math.Round(theta2, 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

        // Publish
        client.Publish(pendulumCouplesTopic, Encoding.UTF8.GetBytes(json), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, false);
    }

    float GetHingeAngle(Transform t, HingeJoint hinge)
    {
        // Calculate angle relative to the hinge's start
        // Problem: HingeJoint doesn't expose "current angle" directly in a simple way without limits.
        // But we can compare the current local rotation to the "zero" rotation around the axis.
        
        // Simple approach: JointAngle() is not a standard Unity API (it's in ArticulationBody). 
        // For HingeJoint, we usually rely on 'angle' (only valid if limits are used?) 
        // actually 'hinge.angle' returns the position relative to the rest angle.
        
        return hinge.angle;
    }

    float GetAxisAngle(Transform t, Axis axis)
    {
        // Use localEulerAngles to be relative to the parent (The Machine Frame)
        // This is safer if the machine is moved/rotated in the world.
        Vector3 rot = t.localEulerAngles;
        
        float val = 0f;
        switch (axis)
        {
            case Axis.X: val = rot.x; break;
            case Axis.Y: val = rot.y; break;
            case Axis.Z: val = rot.z; break;
            default: val = rot.z; break;
        }

        if (showDebugLogs) Debug.Log($"[MqttManager] Raw {t.name} ({axis}): {val}");
        return val;
    }



    float WrapAngle(float a)
    {
        a %= 360;
        if (a > 180) return a - 360;
        if (a < -180) return a + 360;
        return a;
    }

    void ProcessCoupledInput(string json)
    {
        // JSON format: {"th1_i": 45, "th2_i": -30, "f": 0.5, "C": 100, "m1": 1.5, "m2": 0.5}
        // Extract parameters (Optional)
        float? th1 = null, th2 = null, f = null, C = null, m1 = null, m2 = null;

        if (ExtractFloat(json, "th1_i", out float v1)) th1 = v1;
        if (ExtractFloat(json, "th2_i", out float v2)) th2 = v2;
        if (ExtractFloat(json, "f", out float vf)) f = vf;
        if (ExtractFloat(json, "C", out float vc)) C = vc;
        if (ExtractFloat(json, "m1", out float vm1)) m1 = vm1;
        if (ExtractFloat(json, "m2", out float vm2)) m2 = vm2;

        // If any parameter is present, trigger the reset/update routine
        if (th1.HasValue || th2.HasValue || f.HasValue || C.HasValue || m1.HasValue || m2.HasValue)
        {
            StartCoroutine(ResetCoupledPhysicsRoutine(th1, th2, f, C, m1, m2));
        }
    }

    System.Collections.IEnumerator ResetCoupledPhysicsRoutine(float? th1, float? th2, float? f, float? C, float? m1, float? m2)
    {
        if (pendule1 == null || pendule2 == null) yield break;

        Rigidbody rb1 = pendule1.GetComponent<Rigidbody>();
        Rigidbody rb2 = pendule2.GetComponent<Rigidbody>();

        // 1. Stop Physics & Reset Velocities
        if (rb1) { rb1.isKinematic = true; rb1.linearVelocity = Vector3.zero; rb1.angularVelocity = Vector3.zero; }
        if (rb2) { rb2.isKinematic = true; rb2.linearVelocity = Vector3.zero; rb2.angularVelocity = Vector3.zero; }

        yield return new WaitForFixedUpdate();

        // 2. Apply Physics Parameters (Immediate)
        // Linear Damping (f)
        if (f.HasValue)
        {
            if (rb1) rb1.linearDamping = f.Value;
            if (rb2) rb2.linearDamping = f.Value;
        }

        // Mass (m1, m2)
        if (m1.HasValue && rb1) rb1.mass = m1.Value;
        if (m2.HasValue && rb2) rb2.mass = m2.Value;

        // Coupling Constant (C)
        if (C.HasValue && couplingScript != null)
        {
            couplingScript.C = C.Value;
        }

        // 3. Smooth Transition for Angles
        // Only if angles are requested
        if (th1.HasValue || th2.HasValue)
        {
            float duration = 2.0f; // Seconds
            float elapsed = 0f;

            // Capture start angles (Physics Hinge Angle or 0)
            float start1 = 0f;
            float start2 = 0f;

            HingeJoint h1 = pendule1.GetComponent<HingeJoint>();
            HingeJoint h2 = pendule2.GetComponent<HingeJoint>();

            if (h1) start1 = h1.angle;
            else start1 = WrapAngle(pendule1.transform.localEulerAngles.x);

            if (h2) start2 = h2.angle;
            else start2 = WrapAngle(pendule2.transform.localEulerAngles.x);

            // Targets (use current if not provided)
            float target1 = th1.HasValue ? th1.Value : start1;
            float target2 = th2.HasValue ? th2.Value : start2;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                
                // Smooth easing
                t = t * t * (3f - 2f * t);

                float currentAngleSet1 = Mathf.Lerp(start1, target1, t);
                float currentAngleSet2 = Mathf.Lerp(start2, target2, t);

                // Apply rotation frame by frame
                ApplyHingeRotation(pendule1, currentAngleSet1);
                ApplyHingeRotation(pendule2, currentAngleSet2);

                yield return null;
            }
            
            // Ensure final exact value
            ApplyHingeRotation(pendule1, target1);
            ApplyHingeRotation(pendule2, target2);
        }

        yield return new WaitForFixedUpdate();

        // 4. Restart Physics
        if (rb1) { rb1.isKinematic = false; rb1.WakeUp(); }
        if (rb2) { rb2.isKinematic = false; rb2.WakeUp(); }
    }

    void ApplyHingeRotation(GameObject pendule, float targetAngle)
    {
        HingeJoint hinge = pendule.GetComponent<HingeJoint>();
        if (hinge != null)
        {
            // Current angle from hinge (signed)
            float current = hinge.angle;
            float delta = targetAngle - current;
            
            // Rotate the transform around the hinge anchor/axis
            Vector3 worldAnchor = pendule.transform.TransformPoint(hinge.anchor);
            Vector3 worldAxis = pendule.transform.TransformDirection(hinge.axis);
            
            pendule.transform.RotateAround(worldAnchor, worldAxis, delta);
        }
        else
        {
            // Fallback: simple local rotation on X (assuming default setup)
             // This is less accurate if pivot is not origin
             float current = pendule.transform.localEulerAngles.x; 
             // Wrap current to -180/180 for delta calc
             if (current > 180) current -= 360;
             float delta = targetAngle - current;
             pendule.transform.Rotate(delta, 0, 0, Space.Self);
        }
    }
}
