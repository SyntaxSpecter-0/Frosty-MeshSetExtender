using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Controls;
using Frosty.Core.Windows;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin;
using MeshSetPlugin.Resources;

using MeshSetExtender.Decimation;
using MeshSetExtender.Settings;

namespace MeshSetExtender.Helpers
{
    /// <summary>
    /// Orchestrates Auto LOD import: LOD0-only FBX import, reflection-based clone into lower
    /// LODs, decimation, and shader block depot updates. Callable from both toolbar and
    /// context menu.
    /// </summary>
    internal static class AutoLodImporter
    {
        /// <summary>
        /// Runs the full Auto LOD import flow on the given mesh entry.
        /// If <paramref name="editor"/> is supplied, the mesh editor's viewport is refreshed
        /// using the same sequence the vanilla Import button uses.
        /// </summary>
        public static void Run(EbxAssetEntry entry, FrostyAssetEditor editor = null)
        {
            if (entry == null) return;

            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic meshAsset = asset.RootObject;

            ulong resRid = meshAsset.MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);

            int originalLodCount = meshSet.Lods.Count;
            if (originalLodCount <= 1)
            {
                FrostyMessageBox.Show(
                    "This mesh only has 1 LOD — Auto LOD requires at least 2 LODs.",
                    "Auto LOD");
                return;
            }

            if (meshSet.Type == MeshType.MeshType_Composite && !IsCompositeAutoLodEnabledForBuild())
            {
                string message =
                    "Auto LOD for Composite meshes is disabled in release builds due to known stability issues " +
                    "(including map-load crashes).\n\nUse a debug/developer build for Composite testing.";
                App.Logger.LogWarning("[Auto LOD] Composite mesh import is disabled in this build.");
                FrostyMessageBox.Show(message, "Auto LOD");
                return;
            }

            FrostyOpenFileDialog ofd = new FrostyOpenFileDialog(
                "Import FBX (Auto LOD)", "*.fbx (FBX Files)|*.fbx", "Mesh");
            if (!ofd.ShowDialog()) return;
            string inputPath = ofd.FileName;

            AutoLodImportSettings settings = new AutoLodImportSettings();

            var config = new AutoLodConfig();
            config.Load();
            settings.Preset = config.Preset;
            settings.MaxError = config.MaxError;
            settings.LockBorders = config.LockBorders;
            settings.Lod1Ratio = config.Lod1Ratio;
            settings.Lod2Ratio = config.Lod2Ratio;
            settings.Lod3Ratio = config.Lod3Ratio;
            settings.Lod4Ratio = config.Lod4Ratio;
            settings.Lod5Ratio = config.Lod5Ratio;
            settings.DebugLogging = config.DebugLogging;

            if (meshSet.Type == MeshType.MeshType_Skinned)
                settings.SkeletonAsset = Config.Get<string>("MeshSetImportSkeleton", "", ConfigScope.Game);

            ResizeNextImportDialog(450);

            if (FrostyImportExportBox.Show<AutoLodImportSettings>(
                    $"Import Mesh — Auto LOD ({originalLodCount} LODs)",
                    FrostyImportExportType.Import,
                    settings) != MessageBoxResult.OK)
                return;

            if (meshSet.Type == MeshType.MeshType_Skinned && !string.IsNullOrEmpty(settings.SkeletonAsset))
                Config.Add("MeshSetImportSkeleton", settings.SkeletonAsset, ConfigScope.Game);

            bool debug = settings.DebugLogging;

            App.Logger.Log(settings.GetRatiosSummary(originalLodCount));

            // Pause the viewport while we mutate the mesh (mirrors vanilla Import button).
            if (editor != null) MeshEditorRefreshHelper.SetPaused(editor, true);

            try
            {
                FrostyTaskWindow.Show("Importing with Auto LOD", "", (task) =>
                {
                    try
                    {
                        ExecutePipeline(entry, asset, resEntry, meshSet, resRid, inputPath, settings, task, debug);
                        settings.SaveAsDefaults();
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogError($"Auto LOD import failed: {ex.Message}");
                        if (debug) App.Logger.LogError(ex.ToString());
                    }
                });

                // Refresh using the same pattern as vanilla ImportButton_Click.
                if (editor != null)
                    MeshEditorRefreshHelper.RefreshAfterImport(editor, meshSet, asset);
            }
            finally
            {
                if (editor != null) MeshEditorRefreshHelper.SetPaused(editor, false);
            }

            App.EditorWindow.DataExplorer.RefreshAll();
        }

        /// <summary>
        /// Runs the Auto LOD pipeline on a mesh that already has settings + FBX path resolved.
        /// Used by both the single-mesh interactive flow and the batch importer.
        /// </summary>
        public static void RunBatch(EbxAssetEntry entry, string fbxPath, AutoLodImportSettings settings,
            FrostyTaskWindow taskWindow, bool debug)
        {
            EbxAsset asset = App.AssetManager.GetEbx(entry);
            dynamic meshAsset = asset.RootObject;
            ulong resRid = meshAsset.MeshSetResource;
            ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
            MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);

            if (meshSet.Lods.Count <= 1)
            {
                App.Logger.LogWarning($"{entry.Filename}: only 1 LOD — skipped.");
                return;
            }

            if (meshSet.Type == MeshType.MeshType_Composite && !IsCompositeAutoLodEnabledForBuild())
            {
                App.Logger.LogWarning(
                    $"{entry.Filename}: composite mesh Auto LOD is disabled in this build (release safety gate) — skipped.");
                return;
            }

