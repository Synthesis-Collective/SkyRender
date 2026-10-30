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

    /// <summary>What could not be drawn as the game would, for example an outfit armor that gives
    /// no piece for this race and gender, with the reason.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

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
///   of its ArmorAddons (ARMA) for the NPC's race supplies the body (slot 32), hands (33), feet (37),
///   tail (40) or hair (31 / 41) model for the NPC's gender, with the ARMA's skin texture set
///   (NAM0 / NAM1) when it has one. As in the game, the skin only fills the slots the outfit leaves
///   free: a skin ArmorAddon that shares the body slot with an outfit piece, or whose slots the
///   outfit covers entirely, is left out (its texture set is kept, for the skin shapes that armor
///   NIFs carry).</item>
///   <item><b>Head.</b> The FaceGen head the Creation Kit exports for the NPC,
///   <c>meshes\actors\character\facegendata\facegeom\&lt;plugin&gt;\&lt;formid&gt;.nif</c>, and its
///   tint, <c>textures\...\facetint\&lt;plugin&gt;\&lt;formid&gt;.dds</c>, named after the FormKey
///   that supplies the traits. The FaceGen NIF already holds the head parts (hair, eyes, brows,
///   beard); the head part records are read only to tell the renderer which shapes are eyes.</item>
///   <item><b>Outfit.</b> The default outfit (DOFT): each item that is an armor, or a leveled item
///   list (LVLI) resolved to its first armor (all of them when the list has <i>Use All</i>), gives
///   one <see cref="MeshOverride"/> per ArmorAddon for the NPC's race and gender. Within one armor the
///   first matching ArmorAddon wins a slot. Two armors whose own biped slots overlap cannot both be
///   worn: the demo keeps the later one (a choice of the demo; the game showed no fixed rule).
///   Shields (slot 39) are left out, as weapons are.</item>
/// </list>
/// <para>An ArmorAddon is for a race when its race (RNAM) or one of its additional races (MODL) is
/// that race or a race it borrows armor from (the race's armor race, RNAM, followed up the chain),
/// as the engine's <c>TESObjectARMA::IsValidRace</c> checks. There is no fallback to the default
/// race: an ArmorAddon for <c>DefaultRace</c> fits only the races it lists. Its world model for a
/// female is the female model (MOD3), or the male model (MOD2) when the record has no female one
/// ("If MOD3 is not present, MOD2 is both genders": UESP, Skyrim Mod File Format/ARMA).</para>
/// <para>Not covered: the NPC's other inventory, items given by scripts or distributed at runtime,
/// and leveled rolls other than the first entry.</para>
/// <para>Mutagen's overlays are read by one thread at a time: every call takes a lock.</para>
/// </summary>
internal sealed class NpcResolver : INpcMeshDataSource
{
    private const int MaxTemplateDepth = 16;
    // Slot bits, 1 << (slot - 30): head 30, hair 31, body 32, shield 39, circlet 42.
    private const int HeadSlotBit = 1 << 0;
    private const int HairSlotBit = 1 << 1;
    private const int CircletSlotBit = 1 << 12;
    private const int ShieldSlotBit = 1 << 9;

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
            // The engine matches ArmorAddons against the race and the races it borrows armor from
            // (RNAM, followed up the chain).
            var races = ArmorRaces(race);
            trail.Add($"race {race?.EditorID ?? "(none)"}, {gender}" + (races is { Count: > 1 }
                ? ", armor race " + string.Join(" -> ", races.Skip(1).Select(r => _game.Resolve<IRaceGetter>(r)?.EditorID ?? r.ToString()))
                : ""));

