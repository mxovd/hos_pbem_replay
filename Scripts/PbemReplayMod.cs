using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

class PbemReplayMod : GameModification
{
    private Harmony _harmony;

    public PbemReplayMod(Mod mod) : base(mod)
    {
        Log("Registering pbem_replay...");
    }

    public override void OnModInitialization(Mod mod)
    {
        Log("Initializing pbem_replay...");

        PatchGame();
    }

    public override void OnModUnloaded()
    {
        Log("Unloading pbem_replay...");

        _harmony?.UnpatchAll(_harmony.Id);
    }

    private void PatchGame()
    {
        Log("Patching...");

        _harmony = new Harmony("com.hexofsteel.pbem-replay");
        _harmony.PatchAll();
    }
}

[Serializable]
internal sealed class ReplayRpcAction
{
    public string RpcName;

    public byte[] Payload;
}

[Serializable]
internal sealed class ReplayTurnBatch
{
    public long Sequence;

    public int TurnNumber;

    public string PlayerName;

    public List<ReplayRpcAction> Actions = new List<ReplayRpcAction>();
}

[Serializable]
internal sealed class ReplaySnapshotEnvelope
{
    public Guid SessionId;

    public long LastKnownSequence;

    public byte[] SnapshotBytes;
}

internal static class PbemReplayRuntime
{
    public const string ReplayBatchesKey = "pbem_replay.batches.v1";

    public const string ReplayBaselineSnapshotKey = "pbem_replay.baseline_snapshot.v1";

    private static readonly HashSet<string> ReplayableRpcs = new HashSet<string>(StringComparer.Ordinal)
    {
        "RPC_MoveUnit",
        "RPC_AttackUnit",
        "RPC_KillUnit",
        "RPC_SpawnUnit",
        "RPC_RedeployUnit",
        "RPC_DespawnUnit",
        "RPC_RemoveUnit",
        "RPC_AddUnit",
        "RPC_TransferUnit",
        "RPC_CaptureVP",
        "RPC_EmbarkDisembark",
        "RPC_ShowDamage",
        "RPC_DisplayXP",
        "RPC_DisplayFuelLoss",
        "RPC_DisplayAmmoLoss",
        "RPC_PlaySound",
        "RPC_SyncTile",
        "RPC_SwapUnits"
    };

    private static readonly List<ReplayRpcAction> PendingActionsForCurrentTurn = new List<ReplayRpcAction>();

    private const string ReplaySpeedPrefKey = "pbem_replay.speed.v1";

    private const float ReplaySpeedDefault = 1f;

    private const float ReplaySpeedMin = 1f;

    private const float ReplaySpeedMax = 10f;

    private const float ReplayMoveSettleTimeoutSeconds = 20f;

    private const float ReplayZoomFactor = 0.55f;

    private const float ReplayZoomUpperBound = 28f;

    private static byte[] _currentTurnStartSnapshotBytes;

    private static bool _isReplayingFromSnapshot;

    private static bool _isApplyingAuthoritativeFinalState;

    private static bool _startedReplayCoroutine;

    private static bool _suppressScenarioIntroPanel;

    private static bool _isReplayPromptCoroutineRunning;

    private static bool _didLoadReplaySpeedSetting;

    private static float _replaySpeedSetting = ReplaySpeedDefault;

    private static List<ReplayRpcAction> _pendingReplayActions;

    private static byte[] _authoritativeFinalSnapshotBytes;

    private static List<ReplayTurnBatch> _authoritativeBatches;

    public static bool IsReplaying => _isReplayingFromSnapshot;

    private static float GetReplaySpeedSetting()
    {
        EnsureReplaySpeedSettingLoaded();
        return _replaySpeedSetting;
    }

    private static void SetReplaySpeedSetting(float p_value)
    {
        EnsureReplaySpeedSettingLoaded();
        _replaySpeedSetting = Mathf.Clamp(p_value, ReplaySpeedMin, ReplaySpeedMax);
        PlayerPrefs.SetFloat(ReplaySpeedPrefKey, _replaySpeedSetting);
    }

    private static void EnsureReplaySpeedSettingLoaded()
    {
        if (_didLoadReplaySpeedSetting)
        {
            return;
        }

        _didLoadReplaySpeedSetting = true;
        _replaySpeedSetting = Mathf.Clamp(PlayerPrefs.GetFloat(ReplaySpeedPrefKey, ReplaySpeedDefault), ReplaySpeedMin, ReplaySpeedMax);
    }

