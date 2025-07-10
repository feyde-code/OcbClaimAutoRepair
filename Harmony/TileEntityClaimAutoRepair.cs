using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEngine;
using static LightingAround;
using Random = UnityEngine.Random;

public class TileEntityClaimAutoRepair : TileEntitySecureLootContainer
{
    // Flag only for server side code
    public bool isAccessed;

    // Copied from LandClaim code
    public Transform BoundsHelper;

    Vector3i repairPosition;
    BlockValue repairBlock;
    int repairDamage = 0;
    bool doRepair = false;
    int maxRadius;
    int landRadius;
    int currentDistance = 0;
    private string lastMissingItem = null;

    private List<Vector3i>[] allPoints;

    private bool isOn;

    public bool IsOn
    {
        get => this.isOn;
        set
        {
            if (this.isOn != value)
            {
                this.isOn = value;
                this.currentDistance = 0;
                ResetBoundHelper();
                SetModified();
            }
        }
    }

    public TileEntityClaimAutoRepair(Chunk _chunk)
        : base(_chunk)
    {
        isOn = false;
        isAccessed = false;
        if (allPoints == null) InitPoints();
    }

    private void InitPoints()
    {
        Stopwatch sw = new Stopwatch();
        int distance = 0;
        sw.Start();
        landRadius = ((GameStats.GetInt(EnumGameStats.LandClaimSize) - 1) / 2);
        maxRadius = landRadius + 1;
        allPoints = new List<Vector3i>[maxRadius + 1];
        for (int i = 0; i < maxRadius; i++)
        {
            allPoints[i] = new List<Vector3i>();
        }
        try
        {
            for (int x = -landRadius; x <= landRadius; x++)
            {
                for (int y = -landRadius; y <= landRadius; y++)
                {
                    for (int z = -landRadius; z <= landRadius; z++)
                    {

                        Vector3i vector = new Vector3i(x, y, z);
                        distance = Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z)));
                        allPoints[distance].Add(vector);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logs.Write("distance:" + distance + ", maxRadius:" + maxRadius);
            Logs.Error("InitPoints", ex);
        }
        int totalPoints = 0;
        for (int y = 0; y < maxRadius; y++)
        {
            totalPoints += allPoints[y].Count;
        }
        sw.Stop();
        Logs.Write(totalPoints + " block list, with a boundary of " + ((landRadius * 2) + 1).ToString() + ", generated in " + sw.ElapsedMilliseconds.ToString() + " ms.");
    }

    public override TileEntityType GetTileEntityType() => (TileEntityType)242;

    public void ReduceItemCount(Block.SItemNameCount sitem, int count)
    {
        for (int i = 0; i < items.Length; i++)
        {
            ItemStack stack = items[i];
            if (stack.IsEmpty()) continue;
            // ToDo: how expensive is this call for `GetItem(string)`?
            if (stack.itemValue.type == ItemClass.GetItem(sitem.ItemName).type)
            {
                if (count <= stack.count)
                {
                    stack.count -= count;
                    UpdateSlot(i, stack);
                    return;
                }
                else
                {
                    count -= stack.count;
                    stack.count = 0;
                    UpdateSlot(i, stack);
                }
            }
        }
    }

    public int GetItemCount(Block.SItemNameCount sitem)
    {
        int having = 0;
        for (int i = 0; i < items.Length; i++)
        {
            ItemStack stack = items[i];
            if (stack.IsEmpty()) continue;
            // ToDo: how expensive is this call for `GetItem(string)`?
            if (stack.itemValue.type == ItemClass.GetItem(sitem.ItemName).type)
            {
                // Always leave at least one item in the slot
                having += stack.count;
            }
        }
        return having;
    }

    public bool CanRepairBlock(BlockValue block)
    {
        Block localBlock = Block.list[block.type];
        if (localBlock.RepairItems == null) return false;
        float damagePerc = (float)block.damage / (float)localBlock.MaxDamage;

        for (int i = 0; i < localBlock.RepairItems.Count; i++)
        {
            int needed = localBlock.RepairItems[i].Count;
            needed = (int)Mathf.Ceil(damagePerc * needed);
            int available = GetItemCount(localBlock.RepairItems[i]);
            if (available < needed)
            {
                if (available == 0) lastMissingItem = localBlock.RepairItems[i].ItemName;
                return false;
            }
        }

        return localBlock.RepairItems.Count > 0;
    }

    public void TakeRepairMaterials(BlockValue block)
    {
        Block localBlock = Block.list[block.type];
        if (localBlock.RepairItems == null) return;
        float damagePerc = (float)(block.damage) / (float)localBlock.MaxDamage;

        for (int i = 0; i < localBlock.RepairItems.Count; i++)
        {
            int needed = localBlock.RepairItems[i].Count;
            needed = (int)Mathf.Ceil(damagePerc * needed);
            ReduceItemCount(localBlock.RepairItems[i], needed);
        }
    }

