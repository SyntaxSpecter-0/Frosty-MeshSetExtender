using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshSetExtender.Resources;
using Frosty.Core;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using MeshSetPlugin.Resources;

namespace MeshSetExtender.Core
{
    /// <summary>
    /// Per-vertex data extracted from a target mesh, including position and normal direction.
    /// Float normals (full Vector3) are used for direction-based matching against template normals.
    /// </summary>
    public struct ExtractedVertexData
    {
        public ClothVector3 Position;
        public float NormalX, NormalY, NormalZ;  // Full float normal for direction matching
        public bool HasNormal;
    }

    /// <summary>
    /// Settings for cloth data adaptation.
    /// </summary>
    public class ClothAdapterSettings
    {
        /// <summary>
        /// Precision for vertex hashing/deduplication (1-8, default 4).
        /// Controls the decimal places used when hashing vertex positions for cache lookups.
        /// Lower = more tolerant matching (faster, vertices further apart share results).
        /// Higher = stricter matching (slower, more precise).
        /// </summary>
        public int Precision { get; set; } = 6;

        private string _hashFormat;
        public string HashFormat
        {
            get
            {
                if (_hashFormat == null)
                    _hashFormat = "0." + new string('0', Precision);
                return _hashFormat;
            }
        }
    }

    /// <summary>
    /// Adapts cloth data from a template mesh to a target mesh.
    ///
    /// - Template mesh vertices are extracted as a position lookup
    /// - For each target vertex, find closest template MESH vertex (not cloth vertex)
    /// - Use that index to look up normals/tangents/bone weights from template CLOTH WRAPPING
    /// - EACloth is copied verbatim from template
    ///
    /// This two-step lookup (mesh position → cloth wrapping index) is critical because
    /// the cloth wrapping vertices correspond 1:1 with the mesh vertices.
    ///
    /// Uses precision-based hash caching to speed up vertex matching: vertices that round
    /// to the same position at the given precision share cached lookup results.
    /// </summary>
    public static class ClothDataAdapter
    {
        /// <summary>
        /// Adapts cloth wrapping data from template to target mesh.
        ///
        /// LOD0: bone weights matched by POSITION, normals/tangents matched by NORMAL DIRECTION
        /// LOD1+: bone weights matched by position against ADAPTED LOD0, normals by direction against template
        ///
        /// targetLodVertexData: per-LOD vertex data from the target mesh (index 0 = LOD0, etc.)
        ///   Only LOD0 is required; missing LODs will be copied from template.
        /// settings: precision and caching options (null uses defaults).
        /// </summary>
        public static ClothWrappingAssetParsed AdaptClothWrapping(
            ExtractedVertexData[][] targetLodVertexData,
            ExtractedVertexData[] templateVertexData,
            ClothWrappingAssetParsed templateClothData,
            ClothAdapterSettings settings = null)
        {
            if (settings == null)
                settings = new ClothAdapterSettings();

            if (targetLodVertexData == null || targetLodVertexData.Length == 0 || targetLodVertexData[0] == null)
                throw new ArgumentException("Target LOD0 vertex data cannot be empty");

            if (templateClothData?.MeshSections == null || templateClothData.MeshSections.Length == 0)
                throw new ArgumentException("Template cloth data has no mesh sections");

            var targetVertexData = targetLodVertexData[0]; // LOD0
            // Runtime safety: cloth LOD structure must follow the target mesh LOD structure.
            // Using template-only extra LODs can produce valid files that crash in-game.
            int sectionCount = targetLodVertexData.Length;
            var templateClothSection = templateClothData.MeshSections[0]; // LOD0

            bool hasTemplateMesh = templateVertexData != null && templateVertexData.Length > 0;

            // The template's cloth wrapping is indexed by template *mesh* vertex index, so the two
            // must have the same vertex count. When they don't, the template cloth is not the one
            // that belongs to this mesh — most often because a previous generation run overwrote a
            // cloth res that the template and target share. Continuing silently clamps every
            // out-of-range index onto the last cloth vertex and collapses the bone bindings.
            if (hasTemplateMesh && templateClothSection.VertexCount != templateVertexData.Length)
            {
                ClothLogger.LogWarning(
                    $"Template cloth wrapping has {templateClothSection.VertexCount} vertices but the template mesh has " +
                    $"{templateVertexData.Length}. These normally match. Mesh vertex indices above " +
                    $"{templateClothSection.VertexCount - 1} have no cloth vertex and will be clamped onto the last one, " +
                    "which collapses bone bindings. This often means the template's cloth wrapping was overwritten by an " +
                    "earlier generation run against a shared cloth resource.");
            }

            ClothLogger.Log($"Adapting cloth: {targetVertexData.Length} target verts, {templateClothSection.VertexCount} template verts, targetLODs={sectionCount}, templateLODs={templateClothData.MeshSections.Length}, precision={settings.Precision}");
            ClothLogger.LogDebug($"  Hash format: {settings.HashFormat}");
            ClothLogger.LogDebug($"  LOD0 bone weights: matched by position via {(hasTemplateMesh ? $"template mesh ({templateVertexData.Length} verts)" : "template cloth positions")} → cloth wrapping");
            ClothLogger.LogDebug($"  LOD0 normals/tangents: matched by normal direction via template mesh → cloth wrapping");
            ClothLogger.LogDebug($"  LOD1+: bone weights from adapted LOD0 (by position), normals from template (by direction)");

            // Create new cloth wrapping asset preserving ALL template structure
            var result = new ClothWrappingAssetParsed
            {
                BnryHeader = (byte[])templateClothData.BnryHeader.Clone(),
                UnknownField = templateClothData.UnknownField,
                LodCount = (uint)sectionCount,
                MeshSections = new ClothWrappingAssetParsed.MeshSection[sectionCount]
            };

            // --- LOD0: Adapt to target mesh ---
            result.MeshSections[0] = AdaptLod0(targetVertexData, templateVertexData, templateClothSection, hasTemplateMesh, settings);

            // --- LOD1+: Adapt against LOD0 ---
            var adaptedLod0 = result.MeshSections[0];
            for (int lodIdx = 1; lodIdx < sectionCount; lodIdx++)
            {
                int templateLodIdx = System.Math.Min(lodIdx, templateClothData.MeshSections.Length - 1);
                var templateLodSection = templateClothData.MeshSections[templateLodIdx];

                // Check if we have target mesh vertex data for this LOD
                ExtractedVertexData[] lodVertexData = (lodIdx < targetLodVertexData.Length && targetLodVertexData[lodIdx] != null)
                    ? targetLodVertexData[lodIdx]
                    : null;

                if (lodVertexData != null && lodVertexData.Length > 0)
                {
                    // Adapt this LOD: match against adapted LOD0 for bone weights,
                    // match against template for normals
                    result.MeshSections[lodIdx] = AdaptLodN(lodIdx, lodVertexData, adaptedLod0,
                        templateVertexData, templateClothSection, templateLodSection, hasTemplateMesh, settings);
                }
                else
                {
                    // No target mesh data for this LOD — copy from template
                    result.MeshSections[lodIdx] = CopySection(templateLodSection);
                    ClothLogger.LogDebug($"  LOD{lodIdx}: copied from template LOD{templateLodIdx} ({templateLodSection.VertexCount} verts, no target LOD data)");
                }
            }

            int adaptedCount = targetLodVertexData.Count(l => l != null && l.Length > 0);
            ClothLogger.Log($"Cloth adaptation complete: {adaptedCount} LODs adapted, {sectionCount - adaptedCount} copied from template");
            return result;
        }