    private static string FormatReplaySpeedLabel(float p_speed)
    {
        return "Replay speed: " + p_speed.ToString("0.00") + "x";
    }

    private static float ScaleReplayPause(float p_seconds)
    {
        float speed = Mathf.Max(ReplaySpeedMin, GetReplaySpeedSetting());
        return Mathf.Max(0.01f, p_seconds / speed);
    }

    public static bool TryHandleReplayCameraRecentering(Vector3 p_targetWorldPos, ref float p_duration, ref IEnumerator o_result)
    {
        if (!_isReplayingFromSnapshot)
        {
            return false;
        }

        Camera cam = CameraGO.instance != null ? CameraGO.instance.cam : Camera.main;
        if (cam == null)
        {
            return false;
        }

        Vector3 samplePos = p_targetWorldPos;
        float minVisibleZ = cam.transform.position.z + Mathf.Max(1f, cam.nearClipPlane + 0.5f);
        if (samplePos.z <= minVisibleZ)
        {
            samplePos.z = minVisibleZ;
        }

        Vector3 viewportPos = cam.WorldToViewportPoint(samplePos);
        if (viewportPos.z <= 0f)
        {
            return false;
        }

        // Use an asymmetric safe frame so actions near UI-heavy edges (right/bottom) trigger recenter sooner.
        const float leftSafeMargin = 0.08f;
        const float rightSafeMargin = 0.28f;
        const float bottomSafeMargin = 0.20f;
        const float topSafeMargin = 0.12f;
        bool isInsideFrame = viewportPos.x >= leftSafeMargin
            && viewportPos.x <= 1f - rightSafeMargin
            && viewportPos.y >= bottomSafeMargin
            && viewportPos.y <= 1f - topSafeMargin;
        if (isInsideFrame)
        {
            o_result = CR_Empty();
            return true;
        }

        p_duration = Mathf.Max(p_duration, 0.32f);
        return false;
    }

    private static IEnumerator CR_Empty()
    {
        yield break;
    }

    public static void HandleTurnStart(TurnManager p_turnManager)
    {
        OnTurnSceneStarted();

        if (_isReplayingFromSnapshot)
        {
            TryStartReplayCoroutine(p_turnManager);
            return;
        }

        if (ConsumeAuthoritativeFinalStateAppliedFlag())
        {
            TryShowReplayPrompt(p_turnManager, p_isReplayAgainPrompt: true);
            return;
        }

        TryShowReplayPrompt(p_turnManager, p_isReplayAgainPrompt: false);
    }

    public static void TryHideScenarioIntroPanel(MapGO p_mapGo)
    {
        if (!_suppressScenarioIntroPanel || p_mapGo == null || p_mapGo.startScenario_Panel == null)
        {
            return;
        }

        try
        {
            p_mapGo.startScenario_Panel.SetActive(false);
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Failed to suppress scenario intro panel: " + ex.Message);
        }
    }

    public static bool ShouldCaptureActions()
    {
        if (_isReplayingFromSnapshot || _isApplyingAuthoritativeFinalState)
        {
            return false;
        }

        return GameData.Instance != null && GameData.Instance.isPBEM;
    }

    public static void CaptureRpcCall(string p_rpcName, object[] p_args)
    {
        try
        {
            if (!ShouldCaptureActions())
            {
                return;
            }

            if (string.IsNullOrEmpty(p_rpcName) || !ReplayableRpcs.Contains(p_rpcName))
            {
                return;
            }

            byte[] payload = Utils.ConvertObjectToByteArray(p_args ?? Array.Empty<object>());
            if (payload == null || payload.Length == 0)
            {
                return;
            }

            PendingActionsForCurrentTurn.Add(new ReplayRpcAction
            {
                RpcName = p_rpcName,
                Payload = payload
            });
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Failed to capture RPC call: " + ex.Message);
        }
    }