            // ---- outfit (first: the skin only fills the slots it leaves free) ----
            var outfit = new List<MeshOverride>();
            var warnings = new List<string>();
            string? outfitName = null;
            if (!inventory.DefaultOutfit.IsNull && _game.Resolve<IOutfitGetter>(inventory.DefaultOutfit.FormKey) is { } otft)
            {
                outfitName = otft.EditorID ?? otft.FormKey.ToString();
                var armors = new List<IArmorGetter>();
                foreach (var item in otft.Items ?? Array.Empty<Mutagen.Bethesda.Plugins.IFormLinkGetter<IOutfitTargetGetter>>())
                    if (!item.IsNull) CollectArmors(item.FormKey, armors, depth: 0);
                // Shields are left out, as weapons are: the game carries them on an attachment bone
                // (the shield node of the skeleton) that the demo does not place, so the model would
                // stand at the NPC's feet.
                foreach (var shield in armors.Where(IsShield).Distinct().ToList())
                    trail.Add($"outfit armor {Name(shield)} left out: a shield (slot 39), carried like a weapon");
                armors.RemoveAll(IsShield);
                foreach (var armor in WornArmors(armors, warnings))
                {
                    int before = outfit.Count;
                    string why = AddOutfitPieces(armor, female, races, outfit);
                    if (outfit.Count == before)
                        warnings.Add($"outfit armor {Name(armor)} gives no piece: {why} (race {race?.EditorID ?? "(none)"}, {gender})");
                }
                trail.Add($"outfit {outfitName}: {string.Join(", ", armors.Select(Name).Distinct())}" +
                    $" -> {outfit.Count} piece(s)");
            }
            else trail.Add("no default outfit");
            int outfitSlots = 0;
            foreach (var piece in outfit) outfitSlots |= piece.BipedSlots;

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
                foreach (var arma in ArmorAddonsFor(skin, races))
                {
                    var flags = arma.BodyTemplate!.FirstPersonFlags;
                    string? model = WorldModelPath(arma, female);
                    if (model == null) continue;
                    var textures = SkinTextures(arma, female);
                    bool covered = SkinCovered((int)flags, outfitSlots);
                    void Take(ref string? slot, string part)
                    {
                        if (slot != null || chains.ContainsKey(part)) return;
                        // A covered part keeps its texture set: the skin shapes inside armor NIFs
                        // take the race's skin from it.
                        if (textures.Count > 0) txst[part] = textures;
                        if (covered)
                        {
                            chains[part] = $"{skinSource} {skin!.EditorID} -> {arma.EditorID}: left out, the outfit covers its slots";
                            trail.Add($"skin {part} ({arma.EditorID}) left out: the outfit covers its slots");
                            return;
                        }
                        slot = model;
                        chains[part] = $"{skinSource} {skin!.EditorID} -> {arma.EditorID} -> {model}";
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

            return new NpcResolution { Paths = paths, Outfit = outfit, OutfitName = outfitName, Trail = trail, Warnings = warnings };
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

    /// <summary>The races an ArmorAddon may name for <paramref name="race"/> to wear it: the race
    /// itself, then its armor race (RNAM), that race's armor race and so on, as the engine's
    /// <c>TESObjectARMA::IsValidRace</c> follows them. Null when the NPC has no race (every
    /// ArmorAddon is then taken).</summary>
    private List<FormKey>? ArmorRaces(IRaceGetter? race)
    {
        if (race == null) return null;
        var races = new List<FormKey> { race.FormKey };
        var current = race;
        for (int depth = 0; depth < MaxTemplateDepth; depth++)
        {
            if (current.ArmorRace.IsNull || races.Contains(current.ArmorRace.FormKey)) break;
            races.Add(current.ArmorRace.FormKey);
            if (_game.Resolve<IRaceGetter>(current.ArmorRace.FormKey) is not { } next) break;
            current = next;
        }
        return races;
    }

    /// <summary>The armor's ArmorAddons that this race wears: ARMA RNAM or one of its additional
    /// races is one of <paramref name="races"/> (see <see cref="ArmorRaces"/>).</summary>
    private IEnumerable<IArmorAddonGetter> ArmorAddonsFor(IArmorGetter armor, IReadOnlyList<FormKey>? races)
    {
        foreach (var arma in ArmorAddons(armor))
            if (races == null || races.Any(r => NamesRace(arma, r)))
                yield return arma;
    }

    /// <summary>Every ArmorAddon of the armor that resolves and has biped slots.</summary>
    private IEnumerable<IArmorAddonGetter> ArmorAddons(IArmorGetter armor)
    {
        foreach (var link in armor.Armature)
            if (!link.IsNull && _game.Resolve<IArmorAddonGetter>(link.FormKey) is { BodyTemplate: not null } arma)
                yield return arma;
    }

    private static bool NamesRace(IArmorAddonGetter arma, FormKey race)
        => (!arma.Race.IsNull && arma.Race.FormKey == race)
           || arma.AdditionalRaces.Any(r => !r.IsNull && r.FormKey == race);

    /// <summary>
    /// Whether the outfit leaves no room for a skin ArmorAddon on <paramref name="skinSlots"/>. The
    /// game fills a biped slot from the skin (WNAM) only when no worn armor occupies it: a skin
    /// ArmorAddon that shares the body slot with the outfit, or whose slots the outfit covers
    /// entirely, is not worn. The skin torso (32, 34, 35, 36, 38) under a cuirass (32, 34, 38) is
    /// left out by the first rule; the feet (37) under boots (37, 38) and the hands (33) under
    /// gauntlets (33, 34) by the second. The torso stays with boots alone, which share only the
    /// calves (38). The same rule as OpenMW's ESM4 NPC renderer (<c>esm4npcanimation.cpp</c>).
    /// </summary>
    private static bool SkinCovered(int skinSlots, int outfitSlots)
        => (skinSlots & outfitSlots & NpcResolution.BodySlotBit) != 0
           || (skinSlots != 0 && (skinSlots & ~outfitSlots) == 0);

    /// <summary>
    /// The armors of the outfit that are worn together, in outfit order. An armor occupies the
    /// biped slots of its own record (BOD2): two armors whose slots overlap cannot both be equipped,
    /// so the demo keeps the later one. That is a choice of the demo, not the game's rule: a Use All
    /// list can give two helmets this way (Stormcloak sets with a hide helmet and a full helmet), and
    /// in game the helmet a soldier wore changed from spawn to spawn.
    /// </summary>
    private static List<IArmorGetter> WornArmors(List<IArmorGetter> armors, List<string> warnings)
    {
        var worn = new List<IArmorGetter>();
        var seen = new HashSet<FormKey>();
        foreach (var armor in armors)
        {
            if (!seen.Add(armor.FormKey)) continue;
            int slots = (int)(armor.BodyTemplate?.FirstPersonFlags ?? 0);
            for (int i = worn.Count - 1; i >= 0; i--)
            {
                int other = (int)(worn[i].BodyTemplate?.FirstPersonFlags ?? 0);
                if ((slots & other) == 0) continue;
                warnings.Add($"outfit armor {Name(worn[i])} is replaced by {Name(armor)}: both occupy slot(s) {SlotNames(slots & other)}");
                worn.RemoveAt(i);
            }
            worn.Add(armor);
        }
        return worn;
    }

    private static string Name(IArmorGetter armor) => armor.EditorID ?? armor.FormKey.ToString();

    /// <summary>An armor whose own biped slots (BOD2) include the shield slot, 39.</summary>
    private static bool IsShield(IArmorGetter armor)
        => ((int)(armor.BodyTemplate?.FirstPersonFlags ?? 0) & ShieldSlotBit) != 0;

    private static string SlotNames(int slots)
        => string.Join(",", Enumerable.Range(0, 32).Where(b => (slots & (1 << b)) != 0).Select(b => (b + 30).ToString()));

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
    /// body, hands and feet under it are not drawn, as in the game. Returns why the armor gave no
    /// piece, for the log (meaningful only when it added none).</summary>
    private string AddOutfitPieces(IArmorGetter armor, bool female, IReadOnlyList<FormKey>? races, List<MeshOverride> outfit)
    {
        var all = ArmorAddons(armor).ToList();
        if (all.Count == 0) return "it has no ArmorAddon with biped slots";
        var forRace = ArmorAddonsFor(armor, races).ToList();
        if (forRace.Count == 0)
            return $"none of its ArmorAddons ({string.Join(", ", all.Select(a => a.EditorID ?? a.FormKey.ToString()))}) lists the race or its armor race";
        string why = $"its ArmorAddons for the race ({string.Join(", ", forRace.Select(a => a.EditorID ?? a.FormKey.ToString()))}) have no {(female ? "female or male" : "male")} world model";

        int taken = 0;
        foreach (var arma in forRace)
        {
            int slots = (int)arma.BodyTemplate!.FirstPersonFlags;
            if (slots == 0) { why = $"its ArmorAddon {arma.EditorID} has no biped slots"; continue; }
            if ((slots & taken) != 0) continue;   // the first matching ArmorAddon wins a slot
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
        return why;
    }

    // ---- models and textures ----

    /// <summary>The ArmorAddon's world model for the gender: the female model (MOD3), or the male
    /// model (MOD2) when the record has none, which then serves both genders (UESP's ARMA format
    /// notes). Vanilla's Stormcloak boots and full helmet have only a male model.</summary>
    private static IModelGetter? WorldModel(IArmorAddonGetter arma, bool female)
        => female ? arma.WorldModel?.Female ?? arma.WorldModel?.Male : arma.WorldModel?.Male;

    private static string? WorldModelPath(IArmorAddonGetter arma, bool female)
        => ModelPath(WorldModel(arma, female)?.File.GivenPath);

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
        var model = WorldModel(arma, female);
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
