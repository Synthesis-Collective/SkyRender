using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyRender.Character;

namespace SkyRender.CharacterDemo;

/// <summary>
/// What the demo draws for one NPC: the base character (<see cref="Paths"/>: body, hands, feet,
/// FaceGen head, skin colours), the default outfit as mesh overrides, and a short trail of how each
/// part was found, for the log.
/// </summary>
internal sealed class NpcResolution
{
    public required ResolvedNpcMeshPaths Paths { get; init; }

    /// <summary>One override per ArmorAddon of the default outfit's armors, for this race and gender.
    /// Empty when the NPC has no outfit (they are drawn as their skin defines them).</summary>
    public required IReadOnlyList<MeshOverride> Outfit { get; init; }

    /// <summary>The outfit record's EditorID (or FormKey), or null when there is none.</summary>
    public string? OutfitName { get; init; }

    /// <summary>Whether a piece of the outfit occupies the body slot (32).</summary>
    public bool OutfitCoversBody => Outfit.Any(o => (o.BipedSlots & BodySlotBit) != 0);

    public required IReadOnlyList<string> Trail { get; init; }

    /// <summary>Biped slot 32 (body) in the record's slot encoding, <c>1 &lt;&lt; (slot - 30)</c>.</summary>
    public const int BodySlotBit = 1 << 2;
}

/// <summary>
/// Reads an NPC's appearance out of the plugins with Mutagen and turns it into the plain paths the
/// character tier draws (<see cref="ResolvedNpcMeshPaths"/> and <see cref="MeshOverride"/>). This is
/// the demo's implementation of <see cref="INpcMeshDataSource"/>, the one seam through which
/// SkyRender.Character learns about NPCs; the library itself never sees a record.
///
/// <para>What it follows, in the order the game does:</para>
/// <list type="number">
///   <item><b>Templates.</b> An NPC whose ACBS template flags include <i>Traits</i> takes its race,
///   gender, skin, head parts, colours, weight, height and FaceGen from its template; one whose flags
///   include <i>Inventory</i> takes its default outfit from it. Chains are followed to the NPC that
///   supplies the value. A template that is a leveled NPC list (LVLN) resolves to its first entry:
///   the game rolls one at spawn time, the demo always shows the first.</item>
///   <item><b>Skin and body.</b> The NPC's worn armor (WNAM), else the race's skin (RACE WNAM). Each
///   of its ArmorAddons (ARMA) that lists the NPC's race, or the race's armor race (RNAM), supplies
///   the body (slot 32), hands (33), feet (37), tail (40) or hair (31 / 41) model for the NPC's gender,
///   with the ARMA's skin texture set (NAM0 / NAM1) when it has one.</item>
///   <item><b>Head.</b> The FaceGen head the Creation Kit exports for the NPC,
///   <c>meshes\actors\character\facegendata\facegeom\&lt;plugin&gt;\&lt;formid&gt;.nif</c>, and its
///   tint, <c>textures\...\facetint\&lt;plugin&gt;\&lt;formid&gt;.dds</c>, named after the FormKey
///   that supplies the traits. The FaceGen NIF already holds the head parts (hair, eyes, brows,
///   beard); the head part records are read only to tell the renderer which shapes are eyes.</item>
///   <item><b>Outfit.</b> The default outfit (DOFT): each item that is an armor, or a leveled item
///   list (LVLI) resolved to its first armor (all of them when the list has <i>Use All</i>), gives
///   one <see cref="MeshOverride"/> per ArmorAddon for the NPC's race and gender. Within one armor the
///   first matching ArmorAddon wins a slot.</item>
/// </list>
/// <para>Not covered: the NPC's other inventory, items given by scripts or distributed at runtime,
/// and leveled rolls other than the first entry.</para>
/// <para>Mutagen's overlays are read by one thread at a time: every call takes a lock.</para>
/// </summary>
internal sealed class NpcResolver : INpcMeshDataSource
{
    private const int MaxTemplateDepth = 16;
    // Slot bits, 1 << (slot - 30): head 30, hair 31, body 32, circlet 42.
    private const int HeadSlotBit = 1 << 0;
    private const int HairSlotBit = 1 << 1;
    private const int CircletSlotBit = 1 << 12;

    private readonly GameData _game;
    private readonly object _gate = new();

    public NpcResolver(GameData game) => _game = game;

