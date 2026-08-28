using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using UnityEngine.InputSystem;
using DG.Tweening;
using RenderHeads.Media.AVProVideo;
using System.Collections.Generic;

public enum AppLanguage
{
    English,
    Arabic
}

[System.Serializable]
public struct TopicData
{
    public string topicName;
    [Tooltip("The button used to select this topic on the select screen.")]
    public Button topicButton;

    [Header("Localization - Videos")]
    [Tooltip("The English video file name (including extension).")]
    public string videoFileNameEN;
    [Tooltip("The Arabic video file name (including extension).")]
    public string videoFileNameAR;

    [Header("Localization - UI Objects")]
    public GameObject textEN;
    public GameObject textAR;
}

public class AppFlowManager : MonoBehaviour
{
    // Explicit screen state. Never infer this from gameObject.activeSelf --
    // active state lags behind by fadeDuration and lies during transitions.
    private enum AppState { Idle, Select, Topic }

    [Header("Screen Canvas Groups")]
    public CanvasGroup idleScreen;
    public CanvasGroup selectScreen;
    public CanvasGroup topicScreen;

    [Header("Navigation & Language Buttons")]
    [Tooltip("Button on the Idle Screen to start in English")]
    public Button englishButton;
    [Tooltip("Button on the Idle Screen to start in Arabic")]
    public Button arabicButton;
    [Tooltip("The back button on the Topic Screen to return to the Selection Menu")]
    public Button returnToSelectButton;

    [Header("Video Players")]
    public VideoPlayer transitionVideoPlayer;
    public MediaPlayer topicMediaPlayer;

    [Header("Topic Setup")]
    public List<TopicData> topicDatabase;

    [Header("Render Texture Setup")]
    public RenderTexture textureToClear;

    [Header("Settings")]
    public float fadeDuration = 0.5f;

    [Tooltip("Seconds of no interaction before returning to the Idle screen.")]
    public float idleTimeout = 60f;

    [Tooltip("Hard ceiling. Seconds since the last REAL touch before forcing a return to Idle, " +
             "even if a topic video is still playing. Set to 0 to disable. " +
             "Must be longer than your longest topic video plus a little slack.")]
    public float maxSessionTimeout = 420f;

    [Tooltip("Seconds of grace after a topic video finishes before the idle countdown resumes.")]
    public float videoFinishedGrace = 10f;

    [Tooltip("Time in seconds to wait before revealing the topic content underneath the transition video.")]
    public float topicRevealDelay = 0.3f;

    [Header("Input Detection")]
    [Tooltip("Pixels the pointer must move between frames to count as real input. " +
             "Raise this if panel noise keeps waking the kiosk; 20-40 suits most IR/capacitive frames.")]
    public float pointerMoveThreshold = 20f;

    [Tooltip("Log the idle countdown to the console. Turn off for production.")]
    public bool debugIdleTimer = false;

    private float _idleTimer;
    private float _sessionTimer;
    private bool _isTransitioning = false;
    private Tween _revealTween;
    private AppLanguage _currentLanguage = AppLanguage.English;

    private AppState _state = AppState.Idle;
    private Vector2 _lastPointerPosition;
    private bool _hasPointerBaseline = false;
    private float _videoFinishedAt = -1f;

    private void Start()
    {
        if (englishButton != null) englishButton.onClick.AddListener(() => StartSession(AppLanguage.English));
        if (arabicButton != null) arabicButton.onClick.AddListener(() => StartSession(AppLanguage.Arabic));

        if (returnToSelectButton != null)
        {
            returnToSelectButton.onClick.AddListener(ReturnToSelectScreen);
        }

        foreach (TopicData data in topicDatabase)
        {
            TopicData localData = data;

            if (localData.topicButton != null)
            {
                localData.topicButton.onClick.AddListener(() => StartTransitionToTopic(localData));
            }
        }

        if (transitionVideoPlayer != null)
        {
            transitionVideoPlayer.loopPointReached += OnTransitionFinished;

            // A looping transition clip never raises loopPointReached, which would leave
            // _isTransitioning stuck true and soft-lock topic selection.
            if (transitionVideoPlayer.isLooping)
            {
                Debug.LogWarning("[AppFlowManager] Transition VideoPlayer has Loop enabled. " +
                                 "Disabling it so the transition can report completion.", this);
                transitionVideoPlayer.isLooping = false;
            }
        }

        if (idleScreen != null) ShowScreen(idleScreen, 1f);
        if (selectScreen != null) HideScreen(selectScreen);
        if (topicScreen != null) HideScreen(topicScreen);

        // Apply the default language immediately so the menu is never in a mixed
        // state left over from whatever was enabled in the editor.
        ApplyLanguage(_currentLanguage);

        SetState(AppState.Idle);
        _isTransitioning = false;

        ClearTargetRenderTexture();
    }