    public void AutoRepair(World world)
    {
        Vector3i worldPosI = ToWorldPos();
        Vector3 worldPos = ToWorldPos().ToVector3();
        BlockValue currentBlock;
        Chunk chunkFromWorldPos = (Chunk)world.GetChunkFromWorldPos(worldPosI);
        PersistentPlayerList persistentPlayerList = world.GetGameManager().GetPersistentPlayerList();
        PersistentPlayerData playerData = persistentPlayerList.GetPlayerData(this.GetOwner());

        int claimSize = (GameStats.GetInt(EnumGameStats.LandClaimSize) - 1) / 2;

        if (world == null) return;
        if (world.IsDark()) return;

        if (doRepair)
        {
            currentBlock = world.GetBlock(repairPosition);
            if (currentBlock.damage > repairDamage)
            {
                Logs.Write("Block (" + repairPosition.x + "," + repairPosition.z + ", " + repairPosition.y + ") has taken new damage since last check, cancelling repair.");
                ResetAcquiredBlock();
                return;
            }

            world.GetGameManager().PlaySoundAtPositionServer(worldPos, "repair_block", AudioRolloffMode.Logarithmic, 100);
            if (CanRepairBlock(repairBlock))
            {
                Logs.Write("Repairing block (" + repairPosition.x + "," + repairPosition.z + ", " + repairPosition.y + ").");
                TakeRepairMaterials(repairBlock);
                // Completely restore the block
                repairBlock.damage = 0;
                // Update the block at the given position (very low-level function)
                // Note: with this function we can basically install a new block at position
                world.SetBlock(chunkFromWorldPos.ClrIdx, repairPosition, repairBlock, false, false);
                // Take the repair materials from the container
                // ToDo: what if materials have gone missing?
                // BroadCast the changes done to the block
                world.SetBlockRPC(chunkFromWorldPos.ClrIdx, repairPosition, repairBlock, repairBlock.Block.Density);
                // Get material to play material specific sound
                var material = repairBlock.Block.blockMaterial.SurfaceCategory;
                world.GetGameManager().PlaySoundAtPositionServer(repairPosition.ToVector3(), string.Format("ImpactSurface/metalhit{0}", material), AudioRolloffMode.Logarithmic, 100);
                // Reset acquired block
                ResetAcquiredBlock();
            }
            doRepair = false;
        }
        else
        {
            if (currentDistance > landRadius) currentDistance = 0;
            Logs.Write("currentDistance:" + currentDistance + ";" + allPoints[currentDistance].Count);
            foreach (Vector3i distancePosition in allPoints[currentDistance])
            {
                Vector3i currentPosition = new Vector3i(worldPos.x + distancePosition.x, worldPos.y + distancePosition.y, worldPos.z + distancePosition.z);
                currentBlock = world.GetBlock(currentPosition);
                float damagePercent = (float)currentBlock.damage / (float)Block.list[currentBlock.type].MaxDamage;
                if (currentBlock.type != BlockValue.Air.type && damagePercent >= 0.10)
                {
                    if (IsBlockInsideClaim(world, chunkFromWorldPos, currentPosition, playerData, claimSize, true))
                    {
                        if (CanRepairBlock(currentBlock))
                        {
                            repairPosition = currentPosition;
                            repairBlock = currentBlock;
                            doRepair = true;
                            repairDamage = currentBlock.damage;
                            EnableBoundHelper();
                            SetModified();
                            return;
                        }
                        else
                        {
                            Logs.Write("NoRepair:" + damagePercent);
                        }
                    }
                    else
                    {
                        Logs.Write("NotInClaim:" + damagePercent);
                    }
                }
            }
            currentDistance += 1;
        }
    }

    public void ResetAcquiredBlock(string playSound = "", bool broadcast = true)
    {
        if (repairBlock.type != BlockValue.Air.type)
        {
            // Play optional sound (only at the server to broadcast everywhere)
            if (playSound != "" && SingletonMonoBehaviour<ConnectionManager>.Instance.IsServer)
            {
                GameManager.Instance.PlaySoundAtPositionServer(ToWorldPos().ToVector3(), playSound, AudioRolloffMode.Logarithmic, 100);
            }
            doRepair = false;
            repairBlock = BlockValue.Air;
            repairPosition = ToWorldPos();
            ResetBoundHelper();
            if (broadcast)
            {
                SetModified();
            }
        }
    }

    public override void UpdateTick(World world)
    {
        base.UpdateTick(world);

        // Check if storage is being accessed
        if (IsOn && !IsUserAccessing() && !isAccessed)
        {
            AutoRepair(world);
        }
    }

    public override void SetUserAccessing(bool _bUserAccessing)
    {
        if (IsUserAccessing() != _bUserAccessing)
        {
            base.SetUserAccessing(_bUserAccessing);
            if (_bUserAccessing)
            {
                if (lastMissingItem != null)
                {
                    var player = GameManager.Instance?.World?.GetPrimaryPlayer();
                    string msg = Localization.Get("ocbBlockClaimAutoRepairMissed");
                    if (string.IsNullOrEmpty(msg)) msg = "Claim Auto Repair could use {0}";
                    msg = string.Format(msg, ItemClass.GetItemClass(lastMissingItem).GetLocalizedItemName());
                    GameManager.Instance.ChatMessageServer((ClientInfo)null, EChatType.Whisper, player.entityId, msg, new List<int> { player.entityId }, EMessageSender.Server);
                    lastMissingItem = null;
                }

                ResetAcquiredBlock("weapon_jam", false);
                SetModified(); // Force update
            }
            // SetModified is already called OnClose
        }
    }