    /// <summary>The link cache: a new load order makes a new one, which tells the character tier's
    /// cache to drop what it resolved before.</summary>
    public object? CurrentInvalidationToken => _game.Links;

    /// <summary><see cref="INpcMeshDataSource"/>: the identity's cache key is the NPC's FormKey.</summary>
    public ResolvedNpcMeshPaths? Resolve(NpcIdentity identity)
        => FormKey.TryFactory(identity.CacheKey, out var key) ? ResolveNpc(key)?.Paths : null;

    /// <summary>The base character and the outfit of the NPC with <paramref name="key"/>, or null
    /// when there is no such NPC.</summary>
    public NpcResolution? ResolveNpc(FormKey key)
    {
        lock (_gate)
        {
            var npc = _game.Resolve<INpcGetter>(key);
            if (npc == null) return null;
            var trail = new List<string>();

            // ---- templates ----
            var traits = FollowTemplate(npc, NpcConfiguration.TemplateFlag.Traits, trail, "traits");
            var inventory = FollowTemplate(npc, NpcConfiguration.TemplateFlag.Inventory, trail, "inventory");

            bool female = traits.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
            var gender = female ? NpcGender.Female : NpcGender.Male;
            var race = traits.Race.IsNull ? null : _game.Resolve<IRaceGetter>(traits.Race.FormKey);
            FormKey? raceKey = race?.FormKey;
            // The engine matches ArmorAddons against the race's armor race (RNAM) as well.
            FormKey? armorRaceKey = race != null && !race.ArmorRace.IsNull && race.ArmorRace.FormKey != race.FormKey
                ? race.ArmorRace.FormKey : null;
            trail.Add($"race {race?.EditorID ?? "(none)"}, {gender}" +
                (armorRaceKey is { } ar ? $", armor race {_game.Resolve<IRaceGetter>(ar)?.EditorID ?? ar.ToString()}" : ""));

            // ---- skin and body: WNAM, else the race's skin ----
            IArmorGetter? skin = null;
            if (!traits.WornArmor.IsNull) skin = _game.Resolve<IArmorGetter>(traits.WornArmor.FormKey);
            string skinSource = "worn armor";
            if (skin == null && race != null && !race.Skin.IsNull)
            {
                skin = _game.Resolve<IArmorGetter>(race.Skin.FormKey);
                skinSource = "race skin";
            }
            trail.Add(skin == null ? "no skin armor" : $"{skinSource} {skin.EditorID ?? skin.FormKey.ToString()}");

            string? body = null, hands = null, feet = null, hair = null, tail = null;
            var txst = new Dictionary<string, Dictionary<int, string>>();
            var chains = new Dictionary<string, string>();
            if (skin != null)
            {
                foreach (var arma in ArmorAddonsFor(skin, raceKey, armorRaceKey))
                {
                    var flags = arma.BodyTemplate!.FirstPersonFlags;
                    string? model = WorldModelPath(arma, female);
                    if (model == null) continue;
                    var textures = SkinTextures(arma, female);
                    void Take(ref string? slot, string part)
                    {
                        if (slot != null) return;
                        slot = model;
                        chains[part] = $"{skinSource} {skin!.EditorID} -> {arma.EditorID} -> {model}";
                        if (textures.Count > 0) txst[part] = textures;
                    }
                    if (flags.HasFlag(BipedObjectFlag.Body)) Take(ref body, "Body");
                    if (flags.HasFlag(BipedObjectFlag.Hands)) Take(ref hands, "Hands");
                    if (flags.HasFlag(BipedObjectFlag.Feet)) Take(ref feet, "Feet");
                    if (flags.HasFlag(BipedObjectFlag.Tail)) Take(ref tail, "Tail");
                    if ((flags & (BipedObjectFlag.Hair | BipedObjectFlag.LongHair)) != 0) Take(ref hair, "Hair");
                }
            }

            // ---- head: FaceGen named after the NPC that supplies the traits ----
            string plugin = traits.FormKey.ModKey.FileName;
            string formId = traits.FormKey.ID.ToString("X8");
            string head = $@"meshes\actors\character\facegendata\facegeom\{plugin}\{formId}.nif";
            string tint = $@"textures\actors\character\facegendata\facetint\{plugin}\{formId}.dds";
            chains["Head"] = "FaceGen " + head;
            trail.Add("head " + head);

            string? skeleton = race?.SkeletalModel is { } skel ? ModelPath((female ? skel.Female : skel.Male)?.File.GivenPath) : null;

            (float R, float G, float B)? skinTone = traits.TextureLighting is { } q ? (q.R / 255f, q.G / 255f, q.B / 255f) : null;
            (float R, float G, float B)? hairColor = null;
            if (!traits.HairColor.IsNull && _game.Resolve<IColorRecordGetter>(traits.HairColor.FormKey) is { } hclr)
                hairColor = (hclr.Color.R / 255f, hclr.Color.G / 255f, hclr.Color.B / 255f);
            float height = float.IsFinite(traits.Height) && traits.Height > 0f ? traits.Height : 1f;

            var paths = new ResolvedNpcMeshPaths
            {
                BodyMeshPath = body,
                HandsMeshPath = hands,
                FeetMeshPath = feet,
                HeadMeshPath = head,
                HairMeshPath = hair,
                TailMeshPath = tail,
                Gender = gender,
                SkeletonPath = skeleton,
                ResolutionChains = chains,
                TxstTextures = txst,
                FaceTintPath = tint,
                TextureLightingColor = skinTone,
                NpcWeight = Math.Clamp((int)traits.Weight, 0, 100),
                NpcBaseHeight = height,
                HairColorRgb = hairColor,
                EyeShapeNames = EyeShapeNames(traits, race, female),
            };

            // ---- outfit ----
            var outfit = new List<MeshOverride>();
            string? outfitName = null;
            if (!inventory.DefaultOutfit.IsNull && _game.Resolve<IOutfitGetter>(inventory.DefaultOutfit.FormKey) is { } otft)
            {
                outfitName = otft.EditorID ?? otft.FormKey.ToString();
                var armors = new List<IArmorGetter>();
                foreach (var item in otft.Items ?? Array.Empty<Mutagen.Bethesda.Plugins.IFormLinkGetter<IOutfitTargetGetter>>())
                    if (!item.IsNull) CollectArmors(item.FormKey, armors, depth: 0);
                var seen = new HashSet<FormKey>();
                foreach (var armor in armors)
                    if (seen.Add(armor.FormKey)) AddOutfitPieces(armor, female, raceKey, armorRaceKey, outfit);
                trail.Add($"outfit {outfitName}: {string.Join(", ", armors.Select(a => a.EditorID ?? a.FormKey.ToString()).Distinct())}" +
                    $" -> {outfit.Count} piece(s)");
            }
            else trail.Add("no default outfit");

            return new NpcResolution { Paths = paths, Outfit = outfit, OutfitName = outfitName, Trail = trail };
        }
    }