        /// <summary>
        /// Copies template cloth data exactly as-is.
        /// Used when template and target are the same mesh asset — no adaptation needed.
        /// All data (positions, normals, tangents, bone weights) preserved verbatim.
        /// </summary>
        public static ClothWrappingAssetParsed CopyClothWrapping(
            ClothWrappingAssetParsed templateClothData)
        {
            int sectionCount = templateClothData.MeshSections.Length;
            var result = new ClothWrappingAssetParsed
            {
                BnryHeader = (byte[])templateClothData.BnryHeader.Clone(),
                UnknownField = templateClothData.UnknownField,
                LodCount = (uint)sectionCount,
                MeshSections = new ClothWrappingAssetParsed.MeshSection[sectionCount]
            };

            for (int lodIdx = 0; lodIdx < sectionCount; lodIdx++)
            {
                result.MeshSections[lodIdx] = CopySection(templateClothData.MeshSections[lodIdx]);
                ClothLogger.LogDebug($"  LOD{lodIdx}: exact copy from template ({templateClothData.MeshSections[lodIdx].VertexCount} verts)");
            }

            ClothLogger.Log($"Direct copy complete: {sectionCount} LODs copied verbatim from template");
            return result;
        }

        /// <summary>
        /// Hashes a ClothVector3 position using the given format string for cache lookups.
        /// </summary>
        private static string HashPosition(ClothVector3 pos, string hashFormat)
        {
            return pos.X.ToString(hashFormat) + "," + pos.Y.ToString(hashFormat) + "," + pos.Z.ToString(hashFormat);
        }

