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

    private const string ReplayPlayersWithLocalSnapshotKey = "pbem_replay.players_with_local_snapshot.v1";

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
        "RPC_SwapUnits",
        "RPC_WarDeclaration",
        "RPC_AllianceMade",
        "RPC_SignedPeace",
        "RPC_SyncPlayerData"
    };

    private static readonly List<ReplayRpcAction> PendingActionsForCurrentTurn = new List<ReplayRpcAction>();

    private const string ReplaySpeedPrefKey = "pbem_replay.speed.v1";

    private const float ReplaySpeedDefault = 1f;

    private const float ReplaySpeedMin = 1f;

    private const float ReplaySpeedMax = 10f;

    private const float ReplayMoveSettleTimeoutSeconds = 20f;

    private const float ReplayZoomFactor = 0.55f;

    private const float ReplayZoomUpperBound = 28f;

    private const float ReplayHiddenScale = 0.0001f;

    private const int ReplayMinBatchesToKeep = 3;

    private const int ReplayMaxBatchesCap = 8;

    private static byte[] _currentTurnStartSnapshotBytes;

    private static bool _isReplayingFromSnapshot;

    private static bool _isApplyingAuthoritativeFinalState;

    private static bool _startedReplayCoroutine;

    private static bool _suppressScenarioIntroPanel;

    private static bool _isReplayPromptCoroutineRunning;

    private static bool _didLoadReplaySpeedSetting;

    private static float _replaySpeedSetting = ReplaySpeedDefault;

    private static readonly Dictionary<string, int> ReplayFogSuppressedUnits = new Dictionary<string, int>(StringComparer.Ordinal);

    private static readonly Dictionary<string, Vector3> ReplayFogSuppressedScales = new Dictionary<string, Vector3>(StringComparer.Ordinal);

    private static List<ReplayRpcAction> _pendingReplayActions;

    private static byte[] _authoritativeFinalSnapshotBytes;

    private static List<ReplayTurnBatch> _authoritativeBatches;

    private static bool _didApplyReplayRuntimeOverrides;

    private static bool _replayQuickMovementBackup;

    private static bool _replayFollowAIMovesBackup;

    private static float _replayTimeScaleBackup = 1f;

    private static CameraGO _replayCameraBackupSource;

    private static float _replayCameraZoomBackup = -1f;

    private static float _replayCameraTargetZoomBackup = -1f;

    private static bool _isRestoringAuthoritativeState;

    private static string _replayViewerPlayerName;

    private static bool _didApplyReplayViewerPerspective;

    private static bool _didResolveInputGetKeyDownMethod;

    private static MethodInfo _inputGetKeyDownMethod;

    public static bool IsReplaying => _isReplayingFromSnapshot;

    private static bool ShouldSuppressReplayBootstrapPanels()
    {
        return _isReplayingFromSnapshot || _suppressScenarioIntroPanel;
    }

    private static bool TryParseSyncAction(object p_value, out MultiplayerManager.SyncActions o_syncAction)
    {
        o_syncAction = default;
        if (p_value is MultiplayerManager.SyncActions typed)
        {
            o_syncAction = typed;
            return true;
        }

        if (p_value is int boxedInt && Enum.IsDefined(typeof(MultiplayerManager.SyncActions), boxedInt))
        {
            o_syncAction = (MultiplayerManager.SyncActions)boxedInt;
            return true;
        }

        if (p_value is string text && Enum.TryParse(text, ignoreCase: true, out MultiplayerManager.SyncActions parsed))
        {
            o_syncAction = parsed;
            return true;
        }

        return false;
    }

    private static bool ShouldCaptureSyncPlayerDataArgs(object[] p_args)
    {
        if (p_args == null || p_args.Length < 2 || !TryParseSyncAction(p_args[1], out MultiplayerManager.SyncActions syncAction))
        {
            return false;
        }

        return syncAction == MultiplayerManager.SyncActions.PLAYER_DIPLOMACY
            || syncAction == MultiplayerManager.SyncActions.PLAYER_FACTION_CHANGE;
    }

    private static bool ShouldRefreshFogAfterReplayAction(ReplayRpcAction p_action)
    {
        if (p_action == null || string.IsNullOrEmpty(p_action.RpcName))
        {
            return false;
        }

        switch (p_action.RpcName)
        {
            case "RPC_WarDeclaration":
            case "RPC_AllianceMade":
            case "RPC_SignedPeace":
                return true;
            case "RPC_SyncPlayerData":
                try
                {
                    if (p_action.Payload == null || p_action.Payload.Length == 0)
                    {
                        return false;
                    }

                    object[] payload = (object[])Utils.ConvertByteArrayToObject(p_action.Payload);
                    if (payload == null || payload.Length < 2 || !TryParseSyncAction(payload[1], out MultiplayerManager.SyncActions syncAction))
                    {
                        return false;
                    }

                    return syncAction == MultiplayerManager.SyncActions.PLAYER_DIPLOMACY
                        || syncAction == MultiplayerManager.SyncActions.PLAYER_FACTION_CHANGE;
                }
                catch
                {
                    return false;
                }
            default:
                return false;
        }
    }

    private static string ResolveReplayViewerPlayerName(GameData p_gameData)
    {
        if (p_gameData == null)
        {
            return string.Empty;
        }

        if (p_gameData.TryFindLocalPlayer(out Player localPlayer) && localPlayer != null && !string.IsNullOrEmpty(localPlayer.Name))
        {
            return localPlayer.Name;
        }

        if (TurnManager.humanPlayer != null && !string.IsNullOrEmpty(TurnManager.humanPlayer.Name) && p_gameData.TryFindPlayerByName(TurnManager.humanPlayer.Name, out _))
        {
            return TurnManager.humanPlayer.Name;
        }

        if (TurnManager.currPlayer != null && !string.IsNullOrEmpty(TurnManager.currPlayer.Name) && p_gameData.TryFindPlayerByName(TurnManager.currPlayer.Name, out _))
        {
            return TurnManager.currPlayer.Name;
        }

        return string.Empty;
    }

    private static bool TryGetReplayViewerPlayer(out Player o_viewer)
    {
        o_viewer = null;
        GameData current = GameData.Instance;
        if (current == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(_replayViewerPlayerName) && current.TryFindPlayerByName(_replayViewerPlayerName, out Player namedViewer) && namedViewer != null)
        {
            o_viewer = namedViewer;
            return true;
        }

        if (current.TryFindLocalPlayer(out Player localViewer) && localViewer != null)
        {
            o_viewer = localViewer;
            return true;
        }

        if (TurnManager.humanPlayer != null)
        {
            o_viewer = TurnManager.humanPlayer;
            return true;
        }

        if (TurnManager.currPlayer != null)
        {
            o_viewer = TurnManager.currPlayer;
            return true;
        }

        return false;
    }

    private static void RebuildReplayFogForViewer()
    {
        if (GameData.Instance?.map == null)
        {
            return;
        }

        MapGO.PlaceFogOfWarEverywhere();
        MapGO.RemoveFogOfWarAroundVictoryPoint();
        MapGO.RemoveBordersFOW();
        MapGO.RemoveFogOfWarAroundUnits();

        if (MainMenu.isLoadedGame)
        {
            MapGO.RemovePlayerSavedFOW();
        }

        if (MapGO.instance != null)
        {
            MapGO.instance.DrawBordersOnMap();
        }
    }

    private static void EnsureReplayViewerPerspective()
    {
        if (!_isReplayingFromSnapshot || _didApplyReplayViewerPerspective)
        {
            return;
        }

        if (!TryGetReplayViewerPlayer(out Player viewer) || viewer == null)
        {
            return;
        }

        TurnManager.currPlayer = viewer;
        TurnManager.humanPlayer = viewer;
        _didApplyReplayViewerPerspective = true;

        try
        {
            RebuildReplayFogForViewer();
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Failed to rebuild fog for replay viewer: " + ex.Message);
        }
    }

    public static void TryPrepareReplayViewerPerspectiveForTurnStart()
    {
        if (!_isReplayingFromSnapshot)
        {
            return;
        }

        if (!TryGetReplayViewerPlayer(out Player viewer) || viewer == null)
        {
            return;
        }

        TurnManager.currPlayer = viewer;
        TurnManager.humanPlayer = viewer;
    }

    public static bool TrySuppressReplayBootstrapPanels(UIManager p_uiManager, TurnManager p_turnManager)
    {
        if (!ShouldSuppressReplayBootstrapPanels())
        {
            return false;
        }

        TryHideScenarioIntroPanel(MapGO.instance);

        try
        {
            if (p_uiManager != null && p_uiManager.previousTurnSummary_panel != null)
            {
                p_uiManager.previousTurnSummary_panel.SetActive(false);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Failed to suppress previous-turn summary panel: " + ex.Message);
        }

        try
        {
            if (p_turnManager != null && p_turnManager.endTurn_Panel != null)
            {
                p_turnManager.endTurn_Panel.SetActive(false);
                if (GameData.Instance != null)
                {
                    GameData.Instance.endOfTurnPanelOpen = false;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Failed to suppress end-turn panel: " + ex.Message);
        }

        return true;
    }

    public static bool TryAbortReplayFromEscape()
    {
        if (!_isReplayingFromSnapshot || _isRestoringAuthoritativeState || !IsEscapePressedThisFrame())
        {
            return false;
        }

        RestoreAuthoritativeStateAndReload();
        return true;
    }

    private static bool IsEscapePressedThisFrame()
    {
        if (!_didResolveInputGetKeyDownMethod)
        {
            _didResolveInputGetKeyDownMethod = true;
            Type inputType = Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule") ?? Type.GetType("UnityEngine.Input, UnityEngine");
            if (inputType != null)
            {
                _inputGetKeyDownMethod = AccessTools.Method(inputType, "GetKeyDown", new[] { typeof(KeyCode) });
            }
        }

        if (_inputGetKeyDownMethod == null)
        {
            return false;
        }

        try
        {
            object result = _inputGetKeyDownMethod.Invoke(null, new object[] { KeyCode.Escape });
            return result is bool pressed && pressed;
        }
        catch
        {
            return false;
        }
    }

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

    private static float ScaleReplayPause(float p_seconds)
    {
        float speed = Mathf.Max(ReplaySpeedMin, GetReplaySpeedSetting());
        return Mathf.Max(0.01f, p_seconds / speed);
    }

    private static string FormatReplaySpeedLabel(float p_speed)
    {
        return "Replay speed: " + p_speed.ToString("0.00") + "x";
    }

    private static void ApplyReplayRuntimeOverrides()
    {
        if (_didApplyReplayRuntimeOverrides)
        {
            return;
        }

        _didApplyReplayRuntimeOverrides = true;
        _replayQuickMovementBackup = PlayerSettings.Instance.IsQuickMovement;
        _replayFollowAIMovesBackup = PlayerSettings.Instance.FollowAIMoves;
        _replayTimeScaleBackup = Time.timeScale;
        _replayCameraBackupSource = CameraGO.instance;
        _replayCameraZoomBackup = -1f;
        _replayCameraTargetZoomBackup = -1f;

        PlayerSettings.Instance.IsQuickMovement = false;
        PlayerSettings.Instance.FollowAIMoves = true;
        Time.timeScale = GetReplaySpeedSetting();

        CameraGO cameraGo = _replayCameraBackupSource;
        if (cameraGo != null && cameraGo.cam != null)
        {
            _replayCameraZoomBackup = cameraGo.cam.orthographicSize;
            _replayCameraTargetZoomBackup = cameraGo.targetZoom;
            float desiredReplayZoom = Mathf.Clamp(_replayCameraZoomBackup * ReplayZoomFactor, cameraGo.minZoom, Mathf.Min(cameraGo.maxZoom, ReplayZoomUpperBound));
            desiredReplayZoom = Mathf.Min(desiredReplayZoom, _replayCameraZoomBackup);
            cameraGo.targetZoom = desiredReplayZoom;
            cameraGo.cam.orthographicSize = desiredReplayZoom;
        }
    }

    private static void RestoreReplayRuntimeOverrides()
    {
        if (!_didApplyReplayRuntimeOverrides)
        {
            return;
        }

        _didApplyReplayRuntimeOverrides = false;
        Time.timeScale = _replayTimeScaleBackup;

        if (PlayerSettings.Instance != null)
        {
            PlayerSettings.Instance.IsQuickMovement = _replayQuickMovementBackup;
            PlayerSettings.Instance.FollowAIMoves = _replayFollowAIMovesBackup;
        }

        CameraGO cameraGo = _replayCameraBackupSource != null ? _replayCameraBackupSource : CameraGO.instance;
        if (cameraGo != null && cameraGo.cam != null && _replayCameraZoomBackup > 0f)
        {
            cameraGo.targetZoom = _replayCameraTargetZoomBackup > 0f ? _replayCameraTargetZoomBackup : _replayCameraZoomBackup;
            cameraGo.cam.orthographicSize = _replayCameraZoomBackup;
        }

        _replayCameraBackupSource = null;
        _replayCameraZoomBackup = -1f;
        _replayCameraTargetZoomBackup = -1f;
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

    public static void TryApplyFogSafeMoveVisibility(MultiplayerManager p_manager, byte[] p_bytes)
    {
        if (!_isReplayingFromSnapshot || p_manager == null || p_bytes == null || p_bytes.Length == 0)
        {
            return;
        }

        try
        {
            object[] payload = (object[])Utils.ConvertByteArrayToObject(p_bytes);
            if (payload == null || payload.Length < 2 || !(payload[0] is Unit rpcUnit) || !(payload[1] is List<Tile.Coordinates> pathCoords) || pathCoords.Count < 2)
            {
                return;
            }

            EnsureReplayViewerPerspective();

            if (!TryGetReplayViewerPlayer(out Player viewer))
            {
                return;
            }

            if (viewer == null || GameData.Instance?.map == null)
            {
                return;
            }

            if (!GameData.Instance.TryFindPlayerByName(rpcUnit.OwnerName, out Player ownerPlayer) || ownerPlayer == null || viewer.IsAlliedWith(ownerPlayer))
            {
                return;
            }

            Unit liveUnit = ownerPlayer.ListOfUnits.FirstOrDefault(u => u != null && u.ID == rpcUnit.ID);
            UnitGO unitGO = liveUnit?.unitGO;
            if (unitGO == null || unitGO.gameObject == null)
            {
                return;
            }

            int firstVisibleIndex = GetFirstVisiblePathIndex(pathCoords);
            List<Vector2> pathWorldPoints = BuildPathWorldPoints(pathCoords);
            string suppressionKey = BuildFogSuppressionKey(ownerPlayer.Name, liveUnit.ID);
            if (firstVisibleIndex == 0)
            {
                ReplayFogSuppressedUnits.Remove(suppressionKey);
                RestoreFogSuppressedVisual(suppressionKey, unitGO);
                return;
            }

            int generation = 1;
            if (ReplayFogSuppressedUnits.TryGetValue(suppressionKey, out int currentGeneration))
            {
                generation = currentGeneration + 1;
            }
            ReplayFogSuppressedUnits[suppressionKey] = generation;
            ApplyFogSuppressedVisual(suppressionKey, unitGO);

            p_manager.StartCoroutine(CR_KeepUnitHiddenUntilVisible(suppressionKey, generation, unitGO, pathWorldPoints, firstVisibleIndex));
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Failed to apply fog-safe move visibility: " + ex.Message);
        }
    }

    private static int GetFirstVisiblePathIndex(List<Tile.Coordinates> p_pathCoords)
    {
        if (p_pathCoords == null || p_pathCoords.Count == 0 || GameData.Instance?.map == null)
        {
            return -1;
        }

        for (int i = 0; i < p_pathCoords.Count; i++)
        {
            Tile.Coordinates coords = p_pathCoords[i];
            Tile tile = GameData.Instance.map.TilesTable[coords.X, coords.Y];
            if (tile?.tileGO != null && !tile.tileGO.isInFogOfWar)
            {
                return i;
            }
        }

        return -1;
    }

    private static string BuildFogSuppressionKey(string p_ownerName, int p_unitId)
    {
        return (p_ownerName ?? string.Empty) + "#" + p_unitId;
    }

    private static List<Vector2> BuildPathWorldPoints(List<Tile.Coordinates> p_pathCoords)
    {
        List<Vector2> points = new List<Vector2>();
        if (p_pathCoords == null || GameData.Instance?.map == null)
        {
            return points;
        }

        foreach (Tile.Coordinates coords in p_pathCoords)
        {
            Tile tile = GameData.Instance.map.TilesTable[coords.X, coords.Y];
            if (tile?.tileGO == null)
            {
                continue;
            }

            Vector3 pos = tile.tileGO.transform.position;
            points.Add(new Vector2(pos.x, pos.y));
        }

        return points;
    }

    private static int GetNearestPathIndex(List<Vector2> p_pathWorldPoints, Vector2 p_worldPos)
    {
        if (p_pathWorldPoints == null || p_pathWorldPoints.Count == 0)
        {
            return -1;
        }

        int bestIndex = 0;
        float bestDistanceSq = float.MaxValue;
        for (int i = 0; i < p_pathWorldPoints.Count; i++)
        {
            float distSq = (p_pathWorldPoints[i] - p_worldPos).sqrMagnitude;
            if (distSq < bestDistanceSq)
            {
                bestDistanceSq = distSq;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static bool IsSuppressionCurrent(string p_suppressionKey, int p_generation)
    {
        return ReplayFogSuppressedUnits.TryGetValue(p_suppressionKey, out int currentGeneration) && currentGeneration == p_generation;
    }

    private static void ApplyFogSuppressedVisual(string p_suppressionKey, UnitGO p_unitGO)
    {
        if (p_unitGO == null || p_unitGO.transform == null)
        {
            return;
        }

        if (!ReplayFogSuppressedScales.ContainsKey(p_suppressionKey))
        {
            ReplayFogSuppressedScales[p_suppressionKey] = p_unitGO.transform.localScale;
        }

        Vector3 originalScale = ReplayFogSuppressedScales[p_suppressionKey];
        float x = Mathf.Sign(originalScale.x);
        float y = Mathf.Sign(originalScale.y);
        float z = Mathf.Sign(originalScale.z);
        if (x == 0f) x = 1f;
        if (y == 0f) y = 1f;
        if (z == 0f) z = 1f;

        p_unitGO.transform.localScale = new Vector3(x * ReplayHiddenScale, y * ReplayHiddenScale, z * ReplayHiddenScale);
    }

    private static void RestoreFogSuppressedVisual(string p_suppressionKey, UnitGO p_unitGO)
    {
        if (!ReplayFogSuppressedScales.TryGetValue(p_suppressionKey, out Vector3 originalScale))
        {
            return;
        }

        if (p_unitGO != null && p_unitGO.transform != null)
        {
            p_unitGO.transform.localScale = originalScale;
        }

        ReplayFogSuppressedScales.Remove(p_suppressionKey);
    }

    private static void CompleteSuppressionIfCurrent(string p_suppressionKey, int p_generation)
    {
        if (IsSuppressionCurrent(p_suppressionKey, p_generation))
        {
            ReplayFogSuppressedUnits.Remove(p_suppressionKey);
            ReplayFogSuppressedScales.Remove(p_suppressionKey);
        }
    }

    private static IEnumerator CR_KeepUnitHiddenUntilVisible(string p_suppressionKey, int p_generation, UnitGO p_unitGO, List<Vector2> p_pathWorldPoints, int p_firstVisibleIndex)
    {
        if (p_unitGO == null || p_unitGO.gameObject == null)
        {
            CompleteSuppressionIfCurrent(p_suppressionKey, p_generation);
            yield break;
        }

        while (_isReplayingFromSnapshot && p_unitGO != null && p_unitGO.gameObject != null)
        {
            if (!IsSuppressionCurrent(p_suppressionKey, p_generation))
            {
                yield break;
            }

            bool shouldHide = false;
            if (p_firstVisibleIndex < 0)
            {
                shouldHide = p_unitGO.isMoving || p_unitGO.tileGO == null || p_unitGO.tileGO.isInFogOfWar;
            }
            else
            {
                Vector3 unitPos3 = p_unitGO.transform.position;
                int nearestIndex = GetNearestPathIndex(p_pathWorldPoints, new Vector2(unitPos3.x, unitPos3.y));
                shouldHide = nearestIndex < 0 || nearestIndex < p_firstVisibleIndex;
            }

            if (shouldHide)
            {
                ApplyFogSuppressedVisual(p_suppressionKey, p_unitGO);

                yield return null;
                continue;
            }

            break;
        }

        if (p_unitGO != null && p_unitGO.gameObject != null && IsSuppressionCurrent(p_suppressionKey, p_generation))
        {
            RestoreFogSuppressedVisual(p_suppressionKey, p_unitGO);
        }

        CompleteSuppressionIfCurrent(p_suppressionKey, p_generation);
    }

    public static void HandleTurnStart(TurnManager p_turnManager)
    {
        OnTurnSceneStarted();
        TrySuppressReplayBootstrapPanels(UIManager.instance, p_turnManager);

        if (_isReplayingFromSnapshot)
        {
            EnsureReplayViewerPerspective();
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
        if (!ShouldSuppressReplayBootstrapPanels() || p_mapGo == null || p_mapGo.startScenario_Panel == null)
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

            if (p_rpcName == "RPC_SyncPlayerData" && !ShouldCaptureSyncPlayerDataArgs(p_args))
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

            int maxBatchesToKeep = ResolveReplayMaxBatchesToKeep(p_gameData);
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
            TrackCurrentPlayerLocalSnapshotReady(p_gameData);
            TryPruneBaselineSnapshotIfAllHumanPlayersReady(p_gameData);
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

    private static void TrackCurrentPlayerLocalSnapshotReady(GameData p_gameData)
    {
        if (p_gameData == null || TurnManager.currPlayer == null || string.IsNullOrEmpty(TurnManager.currPlayer.Name))
        {
            return;
        }

        if (!p_gameData.ModDataBag.TryGet(ReplayPlayersWithLocalSnapshotKey, out List<string> readyPlayers) || readyPlayers == null)
        {
            readyPlayers = new List<string>();
        }

        if (readyPlayers.Contains(TurnManager.currPlayer.Name))
        {
            return;
        }

        readyPlayers.Add(TurnManager.currPlayer.Name);
        p_gameData.ModDataBag.TrySet(ReplayPlayersWithLocalSnapshotKey, readyPlayers, preferKnownOverUnknown: true);
    }

    private static void TryPruneBaselineSnapshotIfAllHumanPlayersReady(GameData p_gameData)
    {
        if (p_gameData == null || p_gameData.listOfPlayers == null || p_gameData.listOfPlayers.Count == 0)
        {
            return;
        }

        if (!p_gameData.ModDataBag.TryGet(ReplayPlayersWithLocalSnapshotKey, out List<string> readyPlayers) || readyPlayers == null || readyPlayers.Count == 0)
        {
            return;
        }

        HashSet<string> readySet = new HashSet<string>(readyPlayers.Where(n => !string.IsNullOrEmpty(n)), StringComparer.Ordinal);
        foreach (Player player in p_gameData.listOfPlayers)
        {
            if (player == null || player.IsComputer || string.IsNullOrEmpty(player.Name))
            {
                continue;
            }

            if (!readySet.Contains(player.Name))
            {
                return;
            }
        }

        p_gameData.ModDataBag.Remove(ReplayBaselineSnapshotKey);
        p_gameData.ModDataBag.Remove(ReplayPlayersWithLocalSnapshotKey);
    }

    private static int ResolveReplayMaxBatchesToKeep(GameData p_gameData)
    {
        if (p_gameData?.listOfPlayers == null || p_gameData.listOfPlayers.Count == 0)
        {
            return ReplayMinBatchesToKeep;
        }

        int humanPlayers = p_gameData.listOfPlayers.Count(p => p != null && !p.IsComputer);
        int perRoundOpponentTurns = Math.Max(1, humanPlayers - 1);

        return Math.Max(ReplayMinBatchesToKeep, Math.Min(ReplayMaxBatchesCap, perRoundOpponentTurns));
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
            if (_authoritativeFinalSnapshotBytes == null || _authoritativeFinalSnapshotBytes.Length == 0)
            {
                ResetReplayState();
                return false;
            }

            _authoritativeBatches = authoritativeBatches;
            _pendingReplayActions = pendingActions;
            _isReplayingFromSnapshot = true;
            _startedReplayCoroutine = false;
            _suppressScenarioIntroPanel = true;
            _replayViewerPlayerName = ResolveReplayViewerPlayerName(current);
            _didApplyReplayViewerPerspective = false;

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

            MainMenu.isLoadedGame = true;
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
        EnsureReplayViewerPerspective();
        ApplyReplayRuntimeOverrides();

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

                    if (ShouldRefreshFogAfterReplayAction(action))
                    {
                        RebuildReplayFogForViewer();
                    }

                    yield return CR_WaitActionSettle(action);
                }
            }
        }
        finally
        {
            RestoreReplayRuntimeOverrides();
        }

        UIManager.ShowMessage("PBEM replay finished.");
        RestoreAuthoritativeStateAndReload();
    }

    private static bool TryApplyAuthoritativeFinalState()
    {
        if (_authoritativeFinalSnapshotBytes == null || _authoritativeFinalSnapshotBytes.Length == 0)
        {
            return false;
        }

        try
        {
            GameData authoritative = (GameData)Utils.ConvertByteArrayToObject(_authoritativeFinalSnapshotBytes);
            if (authoritative == null)
            {
                return false;
            }

            authoritative.ModDataBag.TrySet(ReplayBatchesKey, _authoritativeBatches ?? new List<ReplayTurnBatch>(), preferKnownOverUnknown: true);
            GameData.Instance.SetData(authoritative);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("[pbem_replay] Could not restore authoritative PBEM state: " + ex.Message);
            return false;
        }
    }

    private static void RestoreAuthoritativeStateAndReload()
    {
        if (_isRestoringAuthoritativeState)
        {
            return;
        }

        _isRestoringAuthoritativeState = true;
        bool appliedAuthoritative = false;

        try
        {
            RestoreReplayRuntimeOverrides();
            appliedAuthoritative = TryApplyAuthoritativeFinalState();
            _isApplyingAuthoritativeFinalState = appliedAuthoritative;
            ResetReplayState(keepApplyAuthoritativeFlag: appliedAuthoritative);
            MainMenu.isLoadedGame = true;
            SceneManager.LoadScene("Game");
        }
        finally
        {
            _isRestoringAuthoritativeState = false;
        }
    }

    private static bool IsCoordinateWithinMap(Tile.Coordinates p_coords, Map p_map)
    {
        return p_map != null && p_coords.X >= 0 && p_coords.Y >= 0 && p_coords.X < p_map.SizeX && p_coords.Y < p_map.SizeY;
    }

    private static float EstimateMoveReplayDurationSeconds(byte[] p_payload)
    {
        if (p_payload == null || p_payload.Length == 0 || GameData.Instance?.map == null)
        {
            return 0f;
        }

        try
        {
            object[] payload = (object[])Utils.ConvertByteArrayToObject(p_payload);
            if (payload == null || payload.Length < 2 || !(payload[1] is List<Tile.Coordinates> pathCoords) || pathCoords.Count < 2)
            {
                return 0f;
            }

            Map map = GameData.Instance.map;
            Tile[,] tiles = map.TilesTable;
            float totalDistance = 0f;
            for (int i = 1; i < pathCoords.Count; i++)
            {
                Tile.Coordinates prev = pathCoords[i - 1];
                Tile.Coordinates next = pathCoords[i];
                if (!IsCoordinateWithinMap(prev, map) || !IsCoordinateWithinMap(next, map))
                {
                    totalDistance += MapGO.DISTANCE_BETWEEN_TWO_TILES;
                    continue;
                }

                Tile prevTile = tiles[prev.X, prev.Y];
                Tile nextTile = tiles[next.X, next.Y];
                if (prevTile?.tileGO == null || nextTile?.tileGO == null)
                {
                    totalDistance += MapGO.DISTANCE_BETWEEN_TWO_TILES;
                    continue;
                }

                totalDistance += Vector3.Distance(prevTile.tileGO.transform.position, nextTile.tileGO.transform.position);
            }

            if (totalDistance <= Mathf.Epsilon)
            {
                return 0f;
            }

            // MultiplayerManager CR_MoveUnitToTile lerps with speed factor 25f / segmentDistance.
            float baseDuration = totalDistance / 25f;
            return Mathf.Clamp(baseDuration, 0.08f, 45f);
        }
        catch
        {
            return 0f;
        }
    }

    private static IEnumerator CR_WaitActionSettle(ReplayRpcAction p_action)
    {
        string rpcName = p_action != null ? p_action.RpcName : string.Empty;
        if (rpcName == "RPC_MoveUnit")
        {
            float estimatedMoveDuration = EstimateMoveReplayDurationSeconds(p_action?.Payload);
            float timeout = Mathf.Max(ReplayMoveSettleTimeoutSeconds, estimatedMoveDuration + 2f);
            bool observedAnyMovingUnit = false;
            while (timeout > 0f)
            {
                UnitGO[] units = UnityEngine.Object.FindObjectsOfType<UnitGO>();
                bool anyMovingUnit = units != null && units.Any(u => u != null && u.isMoving);
                if (!anyMovingUnit)
                {
                    break;
                }

                observedAnyMovingUnit = true;
                timeout -= Time.unscaledDeltaTime;
                yield return null;
            }

            float pauseSeconds;
            if (observedAnyMovingUnit)
            {
                pauseSeconds = estimatedMoveDuration > 0f ? Mathf.Max(0.1f, estimatedMoveDuration * 0.15f) : 0.25f;
            }
            else
            {
                pauseSeconds = estimatedMoveDuration > 0f ? estimatedMoveDuration : 0.25f;
            }

            yield return new WaitForSecondsRealtime(ScaleReplayPause(pauseSeconds));
            yield break;
        }

        yield return new WaitForSecondsRealtime(ScaleReplayPause(GetReplayActionPauseSeconds(rpcName)));
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
        RestoreReplayRuntimeOverrides();
        _isReplayingFromSnapshot = false;
        _startedReplayCoroutine = false;
        _suppressScenarioIntroPanel = false;
        _isReplayPromptCoroutineRunning = false;
        ReplayFogSuppressedUnits.Clear();
        ReplayFogSuppressedScales.Clear();
        _pendingReplayActions = null;
        _authoritativeFinalSnapshotBytes = null;
        _authoritativeBatches = null;
        _replayViewerPlayerName = string.Empty;
        _didApplyReplayViewerPerspective = false;

        if (!keepApplyAuthoritativeFlag)
        {
            _isApplyingAuthoritativeFinalState = false;
        }

        _isRestoringAuthoritativeState = false;
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
        rootRect.anchorMin = new Vector2(0.12f, 0.31f);
        rootRect.anchorMax = new Vector2(0.88f, 0.45f);
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
        labelRect.anchorMin = new Vector2(0f, 0.84f);
        labelRect.anchorMax = new Vector2(1f, 1f);
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        TextMeshProUGUI label = labelGo.GetComponent<TextMeshProUGUI>();
        label.alignment = TextAlignmentOptions.Center;
        label.enableWordWrapping = false;
        label.fontSize = p_reference != null ? Mathf.Max(15f, p_reference.fontSize * 0.52f) : 18f;
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
        sliderRect.anchorMin = new Vector2(0f, 0.12f);
        sliderRect.anchorMax = new Vector2(1f, 0.38f);
        sliderRect.offsetMin = Vector2.zero;
        sliderRect.offsetMax = Vector2.zero;

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

[HarmonyPatch(typeof(MultiplayerManager), "RPC_MoveUnit")]
internal static class PbemReplayMoveVisibilityPatch
{
    [HarmonyPrefix]
    private static void Prefix(MultiplayerManager __instance, byte[] p_bytes)
    {
        PbemReplayRuntime.TryApplyFogSafeMoveVisibility(__instance, p_bytes);
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
    [HarmonyPrefix]
    private static void Prefix()
    {
        PbemReplayRuntime.TryPrepareReplayViewerPerspectiveForTurnStart();
    }

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
        PbemReplayRuntime.TrySuppressReplayBootstrapPanels(UIManager.instance, TurnManager.instance);
    }
}

[HarmonyPatch(typeof(UIManager), "ShowPreviousTurnSummary")]
internal static class PbemReplayPreviousTurnSummaryPatch
{
    [HarmonyPrefix]
    private static bool Prefix(UIManager __instance)
    {
        return !PbemReplayRuntime.TrySuppressReplayBootstrapPanels(__instance, TurnManager.instance);
    }
}

[HarmonyPatch(typeof(TurnManager), "ShowHumanEndTurnPanel")]
internal static class PbemReplayEndTurnPanelPatch
{
    [HarmonyPrefix]
    private static bool Prefix(TurnManager __instance)
    {
        return !PbemReplayRuntime.TrySuppressReplayBootstrapPanels(UIManager.instance, __instance);
    }
}

[HarmonyPatch(typeof(CameraGO), "Update")]
internal static class PbemReplayCameraInputPatch
{
    [HarmonyPrefix]
    private static void Prefix(out bool __state)
    {
        __state = false;
        if (PbemReplayRuntime.TryAbortReplayFromEscape())
        {
            return;
        }

        if (!PbemReplayRuntime.IsReplaying || !UIManager.isUIOpen)
        {
            return;
        }

        __state = true;
        UIManager.isUIOpen = false;
    }

    [HarmonyPostfix]
    private static void Postfix(bool __state)
    {
        if (!__state)
        {
            return;
        }

        UIManager.isUIOpen = true;
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