    public void EnableBoundHelper()
    {
        if (BoundsHelper == null) return;
        BoundsHelper.localPosition = repairPosition.ToVector3() - Origin.position + new Vector3(0.5f, 0.5f, 0.5f);
        BoundsHelper.gameObject.SetActive(this.isOn);
    }

    public void ResetBoundHelper()
    {
        if (BoundsHelper == null) return;
        BoundsHelper.localPosition = ToWorldPos().ToVector3() - Origin.position + new Vector3(0.5f, 0.5f, 0.5f);
        BoundsHelper.gameObject.SetActive(this.isOn);
    }

    private bool IsBlockInsideClaim(World world, Chunk chunk, Vector3i blockPos, PersistentPlayerData lpRelative, int claimSize, bool includeAllies)
    {

        // Vector3i worldPos = chunk.GetWorldPos();
        // Check if block to be repaired is within a trader area?
        // if (world.IsWithinTraderArea(worldPos + blockPos)) return false;

        foreach (var player in world.gameManager.GetPersistentPlayerList().Players)
        {

            PersistentPlayerData playerData = player.Value;
            // PlatformUserIdentifierAbs playerId = player.Key;

            // First check if user is not myself
            if (lpRelative != playerData)
            {
                // Check if allies should be considered and if ACL is there
                if (includeAllies == false || playerData.ACL == null) continue;
                // Now check the actual ACL if player is allied with ourself
                if (!playerData.ACL.Contains(lpRelative.PrimaryId)) continue;
            }

            // Get all land-claim blocks of the allied user (or our-self)
            if (player.Value.GetLandProtectionBlocks() is List<Vector3i> claimPositions)
            {
                for (int i = 0; i < claimPositions.Count; ++i)
                {
                    // Fetch block value at position where claim block should be
                    BlockValue blockValue = world.GetBlock(claimPositions[i]);
                    // The "primary" flag is encoded in `blockValue.meta`
                    if (BlockLandClaim.IsPrimary(blockValue))
                    {
                        // Now check if the block is inside the range
                        if (Mathf.Abs(claimPositions[i].x - blockPos.x) > claimSize) continue;
                        if (Mathf.Abs(claimPositions[i].z - blockPos.z) > claimSize) continue;
                        // Block within my claim
                        return true;
                    }
                }
            }

        }

        // Not inside my claim
        return false;
    }


    public override void read(PooledBinaryReader _br, TileEntity.StreamModeRead _eStreamMode)
    {
        try
        {
            base.read(_br, _eStreamMode);
            this.IsOn = _br.ReadBoolean();
            switch (_eStreamMode)
            {
                case TileEntity.StreamModeRead.Persistency:
                    break;
                case TileEntity.StreamModeRead.FromServer:
                    bool isRepairing = _br.ReadBoolean();
                    this.repairPosition.x = _br.ReadInt32();
                    this.repairPosition.y = _br.ReadInt32();
                    this.repairPosition.z = _br.ReadInt32();
                    this.lastMissingItem = _br.ReadBoolean()
                        ? _br.ReadString() : null;
                    if (isOn && isRepairing)
                    {
                        EnableBoundHelper();
                    }
                    else
                    {
                        ResetBoundHelper();
                    }
                    break;
                case TileEntity.StreamModeRead.FromClient:
                    this.isAccessed = _br.ReadBoolean();
                    if (this.isAccessed)
                    {
                        // This will provoke an update on
                        // all clients to know new state.
                        ResetAcquiredBlock("weapon_jam");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Logs.Error("read", ex);
        }
    }

    public override void write(PooledBinaryWriter _bw, TileEntity.StreamModeWrite _eStreamMode)
    {
        try
        {
            base.write(_bw, _eStreamMode);
            _bw.Write(_value: isOn);
            switch (_eStreamMode)
            {
                case TileEntity.StreamModeWrite.Persistency:
                    break;
                case TileEntity.StreamModeWrite.ToServer:
                    _bw.Write(_value: IsUserAccessing());
                    break;
                case TileEntity.StreamModeWrite.ToClient:
                    _bw.Write(_value: repairBlock.type != BlockValue.Air.type);
                    _bw.Write(_value: this.repairPosition.x);
                    _bw.Write(_value: this.repairPosition.y);
                    _bw.Write(_value: this.repairPosition.z);
                    _bw.Write(_value: this.lastMissingItem != null);
                    if (this.lastMissingItem != null)
                        _bw.Write(_value: this.lastMissingItem);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logs.Error("write", ex);
        }
    }

}