        /// <summary>
        /// Hashes a normal direction for cache lookups.
        /// </summary>
        private static string HashNormal(float nx, float ny, float nz, string hashFormat)
        {
            return nx.ToString(hashFormat) + "," + ny.ToString(hashFormat) + "," + nz.ToString(hashFormat);
        }

        /// <summary>
        /// Adapts LOD0 cloth data to target mesh.
        /// Bone weights: matched by position against template mesh → template cloth wrapping
        /// Normals/tangents: matched by normal direction against template mesh → template cloth wrapping
        /// Uses hash caching for performance.
        /// </summary>
        private static ClothWrappingAssetParsed.MeshSection AdaptLod0(
            ExtractedVertexData[] targetVertexData,
            ExtractedVertexData[] templateVertexData,
            ClothWrappingAssetParsed.MeshSection templateClothSection,
            bool hasTemplateMesh,
            ClothAdapterSettings settings)
        {
            var section = new ClothWrappingAssetParsed.MeshSection
            {
                UnknownId = templateClothSection.UnknownId,
                VertexCount = (uint)targetVertexData.Length,
                UnmappedBytes = (byte[])templateClothSection.UnmappedBytes.Clone(),
                Vertices = new ClothVertex[targetVertexData.Length]
            };

            // Hash caches: vertices at the same rounded position share lookup results
            var positionCache = new Dictionary<string, int>();
            var normalCache = new Dictionary<string, int>();
            string fmt = settings.HashFormat;
            int cacheHitsPos = 0, cacheHitsNorm = 0;
            int clampedPos = 0, clampedNorm = 0;
            float worstMatchDistSq = 0f;
            double matchDistSqSum = 0;
            int matchDistCount = 0;

            for (int i = 0; i < targetVertexData.Length; i++)
            {
                // --- Position lookup (for bone weights) with hash caching ---
                string posHash = HashPosition(targetVertexData[i].Position, fmt);
                int closestPosIdx;

                if (positionCache.ContainsKey(posHash))
                {
                    closestPosIdx = positionCache[posHash];
                    cacheHitsPos++;
                }
                else
                {
                    if (hasTemplateMesh)
                    {
                        closestPosIdx = FindClosestPositionInVertexData(targetVertexData[i].Position, templateVertexData);

                        // How far the nearest template vertex actually is. This is the direct
                        // measure of whether matching worked; a corrupt template makes it huge.
                        float d = targetVertexData[i].Position.DistanceSquared(templateVertexData[closestPosIdx].Position);
                        if (d > worstMatchDistSq) worstMatchDistSq = d;
                        matchDistSqSum += d;
                        matchDistCount++;

                        if (closestPosIdx >= templateClothSection.Vertices.Length)
                        {
                            // Behaviour preserved, but counted: every clamp here binds another
                            // vertex to the same last cloth vertex.
                            closestPosIdx = templateClothSection.Vertices.Length - 1;
                            clampedPos++;
                        }
                    }
                    else
                    {
                        closestPosIdx = FindClosestVertexIndex(targetVertexData[i].Position, templateClothSection.Vertices);
                    }
                    positionCache[posHash] = closestPosIdx;
                }

                var boneSource = templateClothSection.Vertices[closestPosIdx];

                // --- Normal lookup (for normals/tangents) with hash caching ---
                int closestNormalIdx;
                if (hasTemplateMesh && targetVertexData[i].HasNormal)
                {
                    string normHash = HashNormal(targetVertexData[i].NormalX, targetVertexData[i].NormalY, targetVertexData[i].NormalZ, fmt);

                    if (normalCache.ContainsKey(normHash))
                    {
                        closestNormalIdx = normalCache[normHash];
                        cacheHitsNorm++;
                    }
                    else
                    {
                        closestNormalIdx = FindClosestNormalIndex(targetVertexData[i], templateVertexData);
                        if (closestNormalIdx >= templateClothSection.Vertices.Length)
                        {
                            closestNormalIdx = templateClothSection.Vertices.Length - 1;
                            clampedNorm++;
                        }
                        normalCache[normHash] = closestNormalIdx;
                    }
                }
                else
                {
                    closestNormalIdx = closestPosIdx;
                }

                var normalSource = templateClothSection.Vertices[closestNormalIdx];

                section.Vertices[i] = new ClothVertex
                {
                    Position = targetVertexData[i].Position,
                    NormalX = normalSource.NormalX,
                    NormalY = normalSource.NormalY,
                    TangentX = normalSource.TangentX,
                    TangentY = normalSource.TangentY,
                    Weight0 = boneSource.Weight0,
                    Weight1 = boneSource.Weight1,
                    Weight2 = boneSource.Weight2,
                    Weight3 = boneSource.Weight3,
                    Index0 = boneSource.Index0,
                    Index1 = boneSource.Index1,
                    Index2 = boneSource.Index2,
                    Index3 = boneSource.Index3
                };

                if (i > 0 && i % 1000 == 0)
                    ClothLogger.LogDebug($"  LOD0: Processed {i}/{targetVertexData.Length} vertices...");
            }

            ClothLogger.Log($"  LOD0: {targetVertexData.Length} vertices adapted (dedup cache: {cacheHitsPos} pos hits/{positionCache.Count} unique, {cacheHitsNorm} normal hits/{normalCache.Count} unique)");

            if (matchDistCount > 0)
            {
                double meanDist = System.Math.Sqrt(matchDistSqSum / matchDistCount);
                double worstDist = System.Math.Sqrt(worstMatchDistSq);
                string quality = worstDist > 1.0
                    ? " -- TEMPLATE DOES NOT MATCH TARGET, check the template mesh for corrupt vertex data"
                    : "";
                ClothLogger.Log(
                    $"  LOD0: nearest-template-vertex distance mean={meanDist:F4}, worst={worstDist:F4} " +
                    $"over {matchDistCount} lookups{quality}");
            }

            if (clampedPos > 0 || clampedNorm > 0)
            {
                ClothLogger.LogWarning(
                    $"  LOD0: {clampedPos} position and {clampedNorm} normal lookups fell outside the template cloth " +
                    $"wrapping and were clamped onto its last vertex. Those vertices share one binding.");
            }

            // Distinct bone bindings is the direct health measure: a good adaptation produces
            // hundreds or thousands, a collapsed one produces a handful.
            var distinctBindings = new HashSet<string>();
            foreach (var cv in section.Vertices)
                distinctBindings.Add($"{cv.Index0},{cv.Index1},{cv.Index2},{cv.Index3}");

            string verdict = distinctBindings.Count < System.Math.Max(8, targetVertexData.Length / 100)
                ? " -- SUSPICIOUSLY LOW, cloth will likely render wrong"
                : "";
            ClothLogger.Log($"  LOD0: {distinctBindings.Count} distinct bone bindings across {targetVertexData.Length} vertices{verdict}");

            return section;
        }