    // ---- templates ----

    /// <summary>Follows the template chain while each NPC's template flags include
    /// <paramref name="flag"/>, and returns the NPC that supplies that data.</summary>
    private INpcGetter FollowTemplate(INpcGetter npc, NpcConfiguration.TemplateFlag flag, List<string> trail, string what)
    {
        var current = npc;
        var visited = new HashSet<FormKey> { npc.FormKey };
        for (int depth = 0; depth < MaxTemplateDepth; depth++)
        {
            if (!current.Configuration.TemplateFlags.HasFlag(flag) || current.Template.IsNull) break;
            var next = ResolveSpawn(current.Template.FormKey, depth: 0);
            if (next == null || !visited.Add(next.FormKey)) break;
            current = next;
        }
        if (!ReferenceEquals(current, npc))
            trail.Add($"{what} from template {current.EditorID ?? current.FormKey.ToString()}");
        return current;
    }

    /// <summary>A template target as an NPC: an NPC_ itself, or a leveled NPC list's first entry
    /// (followed through nested lists).</summary>
    private INpcGetter? ResolveSpawn(FormKey key, int depth)
    {
        if (depth > MaxTemplateDepth) return null;
        if (_game.Resolve<INpcGetter>(key) is { } npc) return npc;
        if (_game.Resolve<ILeveledNpcGetter>(key) is { Entries: { } entries })
        {
            foreach (var entry in entries)
            {
                var reference = entry.Data?.Reference;
                if (reference == null || reference.IsNull) continue;
                if (ResolveSpawn(reference.FormKey, depth + 1) is { } first) return first;
            }
        }
        return null;
    }

    // ---- armor ----

