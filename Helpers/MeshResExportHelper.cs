using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace MeshSetExtender.Helpers
{
    public struct MeshResExportResult
    {
        public int ResExported;
        public int ResFailed;
        public int ChunksExported;
    }

    /// <summary>
    /// Shared helper for exporting mesh res files and their linked chunks.
    /// Used by both the context menu and toolbar button.
    /// </summary>
    internal static class MeshResExportHelper
    {
        /// <summary>
        /// Discovers all res entries associated with a mesh asset:
        /// MeshSetResource, shader blocks, cloth data, and shader block depots.
        /// </summary>
        public static List<ResAssetEntry> CollectResEntries(EbxAssetEntry entry)
        {
            var result = new List<ResAssetEntry>();
            string assetPath = entry.Name;

            // 1) The main MeshSet resource via RID
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic meshAsset = (dynamic)asset.RootObject;
                ulong resRid = meshAsset.MeshSetResource;
                ResAssetEntry meshSetRes = App.AssetManager.GetResEntry(resRid);
                if (meshSetRes != null)
                    result.Add(meshSetRes);
            }
            catch (Exception ex)
            {
                App.Logger.Log($"Warning: Could not resolve MeshSetResource RID: {ex.Message}");
            }

            // 2) Res files that match the asset path pattern
            var searchPaths = new List<string>
            {
                assetPath,
                assetPath + "_mesh/blocks",
            };

            if (assetPath.EndsWith("_mesh"))
            {
                string basePath = assetPath.Substring(0, assetPath.Length - "_mesh".Length);
                searchPaths.Add(basePath + "_clothwrappingasset");

                int lastSlash = basePath.LastIndexOf('/');
                string assetNameOnly = lastSlash >= 0 ? basePath.Substring(lastSlash + 1) : basePath;
                string parentDir = lastSlash >= 0 ? basePath.Substring(0, lastSlash) : "";
                searchPaths.Add(parentDir + "/cloth/" + assetNameOnly + "_eacloth");
                // Newer SWBF2 assets use "_cloth" instead of "_eacloth".
                // GetResEntry returns null for non-existent paths, so adding both is safe.
                searchPaths.Add(parentDir + "/cloth/" + assetNameOnly + "_cloth");
            }

            foreach (string searchPath in searchPaths)
            {
                ResAssetEntry found = App.AssetManager.GetResEntry(searchPath);
                if (found != null && !result.Any(r => r.Name == found.Name))
                    result.Add(found);
            }

            // 3) Shader block depots that reference this mesh
            string meshPathLower = "/" + entry.Filename.ToLower();
            foreach (ResAssetEntry sbeEntry in App.AssetManager.EnumerateRes(resType: (uint)ResourceType.ShaderBlockDepot))
            {
                if (sbeEntry.Name.Contains(meshPathLower) && !result.Any(r => r.Name == sbeEntry.Name))
                    result.Add(sbeEntry);
            }

            return result;
        }

        /// <summary>
        /// Exports a list of res entries and their associated chunks to the given directory.
        /// </summary>
        public static MeshResExportResult Export(EbxAssetEntry entry, List<ResAssetEntry> resEntries, string exportDir, Action<string, double> progressCallback)
        {
            var result = new MeshResExportResult();

            for (int i = 0; i < resEntries.Count; i++)
            {
                ResAssetEntry resFile = resEntries[i];
                progressCallback?.Invoke($"Exporting {resFile.Filename}...", (double)i / resEntries.Count * 90.0);

                try
                {
                    string outPath = Path.Combine(exportDir, resFile.Filename + ".res");
                    string outDir = Path.GetDirectoryName(outPath);
                    if (!Directory.Exists(outDir))
                        Directory.CreateDirectory(outDir);

                    ExportResFile(resFile, outPath);
                    result.ResExported++;
                    App.Logger.Log($"Exported: {resFile.Name}");
                }
                catch (Exception ex)
                {
                    result.ResFailed++;
                    App.Logger.Log($"Failed to export {resFile.Name}: {ex.Message}");
                }
            }

            progressCallback?.Invoke("Exporting chunks...", 90);
            result.ChunksExported = ExportChunks(entry, resEntries, exportDir);

            return result;
        }

        /// <summary>
        /// Formats a human-readable summary string from an export result.
        /// </summary>
        public static string FormatSummary(MeshResExportResult result, string exportDir)
        {
            string summary = $"Exported {result.ResExported} res file(s)";
            if (result.ChunksExported > 0)
                summary += $" and {result.ChunksExported} chunk file(s)";
            if (result.ResFailed > 0)
                summary += $" ({result.ResFailed} failed)";
            summary += $" to:\n{exportDir}";
            return summary;
        }

        private static void ExportResFile(ResAssetEntry resEntry, string outputPath)
        {
            byte[] data = null;

            // For modified resources that only store a delta (DataObject),
            // GetRes returns original bytes. We need to load via GetResAs
            // and call SaveBytes() to get the fully resolved modified data.
            if (resEntry.IsModified && resEntry.ModifiedEntry?.DataObject != null)
            {
                try
                {
                    // Try ShaderBlockDepot first (most common delta-only resource)
                    var depot = App.AssetManager.GetResAs<ShaderBlockDepot>(resEntry);
                    if (depot != null)
                        data = depot.SaveBytes();
                }
                catch { /* fall through to GetRes */ }
            }

            if (data == null)
            {
                Stream resStream = App.AssetManager.GetRes(resEntry);
                if (resStream == null)
                    throw new Exception("Unable to read res data");
                using (NativeReader reader = new NativeReader(resStream))
                    data = reader.ReadToEnd();
            }

            // ResMeta on the entry is the *base* metadata. For a modified res the live metadata
            // (notably ByteSize) lives on ModifiedEntry — exporting the base copy produces a file
            // whose header contradicts its payload.
            byte[] meta = resEntry.ModifiedEntry?.ResMeta ?? resEntry.ResMeta;

            using (NativeWriter writer = new NativeWriter(new FileStream(outputPath, FileMode.Create)))
            {
                writer.Write(meta);
                writer.Write(data);
            }
        }

        private static int ExportChunks(EbxAssetEntry entry, List<ResAssetEntry> resEntries, string exportDir)
        {
            int count = 0;
            var exportedChunks = new HashSet<Guid>();

            // 1) Get chunks directly from MeshSet LOD ChunkIds
            try
            {
                EbxAsset asset = App.AssetManager.GetEbx(entry);
                dynamic meshAsset = (dynamic)asset.RootObject;
                ulong resRid = meshAsset.MeshSetResource;
                ResAssetEntry resEntry = App.AssetManager.GetResEntry(resRid);
                MeshSet meshSet = App.AssetManager.GetResAs<MeshSet>(resEntry);

                foreach (MeshSetLod lod in meshSet.Lods)
                {
                    if (lod.ChunkId == Guid.Empty || exportedChunks.Contains(lod.ChunkId))
                        continue;

                    count += ExportSingleChunk(lod.ChunkId, exportDir, exportedChunks);
                }
            }
            catch (Exception ex)
            {
                App.Logger.Log($"Warning: Could not export LOD chunks: {ex.Message}");
            }

            // 2) Also check LinkedAssets on all res entries (catches modified/linked chunks)
            try
            {
                foreach (ResAssetEntry resEntry in resEntries)
                {
                    foreach (AssetEntry linked in resEntry.LinkedAssets)
                    {
                        ChunkAssetEntry chunkEntry = linked as ChunkAssetEntry;
                        if (chunkEntry == null || exportedChunks.Contains(chunkEntry.Id))
                            continue;

                        count += ExportSingleChunk(chunkEntry.Id, exportDir, exportedChunks);
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.Log($"Warning: Could not export linked chunks: {ex.Message}");
            }

            return count;
        }

        private static int ExportSingleChunk(Guid chunkId, string exportDir, HashSet<Guid> exportedChunks)
        {
            ChunkAssetEntry chunkEntry = App.AssetManager.GetChunkEntry(chunkId);
            if (chunkEntry == null) return 0;

            Stream chunkStream = App.AssetManager.GetChunk(chunkEntry);
            if (chunkStream == null) return 0;

            string chunkPath = Path.Combine(exportDir, chunkId.ToString() + ".chunk");
            using (NativeWriter writer = new NativeWriter(new FileStream(chunkPath, FileMode.Create)))
            {
                using (NativeReader reader = new NativeReader(chunkStream))
                    writer.Write(reader.ReadToEnd());
            }

            exportedChunks.Add(chunkId);
            App.Logger.Log($"Exported chunk: {chunkId}");
            return 1;
        }
    }
}