        /// <summary>
        /// Adapts LOD1+ cloth data.
        /// - Bone weights: matched by position against ADAPTED LOD0 (ensures LOD consistency)
        /// - Normals/tangents: matched by normal direction against template mesh → template cloth wrapping
        /// Uses hash caching for performance.
        /// </summary>
        private static ClothWrappingAssetParsed.MeshSection AdaptLodN(
            int lodIdx,
            ExtractedVertexData[] lodVertexData,
            ClothWrappingAssetParsed.MeshSection adaptedLod0,
            ExtractedVertexData[] templateVertexData,
            ClothWrappingAssetParsed.MeshSection templateClothSection,
            ClothWrappingAssetParsed.MeshSection templateLodSection,
            bool hasTemplateMesh,
            ClothAdapterSettings settings)
        {
            var section = new ClothWrappingAssetParsed.MeshSection
            {
                UnknownId = templateLodSection.UnknownId,
                VertexCount = (uint)lodVertexData.Length,
                UnmappedBytes = (byte[])templateLodSection.UnmappedBytes.Clone(),
                Vertices = new ClothVertex[lodVertexData.Length]
            };

            // Hash caches for this LOD
            var positionCache = new Dictionary<string, int>();
            var normalCache = new Dictionary<string, int>();
            string fmt = settings.HashFormat;

            for (int i = 0; i < lodVertexData.Length; i++)
            {
                // Bone weights: match against ADAPTED LOD0 vertices by position (with cache)
                string posHash = HashPosition(lodVertexData[i].Position, fmt);
                int closestLod0Idx;

                if (positionCache.ContainsKey(posHash))
                {
                    closestLod0Idx = positionCache[posHash];
                }
                else
                {
                    closestLod0Idx = FindClosestVertexIndex(lodVertexData[i].Position, adaptedLod0.Vertices);
                    positionCache[posHash] = closestLod0Idx;
                }
                var boneSource = adaptedLod0.Vertices[closestLod0Idx];

                // Normals/tangents: match against template mesh by normal direction (with cache)
                int closestNormalIdx;
                if (hasTemplateMesh && lodVertexData[i].HasNormal)
                {
                    string normHash = HashNormal(lodVertexData[i].NormalX, lodVertexData[i].NormalY, lodVertexData[i].NormalZ, fmt);

                    if (normalCache.ContainsKey(normHash))
                    {
                        closestNormalIdx = normalCache[normHash];
                    }
                    else
                    {
                        closestNormalIdx = FindClosestNormalIndex(lodVertexData[i], templateVertexData);
                        if (closestNormalIdx >= templateClothSection.Vertices.Length)
                            closestNormalIdx = templateClothSection.Vertices.Length - 1;
                        normalCache[normHash] = closestNormalIdx;
                    }
                }
                else
                {
                    closestNormalIdx = FindClosestVertexIndex(lodVertexData[i].Position, templateClothSection.Vertices);
                }

                var normalSource = templateClothSection.Vertices[closestNormalIdx];

                section.Vertices[i] = new ClothVertex
                {
                    Position = lodVertexData[i].Position,
                    NormalX = normalSource.NormalX,
                    NormalY = normalSource.NormalY,
                    TangentX = normalSource.TangentX,
                    TangentY = normalSource.TangentY,
                    Weight0 = boneSource.Weight0,
                    Weight1 = boneSource.Weight1,
                    Weight2 = boneSource.Weight2,
                    Weight3 = boneSource.Weight3,
                    Index0 = boneSource.Index0,
                    Index1 = boneSource.Index1,
                    Index2 = boneSource.Index2,
                    Index3 = boneSource.Index3
                };
            }

            ClothLogger.Log($"  LOD{lodIdx}: {lodVertexData.Length} vertices adapted (bone weights from adapted LOD0, cache: {positionCache.Count} unique positions)");
            return section;
        }

