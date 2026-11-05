namespace SkyRender.Character;

/// <summary>
/// Slot-occupancy bookkeeping the character viewer attaches to each of its meshes (through
/// <see cref="GlMesh.HostData"/>). Each rendered shape carries the biped-object slots it occupies,
/// so "headgear hides hair" / "clothing hides body" visibility resolves from slot collisions rather
/// than from body-part strings. See the mesh-override section of docs/CHARACTER_PIPELINE.md. Base shapes get
/// their slots from their body-part label at install time; mesh-override shapes from
/// <see cref="MeshOverride.BipedSlots"/>.
/// </summary>
public sealed class NpcMeshInfo
{
    /// <summary>Bitmask of biped-object slots this shape occupies, encoded as the
    /// <c>(BipedObjectFlag)N</c> bits (<c>1 &lt;&lt; (slot-30)</c>). 0 for shapes with no slot
    /// association (e.g. FaceGen accessories).</summary>
    public int BipedSlots { get; set; }

    /// <summary>Bitmask of slots whose lower-priority occupants this shape hides. Non-zero only for
    /// mesh-override shapes that should occlude what they cover (armor over body, headgear over
    /// hair). Base shapes leave this 0 — the base body never hides anything.</summary>
    public int HidesSlots { get; set; }

    /// <summary>Slot-occupancy precedence: a shape can only be hidden by another shape of strictly
    /// higher priority. 0 = skin / base (the body, an auxiliary skin mesh), 1 = armor,
    /// 2 = headgear.</summary>
    public int SlotDrawPriority { get; set; }

    /// <summary>Non-null for shapes synthesized by <see cref="VM_CharacterViewer.ApplyMeshOverrides"/>
    /// (the MeshOverride.Key they came from); null for base NPC shapes. Lets a re-applied override
    /// set find and tear down the shapes the previous set created.</summary>
    public string? OverrideKey { get; set; }

    /// <summary>Partition slots of this worn shape that its ArmorAddon does not list, so the game
    /// never draws them (see <see cref="PartitionOcclusion.UnclaimedSlots"/>). 0 for base shapes and
    /// for pieces whose partitions all match their slots.</summary>
    public int UnclaimedPartitionSlots { get; set; }

    /// <summary>True when every partition of this worn shape is unclaimed, so the whole shape is
    /// hidden: a stray BodySlide reference body left in an earring NIF.</summary>
    public bool AllPartitionsUnclaimed { get; set; }
}

/// <summary>
/// Dismember-partition hiding between worn pieces: which partition slots of one shape another shape
/// covers. The game hides a worn piece's partition (an armor's calf partition, SBP_38_CALVES) when
/// another worn piece covers that slot (boots whose ArmorAddon lists slot 38). See the partition
/// section of docs/CHARACTER_PIPELINE.md.
/// </summary>
public static class PartitionOcclusion
{
    /// <summary>
    /// Slots (BipedObjectFlag bits) whose partition triangles <paramref name="occluder"/> hides on
    /// <paramref name="target"/>. Rules:
    /// <list type="bullet">
    /// <item>A shape never hides partitions of a shape from the same piece (same non-null
    /// <see cref="NpcMeshInfo.OverrideKey"/>), nor of itself.</item>
    /// <item>Priority 0 (skin, base shapes) hides nothing. A higher <see cref="NpcMeshInfo.SlotDrawPriority"/>
    /// hides every slot in its
    /// <see cref="NpcMeshInfo.HidesSlots"/>; a lower one hides nothing.</item>
    /// <item>At equal priority, a slot the target does not occupy is hidden. A slot both occupy
    /// goes to the piece occupying fewer slots, the more specific piece: boots (37, 38) keep the
    /// calves over a cuirass (32, 34, 38), gauntlets (33, 34) the forearms. A tie hides neither.
    /// This tie-break is a choice that matches the in-game observation for those pieces, not a
    /// documented engine rule.</item>
    /// </list>
    /// </summary>
    public static int HiddenSlots(NpcMeshInfo target, NpcMeshInfo occluder)
    {
        if (ReferenceEquals(target, occluder) || occluder.HidesSlots == 0) return 0;
        if (target.OverrideKey != null && target.OverrideKey == occluder.OverrideKey) return 0;
        // Priority 0 (skin, base shapes) never hides partitions, as it never hides whole shapes.
        if (occluder.SlotDrawPriority == 0 || occluder.SlotDrawPriority < target.SlotDrawPriority) return 0;
        if (occluder.SlotDrawPriority > target.SlotDrawPriority) return occluder.HidesSlots;

        int hidden = occluder.HidesSlots & ~target.BipedSlots;
        int shared = occluder.HidesSlots & target.BipedSlots;
        if (shared != 0
            && System.Numerics.BitOperations.PopCount((uint)occluder.BipedSlots)
               < System.Numerics.BitOperations.PopCount((uint)target.BipedSlots))
        {
            hidden |= shared;
        }
        return hidden;
    }

    /// <summary>
    /// Partition slots (BipedObjectFlag bits) of a worn piece's shape that the piece's ArmorAddon
    /// does not list in <paramref name="armaSlots"/>. The game does not draw such a partition at
    /// all, whatever else is worn: an earring ArmorAddon on slot 43 whose NIF also carries a
    /// leftover SBP_32_BODY reference body shows only the earring. Checked in game by retagging
    /// that body shape: as 43 it drew over the face, as an unworn 49 it stayed hidden.
    /// <para>Only the 30-61 IDs count. The 130-161 duplicates and the 230-range head IDs are left
    /// drawn, as that rule has not been checked for them. <paramref name="allUnclaimed"/> is true
    /// when the shape has partitions and every one is an unclaimed 30-61 slot, so none of it is
    /// drawn.</para>
    /// </summary>
    public static int UnclaimedSlots(IReadOnlyList<ushort>? partitionIds, int armaSlots, out bool allUnclaimed)
    {
        allUnclaimed = false;
        if (partitionIds == null || partitionIds.Count == 0) return 0;
        int mask = 0;
        bool all = true;
        foreach (ushort id in partitionIds)
        {
            int bit = id is >= 30 and <= 61 ? 1 << (id - 30) : 0;
            if (bit != 0 && (armaSlots & bit) == 0) mask |= bit;
            else all = false;
        }
        allUnclaimed = all;
        return mask;
    }

    /// <summary>Union of <see cref="HiddenSlots(NpcMeshInfo, NpcMeshInfo)"/> over every
    /// occluder.</summary>
    public static int HiddenSlots(NpcMeshInfo target, IEnumerable<NpcMeshInfo> occluders)
    {
        int mask = 0;
        foreach (var o in occluders) mask |= HiddenSlots(target, o);
        return mask;
    }
}

/// <summary>Access to the <see cref="NpcMeshInfo"/> attached to a viewer mesh.</summary>
public static class NpcMeshInfoExtensions
{
    /// <summary>The mesh's <see cref="NpcMeshInfo"/>, attached on first use.</summary>
    public static NpcMeshInfo Npc(this GlMesh mesh)
    {
        if (mesh.HostData is NpcMeshInfo info) return info;
        info = new NpcMeshInfo();
        mesh.HostData = info;
        return info;
    }
}