    private void OnDestroy()
    {
        if (transitionVideoPlayer != null)
        {
            transitionVideoPlayer.loopPointReached -= OnTransitionFinished;
        }
    }

    private void Update()
    {
        bool hasInput = DetectRealInput();

        if (hasInput)
        {
            ResetIdleTimer();
        }

        // Nothing to time out to while we're already on the idle screen.
        if (_state == AppState.Idle) return;

        // Don't yank the screen out from under an in-flight transition.
        if (_isTransitioning) return;

        // --- Hard session ceiling -------------------------------------------
        // Counts down regardless of video playback, so a looping or stalled
        // video can never trap the kiosk on a topic screen forever.
        if (maxSessionTimeout > 0f)
        {
            _sessionTimer -= Time.deltaTime;
            if (_sessionTimer <= 0f)
            {
                if (debugIdleTimer) Debug.Log("[AppFlowManager] Max session timeout reached.", this);
                ReturnToIdle();
                return;
            }
        }

        // --- Normal idle countdown ------------------------------------------
        if (IsTopicVideoActive())
        {
            // Someone is watching. Hold the timer full, but remember when playback ended.
            _idleTimer = idleTimeout;
            _videoFinishedAt = -1f;
        }
        else
        {
            // Short grace period after a video ends before we start counting.
            if (_videoFinishedAt < 0f && _state == AppState.Topic)
            {
                _videoFinishedAt = Time.time;
            }

            if (_state == AppState.Topic && Time.time - _videoFinishedAt < videoFinishedGrace)
            {
                return;
            }

            _idleTimer -= Time.deltaTime;

            if (debugIdleTimer && Mathf.FloorToInt(_idleTimer) != Mathf.FloorToInt(_idleTimer + Time.deltaTime))
            {
                Debug.Log($"[AppFlowManager] Idle in {_idleTimer:F0}s (state: {_state})", this);
            }

            if (_idleTimer <= 0f)
            {
                ReturnToIdle();
            }
        }
    }

    /// <summary>
    /// Position-based input detection. Comparing absolute position between frames is
    /// far more stable than reading delta, which can persist for a frame after touch
    /// release and picks up driver noise from an idle mouse.
    /// </summary>
    private bool DetectRealInput()
    {
        bool hasInput = false;

        Pointer pointer = Pointer.current;
        if (pointer != null)
        {
            Vector2 position = pointer.position.ReadValue();

            if (!_hasPointerBaseline)
            {
                _lastPointerPosition = position;
                _hasPointerBaseline = true;
            }
            else if ((position - _lastPointerPosition).sqrMagnitude >
                     pointerMoveThreshold * pointerMoveThreshold)
            {
                hasInput = true;
            }

            _lastPointerPosition = position;

            // An actual press always counts, however small the movement.
            if (pointer.press.isPressed) hasInput = true;
        }

        if (Keyboard.current != null && Keyboard.current.anyKey.isPressed)
        {
            hasInput = true;
        }

        return hasInput;
    }

    private bool IsTopicVideoActive()
    {
        if (_state != AppState.Topic) return false;
        if (topicMediaPlayer == null || topicMediaPlayer.Control == null) return false;

        // IsFinished() catches players that sit on the last frame still reporting IsPlaying().
        return topicMediaPlayer.Control.IsPlaying() && !topicMediaPlayer.Control.IsFinished();
    }

    private void ResetIdleTimer()
    {
        _idleTimer = idleTimeout;
        _sessionTimer = maxSessionTimeout;
        _videoFinishedAt = -1f;
    }

    private void SetState(AppState newState)
    {
        _state = newState;
        ResetIdleTimer();
    }

    private void ApplyLanguage(AppLanguage language)
    {
        foreach (TopicData data in topicDatabase)
        {
            if (data.textEN != null) data.textEN.SetActive(language == AppLanguage.English);
            if (data.textAR != null) data.textAR.SetActive(language == AppLanguage.Arabic);
        }
    }