        /// <summary>
        /// Deep copies a cloth wrapping section (for LODs where no target mesh data exists).
        /// </summary>
        private static ClothWrappingAssetParsed.MeshSection CopySection(ClothWrappingAssetParsed.MeshSection src)
        {
            var section = new ClothWrappingAssetParsed.MeshSection
            {
                UnknownId = src.UnknownId,
                VertexCount = src.VertexCount,
                UnmappedBytes = (byte[])src.UnmappedBytes.Clone(),
                Vertices = new ClothVertex[src.VertexCount]
            };

            for (int i = 0; i < src.VertexCount; i++)
            {
                var sv = src.Vertices[i];
                section.Vertices[i] = new ClothVertex
                {
                    Position = sv.Position,
                    NormalX = sv.NormalX, NormalY = sv.NormalY,
                    TangentX = sv.TangentX, TangentY = sv.TangentY,
                    Weight0 = sv.Weight0, Weight1 = sv.Weight1,
                    Weight2 = sv.Weight2, Weight3 = sv.Weight3,
                    Index0 = sv.Index0, Index1 = sv.Index1,
                    Index2 = sv.Index2, Index3 = sv.Index3
                };
            }

            return section;
        }

        private static int FindClosestVertexIndex(ClothVector3 position, ClothVertex[] vertices)
        {
            int closestIdx = 0;
            float closestDistSq = float.MaxValue;

            for (int i = 0; i < vertices.Length; i++)
            {
                float distSq = position.DistanceSquared(vertices[i].Position);
                if (distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    closestIdx = i;
                }
            }

            return closestIdx;
        }