            ExecutePipeline(entry, asset, resEntry, meshSet, resRid, fbxPath, settings, taskWindow, debug);
        }

        /// <summary>
        /// Core Auto LOD pipeline: import LOD0, native re-import for stride-mismatched LODs,
        /// verbatim clone for same-stride LODs, decimate, update shader block depots, commit.
        /// </summary>
        private static void ExecutePipeline(EbxAssetEntry entry, EbxAsset asset, ResAssetEntry resEntry,
            MeshSet meshSet, ulong resRid, string inputPath, AutoLodImportSettings settings,
            FrostyTaskWindow task, bool debug)
        {
            var originalLods = meshSet.Lods.ToList();
            bool isCompositeMesh = meshSet.Type == MeshType.MeshType_Composite;
            uint lod0Stride = originalLods[0].Sections.Count > 0 ? originalLods[0].Sections[0].VertexStride : 0u;
            var lod0UnsupportedSnapshots = CaptureUnsupportedSectionSnapshots(originalLods[0], debug);
            var compositePartSnapshot = isCompositeMesh ? CaptureCompositePartData(meshSet, originalLods) : null;
            var expectedRenderableNames = BuildExpectedRenderableNameSet(originalLods);

            // Identify LODs whose native stride differs from LOD0's. For these we re-import
            // the FBX directly into their native structure, because format-
            // converting LOD0 bytes into their layout doesn't match their pre-compiled
            // shader bytecode. Same-stride LODs are still handled via verbatim clone.
            var lodsNeedingReimport = new List<int>();
            for (int i = 1; i < originalLods.Count; i++)
            {
                uint s = originalLods[i].Sections.Count > 0 ? originalLods[i].Sections[0].VertexStride : 0u;
                if (s != lod0Stride && s > 0) lodsNeedingReimport.Add(i);
            }

            // Import LOD0 first (standard path)
            task.Update("Importing LODs...", 10);
            while (meshSet.Lods.Count > 1)
                meshSet.Lods.RemoveAt(meshSet.Lods.Count - 1);

            var geomPatches = GeometryDeclPatcher.PatchForImport(meshSet, App.Logger, debug);
            try
            {
                ImportFbxIntoCurrentLodSet(inputPath, meshSet, asset, entry, settings);
            }
            finally
            {
                GeometryDeclPatcher.Restore(geomPatches);
            }

            MeshSetLod importedLod0 = meshSet.Lods[0];
            RestoreUnsupportedSectionData(importedLod0, lod0UnsupportedSnapshots, debug);

            if (debug)
            {
                App.Logger.Log("[DEBUG] LOD0 after import:");
                foreach (var sec in importedLod0.Sections)
                    App.Logger.Log($"[DEBUG]   '{sec.Name}': V={sec.VertexCount}, P={sec.PrimitiveCount}, Stride={sec.VertexStride}");
            }

            // Guardrail: Auto LOD expects the imported FBX to preserve the target mesh's
            // section/material slot layout. If extra slots are introduced (common when DCC
            // tools add default materials like "lambert1"), downstream composite processing
            // can fail or produce broken lower LODs.
            AnalyzeImportedRenderableLayout(originalLods, importedLod0, out var unexpectedSlots, out var missingSlots);

            bool forceCompositeCloneMode = false;
            if (unexpectedSlots.Count > 0 || missingSlots.Count > 0)
            {
                if (isCompositeMesh && missingSlots.Count == 0)
                {
                    // Composite-safe fallback: when the FBX has extra render slots but still
                    // contains every required LOD0 slot, skip native lower-LOD re-import and
                    // clone from imported LOD0. This avoids FBXImporter's composite part-data
                    // null-ref path on some SWBF2 variants.
                    forceCompositeCloneMode = true;

                    string unexpectedText = string.Join(", ", unexpectedSlots);
                    App.Logger.LogWarning(
                        "Imported FBX has extra render slot(s) not present on the target mesh: " +
                        $"{unexpectedText}. Continuing in composite-safe mode: native lower-LOD re-import will be skipped.");

                    // Composite safety: prevent unexpected imported render sections
                    // (e.g. default DCC slots like lambert1) from participating in
                    // runtime rendering/decimation. Keep section structure, but zero them.
                    ZeroUnexpectedRenderableSections(importedLod0, expectedRenderableNames, debug);
                }
                else
                {
                    throw new InvalidOperationException(BuildRenderableLayoutMismatchMessage(unexpectedSlots, missingSlots));
                }
            }

            // Re-import the FBX natively into each stride-mismatched LOD.
            var nativeImportedLods = new Dictionary<int, MeshSetLod>();
            var forceCloneLods = new HashSet<int>();
            for (int idx = 0; idx < lodsNeedingReimport.Count; idx++)
            {
                int lodIndex = lodsNeedingReimport[idx];

                if (forceCompositeCloneMode)
                {
                    forceCloneLods.Add(lodIndex);
                    if (debug)
                        App.Logger.Log($"[DEBUG] LOD{lodIndex} native re-import skipped (composite-safe fallback mode)");
                    continue;
                }

                var lodUnsupportedSnapshots = CaptureUnsupportedSectionSnapshots(originalLods[lodIndex], debug);
                meshSet.Lods.Clear();
                meshSet.Lods.Add(originalLods[lodIndex]);

                var patches = GeometryDeclPatcher.PatchForImport(meshSet, App.Logger, debug);
                try
                {
                    ImportFbxIntoCurrentLodSet(inputPath, meshSet, asset, entry, settings);
                    nativeImportedLods[lodIndex] = meshSet.Lods[0];
                    RestoreUnsupportedSectionData(nativeImportedLods[lodIndex], lodUnsupportedSnapshots, debug);

                    if (debug)
                    {
                        App.Logger.Log($"[DEBUG] LOD{lodIndex} after native re-import:");
                        foreach (var sec in meshSet.Lods[0].Sections)
                            App.Logger.Log($"[DEBUG]   '{sec.Name}': V={sec.VertexCount}, P={sec.PrimitiveCount}, Stride={sec.VertexStride}");
                    }
                }
                catch (Exception ex)
                {
                    if (!isCompositeMesh)
                        throw;

                    // Some composite variants fail inside MeshSetPlugin's part-data rebuild
                    // during native lower-LOD re-import. Mark these LODs for composite fallback.
                    forceCloneLods.Add(lodIndex);
                    App.Logger.LogWarning(
                        $"LOD{lodIndex} native re-import failed for composite mesh; " +
                        $"marking this LOD for composite fallback handling. ({ex.Message})");
                    if (debug) App.Logger.LogWarning(ex.ToString());
                }
                finally
                {
                    GeometryDeclPatcher.Restore(patches);
                }
            }

            bool compositeLowerLodFallback = isCompositeMesh &&
                                             (forceCompositeCloneMode || forceCloneLods.Count > 0);

            // Reassemble full LOD structure
            meshSet.Lods.Clear();
            meshSet.Lods.Add(importedLod0);

            if (compositeLowerLodFallback)
            {
                // Composite fallback generation path: native lower-LOD re-import failed,
                // so regenerate LOD1+ from imported LOD0 via clone + decimation.
                for (int i = 1; i < originalLods.Count; i++)
                    meshSet.Lods.Add(originalLods[i]);

                App.Logger.LogWarning(
                    "Composite fallback mode active: generating LOD1+ from imported LOD0 (native lower-LOD re-import failed).");

                // Force clone all lower LODs in fallback mode.
                var forceAllLowerLods = new HashSet<int>();
                for (int i = 1; i < meshSet.Lods.Count; i++)
                    forceAllLowerLods.Add(i);

                DeepCloneLod0(meshSet, resEntry, debug, forceAllLowerLods, allowSourceReuseForComposite: true);

                task.Update("Decimating LODs...", 60);
                var decimator = new PostImportLodDecimator(App.Logger, debug, settings.MaxError, settings.LockBorders);
                decimator.DecimateLods(
                    meshSet, resEntry, settings.GetRatios(),
                    (message, progress) => task.Update("Decimating LODs...", 60 + progress * 0.3)
                );

                // Preserve original composite part metadata; native importer can partially
                // rebuild it before failing on some assets, which can cause runtime crashes.
                RestoreCompositePartData(meshSet, compositePartSnapshot, debug);
            }
            else
            {
                for (int i = 1; i < originalLods.Count; i++)
                {
                    if (nativeImportedLods.TryGetValue(i, out var lod))
                        meshSet.Lods.Add(lod);
                    else
                        meshSet.Lods.Add(originalLods[i]);
                }

                // Verbatim clone LOD0 into any remaining same-stride LODs
                DeepCloneLod0(meshSet, resEntry, debug, forceCloneLods);

                // Decimate all lower LODs (single static message — no per-LOD spam)
                task.Update("Decimating LODs...", 60);
                var decimator = new PostImportLodDecimator(App.Logger, debug, settings.MaxError, settings.LockBorders);
                decimator.DecimateLods(
                    meshSet, resEntry, settings.GetRatios(),
                    (message, progress) => task.Update("Decimating LODs...", 60 + progress * 0.3)
                );
            }

            // Update shader block depots
            task.Update("Updating shader block depots...", 92);
            var shaderBlockDepots = new List<ShaderBlockDepot>();
            foreach (var linkedEntry in resEntry.LinkedAssets)
            {
                if (linkedEntry is ResAssetEntry linkedRes && linkedRes.Type == "ShaderBlockDepot")
                {
                    var depot = App.AssetManager.GetResAs<ShaderBlockDepot>(linkedRes);
                    if (depot != null) shaderBlockDepots.Add(depot);
                }
            }
            ShaderBlockDepotHelper.Update(meshSet, resEntry, shaderBlockDepots, debug);

            // Commit
            task.Update("Finalizing...", 98);
            App.AssetManager.ModifyRes(resRid, meshSet);
            entry.LinkAsset(resEntry);

            LodSummaryHelper.LogSummary(App.Logger, meshSet, entry.Filename);
        }

        /// <summary>
        /// Clones LOD0 into all lower LODs that share LOD0's vertex format. Different-stride
        /// LODs were natively re-imported earlier in ExecutePipeline, so their vertex data
        /// already matches their pre-compiled shader bytecode and we skip them here.
        /// </summary>
        private static void DeepCloneLod0(MeshSet meshSet, ResAssetEntry resEntry, bool debug,
            HashSet<int> forceCloneLods = null, bool allowSourceReuseForComposite = false)
        {
            if (meshSet.Lods.Count < 2) return;

            MeshSetLod lod0 = meshSet.Lods[0];

            byte[] lod0Bytes;
            using (Stream s = LodStreamHelper.GetLodStream(lod0))
            {
                if (s == null) { App.Logger.LogError("Failed to read LOD0 chunk."); return; }
                using (MemoryStream ms = new MemoryStream()) { s.CopyTo(ms); lod0Bytes = ms.ToArray(); }
            }

            uint lod0Stride = lod0.Sections.Count > 0 ? lod0.Sections[0].VertexStride : 0u;

            for (int i = 1; i < meshSet.Lods.Count; i++)
            {
                MeshSetLod targetLod = meshSet.Lods[i];
                uint targetStride = targetLod.Sections.Count > 0 ? targetLod.Sections[0].VertexStride : 0u;
                bool forceClone = forceCloneLods != null && forceCloneLods.Contains(i);

                if (targetStride != lod0Stride && targetStride > 0 && !forceClone)
                {
                    // Different-stride LODs were natively re-imported earlier in Run(), so
                    // their vertex data already matches their pre-compiled shader bytecode.
                    // Skip cloning — the decimator will trim their triangle count next.
                    if (debug)
                        App.Logger.Log($"[DEBUG] LOD{i} skipped in clone stage (stride={targetStride}, native-reimported)");
                    continue;
                }

                if (forceClone && debug)
                    App.Logger.Log($"[DEBUG] LOD{i} forced through clone stage after native re-import failure");

                // Same stride — clone verbatim
                if (targetLod.ChunkId != Guid.Empty)
                {
                    App.AssetManager.ModifyChunk(targetLod.ChunkId, lod0Bytes);
                    ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(targetLod.ChunkId);
                    if (chunkEntry != null) resEntry.LinkAsset(chunkEntry);
                }
                else
                    targetLod.SetInlineData(lod0Bytes);

                targetLod.VertexBufferSize = lod0.VertexBufferSize;
                targetLod.IndexBufferSize = lod0.IndexBufferSize;
                targetLod.SetIndexBufferFormatSize(lod0.IndexUnitSize == 32 ? 4 : 2);

                var lod0Renderables = lod0.Sections.Where(s => !string.IsNullOrEmpty(s.Name)).ToList();
                var lod0DepthShadow = lod0.Sections.Where(s => string.IsNullOrEmpty(s.Name)).ToList();
                var targetRenderables = targetLod.Sections.Where(s => !string.IsNullOrEmpty(s.Name)).ToList();
                var targetDepthShadow = targetLod.Sections.Where(s => string.IsNullOrEmpty(s.Name)).ToList();
                var preferredTargetsByNorm = BuildPreferredTargetSectionsByNormalizedName(lod0Renderables, targetRenderables);

                // Composite LODs often reorder/rename sections (e.g. "Fuselage" -> "FuselageLOD").
                // Match by name first (with normalization). Do not blindly fall back to
                // unrelated sections because that can cross-wire materials/parts.
                var usedLod0RenderableIndices = new HashSet<int>();
                for (int s = 0; s < targetRenderables.Count; s++)
                {
                    string targetNorm = NormalizeSectionName(targetRenderables[s].Name);
                    if (preferredTargetsByNorm.TryGetValue(targetNorm, out int preferredIndex) && preferredIndex != s)
                    {
                        targetRenderables[s].VertexCount = 0;
                        targetRenderables[s].PrimitiveCount = 0;
                        targetRenderables[s].VertexOffset = 0;
                        targetRenderables[s].StartIndex = 0;

                        if (debug)
                        {
                            App.Logger.Log(
                                $"[DEBUG] Target section '{targetRenderables[s].Name}' on LOD{i} was zeroed " +
                                $"because normalized slot '{targetNorm}' is preferred on '{targetRenderables[preferredIndex].Name}'.");
                        }
                        continue;
                    }

                    MeshSetSection sourceSection = FindBestRenderableSourceSection(
                        lod0Renderables, targetRenderables[s], usedLod0RenderableIndices, allowSourceReuseForComposite);
                    if (sourceSection == null)
                    {
                        // No trustworthy source match for this target section.
                        // Zero it instead of assigning unrelated geometry.
                        targetRenderables[s].VertexCount = 0;
                        targetRenderables[s].PrimitiveCount = 0;
                        targetRenderables[s].VertexOffset = 0;
                        targetRenderables[s].StartIndex = 0;

                        if (debug)
                            App.Logger.LogWarning(
                                $"[DEBUG] No LOD0 source match for target section '{targetRenderables[s].Name}' on LOD{i}; section was zeroed.");
                        continue;
                    }

                    bool copyMaterialMetadata = string.Equals(
                        sourceSection.Name, targetRenderables[s].Name, StringComparison.OrdinalIgnoreCase);

                    CloneSectionMetadata(sourceSection, targetRenderables[s], copyMaterialMetadata);

                    if (debug && !string.Equals(sourceSection.Name, targetRenderables[s].Name, StringComparison.OrdinalIgnoreCase))
                    {
                        App.Logger.Log(
                            $"[DEBUG] Section remap LOD0 '{sourceSection.Name}' -> target '{targetRenderables[s].Name}' " +
                            $"(material: keep-target, srcMat={sourceSection.MaterialId}, dstMat={targetRenderables[s].MaterialId})");
                    }
                }

                for (int s = 0; s < targetDepthShadow.Count; s++)
                {
                    if (s < lod0DepthShadow.Count)
                        CloneSectionMetadata(lod0DepthShadow[s], targetDepthShadow[s]);
                    else
                    {
                        targetDepthShadow[s].VertexCount = 0;
                        targetDepthShadow[s].PrimitiveCount = 0;
                        targetDepthShadow[s].VertexOffset = 0;
                        targetDepthShadow[s].StartIndex = 0;
                    }
                }

                targetLod.ClearBones();
                if (lod0.BoneCount > 0)
                {
                    targetLod.BoneIndexArray.Clear();
                    foreach (uint b in lod0.BoneIndexArray) targetLod.BoneIndexArray.Add(b);
                    targetLod.BoneShortNameArray.Clear();
                    foreach (uint h in lod0.BoneShortNameArray) targetLod.BoneShortNameArray.Add(h);
                }

                LodMetadataHelper.CloneLodBounds(lod0, targetLod);

                if (debug) App.Logger.Log($"[DEBUG] LOD{i} cloned ({targetLod.Sections.Count} sections, stride={targetLod.Sections[0].VertexStride})");
            }
        }

        private static void AnalyzeImportedRenderableLayout(List<MeshSetLod> originalLods, MeshSetLod importedLod0,
            out List<string> unexpected, out List<string> missing)
        {
            unexpected = new List<string>();
            missing = new List<string>();

            if (originalLods == null || originalLods.Count == 0 || importedLod0 == null)
                return;

            var expectedAll = new HashSet<string>(
                originalLods
                    .SelectMany(l => l.Sections)
                    .Where(s => !string.IsNullOrEmpty(s.Name))
                    .Select(s => NormalizeSectionName(s.Name)));

            var requiredLod0 = new HashSet<string>(
                originalLods[0]
                    .Sections
                    .Where(s => !string.IsNullOrEmpty(s.Name))
                    .Select(s => NormalizeSectionName(s.Name)));

            var importedNames = importedLod0
                .Sections
                .Where(s => !string.IsNullOrEmpty(s.Name))
                .Select(s => s.Name)
                .ToList();

            var importedNormalized = new HashSet<string>(importedNames.Select(NormalizeSectionName));

            unexpected = importedNames
                .Where(n => !expectedAll.Contains(NormalizeSectionName(n)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            missing = requiredLod0
                .Where(required => !importedNormalized.Contains(required))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string BuildRenderableLayoutMismatchMessage(List<string> unexpected, List<string> missing)
        {
            string unexpectedText = unexpected.Count > 0 ? string.Join(", ", unexpected) : "(none)";
            string missingText = missing.Count > 0 ? string.Join(", ", missing) : "(none)";

            return
                "Imported FBX section/material layout does not match the target mesh.\n" +
                $"Unexpected slots: {unexpectedText}\n" +
                $"Missing required LOD0 slots: {missingText}\n\n" +
                "Auto LOD requires the same slot layout as the target mesh. " +
                "Remove extra/default materials (for example 'lambert1') and keep section names aligned.";
        }

        private static HashSet<string> BuildExpectedRenderableNameSet(List<MeshSetLod> originalLods)
        {
            return new HashSet<string>(
                originalLods
                    .SelectMany(l => l.Sections)
                    .Where(s => !string.IsNullOrEmpty(s.Name))
                    .Select(s => NormalizeSectionName(s.Name)));
        }

        private static void ZeroUnexpectedRenderableSections(
            MeshSetLod lod,
            HashSet<string> expectedRenderableNames,
            bool debug)
        {
            if (lod == null || expectedRenderableNames == null || expectedRenderableNames.Count == 0)
                return;

            var zeroed = new List<string>();
            foreach (var sec in lod.Sections)
            {
                if (sec == null || string.IsNullOrEmpty(sec.Name))
                    continue;

                if (expectedRenderableNames.Contains(NormalizeSectionName(sec.Name)))
                    continue;

                sec.VertexCount = 0;
                sec.PrimitiveCount = 0;
                sec.VertexOffset = 0;
                sec.StartIndex = 0;
                zeroed.Add(sec.Name);
            }

            if (zeroed.Count > 0)
            {
                App.Logger.LogWarning(
                    "Zeroed unexpected imported render section(s): " +
                    string.Join(", ", zeroed.Distinct(StringComparer.OrdinalIgnoreCase)));
            }
            else if (debug)
            {
                App.Logger.Log("[DEBUG] No unexpected imported render sections needed zeroing.");
            }
        }

        private static Dictionary<string, int> BuildPreferredTargetSectionsByNormalizedName(
            List<MeshSetSection> sourceRenderables,
            List<MeshSetSection> targetRenderables)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (sourceRenderables == null || targetRenderables == null)
                return result;

            var sourceCountsByNorm = sourceRenderables
                .GroupBy(s => NormalizeSectionName(s.Name))
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var targetGroups = targetRenderables
                .Select((s, i) => new { Section = s, Index = i, Norm = NormalizeSectionName(s.Name) })
                .GroupBy(x => x.Norm);

            foreach (var group in targetGroups)
            {
                string norm = group.Key;
                if (string.IsNullOrEmpty(norm))
                    continue;

                sourceCountsByNorm.TryGetValue(norm, out int sourceCount);
                if (sourceCount != 1 || group.Count() <= 1)
                    continue;

                // When multiple target sections share the same normalized slot but only one
                // source exists, choose the heaviest original target section as the canonical
                // recipient and zero the others.
                int preferred = group
                    .OrderByDescending(x => x.Section.PrimitiveCount)
                    .ThenByDescending(x => x.Section.VertexCount)
                    .ThenByDescending(x => x.Section.Name?.ToLowerInvariant().Contains("lod") == true)
                    .Select(x => x.Index)
                    .First();

                result[norm] = preferred;
            }

            return result;
        }

        private sealed class UnsupportedSectionSnapshot
        {
            public string Name;
            public uint VertexStride;
            public uint VertexCount;
            public uint PrimitiveCount;
            public byte[] VertexBytes;
            public byte[] IndexBytes;
            public object GeometryDeclDescValue;
            public object VertexStrideValue;
            public object BonesPerVertexValue;
            public object PrimitiveTypeValue;
        }

        private static List<UnsupportedSectionSnapshot> CaptureUnsupportedSectionSnapshots(MeshSetLod lod, bool debug)
        {
            var snapshots = new List<UnsupportedSectionSnapshot>();
            if (lod == null)
                return snapshots;

            byte[] lodBytes = ReadLodBytes(lod);
            if (lodBytes == null || lodBytes.Length == 0)
                return snapshots;

            int indexSize = lod.IndexUnitSize == 32 ? 4 : 2;

            foreach (var section in lod.Sections)
            {
                if (section == null || string.IsNullOrEmpty(section.Name))
                    continue;
                if (!SectionHasUnsupportedElements(section))
                    continue;

                long vtxOffset = section.VertexOffset;
                long vtxLength = (long)section.VertexCount * section.VertexStride;
                long idxOffset = (long)lod.VertexBufferSize + ((long)section.StartIndex * indexSize);
                long idxLength = (long)section.PrimitiveCount * 3L * indexSize;

                if (!IsRangeValid(vtxOffset, vtxLength, lodBytes.Length) ||
                    !IsRangeValid(idxOffset, idxLength, lodBytes.Length))
                {
                    if (debug)
                        App.Logger.LogWarning(
                            $"[DEBUG] Skipped unsupported-section snapshot for '{section.Name}' due to out-of-range data.");
                    continue;
                }

                var snap = new UnsupportedSectionSnapshot
                {
                    Name = section.Name,
                    VertexStride = section.VertexStride,
                    VertexCount = section.VertexCount,
                    PrimitiveCount = section.PrimitiveCount,
                    VertexBytes = new byte[vtxLength],
                    IndexBytes = new byte[idxLength],
                    GeometryDeclDescValue = GetSectionFieldValue(section, "m_geometryDeclarationDesc"),
                    VertexStrideValue = GetSectionFieldValue(section, "m_vertexStride"),
                    BonesPerVertexValue = GetSectionFieldValue(section, "m_bonesPerVertex"),
                    PrimitiveTypeValue = GetSectionFieldValue(section, "m_primitiveType")
                };

                Buffer.BlockCopy(lodBytes, (int)vtxOffset, snap.VertexBytes, 0, (int)vtxLength);
                Buffer.BlockCopy(lodBytes, (int)idxOffset, snap.IndexBytes, 0, (int)idxLength);
                snapshots.Add(snap);

                if (debug)
                {
                    App.Logger.Log(
                        $"[DEBUG] Snapshot unsupported section '{snap.Name}' " +
                        $"(V={snap.VertexCount}, P={snap.PrimitiveCount}, Stride={snap.VertexStride})");
                }
            }

            return snapshots;
        }

        private static void RestoreUnsupportedSectionData(MeshSetLod lod, List<UnsupportedSectionSnapshot> snapshots, bool debug)
        {
            if (lod == null || snapshots == null || snapshots.Count == 0)
                return;

            byte[] lodBytes = ReadLodBytes(lod);
            if (lodBytes == null || lodBytes.Length == 0)
                return;

            int indexSize = lod.IndexUnitSize == 32 ? 4 : 2;
            int restored = 0;

            foreach (var snap in snapshots)
            {
                MeshSetSection target = lod.Sections.FirstOrDefault(
                    s => s != null && string.Equals(s.Name, snap.Name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    continue;

                if (target.VertexStride != snap.VertexStride ||
                    target.VertexCount != snap.VertexCount ||
                    target.PrimitiveCount != snap.PrimitiveCount)
                {
                    if (debug)
                    {
                        App.Logger.LogWarning(
                            $"[DEBUG] Unsupported-section restore skipped for '{snap.Name}' due to layout mismatch " +
                            $"(src V={snap.VertexCount}/P={snap.PrimitiveCount}/S={snap.VertexStride}, " +
                            $"dst V={target.VertexCount}/P={target.PrimitiveCount}/S={target.VertexStride}).");
                    }
                    continue;
                }

                long vtxOffset = target.VertexOffset;
                long idxOffset = (long)lod.VertexBufferSize + ((long)target.StartIndex * indexSize);
                long vtxLength = snap.VertexBytes.Length;
                long idxLength = snap.IndexBytes.Length;

                if (!IsRangeValid(vtxOffset, vtxLength, lodBytes.Length) ||
                    !IsRangeValid(idxOffset, idxLength, lodBytes.Length))
                {
                    if (debug)
                        App.Logger.LogWarning(
                            $"[DEBUG] Unsupported-section restore skipped for '{snap.Name}' due to out-of-range target offsets.");
                    continue;
                }

                Buffer.BlockCopy(snap.VertexBytes, 0, lodBytes, (int)vtxOffset, snap.VertexBytes.Length);
                Buffer.BlockCopy(snap.IndexBytes, 0, lodBytes, (int)idxOffset, snap.IndexBytes.Length);

                // Restore original section declaration metadata so unsupported
                // usages (for example SubMaterialIndex) remain intact.
                SetSectionFieldValue(target, "m_geometryDeclarationDesc", snap.GeometryDeclDescValue);
                SetSectionFieldValue(target, "m_vertexStride", snap.VertexStrideValue);
                SetSectionFieldValue(target, "m_bonesPerVertex", snap.BonesPerVertexValue);
                SetSectionFieldValue(target, "m_primitiveType", snap.PrimitiveTypeValue);

                restored++;
            }

            if (restored == 0)
                return;

            if (lod.ChunkId != Guid.Empty)
                App.AssetManager.ModifyChunk(lod.ChunkId, lodBytes);
            else
                lod.SetInlineData(lodBytes);

            if (debug)
                App.Logger.Log($"[DEBUG] Restored raw data for {restored} unsupported section(s) after import.");
        }

        private static byte[] ReadLodBytes(MeshSetLod lod)
        {
            using (Stream s = LodStreamHelper.GetLodStream(lod))
            {
                if (s == null)
                    return null;
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        private static bool SectionHasUnsupportedElements(MeshSetSection section)
        {
            if (section?.GeometryDeclDesc == null)
                return false;

            foreach (var decl in section.GeometryDeclDesc)
            {
                if (decl.Elements == null)
                    continue;
                for (int i = 0; i < decl.ElementCount; i++)
                {
                    if (decl.Elements[i].Usage == FrostySdk.VertexElementUsage.SubMaterialIndex)
                        return true;
                }
            }
            return false;
        }

        private static bool IsRangeValid(long offset, long length, int bufferLength)
        {
            if (offset < 0 || length < 0)
                return false;
            if (offset > bufferLength)
                return false;
            return offset + length <= bufferLength;
        }

        private static object GetSectionFieldValue(MeshSetSection section, string fieldName)
        {
            if (section == null || string.IsNullOrEmpty(fieldName))
                return null;
            var field = typeof(MeshSetSection).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(section);
        }

        private static void SetSectionFieldValue(MeshSetSection section, string fieldName, object value)
        {
            if (section == null || string.IsNullOrEmpty(fieldName))
                return;
            var field = typeof(MeshSetSection).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
                field.SetValue(section, value);
        }

        private sealed class CompositePartDataSnapshot
        {
            public List<AxisAlignedBox> MeshPartBoundingBoxes = new List<AxisAlignedBox>();
            public List<LinearTransform> MeshPartTransforms = new List<LinearTransform>();
            public List<LodPartDataSnapshot> Lods = new List<LodPartDataSnapshot>();
        }

        private sealed class LodPartDataSnapshot
        {
            public List<AxisAlignedBox> PartBoundingBoxes = new List<AxisAlignedBox>();
            public List<LinearTransform> PartTransforms = new List<LinearTransform>();
            public List<List<int>> PartIndices = new List<List<int>>();
        }

        private static CompositePartDataSnapshot CaptureCompositePartData(MeshSet meshSet, List<MeshSetLod> originalLods)
        {
            var snapshot = new CompositePartDataSnapshot();
            if (meshSet == null)
                return snapshot;

            var meshType = typeof(MeshSet);
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var meshPartBBoxesField = meshType.GetField("m_partBoundingBoxes", flags);
            var meshPartTransformsField = meshType.GetField("m_partTransforms", flags);

            if (meshPartBBoxesField?.GetValue(meshSet) is List<AxisAlignedBox> meshPartBBoxes)
                snapshot.MeshPartBoundingBoxes = new List<AxisAlignedBox>(meshPartBBoxes);
            if (meshPartTransformsField?.GetValue(meshSet) is List<LinearTransform> meshPartTransforms)
                snapshot.MeshPartTransforms = new List<LinearTransform>(meshPartTransforms);

            if (originalLods != null)
            {
                foreach (var lod in originalLods)
                {
                    var lodSnap = new LodPartDataSnapshot
                    {
                        PartBoundingBoxes = lod?.PartBoundingBoxes != null
                            ? new List<AxisAlignedBox>(lod.PartBoundingBoxes)
                            : new List<AxisAlignedBox>(),
                        PartTransforms = lod?.PartTransforms != null
                            ? new List<LinearTransform>(lod.PartTransforms)
                            : new List<LinearTransform>(),
                        PartIndices = lod?.PartIndices != null
                            ? lod.PartIndices.Select(x => x != null ? new List<int>(x) : new List<int>()).ToList()
                            : new List<List<int>>()
                    };
                    snapshot.Lods.Add(lodSnap);
                }
            }

            return snapshot;
        }

        private static void RestoreCompositePartData(MeshSet meshSet, CompositePartDataSnapshot snapshot, bool debug)
        {
            if (meshSet == null || snapshot == null)
                return;

            var meshType = typeof(MeshSet);
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var meshPartBBoxesField = meshType.GetField("m_partBoundingBoxes", flags);
            var meshPartTransformsField = meshType.GetField("m_partTransforms", flags);

            meshPartBBoxesField?.SetValue(meshSet, new List<AxisAlignedBox>(snapshot.MeshPartBoundingBoxes));
            meshPartTransformsField?.SetValue(meshSet, new List<LinearTransform>(snapshot.MeshPartTransforms));

            int lodCount = System.Math.Min(meshSet.Lods.Count, snapshot.Lods.Count);
            for (int i = 0; i < lodCount; i++)
            {
                var lod = meshSet.Lods[i];
                var lodSnap = snapshot.Lods[i];
                if (lod == null || lodSnap == null)
                    continue;

                var clonedIndices = lodSnap.PartIndices?
                    .Select(x => x != null ? new List<int>(x) : new List<int>())
                    .ToList() ?? new List<List<int>>();

                lod.SetParts(
                    new List<LinearTransform>(lodSnap.PartTransforms ?? new List<LinearTransform>()),
                    new List<AxisAlignedBox>(lodSnap.PartBoundingBoxes ?? new List<AxisAlignedBox>()),
                    clonedIndices);
            }

            if (debug)
            {
                App.Logger.Log(
                    $"[DEBUG] Restored composite part metadata: meshParts={snapshot.MeshPartBoundingBoxes.Count}, lods={lodCount}");
            }
        }

        private static bool IsCompositeAutoLodEnabledForBuild()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        private static MeshSetSection FindBestRenderableSourceSection(
            List<MeshSetSection> sourceRenderables,
            MeshSetSection targetSection,
            HashSet<int> usedSourceIndices,
            bool allowReuse = false)
        {
            if (sourceRenderables == null || targetSection == null)
                return null;

            string targetName = targetSection.Name ?? string.Empty;
            string targetNorm = NormalizeSectionName(targetName);

            // 1) Exact name match.
            for (int i = 0; i < sourceRenderables.Count; i++)
            {
                if (usedSourceIndices.Contains(i)) continue;
                if (string.Equals(sourceRenderables[i].Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    usedSourceIndices.Add(i);
                    return sourceRenderables[i];
                }
            }

            // 2) Normalized name match (handles "FooLOD" -> "Foo").
            for (int i = 0; i < sourceRenderables.Count; i++)
            {
                if (usedSourceIndices.Contains(i)) continue;
                if (NormalizeSectionName(sourceRenderables[i].Name) == targetNorm)
                {
                    usedSourceIndices.Add(i);
                    return sourceRenderables[i];
                }
            }

            if (allowReuse)
            {
                // 3) Reuse exact match even if already consumed (composite fallback mode).
                for (int i = 0; i < sourceRenderables.Count; i++)
                {
                    if (string.Equals(sourceRenderables[i].Name, targetName, StringComparison.OrdinalIgnoreCase))
                        return sourceRenderables[i];
                }

                // 4) Reuse normalized match even if already consumed.
                for (int i = 0; i < sourceRenderables.Count; i++)
                {
                    if (NormalizeSectionName(sourceRenderables[i].Name) == targetNorm)
                        return sourceRenderables[i];
                }
            }

            // Never reuse a source section for multiple targets; duplicated geometry can
            // inflate lower LODs and produce invalid composite layouts.
            return null;
        }

        private static string NormalizeSectionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string n = name.Trim().ToLowerInvariant();

            // Keep alnum only so variants like "Fuselage_LOD" and "Fuselage LOD" normalize similarly.
            var sb = new StringBuilder(n.Length);
            foreach (char c in n)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }
            n = sb.ToString();

            // Strip trailing LOD tags repeatedly:
            //   "fuselagelod"   -> "fuselage"
            //   "fuselagelod0"  -> "fuselage"
            //   "wingslod0lod1" -> "wings"
            // This handles common Blender/Frosty naming variants.
            while (!string.IsNullOrEmpty(n))
            {
                if (n.EndsWith("lod"))
                {
                    n = n.Substring(0, n.Length - 3);
                    continue;
                }

                int lodIdx = n.LastIndexOf("lod", StringComparison.Ordinal);
                if (lodIdx < 0 || lodIdx + 3 >= n.Length)
                    break;

                bool digitsOnly = true;
                for (int i = lodIdx + 3; i < n.Length; i++)
                {
                    if (!char.IsDigit(n[i]))
                    {
                        digitsOnly = false;
                        break;
                    }
                }

                if (!digitsOnly)
                    break;

                n = n.Substring(0, lodIdx);
            }

            return n;
        }

        /// <summary>
        /// Executes FBX import on the current mesh LOD set.
        /// Composite meshes must remain MeshType_Composite here so MeshSetPlugin's importer
        /// takes its composite-specific section path.
        /// </summary>
        private static void ImportFbxIntoCurrentLodSet(string inputPath, MeshSet meshSet, EbxAsset asset,
            EbxAssetEntry entry, AutoLodImportSettings settings)
        {
            new FBXImporter(App.Logger).ImportFBX(inputPath, meshSet, asset, entry, settings);
        }

        /// <summary>
        /// Clones section metadata including private fields (stride, geometry declaration,
        /// bones per vertex, primitive type) via reflection. Critical because stride-40 LODs
        /// need to be promoted to stride-52 to match cloned LOD0 data.
        /// </summary>
        private static void CloneSectionMetadata(MeshSetSection source, MeshSetSection target, bool copyMaterialMetadata = true)
        {
            target.VertexOffset = source.VertexOffset;
            target.StartIndex = source.StartIndex;
            target.VertexCount = source.VertexCount;
            target.PrimitiveCount = source.PrimitiveCount;
            target.SetBones(source.BoneList);

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var t = typeof(MeshSetSection);

            foreach (string fieldName in new[]
            {
                "m_vertexStride",
                "m_geometryDeclarationDesc",
                "m_bonesPerVertex",
                "m_primitiveType",
                "m_boundingBox"
            })
            {
                var field = t.GetField(fieldName, flags);
                if (field != null) field.SetValue(target, field.GetValue(source));
            }

            if (copyMaterialMetadata)
            {
                foreach (string fieldName in new[] { "m_materialId", "m_materialName", "m_lightMapUvMappingIndex", "m_texCoordRatios" })
                {
                    var field = t.GetField(fieldName, flags);
                    if (field != null) field.SetValue(target, field.GetValue(source));
                }
            }
        }

        public static void ResizeNextImportDialog(double height)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                try
                {
                    foreach (Window win in Application.Current.Windows)
                    {
                        if (win.GetType().Name.Contains("ImportExportBox") && win.IsVisible)
                        {
                            win.Height = height;
                            win.MinHeight = height;
                            break;
                        }
                    }
                }
                catch { }
            };
            timer.Start();
        }
    }
}
