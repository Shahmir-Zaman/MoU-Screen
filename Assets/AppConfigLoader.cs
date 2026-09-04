using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;

[System.Serializable]
public class TopicConfig
{
    public string topicName;
    public string videoFileNameEN;
    public string videoFileNameAR;
}

[System.Serializable]
public class AppConfig
{
    public float fadeDuration = 0.5f;
    public float idleTimeout = 60f;
    public float maxSessionTimeout = 420f;
    public float videoFinishedGrace = 10f;
    public float topicRevealDelay = 0.3f;
    public float pointerMoveThreshold = 20f;
    public int tuioPort = 3333;
    public float buttonHitAreaPadding = 50f;
    public List<TopicConfig> topics = new List<TopicConfig>();
}

[DefaultExecutionOrder(-100)]
public class AppConfigLoader : MonoBehaviour
{
    private void Awake()
    {
        LoadAndApplyConfig();
    }

    private void LoadAndApplyConfig()
    {
        string path = Path.Combine(Application.streamingAssetsPath, "config.json");
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                AppConfig cfg = JsonUtility.FromJson<AppConfig>(json);
                ApplyConfig(cfg);
                Debug.Log("[AppConfigLoader] Successfully loaded and applied config.json");
            }
            catch (System.Exception e)
            {
                Debug.LogError("[AppConfigLoader] Failed to parse config.json: " + e.Message);
            }
        }
        else
        {
            Debug.LogWarning("[AppConfigLoader] config.json not found in StreamingAssets.");
        }
    }

    private void ApplyConfig(AppConfig cfg)
    {
        var flowManager = FindObjectOfType<AppFlowManager>();
        if (flowManager != null)
        {
            flowManager.fadeDuration = cfg.fadeDuration;
            flowManager.idleTimeout = cfg.idleTimeout;
            flowManager.maxSessionTimeout = cfg.maxSessionTimeout;
            flowManager.videoFinishedGrace = cfg.videoFinishedGrace;
            flowManager.topicRevealDelay = cfg.topicRevealDelay;
            flowManager.pointerMoveThreshold = cfg.pointerMoveThreshold;
            
            if (cfg.topics != null)
            {
                for (int i = 0; i < flowManager.topicDatabase.Count; i++)
                {
                    TopicData td = flowManager.topicDatabase[i];
                    TopicConfig match = cfg.topics.Find(t => t.topicName == td.topicName);
                    if (match != null)
                    {
                        td.videoFileNameEN = match.videoFileNameEN;
                        td.videoFileNameAR = match.videoFileNameAR;
                        flowManager.topicDatabase[i] = td;
                    }
                }
            }
        }
        else 
        {
            Debug.LogWarning("[AppConfigLoader] AppFlowManager not found in scene.");
        }

        var tuioInput = FindObjectOfType<TouchScript.InputSources.TuioInput>();
        if (tuioInput != null)
        {
            tuioInput.TuioPort = cfg.tuioPort;
        }

        if (cfg.buttonHitAreaPadding > 0)
        {
            float p = -cfg.buttonHitAreaPadding;
            Vector4 padding = new Vector4(p, p, p, p);
            var buttons = FindObjectsByType<UnityEngine.UI.Button>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var btn in buttons)
            {
                if (btn.targetGraphic != null)
                {
                    btn.targetGraphic.raycastPadding = padding;
                }
            }
        }
    }
}