    /// <summary>The armor's ArmorAddons that this race wears: ARMA RNAM or one of its additional
    /// races is the NPC's race or the race's armor race.</summary>
    private IEnumerable<IArmorAddonGetter> ArmorAddonsFor(IArmorGetter armor, FormKey? race, FormKey? armorRace)
    {
        foreach (var link in armor.Armature)
        {
            if (link.IsNull || _game.Resolve<IArmorAddonGetter>(link.FormKey) is not { BodyTemplate: not null } arma) continue;
            if (race == null || NamesRace(arma, race.Value) || (armorRace is { } a && NamesRace(arma, a)))
                yield return arma;
        }
    }

    private static bool NamesRace(IArmorAddonGetter arma, FormKey race)
        => (!arma.Race.IsNull && arma.Race.FormKey == race)
           || arma.AdditionalRaces.Any(r => !r.IsNull && r.FormKey == race);

    /// <summary>Armors an outfit item stands for: an armor, or a leveled item list's first armor (every
    /// entry's, when the list has Use All), through nested lists.</summary>
    private void CollectArmors(FormKey key, List<IArmorGetter> armors, int depth)
    {
        if (depth > MaxTemplateDepth) return;
        if (_game.Resolve<IArmorGetter>(key) is { } armor)
        {
            armors.Add(armor);
            return;
        }
        if (_game.Resolve<ILeveledItemGetter>(key) is not { Entries: { } entries } lvli) return;
        bool useAll = lvli.Flags.HasFlag(LeveledItem.Flag.UseAll);
        foreach (var entry in entries)
        {
            var reference = entry.Data?.Reference;
            if (reference == null || reference.IsNull) continue;
            int before = armors.Count;
            CollectArmors(reference.FormKey, armors, depth + 1);
            if (!useAll && armors.Count > before) return;   // a plain list: the first entry that gives an armor
        }
    }

    /// <summary>One mesh override per ArmorAddon of <paramref name="armor"/> for this race and gender.
    /// Pieces on the head (30) or circlet (42) slots are headgear, as are hoods on the hair slot (31):
    /// they hide the hair but never the face. Everything else hides the slots it occupies, so the
    /// body, hands and feet under it are not drawn, as in the game.</summary>
    private void AddOutfitPieces(IArmorGetter armor, bool female, FormKey? race, FormKey? armorRace, List<MeshOverride> outfit)
    {
        int taken = 0;
        foreach (var arma in ArmorAddonsFor(armor, race, armorRace))
        {
            int slots = (int)arma.BodyTemplate!.FirstPersonFlags;
            if (slots == 0 || (slots & taken) != 0) continue;   // the first matching ArmorAddon wins a slot
            string? model = WorldModelPath(arma, female);
            if (model == null) continue;
            taken |= slots;

            bool isHead = (slots & (HeadSlotBit | CircletSlotBit | HairSlotBit)) != 0;
            int? hides = null;
            if (isHead)
            {
                hides = slots & ~HeadSlotBit;
                if ((slots & (HeadSlotBit | HairSlotBit)) != 0) hides |= HairSlotBit;
            }
            var textures = SkinTextures(arma, female);
            var alternates = AlternateTextures(arma, female);
            outfit.Add(new MeshOverride
            {
                Key = (isHead ? "Headgear:" : "Outfit:") + arma.FormKey + ":" + slots,
                MeshPath = model,
                BipedSlots = slots,
                HidesSlots = hides,
                Kind = isHead ? MeshOverrideKind.Headgear : MeshOverrideKind.Armor,
                Textures = textures.Count > 0 ? textures : null,
                AlternateTextures = alternates.Count > 0 ? alternates : null,
                HasWeightVariants = female ? arma.WeightSliderEnabled.Female : arma.WeightSliderEnabled.Male,
            });
        }
    }

    // ---- models and textures ----

    private static string? WorldModelPath(IArmorAddonGetter arma, bool female)
        => ModelPath((female ? arma.WorldModel?.Female : arma.WorldModel?.Male)?.File.GivenPath);

