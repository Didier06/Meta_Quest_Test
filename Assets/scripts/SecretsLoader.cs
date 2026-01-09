using UnityEngine;

public class SecretsLoader : MonoBehaviour
{
    public static SecretsData Data;

    [System.Serializable]
    public class SecretsData
    {
        public string mqttUser;
        public string mqttPassword;
        public int mqttPort;
    }

    // Load automatically on Awake so it's ready for others
    void Awake()
    {
        LoadSecrets();
    }

    public static void LoadSecrets()
    {
        if (Data != null) return;

        TextAsset targetFile = Resources.Load<TextAsset>("secrets");
        if (targetFile != null)
        {
            Data = JsonUtility.FromJson<SecretsData>(targetFile.text);
            Debug.Log($"Secrets loaded. Port: {Data.mqttPort}");
        }
        else
        {
            Debug.LogError("Could not load 'secrets' from Resources. Make sure secrets.json is in Assets/Resources.");
        }
    }
}
