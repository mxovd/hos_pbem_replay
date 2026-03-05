# PBEM Replay Mod (`pbem_replay`)

Mod for **Hex of Steel** PBEM games that lets a player watch a replay of actions that happened since their last turn.

## What It Does

- Captures replayable multiplayer RPC actions during PBEM play.
- Stores recent action batches inside `GameData.ModDataBag`.
- When a player opens their turn, offers a replay prompt.
- Reconstructs replay by loading an anchor snapshot, then re-applying pending actions in order.
- Restores the authoritative state after replay is complete (or skipped/aborted).
- Fog-only replay events are fast-forwarded (and fog-only visual popups/sounds are skipped).

## Replay Flow

1. On turn start, the mod captures a baseline snapshot of the current `GameData`.
2. During gameplay, selected RPC calls are captured.
3. On PBEM `EndTurn`, captured actions are appended as one `ReplayTurnBatch`.
4. Batch history is trimmed to a bounded window (dynamic by human player count).
5. Next player can replay actions newer than their local anchor sequence.

## Data Added To PBEM Save Data

The mod does **not** add new top-level API fields in `EndTurnRequest`.  
It modifies the serialized `GameData` (the `data` byte array) before upload.

`GameData.ModDataBag` keys used:

- `pbem_replay.batches.v1`
  - Type: `List<ReplayTurnBatch>`
  - Purpose: ordered replay batches
- `pbem_replay.baseline_snapshot.v1`
  - Type: `byte[]` (serialized `GameData`)
  - Purpose: shared baseline fallback snapshot
- `pbem_replay.players_with_local_snapshot.v1`
  - Type: `List<string>`
  - Purpose: tracks human players that already have local replay anchors; used to prune shared baseline data

### Replay Batch Shape

`ReplayTurnBatch`:

- `Sequence` (`long`): global monotonic order
- `TurnNumber` (`int`): map turn number at capture time
- `PlayerName` (`string`): current player name at batch creation
- `Actions` (`List<ReplayRpcAction>`)

`ReplayRpcAction`:

- `RpcName` (`string`)
- `Payload` (`byte[]`) - serialized RPC argument array (`object[]`)

## Batch Retention Policy

Retention is dynamic:

- `keep = clamp(humanPlayers - 1, min=3, max=8)`

This aims to retain roughly one full round of opponent turns while preventing unbounded save growth.

## RPCs Captured For Replay

- `RPC_MoveUnit`
- `RPC_AttackUnit`
- `RPC_KillUnit`
- `RPC_SpawnUnit`
- `RPC_RedeployUnit`
- `RPC_DespawnUnit`
- `RPC_RemoveUnit`
- `RPC_AddUnit`
- `RPC_TransferUnit`
- `RPC_CaptureVP`
- `RPC_EmbarkDisembark`
- `RPC_ShowDamage`
- `RPC_DisplayXP`
- `RPC_DisplayFuelLoss`
- `RPC_DisplayAmmoLoss`
- `RPC_PlaySound`
- `RPC_SyncTile`
- `RPC_SwapUnits`

If a turn produces no replayable RPCs, no batch is added.

## Local-Only Data (Not Uploaded)

Per player, per session snapshot envelope is stored in:

- `<persistentDataPath>/pbem_replay/<sessionId>_<userId>.bin`

It contains:

- `SessionId`
- `LastKnownSequence` (anchor sequence)
- `SnapshotBytes` (serialized `GameData`)

Replay speed preference is also local (`PlayerPrefs` key: `pbem_replay.speed.v1`).

## Build

Requirements:

- .NET SDK with `dotnet` CLI
- Game assemblies in `Libraries/` (or refresh via helper script)

Build:

```bash
dotnet build -c Release
```

Output DLL:

- `output/net48/PbemReplay.dll`

## Package / Install Helper

This repo includes `hos_mod_utils.py` for packaging and optional install.

Common commands:

```bash
# Build + create package in ./package/
python3 hos_mod_utils.py --deploy

# Build + package + install to detected Hex of Steel MODS folder
python3 hos_mod_utils.py --deploy --install

# Refresh local Libraries/ from game install before build/package
python3 hos_mod_utils.py --deploy --refresh-libs
```

Optional environment overrides (via env var or `.env`):

- `HOS_MANAGED_DIR` - path to game `.../Managed`
- `HOS_MODS_PATH` - path to game `.../MODS`

## Compatibility

- Mod name: `pbem_replay`
- Supported game version: `8.1.0+`
- Dependency: `HarmonyCore`

See `Manifest.json` for current metadata.

## Notes / Tradeoffs

- Replay data is embedded in save payload (`GameData.ModDataBag`), so larger retention means larger uploads.
- Trimming too aggressively can make long-gap replays incomplete.
- Current bounded dynamic retention is a balance between replay continuity and payload size.