        /// <summary>
        /// Finds the closest position in an array of ExtractedVertexData.
        /// Used for bone weight matching (position-based lookup).
        /// </summary>
        private static int FindClosestPositionInVertexData(ClothVector3 position, ExtractedVertexData[] vertices)
        {
            int closestIdx = 0;
            float closestDistSq = float.MaxValue;

            for (int i = 0; i < vertices.Length; i++)
            {
                float distSq = position.DistanceSquared(vertices[i].Position);
                if (distSq < closestDistSq)
                {
                    closestDistSq = distSq;
                    closestIdx = i;
                }
            }

            return closestIdx;
        }

        /// <summary>
        /// Finds the template vertex with the most similar normal direction (by dot product).
        /// Used for normal/tangent matching — matching by normal direction ensures correct
        /// cloth wrapping normals even when meshes differ spatially.
        /// </summary>
        private static int FindClosestNormalIndex(ExtractedVertexData target, ExtractedVertexData[] templateVerts)
        {
            int bestIdx = 0;
            float bestDot = float.MinValue;

            for (int i = 0; i < templateVerts.Length; i++)
            {
                if (!templateVerts[i].HasNormal) continue;

                float dot = target.NormalX * templateVerts[i].NormalX +
                            target.NormalY * templateVerts[i].NormalY +
                            target.NormalZ * templateVerts[i].NormalZ;
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestIdx = i;
                }
            }

            return bestIdx;
        }