    /// <summary>A record's model path is relative to <c>meshes\</c>; the renderer takes Data-relative paths.</summary>
    private static string? ModelPath(string? given)
    {
        if (string.IsNullOrWhiteSpace(given)) return null;
        return given.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) || given.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)
            ? given : @"meshes\" + given;
    }

    /// <summary>The ArmorAddon's skin texture set (NAM0 male, NAM1 female) as texture slots.</summary>
    private Dictionary<int, string> SkinTextures(IArmorAddonGetter arma, bool female)
    {
        var slots = new Dictionary<int, string>();
        var link = female ? arma.SkinTexture?.Female : arma.SkinTexture?.Male;
        if (link != null && !link.IsNull && _game.Resolve<ITextureSetGetter>(link.FormKey) is { } set)
            TextureSlots(set, slots);
        return slots;
    }

    /// <summary>The world model's per-shape texture sets (MODS), with both the shape name and index
    /// the record stores (the renderer matches by name first, then by index).</summary>
    private List<AlternateTextureSpec> AlternateTextures(IArmorAddonGetter arma, bool female)
    {
        var specs = new List<AlternateTextureSpec>();
        var model = female ? arma.WorldModel?.Female : arma.WorldModel?.Male;
        if (model?.AlternateTextures is not { Count: > 0 } alternates) return specs;
        foreach (var alt in alternates)
        {
            if (alt.NewTexture.IsNull || _game.Resolve<ITextureSetGetter>(alt.NewTexture.FormKey) is not { } set) continue;
            var slots = new Dictionary<int, string>();
            TextureSlots(set, slots);
            if (slots.Count > 0)
                specs.Add(new AlternateTextureSpec { ShapeName = alt.Name ?? "", ShapeIndex = alt.Index, Textures = slots });
        }
        return specs;
    }

    /// <summary>
    /// A texture set's paths in the renderer's NIF slot order: 0 diffuse, 1 normal, 2 subsurface (or
    /// glow), 3 detail / height, 4 environment, 5 environment mask, 6 multilayer, 7 backlight /
    /// specular. TX02 is the subsurface map of a skin shader and the environment mask of an
    /// environment-mapped one, so it fills both slots 2 and 5; TX03 and TX04 go to slot 3.
    /// </summary>
    private static void TextureSlots(ITextureSetGetter set, Dictionary<int, string> slots)
    {
        void Add(int slot, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            slots[slot] = path.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) || path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
                ? path : @"textures\" + path;
        }
        Add(0, set.Diffuse?.DataRelativePath.Path);
        Add(1, set.NormalOrGloss?.DataRelativePath.Path);
        Add(2, set.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path);
        Add(5, set.EnvironmentMaskOrSubsurfaceTint?.DataRelativePath.Path);
        Add(3, set.GlowOrDetailMap?.DataRelativePath.Path);
        Add(3, set.Height?.DataRelativePath.Path);
        Add(4, set.Environment?.DataRelativePath.Path);
        Add(6, set.Multilayer?.DataRelativePath.Path);
        Add(7, set.BacklightMaskOrSpecular?.DataRelativePath.Path);
    }

    // ---- head parts ----

    /// <summary>
    /// The FaceGen shape names that are eyes. The Creation Kit bakes one shape per head part into the
    /// FaceGen NIF, named after the head part's EditorID, so the EditorIDs of the NPC's eye head parts
    /// (its own, or the race's default for its gender when it has none) and their extra parts name them.
    /// </summary>
    private IReadOnlySet<string> EyeShapeNames(INpcGetter npc, IRaceGetter? race, bool female)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<FormKey>();
        var npcTypes = new HashSet<HeadPart.TypeEnum?>();
        foreach (var link in npc.HeadParts)
        {
            if (link.IsNull || _game.Resolve<IHeadPartGetter>(link.FormKey) is not { } part) continue;
            npcTypes.Add(part.Type);
            if (part.Type == HeadPart.TypeEnum.Eyes) CollectHeadPart(part, names, visited);
        }
        var headData = female ? race?.HeadData?.Female : race?.HeadData?.Male;
        foreach (var reference in headData?.HeadParts ?? Array.Empty<IHeadPartReferenceGetter>())
        {
            if (reference.Head.IsNull || _game.Resolve<IHeadPartGetter>(reference.Head.FormKey) is not { } part) continue;
            if (part.Type == HeadPart.TypeEnum.Eyes && !npcTypes.Contains(part.Type)) CollectHeadPart(part, names, visited);
        }
        return names;
    }

    private void CollectHeadPart(IHeadPartGetter part, HashSet<string> names, HashSet<FormKey> visited)
    {
        if (!visited.Add(part.FormKey)) return;
        if (!string.IsNullOrEmpty(part.EditorID)) names.Add(part.EditorID);
        foreach (var extra in part.ExtraParts)
            if (!extra.IsNull && _game.Resolve<IHeadPartGetter>(extra.FormKey) is { } e) CollectHeadPart(e, names, visited);
    }
}