    public static void OnEndTurn(GameData p_gameData)
    {
        try
        {
            if (p_gameData == null || !p_gameData.isPBEM || p_gameData.sessionID == Guid.Empty)
            {
                PendingActionsForCurrentTurn.Clear();
                return;
            }

            if (!p_gameData.ModDataBag.TryGet(ReplayBatchesKey, out List<ReplayTurnBatch> batches) || batches == null)
            {
                batches = new List<ReplayTurnBatch>();
            }

            if ((!p_gameData.ModDataBag.TryGet(ReplayBaselineSnapshotKey, out byte[] baseline) || baseline == null || baseline.Length == 0) && _currentTurnStartSnapshotBytes != null && _currentTurnStartSnapshotBytes.Length > 0)
            {
                p_gameData.ModDataBag.TrySet(ReplayBaselineSnapshotKey, _currentTurnStartSnapshotBytes, preferKnownOverUnknown: true);
            }

            long nextSequence = batches.Count == 0 ? 1 : batches.Max(b => b.Sequence) + 1;

            if (PendingActionsForCurrentTurn.Count > 0)
            {
                batches.Add(new ReplayTurnBatch
                {
                    Sequence = nextSequence,
                    TurnNumber = p_gameData.map != null ? p_gameData.map.turnCount : 0,
                    PlayerName = TurnManager.currPlayer != null ? TurnManager.currPlayer.Name : string.Empty,
                    Actions = PendingActionsForCurrentTurn
                        .Select(a => new ReplayRpcAction { RpcName = a.RpcName, Payload = a.Payload })
                        .ToList()
                });
            }

            const int maxBatchesToKeep = 256;
            if (batches.Count > maxBatchesToKeep)
            {
                int toRemove = batches.Count - maxBatchesToKeep;
                batches.RemoveRange(0, toRemove);
            }

            p_gameData.ModDataBag.TrySet(ReplayBatchesKey, batches, preferKnownOverUnknown: true);

            ReplaySnapshotEnvelope envelope = new ReplaySnapshotEnvelope
            {
                SessionId = p_gameData.sessionID,
                LastKnownSequence = batches.Count == 0 ? 0 : batches.Max(b => b.Sequence),
                SnapshotBytes = Utils.ConvertObjectToByteArray(p_gameData)
            };

            SaveLocalSnapshotEnvelope(envelope);
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Failed to prepare end-turn replay data: " + ex.Message);
        }
        finally
        {
            PendingActionsForCurrentTurn.Clear();
            _currentTurnStartSnapshotBytes = null;
        }
    }

