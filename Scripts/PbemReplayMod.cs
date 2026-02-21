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

    private static byte[] _currentTurnStartSnapshotBytes;

    private static bool _isReplayingFromSnapshot;

    private static bool _isApplyingAuthoritativeFinalState;

    private static bool _startedReplayCoroutine;

    private static List<ReplayRpcAction> _pendingReplayActions;

    private static byte[] _authoritativeFinalSnapshotBytes;

    private static List<ReplayTurnBatch> _authoritativeBatches;

    public static bool IsReplaying => _isReplayingFromSnapshot;

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
        if (current == null || !current.isPBEM || current.sessionID == Guid.Empty)
        {
            return false;
        }

        if (!current.ModDataBag.TryGet(ReplayBatchesKey, out List<ReplayTurnBatch> authoritativeBatches) || authoritativeBatches == null || authoritativeBatches.Count == 0)
        {
            return false;
        }

        long anchorSequence = 0;
        byte[] anchorSnapshotBytes = null;

        if (TryLoadLocalSnapshotEnvelope(current.sessionID, out ReplaySnapshotEnvelope envelope) && envelope?.SnapshotBytes != null && envelope.SnapshotBytes.Length > 0)
        {
            anchorSequence = envelope.LastKnownSequence;
            anchorSnapshotBytes = envelope.SnapshotBytes;
        }
        else if (current.ModDataBag.TryGet(ReplayBaselineSnapshotKey, out byte[] baselineBytes) && baselineBytes != null && baselineBytes.Length > 0)
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

        try
        {
            _authoritativeFinalSnapshotBytes = Utils.ConvertObjectToByteArray(current);
            _authoritativeBatches = authoritativeBatches;
            _pendingReplayActions = pendingActions;
            _isReplayingFromSnapshot = true;
            _startedReplayCoroutine = false;

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
        PlayerSettings.Instance.IsQuickMovement = false;
        PlayerSettings.Instance.FollowAIMoves = true;

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

        PlayerSettings.Instance.IsQuickMovement = quickMovementBackup;
        PlayerSettings.Instance.FollowAIMoves = followAIMovesBackup;

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
            float timeout = 12f;
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
        }
        else
        {
            yield return new WaitForSeconds(0.1f);
        }
    }

    private static void ResetReplayState(bool keepApplyAuthoritativeFlag = false)
    {
        _isReplayingFromSnapshot = false;
        _startedReplayCoroutine = false;
        _pendingReplayActions = null;
        _authoritativeFinalSnapshotBytes = null;
        _authoritativeBatches = null;

        if (!keepApplyAuthoritativeFlag)
        {
            _isApplyingAuthoritativeFinalState = false;
        }
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
        PbemReplayRuntime.OnTurnSceneStarted();

        if (PbemReplayRuntime.TryBootstrapReplayFromLocalSnapshot())
        {
            return;
        }

        PbemReplayRuntime.TryStartReplayCoroutine(__instance);
    }
}