        /// <summary>
        /// Extracts vertex positions and normals from a Frosty MeshSet LOD.
        /// Float normals (full Vector3) are stored for direction-based matching.
        ///
        /// Chunk layout: [prefix_data] [vertex_buffer] [index_buffer]
        /// vertexBufferStart = chunkLength - IndexBufferSize - VertexBufferSize
        /// vertex data = vertexBufferStart + section.VertexOffset + (v * stride) + elementOffset
        /// </summary>
        public static ExtractedVertexData[] ExtractMeshVertices(MeshSet meshSet, int lodIndex = 0)
        {
            if (meshSet?.Lods == null || meshSet.Lods.Count == 0)
                throw new ArgumentException("MeshSet has no LODs");

            if (lodIndex >= meshSet.Lods.Count)
                throw new ArgumentException($"LOD {lodIndex} does not exist (mesh has {meshSet.Lods.Count} LODs)");

            var lod = meshSet.Lods[lodIndex];
            var vertexData = new List<ExtractedVertexData>();

            // Get chunk data stream
            Stream chunkStream = null;
            
            if (lod.ChunkId != Guid.Empty)
            {
                var chunkEntry = App.AssetManager.GetChunkEntry(lod.ChunkId);
                if (chunkEntry != null)
                {
                    chunkStream = App.AssetManager.GetChunk(chunkEntry);
                }
            }
            
            if (chunkStream == null && lod.InlineData != null && lod.InlineData.Length > 0)
            {
                chunkStream = new MemoryStream(lod.InlineData);
            }

            if (chunkStream == null)
                throw new InvalidOperationException("Could not get mesh chunk data");

            // Calculate vertex buffer start offset (critical!)
            // Chunk layout: [prefix_data] [vertex_buffer] [index_buffer]
            long chunkLength = chunkStream.Length;
            long vertexBufferStart = chunkLength - lod.IndexBufferSize - lod.VertexBufferSize;

            ClothLogger.LogDebug($"  Chunk: {chunkLength} bytes, VertexBufferSize={lod.VertexBufferSize}, IndexBufferSize={lod.IndexBufferSize}, vertexBufferStart={vertexBufferStart}");
            ClothLogger.LogDebug($"  LOD{lodIndex} sections: {lod.Sections.Count}, IndexUnitSize={lod.IndexUnitSize}");

            // The engine's per-LOD vertex index follows buffer layout (VertexOffset), not the
            // order sections happen to be declared in. Those disagree on some LODs of composite
            // meshes, which silently phase-shifts the cloth data. Sort by VertexOffset; OrderBy
            // is stable, so declared order still breaks ties.
            var orderedSections = lod.Sections
                .Where(s => !string.IsNullOrEmpty(s.Name) && s.VertexCount != 0)
                .OrderBy(s => s.VertexOffset)
                .ToList();

            if (!orderedSections.SequenceEqual(lod.Sections.Where(s => !string.IsNullOrEmpty(s.Name) && s.VertexCount != 0)))
            {
                ClothLogger.LogWarning(
                    $"  LOD{lodIndex}: section declaration order does not match buffer order; " +
                    $"reordered to {string.Join(", ", orderedSections.Select(s => $"{s.Name}@{s.VertexOffset}"))}");
            }

            using (var reader = new NativeReader(chunkStream))
            {
                foreach (var section in orderedSections)
                {

                    var geomDecl = section.GeometryDeclDesc[0];

                    // Full geometry declaration. The reads below assume a single interleaved
                    // stream starting at VertexOffset; if positions live in a second stream, or a
                    // later GeometryDeclDesc applies, that assumption is wrong and positions
                    // decode to garbage while same-stream fields still look fine.
                    if (ClothLogger.DebugMode)
                    {
                        ClothLogger.LogDebug($"    GeomDecl: {section.GeometryDeclDesc.Length} declaration(s), using [0] with {geomDecl.ElementCount} element(s)");
                        for (int e = 0; e < geomDecl.ElementCount; e++)
                        {
                            var el = geomDecl.Elements[e];
                            ClothLogger.LogDebug($"      element[{e}]: usage={el.Usage}, format={el.Format}, offset={el.Offset}, stream={el.StreamIndex}");
                        }
                        for (int st = 0; st < geomDecl.Streams.Length; st++)
                        {
                            var strm = geomDecl.Streams[st];
                            if (strm.VertexStride == 0) continue;
                            ClothLogger.LogDebug($"      stream[{st}]: stride={strm.VertexStride}, classification={strm.Classification}");
                        }
                    }

                    // Find position element
                    int posOffset = -1;
                    int posStreamIdx = -1;
                    VertexElementFormat posFormat = VertexElementFormat.None;

                    // Find normal element
                    int normalOffset = -1;
                    int normalStreamIdx = -1;
                    VertexElementFormat normalFormat = VertexElementFormat.None;

                    for (int e = 0; e < geomDecl.ElementCount; e++)
                    {
                        var elem = geomDecl.Elements[e];
                        if (elem.Usage == VertexElementUsage.Pos)
                        {
                            posOffset = elem.Offset;
                            posStreamIdx = elem.StreamIndex;
                            posFormat = elem.Format;
                        }
                        else if (elem.Usage == VertexElementUsage.Normal)
                        {
                            normalOffset = elem.Offset;
                            normalStreamIdx = elem.StreamIndex;
                            normalFormat = elem.Format;
                        }
                    }

                    if (posOffset < 0)
                    {
                        ClothLogger.LogWarning($"  Section '{section.Name}': no position element, skipping");
                        continue;
                    }

                    int vertexStride = geomDecl.Streams[posStreamIdx].VertexStride;

                    // For normals/tangents in the same stream as position, we can read them
                    // using the same stride and base offset (interleaved layout).
                    // If they're in a different stream, we skip them and fall back to template values.
                    bool hasNormal = normalOffset >= 0 && normalStreamIdx == posStreamIdx;

                    ClothLogger.LogDebug($"  Section '{section.Name}': {section.VertexCount} verts, stride={vertexStride}, posOffset={posOffset}, posFormat={posFormat}, hasNormal={hasNormal} (offset={normalOffset}, fmt={normalFormat})");

                    // Read vertex data
                    for (uint v = 0; v < section.VertexCount; v++)
                    {
                        var vd = new ExtractedVertexData();

                        // Read position (same offset pattern as original)
                        reader.Position = vertexBufferStart + section.VertexOffset + (v * vertexStride) + posOffset;
                        
                        float x, y, z;
                        switch (posFormat)
                        {
                            case VertexElementFormat.Float3:
                                x = reader.ReadFloat();
                                y = reader.ReadFloat();
                                z = reader.ReadFloat();
                                break;
                            case VertexElementFormat.Float4:
                                x = reader.ReadFloat();
                                y = reader.ReadFloat();
                                z = reader.ReadFloat();
                                reader.ReadFloat(); // w
                                break;
                            case VertexElementFormat.Half3:
                                x = HalfUtils.Unpack(reader.ReadUShort());
                                y = HalfUtils.Unpack(reader.ReadUShort());
                                z = HalfUtils.Unpack(reader.ReadUShort());
                                break;
                            case VertexElementFormat.Half4:
                                x = HalfUtils.Unpack(reader.ReadUShort());
                                y = HalfUtils.Unpack(reader.ReadUShort());
                                z = HalfUtils.Unpack(reader.ReadUShort());
                                reader.ReadUShort(); // w
                                break;
                            default:
                                x = reader.ReadFloat();
                                y = reader.ReadFloat();
                                z = reader.ReadFloat();
                                break;
                        }
                        vd.Position = new ClothVector3(x, y, z);

                        // Read normal as full float Vector3 (for direction matching against template)
                        if (hasNormal)
                        {
                            reader.Position = vertexBufferStart + section.VertexOffset + (v * vertexStride) + normalOffset;

                            float nx, ny, nz;
                            switch (normalFormat)
                            {
                                case VertexElementFormat.Float3:
                                    nx = reader.ReadFloat();
                                    ny = reader.ReadFloat();
                                    nz = reader.ReadFloat();
                                    break;
                                case VertexElementFormat.Float4:
                                    nx = reader.ReadFloat();
                                    ny = reader.ReadFloat();
                                    nz = reader.ReadFloat();
                                    reader.ReadFloat(); // nw
                                    break;
                                case VertexElementFormat.Half3:
                                    nx = HalfUtils.Unpack(reader.ReadUShort());
                                    ny = HalfUtils.Unpack(reader.ReadUShort());
                                    nz = HalfUtils.Unpack(reader.ReadUShort());
                                    break;
                                case VertexElementFormat.Half4:
                                    nx = HalfUtils.Unpack(reader.ReadUShort());
                                    ny = HalfUtils.Unpack(reader.ReadUShort());
                                    nz = HalfUtils.Unpack(reader.ReadUShort());
                                    reader.ReadUShort(); // nw
                                    break;
                                default:
                                    nx = HalfUtils.Unpack(reader.ReadUShort());
                                    ny = HalfUtils.Unpack(reader.ReadUShort());
                                    nz = 0f;
                                    break;
                            }

                            vd.NormalX = nx;
                            vd.NormalY = ny;
                            vd.NormalZ = nz;
                            vd.HasNormal = true;
                        }

                        vertexData.Add(vd);
                    }

                    // Log first 3 vertex positions for debugging
                    int startIdx = vertexData.Count - (int)section.VertexCount;
                    for (int d = 0; d < System.Math.Min(3, (int)section.VertexCount); d++)
                    {
                        var dbg = vertexData[startIdx + d];
                        ClothLogger.LogDebug($"    v[{d}]: pos=({dbg.Position.X:F6}, {dbg.Position.Y:F6}, {dbg.Position.Z:F6}), normal=({dbg.NormalX:F4}, {dbg.NormalY:F4}, {dbg.NormalZ:F4})");

                        // Raw bytes for the same vertex. When the decoded position looks wrong,
                        // this distinguishes "the buffer really holds this" from "we decoded it wrong".
                        long rawStart = vertexBufferStart + section.VertexOffset + (d * vertexStride);
                        int rawLen = System.Math.Min(vertexStride, (int)(chunkLength - rawStart));
                        if (rawStart >= 0 && rawLen > 0)
                        {
                            reader.Position = rawStart;
                            byte[] raw = reader.ReadBytes(rawLen);
                            ClothLogger.LogDebug($"      raw @{rawStart} ({rawLen}B): {BitConverter.ToString(raw).Replace('-', ' ')}");
                        }
                    }

                    // Sanity check: how much of this section actually decoded to usable positions?
                    int degenerate = 0;
                    for (int d = 0; d < (int)section.VertexCount; d++)
                    {
                        var p = vertexData[startIdx + d].Position;
                        if ((p.X == 0f && p.Z == 0f) ||
                            float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z) ||
                            System.Math.Abs(p.X) > 1e6f || System.Math.Abs(p.Y) > 1e6f || System.Math.Abs(p.Z) > 1e6f)
                        {
                            degenerate++;
                        }
                    }
                    if (degenerate > 0)
                    {
                        ClothLogger.LogWarning(
                            $"    Section '{section.Name}': {degenerate}/{section.VertexCount} vertices decoded to " +
                            $"degenerate positions (zero/NaN/out-of-range). Position matching will not work against this mesh.");
                    }
                }
            }

            chunkStream.Dispose();

            ClothLogger.LogDebug($"Extracted {vertexData.Count} vertices from MeshSet");
            return vertexData.ToArray();
        }

    }
}