    public void StartSession(AppLanguage selectedLanguage)
    {
        _currentLanguage = selectedLanguage;
        ApplyLanguage(_currentLanguage);
        GoToSelectScreen();
    }

    public void GoToSelectScreen()
    {
        _isTransitioning = false;
        SetState(AppState.Select);

        FadeOutScreen(idleScreen);
        FadeInScreen(selectScreen);
    }

    private void StartTransitionToTopic(TopicData topic)
    {
        if (_isTransitioning) return;
        _isTransitioning = true;

        ResetIdleTimer();

        if (selectScreen != null) DisableInteraction(selectScreen);

        string fileName = _currentLanguage == AppLanguage.English ? topic.videoFileNameEN : topic.videoFileNameAR;
        topicMediaPlayer.OpenMedia(MediaPathType.RelativeToStreamingAssetsFolder, fileName, autoPlay: false);

        transitionVideoPlayer.gameObject.SetActive(true);
        transitionVideoPlayer.Play();

        _revealTween = DOVirtual.DelayedCall(topicRevealDelay, () => RevealTopicContent());
    }

    private void RevealTopicContent()
    {
        SetState(AppState.Topic);

        FadeInScreen(topicScreen);

        topicMediaPlayer.Play();

        FadeOutScreen(selectScreen);
    }

    private void OnTransitionFinished(VideoPlayer vp)
    {
        _isTransitioning = false;
        transitionVideoPlayer.gameObject.SetActive(false);

        // The transition froze the countdown; start it clean now that we've landed.
        ResetIdleTimer();
    }

    public void ReturnToSelectScreen()
    {
        _isTransitioning = false;
        SetState(AppState.Select);

        KillRevealTween();
        StopAllVideo();

        if (topicScreen != null && topicScreen.gameObject.activeSelf)
        {
            FadeOutScreen(topicScreen);
        }

        FadeInScreen(selectScreen);

        ClearTargetRenderTexture();
    }

    public void ReturnToIdle()
    {
        // Guard against re-entry from a timer that fires while we're already going home.
        if (_state == AppState.Idle) return;

        _isTransitioning = false;
        SetState(AppState.Idle);

        KillRevealTween();
        StopAllVideo();

        if (topicScreen != null && topicScreen.gameObject.activeSelf)
        {
            FadeOutScreen(topicScreen);
        }
        if (selectScreen != null && selectScreen.gameObject.activeSelf)
        {
            FadeOutScreen(selectScreen);
        }

        FadeInScreen(idleScreen);

        ClearTargetRenderTexture();
    }

    private void KillRevealTween()
    {
        if (_revealTween != null && _revealTween.IsActive())
        {
            _revealTween.Kill();
        }
        _revealTween = null;
    }

    private void StopAllVideo()
    {
        if (topicMediaPlayer != null) topicMediaPlayer.Stop();

        if (transitionVideoPlayer != null)
        {
            transitionVideoPlayer.Stop();
            transitionVideoPlayer.gameObject.SetActive(false);
        }
    }

    private void ClearTargetRenderTexture()
    {
        if (textureToClear == null) return;

        RenderTexture previousActiveRT = RenderTexture.active;
        RenderTexture.active = textureToClear;

        GL.Clear(true, true, Color.clear);

        RenderTexture.active = previousActiveRT;
    }

    private void ShowScreen(CanvasGroup group, float alpha)
    {
        group.DOKill();
        group.gameObject.SetActive(true);
        group.alpha = alpha;
        EnableInteraction(group);
    }

    private void HideScreen(CanvasGroup group)
    {
        group.DOKill();
        group.alpha = 0;
        group.interactable = false;
        group.blocksRaycasts = false;
        group.gameObject.SetActive(false);
    }

    private void FadeInScreen(CanvasGroup group)
    {
        if (group == null) return;
        group.DOKill();
        group.gameObject.SetActive(true);
        group.DOFade(1, fadeDuration).OnStart(() => EnableInteraction(group));
    }

    private void FadeOutScreen(CanvasGroup group)
    {
        if (group == null) return;
        DisableInteraction(group);
        group.DOKill();
        group.DOFade(0, fadeDuration).OnComplete(() => HideScreen(group));
    }

    private void EnableInteraction(CanvasGroup group)
    {
        group.interactable = true;
        group.blocksRaycasts = true;
    }

    private void DisableInteraction(CanvasGroup group)
    {
        group.interactable = false;
        group.blocksRaycasts = false;
    }
}