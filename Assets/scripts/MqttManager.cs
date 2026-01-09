using UnityEngine;
using System;
using System.Text;
using System.Collections.Concurrent;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using System.Net;
using System.Collections.Generic;

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

    [Serializable]
    public class ObjectTransformData
    {
        public string targetName;
        public Vector3 position;
        public Vector3 rotation; // Absolute rotation
        public Vector3 scale;    // Zoom/Scale (default 0,0,0 if omitted)
        public Vector3 rotationSpeed; // Continuous rotation speed (degrees/sec)
    }

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
                client.Subscribe(new string[] { mqttTopic }, new byte[] { MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE });

                // Send Hello Message
                string helloMsg = "Hello from meta Quest 3";
                client.Publish(mqttTopicOut, Encoding.UTF8.GetBytes(helloMsg), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, false);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("MQTT Connection Failed: " + e.Message);
        }
    }

    private void Client_ConnectionClosed(object sender, EventArgs e)
    {
        Debug.LogWarning("MQTT Connection Closed.");
    }

    private void Client_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
    {
        string msg = Encoding.UTF8.GetString(e.Message);
        // Enqueue message to be processed in the main thread
        messageQueue.Enqueue(msg);
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
                ProcessMessage(msg);
            }
        }

        // Test sending positions
        if (Input.GetKeyDown(KeyCode.S))
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
    }

    void ProcessMessage(string json)
    {
        try
        {
            // Parse JSON
            ObjectTransformData data = JsonUtility.FromJson<ObjectTransformData>(json);

            if (data != null && !string.IsNullOrEmpty(data.targetName))
            {
                // Find method: flexible but potentially slow if many objects. 
                // Creating a lookup Dictionary is better for performance if managing many objects.
                GameObject target = GameObject.Find(data.targetName);

                if (target != null)
                {
                    target.transform.position = data.position;
                    
                    // Apply absolute rotation if it's not zero
                    if (data.rotation != Vector3.zero) 
                    {
                        target.transform.rotation = Quaternion.Euler(data.rotation);
                    }

                    // Apply Scale (Zoom) - Ignore if zero (missing in JSON)
                    if (data.scale != Vector3.zero)
                    {
                        target.transform.localScale = data.scale;
                    }

                    // Handle Continuous Rotation
                    if (data.rotationSpeed != Vector3.zero)
                    {
                        // Add or Update rotation speed
                        if (!rotatingObjects.ContainsKey(target))
                            rotatingObjects.Add(target, data.rotationSpeed);
                        else
                            rotatingObjects[target] = data.rotationSpeed;
                    }
                    else
                    {
                        // If speed is zero, stop rotating
                        if (rotatingObjects.ContainsKey(target))
                            rotatingObjects.Remove(target);
                    }
                    
                    Debug.Log($"Updated object '{data.targetName}' | Pos: {data.position} | Scale: {data.scale} | RotSpeed: {data.rotationSpeed}");
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
