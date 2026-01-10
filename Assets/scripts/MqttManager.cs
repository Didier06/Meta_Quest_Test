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
    public string mqttTopic = "FABLAB_21_22/unity/metaquest/in";
    public string mqttTopicOut = "FABLAB_21_22/unity/metaquest/out";
    
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
        public float temperature; // Temperature for thermometer
        public float pressure;    // Pressure for manometer
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

                // Subscribe to all Gauge Bindings
                foreach (var binding in gaugeBindings)
                {
                    if (!string.IsNullOrEmpty(binding.topic) && !topics.Contains(binding.topic))
                    {
                        topics.Add(binding.topic);
                        qos.Add(MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE);
                    }
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
        if (topic == mqttTopic)
        {
            // Main JSON Channel
            messageQueue.Enqueue(msg);
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

                    // --- TEMPERATURE (Thermometer Pointer) ---
                    if (json.Contains("\"temperature\""))
                    {
                        // 1. Try to find custom calibration
                        var binding = gaugeBindings.Find(x => x.targetName == data.targetName && x.type == GaugeType.Gauge);
                        
                        // 2. Defaults
                        float min = (binding != null) ? binding.minValue : 0f;
                        float max = (binding != null) ? binding.maxValue : 30f;
                        float maxAng = (binding != null) ? binding.maxAngle : 180f;

                        // 3. Clamp (Safety)
                        float val = Mathf.Clamp(data.temperature, min, max);

                        Transform pointer = FindDeepChild(target.transform, "Pointer");
                        if (pointer != null)
                        {
                            // 4. Direct Formula (Simple Rule of Three) with user fix
                            // angle = -val * (maxAngle / (max - min))
                            float range = max - min;
                            float ratio = (range != 0) ? (maxAng / range) : 0f;
                            float angle = -val * ratio;

                            Quaternion targetRot = Quaternion.Euler(0f, angle, 0f);
                            
                            if (!smoothTargets.ContainsKey(pointer)) smoothTargets.Add(pointer, targetRot);
                            else smoothTargets[pointer] = targetRot;

                            Debug.Log($"Updated Temperature: {val} -> Angle: {angle}");
                        }
                        else
                        {
                            Debug.LogWarning($"Target '{data.targetName}' received temperature but has no child named 'Pointer'.");
                        }
                    }

                    // --- PRESSURE (Manometer Pointer) ---
                    if (json.Contains("\"pressure\""))
                    {
                        var binding = gaugeBindings.Find(x => x.targetName == data.targetName && x.type == GaugeType.Gauge);
                        float min = (binding != null) ? binding.minValue : 0f;
                        float max = (binding != null) ? binding.maxValue : 10f;
                        float maxAng = (binding != null) ? binding.maxAngle : 180f;

                        float val = Mathf.Clamp(data.pressure, min, max);

                        Transform pointer = FindDeepChild(target.transform, "Pointer");
                        if (pointer != null)
                        {
                            // Simple Ratio with user fix
                            float range = max - min;
                            float ratio = (range != 0) ? (maxAng / range) : 0f;
                            float angle = -val * ratio;
                            
                            Quaternion targetRot = Quaternion.Euler(0f, angle, 0f);
                            
                            if (!smoothTargets.ContainsKey(pointer)) smoothTargets.Add(pointer, targetRot);
                            else smoothTargets[pointer] = targetRot;

                            Debug.Log($"Updated Pressure: {val} -> Angle: {angle}");
                        }
                        else
                        {
                            Debug.LogWarning($"Target '{data.targetName}' received pressure but has no child named 'Pointer'.");
                        }
                    }

                    // Physics WakeUp ensures changes are registered immediately
                    if (hasRb && !rb.isKinematic) rb.WakeUp();

                    Debug.Log($"MQTT Update '{data.targetName}': Pos={(json.Contains("\"position\"") ? "Set" : "Skip")} Temp/Press={(json.Contains("\"temperature\"") ? data.temperature.ToString() : (json.Contains("\"pressure\"") ? data.pressure.ToString() : "N/A"))}");
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

    public void PublishExampleMessage()
    {
        if (client != null && client.IsConnected)
        {
            string msg = "{\"targetName\":\"Cube\", \"position\":{\"x\":0,\"y\":1,\"z\":2}, \"scale\":{\"x\":1.5,\"y\":1.5,\"z\":1.5}, \"rotationSpeed\":{\"x\":0,\"y\":45,\"z\":0}}";
            client.Publish(mqttTopicOut, Encoding.UTF8.GetBytes(msg), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, false);
            Debug.Log("Sent example message: " + msg);
        }
    }

    void OnDestroy()
    {
        if (client != null && client.IsConnected)
        {
            client.Disconnect();
        }
    }
}
