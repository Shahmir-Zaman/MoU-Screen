using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public class AppStressTester : MonoBehaviour
{
    [Header("Session Management")]
    [Tooltip("Minimum time (in seconds) to spend in one language before letting it time out")]
    public float minSessionDuration = 300f; // 5 mins
    [Tooltip("Maximum time (in seconds) to spend in one language before letting it time out")]
    public float maxSessionDuration = 600f; // 10 mins

    [Header("Navigation Delays (Idle/Select Screens)")]
    public float minNavDelay = 1f;
    public float maxNavDelay = 4f;

    [Header("Topic Delays (Video Screen)")]
    [Tooltip("How long to pretend to watch the video before randomly interrupting it")]
    public float minTopicWatchDelay = 3f;
    public float maxTopicWatchDelay = 10f;
    
    private AppFlowManager flowManager;
    private float sessionEndTime;
    private bool isWaitingForIdle;
    private bool isFirstSession = true;
    private AppLanguage lastLanguage = AppLanguage.English;

    private void Start()
    {
        flowManager = GetComponent<AppFlowManager>();
        if (flowManager == null)
        {
            Debug.LogError("[AppStressTester] AppFlowManager not found on this object. Stress tester disabled.");
            return;
        }

        Debug.LogWarning("[AppStressTester] Automated stress testing is running!");
        isFirstSession = true;
        isWaitingForIdle = false;
        
        StartCoroutine(StressTestRoutine());
    }

    private void StartNewSessionTimer()
    {
        float duration = Random.Range(minSessionDuration, maxSessionDuration);
        sessionEndTime = Time.time + duration;
        isWaitingForIdle = false;
        Debug.LogWarning($"[AppStressTester] Starting new session for {duration:F0} seconds in {lastLanguage}.");
    }

    private IEnumerator StressTestRoutine()
    {
        while (true)
        {
            // 1. Check if we need to enter idle-waiting mode
            if (!isWaitingForIdle && Time.time >= sessionEndTime && 
                !(flowManager.idleScreen != null && flowManager.idleScreen.interactable)) 
            {
                isWaitingForIdle = true;
                Debug.LogWarning("[AppStressTester] Session duration reached! Hands off... waiting for natural idle timeout.");
            }

            // Determine state by checking which CanvasGroup is currently interactable
            if (flowManager.idleScreen != null && flowManager.idleScreen.interactable)
            {
                if (isWaitingForIdle)
                {
                    Debug.Log("[AppStressTester] Successfully idled out.");
                    isWaitingForIdle = false;
                }

                yield return new WaitForSeconds(Random.Range(minNavDelay, maxNavDelay));
                
                // Re-check in case user manually clicked something during our wait
                if (flowManager.idleScreen.interactable)
                {
                    if (isFirstSession)
                    {
                        lastLanguage = Random.value > 0.5f ? AppLanguage.English : AppLanguage.Arabic;
                        isFirstSession = false;
                    }
                    else
                    {
                        // Toggle language
                        lastLanguage = (lastLanguage == AppLanguage.English) ? AppLanguage.Arabic : AppLanguage.English;
                    }

                    Button btn = (lastLanguage == AppLanguage.English) ? flowManager.englishButton : flowManager.arabicButton;
                    
                    if (btn != null)
                    {
                        Debug.Log($"[AppStressTester] Idle state: Clicking {btn.name} ({lastLanguage})");
                        btn.onClick.Invoke();
                        StartNewSessionTimer();
                    }
                }
            }
            else if (flowManager.selectScreen != null && flowManager.selectScreen.interactable)
            {
                if (isWaitingForIdle)
                {
                    // Do nothing, just wait for the app to time out naturally
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                yield return new WaitForSeconds(Random.Range(minNavDelay, maxNavDelay));

                if (Time.time >= sessionEndTime) continue; // abort click if session expired while waiting

                if (flowManager.selectScreen.interactable && flowManager.topicDatabase != null && flowManager.topicDatabase.Count > 0)
                {
                    int index = Random.Range(0, flowManager.topicDatabase.Count);
                    Button btn = flowManager.topicDatabase[index].topicButton;
                    if (btn != null)
                    {
                        Debug.Log("[AppStressTester] Select state: Clicking topic '" + flowManager.topicDatabase[index].topicName + "'");
                        btn.onClick.Invoke();
                    }
                }
            }
            else if (flowManager.topicScreen != null && flowManager.topicScreen.interactable)
            {
                if (isWaitingForIdle)
                {
                    // Do nothing, wait for video to end and app to time out
                    yield return new WaitForSeconds(1f);
                    continue;
                }

                yield return new WaitForSeconds(Random.Range(minTopicWatchDelay, maxTopicWatchDelay));

                if (Time.time >= sessionEndTime) continue; // abort click if session expired while waiting

                if (flowManager.topicScreen.interactable)
                {
                    // 50% chance to interrupt the video, 50% chance to just let it finish/timeout normally
                    if (Random.value > 0.5f)
                    {
                        Button btn = flowManager.returnToSelectButton;
                        if (btn != null)
                        {
                            Debug.Log("[AppStressTester] Topic state: Interrupting video and returning to select");
                            btn.onClick.Invoke();
                        }
                    }
                    else 
                    {
                        Debug.Log("[AppStressTester] Topic state: Decided not to interrupt, waiting for next cycle...");
                    }
                }
            }
            else
            {
                // Nothing is interactable (we are in a transition). Wait a moment and check again.
                yield return new WaitForSeconds(0.2f);
            }
        }
    }
}