    public static void OnTurnSceneStarted()
    {
        if (_isReplayingFromSnapshot || _isApplyingAuthoritativeFinalState)
        {
            return;
        }

        GameData current = GameData.Instance;
        if (current == null || !current.isPBEM)
        {
            return;
        }

        try
        {
            _currentTurnStartSnapshotBytes = Utils.ConvertObjectToByteArray(current);

            if ((!current.ModDataBag.TryGet(ReplayBaselineSnapshotKey, out byte[] baseline) || baseline == null || baseline.Length == 0) && _currentTurnStartSnapshotBytes != null && _currentTurnStartSnapshotBytes.Length > 0)
            {
                current.ModDataBag.TrySet(ReplayBaselineSnapshotKey, _currentTurnStartSnapshotBytes, preferKnownOverUnknown: true);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Failed to capture turn-start snapshot: " + ex.Message);
        }
    }

    public static bool TryBootstrapReplayFromLocalSnapshot()
    {
        if (_isApplyingAuthoritativeFinalState)
        {
            _isApplyingAuthoritativeFinalState = false;
            return false;
        }

        if (_isReplayingFromSnapshot)
        {
            return false;
        }

        GameData current = GameData.Instance;
        if (!TryBuildReplayBootstrapData(current, out List<ReplayTurnBatch> authoritativeBatches, out byte[] anchorSnapshotBytes, out List<ReplayRpcAction> pendingActions))
        {
            return false;
        }

        try
        {
            _authoritativeFinalSnapshotBytes = Utils.ConvertObjectToByteArray(current);
            _authoritativeBatches = authoritativeBatches;
            _pendingReplayActions = pendingActions;
            _isReplayingFromSnapshot = true;
            _startedReplayCoroutine = false;
            _suppressScenarioIntroPanel = true;

            GameData snapshotData = (GameData)Utils.ConvertByteArrayToObject(anchorSnapshotBytes);
            if (snapshotData == null)
            {
                ResetReplayState();
                return false;
            }

            snapshotData.ModDataBag.TrySet(ReplayBatchesKey, authoritativeBatches, preferKnownOverUnknown: true);
            if (current.ModDataBag.TryGet(ReplayBaselineSnapshotKey, out byte[] existingBaseline) && existingBaseline != null && existingBaseline.Length > 0)
            {
                snapshotData.ModDataBag.TrySet(ReplayBaselineSnapshotKey, existingBaseline, preferKnownOverUnknown: true);
            }
            GameData.Instance.SetData(snapshotData);

            SceneManager.LoadScene("Game");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Could not bootstrap replay from local snapshot: " + ex.Message);
            ResetReplayState();
            return false;
        }
    }

    private static void TryShowReplayPrompt(TurnManager p_turnManager, bool p_isReplayAgainPrompt)
    {
        if (p_turnManager == null || _isReplayPromptCoroutineRunning || _isReplayingFromSnapshot || _isApplyingAuthoritativeFinalState)
        {
            return;
        }

        if (!CanBootstrapReplayFromLocalSnapshot())
        {
            return;
        }

        _isReplayPromptCoroutineRunning = true;
        p_turnManager.StartCoroutine(CR_ShowReplayPrompt(p_isReplayAgainPrompt));
    }

    private static IEnumerator CR_ShowReplayPrompt(bool p_isReplayAgainPrompt)
    {
        float timeout = 3f;
        while (timeout > 0f && UIManager.instance == null)
        {
            timeout -= Time.unscaledDeltaTime;
            yield return null;
        }

        _isReplayPromptCoroutineRunning = false;

        if (UIManager.instance == null || _isReplayingFromSnapshot || _isApplyingAuthoritativeFinalState || !CanBootstrapReplayFromLocalSnapshot())
        {
            yield break;
        }

        string text = p_isReplayAgainPrompt ? "PBEM replay finished. Replay again?" : "PBEM replay available. Start replay?";
        GameObject promptWindow = UIManager.ShowConfirmationWindow(text, delegate
        {
            TryBootstrapReplayFromLocalSnapshot();
        });
        TryConfigureReplayPromptButtons(promptWindow, p_isReplayAgainPrompt);
    }

    public static void TryStartReplayCoroutine(TurnManager p_turnManager)
    {
        if (!_isReplayingFromSnapshot || _startedReplayCoroutine || p_turnManager == null)
        {
            return;
        }

        _startedReplayCoroutine = true;
        p_turnManager.StartCoroutine(CR_PlayReplayThenRestoreAuthoritativeState());
    }

    private static IEnumerator CR_PlayReplayThenRestoreAuthoritativeState()
    {
        UIManager.isUIOpen = true;
        UIManager.ShowMessage("PBEM replay: playing actions...");

        bool quickMovementBackup = PlayerSettings.Instance.IsQuickMovement;
        bool followAIMovesBackup = PlayerSettings.Instance.FollowAIMoves;
        float timeScaleBackup = Time.timeScale;
        float cameraZoomBackup = -1f;
        float cameraTargetZoomBackup = -1f;
        PlayerSettings.Instance.IsQuickMovement = false;
        PlayerSettings.Instance.FollowAIMoves = true;
        Time.timeScale = GetReplaySpeedSetting();

        CameraGO cameraGo = CameraGO.instance;
        if (cameraGo != null && cameraGo.cam != null)
        {
            cameraZoomBackup = cameraGo.cam.orthographicSize;
            cameraTargetZoomBackup = cameraGo.targetZoom;
            float desiredReplayZoom = Mathf.Clamp(cameraZoomBackup * ReplayZoomFactor, cameraGo.minZoom, Mathf.Min(cameraGo.maxZoom, ReplayZoomUpperBound));
            desiredReplayZoom = Mathf.Min(desiredReplayZoom, cameraZoomBackup);
            cameraGo.targetZoom = desiredReplayZoom;
            cameraGo.cam.orthographicSize = desiredReplayZoom;
        }

        try
        {
            MultiplayerManager manager = MultiplayerManager.Instance;
            if (manager != null)
            {
                foreach (ReplayRpcAction action in _pendingReplayActions ?? new List<ReplayRpcAction>())
                {
                    if (action?.Payload == null || string.IsNullOrEmpty(action.RpcName))
                    {
                        continue;
                    }

                    MethodInfo method = AccessTools.Method(typeof(MultiplayerManager), action.RpcName, new[] { typeof(byte[]) });
                    if (method == null)
                    {
                        continue;
                    }

                    try
                    {
                        method.Invoke(manager, new object[] { action.Payload });
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[pbem_replay] Failed to replay action '" + action.RpcName + "': " + ex.Message);
                    }

                    yield return CR_WaitActionSettle(action.RpcName);
                }
            }
        }
        finally
        {
            Time.timeScale = timeScaleBackup;
            PlayerSettings.Instance.IsQuickMovement = quickMovementBackup;
            PlayerSettings.Instance.FollowAIMoves = followAIMovesBackup;
            if (cameraGo != null && cameraGo.cam != null && cameraZoomBackup > 0f)
            {
                cameraGo.targetZoom = cameraTargetZoomBackup > 0f ? cameraTargetZoomBackup : cameraZoomBackup;
                cameraGo.cam.orthographicSize = cameraZoomBackup;
            }
        }

        UIManager.ShowMessage("PBEM replay finished.");

        if (_authoritativeFinalSnapshotBytes != null && _authoritativeFinalSnapshotBytes.Length > 0)
        {
            try
            {
                GameData authoritative = (GameData)Utils.ConvertByteArrayToObject(_authoritativeFinalSnapshotBytes);
                if (authoritative != null)
                {
                    authoritative.ModDataBag.TrySet(ReplayBatchesKey, _authoritativeBatches ?? new List<ReplayTurnBatch>(), preferKnownOverUnknown: true);
                    GameData.Instance.SetData(authoritative);
                    _isApplyingAuthoritativeFinalState = true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[pbem_replay] Could not restore authoritative PBEM state: " + ex.Message);
            }
        }

        ResetReplayState(keepApplyAuthoritativeFlag: true);
        SceneManager.LoadScene("Game");
    }

    private static IEnumerator CR_WaitActionSettle(string p_rpcName)
    {
        if (p_rpcName == "RPC_MoveUnit")
        {
            float timeout = ReplayMoveSettleTimeoutSeconds;
            while (timeout > 0f)
            {
                UnitGO[] units = UnityEngine.Object.FindObjectsOfType<UnitGO>();
                if (units == null || !units.Any(u => u != null && u.isMoving))
                {
                    break;
                }

                timeout -= Time.unscaledDeltaTime;
                yield return null;
            }

            yield return new WaitForSecondsRealtime(ScaleReplayPause(0.25f));
            yield break;
        }

        yield return new WaitForSecondsRealtime(ScaleReplayPause(GetReplayActionPauseSeconds(p_rpcName)));
    }

    private static float GetReplayActionPauseSeconds(string p_rpcName)
    {
        switch (p_rpcName)
        {
            case "RPC_AttackUnit":
            case "RPC_KillUnit":
                return 0.75f;
            case "RPC_ShowDamage":
            case "RPC_DisplayXP":
            case "RPC_DisplayFuelLoss":
            case "RPC_DisplayAmmoLoss":
                return 0.4f;
            case "RPC_PlaySound":
                return 0.05f;
            case "RPC_SyncTile":
                return 0.12f;
            default:
                return 0.3f;
        }
    }

    private static void ResetReplayState(bool keepApplyAuthoritativeFlag = false)
    {
        _isReplayingFromSnapshot = false;
        _startedReplayCoroutine = false;
        _suppressScenarioIntroPanel = false;
        _isReplayPromptCoroutineRunning = false;
        _pendingReplayActions = null;
        _authoritativeFinalSnapshotBytes = null;
        _authoritativeBatches = null;

        if (!keepApplyAuthoritativeFlag)
        {
            _isApplyingAuthoritativeFinalState = false;
        }
    }

    private static bool CanBootstrapReplayFromLocalSnapshot()
    {
        if (_isReplayingFromSnapshot || _isApplyingAuthoritativeFinalState)
        {
            return false;
        }

        return TryBuildReplayBootstrapData(GameData.Instance, out _, out _, out _);
    }

    private static bool ConsumeAuthoritativeFinalStateAppliedFlag()
    {
        if (!_isApplyingAuthoritativeFinalState)
        {
            return false;
        }

        _isApplyingAuthoritativeFinalState = false;
        return true;
    }

    private static bool TryBuildReplayBootstrapData(GameData p_current, out List<ReplayTurnBatch> o_authoritativeBatches, out byte[] o_anchorSnapshotBytes, out List<ReplayRpcAction> o_pendingActions)
    {
        o_authoritativeBatches = null;
        o_anchorSnapshotBytes = null;
        o_pendingActions = null;

        if (p_current == null || !p_current.isPBEM || p_current.sessionID == Guid.Empty)
        {
            return false;
        }

        if (!p_current.ModDataBag.TryGet(ReplayBatchesKey, out List<ReplayTurnBatch> authoritativeBatches) || authoritativeBatches == null || authoritativeBatches.Count == 0)
        {
            return false;
        }

        long anchorSequence = 0;
        byte[] anchorSnapshotBytes = null;

        if (TryLoadLocalSnapshotEnvelope(p_current.sessionID, out ReplaySnapshotEnvelope envelope) && envelope?.SnapshotBytes != null && envelope.SnapshotBytes.Length > 0)
        {
            anchorSequence = envelope.LastKnownSequence;
            anchorSnapshotBytes = envelope.SnapshotBytes;
        }
        else if (p_current.ModDataBag.TryGet(ReplayBaselineSnapshotKey, out byte[] baselineBytes) && baselineBytes != null && baselineBytes.Length > 0)
        {
            anchorSequence = 0;
            anchorSnapshotBytes = baselineBytes;
        }
        else
        {
            return false;
        }

        long maxSequence = authoritativeBatches.Max(b => b.Sequence);
        if (maxSequence <= anchorSequence)
        {
            return false;
        }

        List<ReplayTurnBatch> pendingBatches = authoritativeBatches
            .Where(b => b.Sequence > anchorSequence)
            .OrderBy(b => b.Sequence)
            .ToList();

        List<ReplayRpcAction> pendingActions = pendingBatches
            .SelectMany(b => b.Actions ?? new List<ReplayRpcAction>())
            .Where(a => a != null && !string.IsNullOrEmpty(a.RpcName) && a.Payload != null)
            .ToList();

        if (pendingActions.Count == 0)
        {
            return false;
        }

        o_authoritativeBatches = authoritativeBatches;
        o_anchorSnapshotBytes = anchorSnapshotBytes;
        o_pendingActions = pendingActions;
        return true;
    }

    private static void TryConfigureReplayPromptButtons(GameObject p_promptWindow, bool p_isReplayAgainPrompt)
    {
        if (p_promptWindow == null || !p_promptWindow.TryGetComponent<ConfirmationWindowGO>(out ConfirmationWindowGO confirmation))
        {
            return;
        }

        TrySetButtonLabel(confirmation.yes_button, p_isReplayAgainPrompt ? "Replay" : "Start");
        TrySetButtonLabel(confirmation.no_button, p_isReplayAgainPrompt ? "Close" : "Skip");
        TryAttachReplaySpeedSlider(p_promptWindow, confirmation);
    }

    private static void TrySetButtonLabel(Button p_button, string p_text)
    {
        if (p_button == null || string.IsNullOrEmpty(p_text))
        {
            return;
        }

        TextMeshProUGUI label = p_button.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
        if (label != null)
        {
            label.text = p_text;
        }
    }

    private static void TryAttachReplaySpeedSlider(GameObject p_promptWindow, ConfirmationWindowGO p_confirmation)
    {
        if (p_promptWindow == null || p_confirmation == null)
        {
            return;
        }

        if (p_promptWindow.transform.Find("pbem_replay_speed_root") != null)
        {
            return;
        }

        EnsureReplaySpeedSettingLoaded();

        RectTransform parent = p_confirmation.description_text != null ? p_confirmation.description_text.rectTransform.parent as RectTransform : p_promptWindow.GetComponent<RectTransform>();
        if (parent == null)
        {
            return;
        }

        GameObject root = new GameObject("pbem_replay_speed_root", typeof(RectTransform));
        root.transform.SetParent(parent, worldPositionStays: false);
        RectTransform rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.12f, 0.22f);
        rootRect.anchorMax = new Vector2(0.88f, 0.46f);
        rootRect.offsetMin = Vector2.zero;
        rootRect.offsetMax = Vector2.zero;

        TextMeshProUGUI label = CreateReplaySpeedLabel(root.transform, p_confirmation.description_text);
        Slider slider = CreateReplaySpeedSlider(root.transform);
        if (slider == null)
        {
            return;
        }

        slider.minValue = ReplaySpeedMin;
        slider.maxValue = ReplaySpeedMax;
        slider.wholeNumbers = false;
        slider.SetValueWithoutNotify(_replaySpeedSetting);
        if (label != null)
        {
            label.text = FormatReplaySpeedLabel(_replaySpeedSetting);
        }

        slider.onValueChanged.AddListener(delegate (float p_value)
        {
            SetReplaySpeedSetting(p_value);
            if (label != null)
            {
                label.text = FormatReplaySpeedLabel(_replaySpeedSetting);
            }
        });
    }

    private static TextMeshProUGUI CreateReplaySpeedLabel(Transform p_parent, TextMeshProUGUI p_reference)
    {
        GameObject labelGo = new GameObject("ReplaySpeedLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelGo.transform.SetParent(p_parent, worldPositionStays: false);

        RectTransform labelRect = labelGo.GetComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0f, 0.66f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.offsetMin = new Vector2(0f, 2f);
        labelRect.offsetMax = Vector2.zero;

        TextMeshProUGUI label = labelGo.GetComponent<TextMeshProUGUI>();
        label.alignment = TextAlignmentOptions.Center;
        label.enableWordWrapping = false;
        label.fontSize = p_reference != null ? Mathf.Max(18f, p_reference.fontSize * 0.7f) : 24f;
        label.color = p_reference != null ? p_reference.color : Color.white;
        if (p_reference != null)
        {
            label.font = p_reference.font;
            label.fontSharedMaterial = p_reference.fontSharedMaterial;
        }

        return label;
    }

    private static Slider CreateReplaySpeedSlider(Transform p_parent)
    {
        GameObject sliderGo = new GameObject("ReplaySpeedSlider", typeof(RectTransform), typeof(Slider));
        sliderGo.transform.SetParent(p_parent, worldPositionStays: false);

        RectTransform sliderRect = sliderGo.GetComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0f, 0.04f);
        sliderRect.anchorMax = new Vector2(1f, 0.42f);
        sliderRect.offsetMin = Vector2.zero;
        sliderRect.offsetMax = new Vector2(0f, -2f);

        Slider slider = sliderGo.GetComponent<Slider>();
        slider.direction = Slider.Direction.LeftToRight;

        Image background = CreateSliderImage(sliderGo.transform, "Background", new Color(0f, 0f, 0f, 0.45f));
        RectTransform backgroundRect = background.rectTransform;
        backgroundRect.anchorMin = new Vector2(0f, 0.2f);
        backgroundRect.anchorMax = new Vector2(1f, 0.8f);
        backgroundRect.offsetMin = new Vector2(0f, 0f);
        backgroundRect.offsetMax = new Vector2(0f, 0f);

        GameObject fillAreaGo = new GameObject("Fill Area", typeof(RectTransform));
        fillAreaGo.transform.SetParent(sliderGo.transform, worldPositionStays: false);
        RectTransform fillAreaRect = fillAreaGo.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0f, 0.2f);
        fillAreaRect.anchorMax = new Vector2(1f, 0.8f);
        fillAreaRect.offsetMin = new Vector2(8f, 0f);
        fillAreaRect.offsetMax = new Vector2(-8f, 0f);

        Image fill = CreateSliderImage(fillAreaGo.transform, "Fill", new Color(0.2f, 0.72f, 1f, 0.95f));
        RectTransform fillRect = fill.rectTransform;
        fillRect.anchorMin = new Vector2(0f, 0f);
        fillRect.anchorMax = new Vector2(1f, 1f);
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        GameObject handleSlideAreaGo = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleSlideAreaGo.transform.SetParent(sliderGo.transform, worldPositionStays: false);
        RectTransform handleSlideAreaRect = handleSlideAreaGo.GetComponent<RectTransform>();
        handleSlideAreaRect.anchorMin = new Vector2(0f, 0f);
        handleSlideAreaRect.anchorMax = new Vector2(1f, 1f);
        handleSlideAreaRect.offsetMin = new Vector2(8f, 0f);
        handleSlideAreaRect.offsetMax = new Vector2(-8f, 0f);

        Image handle = CreateSliderImage(handleSlideAreaGo.transform, "Handle", new Color(1f, 1f, 1f, 0.95f));
        RectTransform handleRect = handle.rectTransform;
        handleRect.sizeDelta = new Vector2(16f, 28f);

        slider.fillRect = fillRect;
        slider.handleRect = handleRect;
        slider.targetGraphic = handle;

        return slider;
    }

    private static Image CreateSliderImage(Transform p_parent, string p_name, Color p_color)
    {
        GameObject imageGo = new GameObject(p_name, typeof(RectTransform), typeof(Image));
        imageGo.transform.SetParent(p_parent, worldPositionStays: false);
        Image image = imageGo.GetComponent<Image>();
        image.color = p_color;
        return image;
    }

    private static string GetSnapshotPath(Guid p_sessionId)
    {
        string root = Path.Combine(Application.persistentDataPath, "pbem_replay");
        Directory.CreateDirectory(root);
        return Path.Combine(root, p_sessionId.ToString("N") + "_" + PlayerSettings.Instance.UserId.ToString("N") + ".bin");
    }

    private static void SaveLocalSnapshotEnvelope(ReplaySnapshotEnvelope p_envelope)
    {
        if (p_envelope == null || p_envelope.SessionId == Guid.Empty || p_envelope.SnapshotBytes == null)
        {
            return;
        }

        byte[] bytes = Utils.ConvertObjectToByteArray(p_envelope);
        File.WriteAllBytes(GetSnapshotPath(p_envelope.SessionId), bytes);
    }

    private static bool TryLoadLocalSnapshotEnvelope(Guid p_sessionId, out ReplaySnapshotEnvelope o_envelope)
    {
        o_envelope = null;
        string path = GetSnapshotPath(p_sessionId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            o_envelope = (ReplaySnapshotEnvelope)Utils.ConvertByteArrayToObject(bytes);
            if (o_envelope == null || o_envelope.SessionId != p_sessionId)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}

[HarmonyPatch(typeof(MultiplayerManager))]
internal static class PbemReplayRpcCapturePatches
{
    [HarmonyPatch("RunRPC", new[] { typeof(string), typeof(string), typeof(object[]) })]
    [HarmonyPrefix]
    private static void RunRpcToCodePrefix(string p_RPCname, object[] p_args)
    {
        PbemReplayRuntime.CaptureRpcCall(p_RPCname, p_args);
    }

    [HarmonyPatch("RunRPC", new[] { typeof(string), typeof(Photon.Pun.RpcTarget), typeof(object[]) })]
    [HarmonyPrefix]
    private static void RunRpcToTargetPrefix(string p_RPCname, object[] p_args)
    {
        PbemReplayRuntime.CaptureRpcCall(p_RPCname, p_args);
    }

}

[HarmonyPatch(typeof(ServerGameService), "EndTurn")]
internal static class PbemReplayEndTurnPatch
{
    [HarmonyPrefix]
    private static void Prefix(GameData p_gameData, CancellationToken p_ct)
    {
        PbemReplayRuntime.OnEndTurn(p_gameData);
    }
}

[HarmonyPatch(typeof(TurnManager), "Start")]
internal static class PbemReplayTurnStartPatch
{
    [HarmonyPostfix]
    private static void Postfix(TurnManager __instance)
    {
        PbemReplayRuntime.HandleTurnStart(__instance);
    }
}

[HarmonyPatch(typeof(MapGO), "Awake")]
internal static class PbemReplayScenarioIntroPanelPatch
{
    [HarmonyPostfix]
    private static void Postfix(MapGO __instance)
    {
        PbemReplayRuntime.TryHideScenarioIntroPanel(__instance);
    }
}

[HarmonyPatch(typeof(UIManager), "Start")]
internal static class PbemReplayUiStartPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        PbemReplayRuntime.TryHideScenarioIntroPanel(MapGO.instance);
    }
}

[HarmonyPatch(typeof(UIManager), "CenterCameraOnUnitCoroutine")]
internal static class PbemReplayCameraRecenteringPatch
{
    [HarmonyPrefix]
    private static bool Prefix(Vector3 pos2, ref float duration, ref IEnumerator __result)
    {
        if (PbemReplayRuntime.TryHandleReplayCameraRecentering(pos2, ref duration, ref __result))
        {
            return false;
        }

        return true;
    }
}
