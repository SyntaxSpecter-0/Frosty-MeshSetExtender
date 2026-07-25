using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Frosty.Controls;
using Frosty.Core;
using Frosty.Core.Windows;
using FrostySdk.Managers;
using FrostySdk.IO;
using MeshSetExtender.Core;
using MeshSetExtender.Helpers;
using MeshSetExtender.Resources;

namespace MeshSetExtender.Windows
{
    /// <summary>
    /// Cloth Data Generator Window - copies cloth data from template mesh to target mesh
    /// 
    /// WORKFLOW:
    /// 1. Target mesh is auto-loaded from selection (the mesh you want to add cloth to)
    /// 2. User selects a template mesh asset that has cloth (e.g., Leia's skirt)
    /// 3. Plugin auto-finds the template's ClothWrapping and EACloth resources
    /// 4. Plugin auto-finds target mesh's existing cloth resources (if any)
    /// 5. Generate copies the template cloth data to the target
    /// 
    /// BINARY FORMAT:
    /// - GetRes() returns data WITHOUT the 16-byte header (starts at BNRY)
    /// - ModifyRes() expects data WITH the 16-byte header
    /// - Header: [size (4 bytes, = content length)] [12 zeros] [BNRY content...]
    /// </summary>
    public partial class ClothDataGeneratorWindow : FrostyDockableWindow
    {
        // Target mesh (the one we want to add cloth to)
        private EbxAssetEntry _targetMeshAssetEntry;
        private MeshData _targetMeshData;
        
        // Template mesh (has existing cloth we want to copy from)
        private EbxAssetEntry _templateMeshAssetEntry;
        
        // Template cloth resource entries (auto-detected from template mesh)
        private ResAssetEntry _templateClothWrappingEntry;
        private ResAssetEntry _templateEAClothEntry;
        
        // Target cloth resource entries (auto-detected from target mesh, to replace)
        private ResAssetEntry _targetClothWrappingEntry;
        private ResAssetEntry _targetEAClothEntry;
        
        // Raw template bytes (from GetRes, WITHOUT 16-byte header)
        private byte[] _templateClothWrappingBytes;
        private byte[] _templateEAClothBytes;
        
        // Parsed cloth data for adaptation
        private ClothWrappingAssetParsed _templateClothWrappingParsed;
        
        // Parsed target cloth data (if target already has cloth, used for normals/tangents)
        private ClothWrappingAssetParsed _targetClothWrappingParsed;
        
        // Target mesh MeshSet for vertex extraction
        private MeshSetPlugin.Resources.MeshSet _targetMeshSet;
        private MeshSetPlugin.Resources.MeshSet _templateMeshSet;
        
        // Bundle IDs for new resources
        private List<int> _meshBundles = new List<int>();

        // Sibling meshes that share the target's EACloth (empty when not shared).
        private List<EbxAssetEntry> _sharedEAClothUsers = new List<EbxAssetEntry>();

        public ClothDataGeneratorWindow() : this(null)
        {
        }

        public ClothDataGeneratorWindow(EbxAssetEntry selectedAsset)
        {
            InitializeComponent();

            if (selectedAsset != null)
            {
                _targetMeshAssetEntry = selectedAsset;
            }
        }

        private void FrostyDockableWindow_FrostyLoaded(object sender, EventArgs e)
        {
            if (_targetMeshAssetEntry != null)
            {
                LoadTargetMesh(_targetMeshAssetEntry);
            }
            else
            {
                // Try to get currently selected asset
                AssetEntry currentSelection = App.EditorWindow?.DataExplorer?.SelectedAsset;
                if (currentSelection != null && currentSelection.Type.Contains("MeshAsset"))
                {
                    EbxAssetEntry ebxEntry = currentSelection as EbxAssetEntry;
                    if (ebxEntry != null)
                    {
                        LoadTargetMesh(ebxEntry);
                    }
                }
            }
        }

        #region Target Mesh Loading

        private void LoadTargetMesh(EbxAssetEntry entry)
        {
            try
            {
                _targetMeshAssetEntry = entry;
                AssetPathText.Text = entry.Name;
                
                string baseName = System.IO.Path.GetFileName(entry.Name).ToLower();
                NewResourceNameText.Text = baseName;

                FrostyTaskWindow.Show("Loading Target Mesh", "", (task) =>
                {
                    task.Update("Loading mesh data...");
                    LoadTargetMeshData();
                    
                    task.Update("Finding existing cloth resources...");
                    AutoDetectTargetClothResources(entry);
                    
                    // If target already has cloth wrapping, parse it for normals/tangents
                    if (_targetClothWrappingEntry != null)
                    {
                        task.Update("Loading target cloth data...");
                        LoadTargetClothWrapping();
                    }
                });

                if (_targetMeshData != null)
                {
                    MeshInfoText.Text = $"Verts: {_targetMeshData.VertexCount}, Tris: {_targetMeshData.TriangleCount}";
                }

                UpdateTargetResourcesUI();
                UpdateSharedClothWarning();

                StatusText.Text = _targetClothWrappingEntry != null || _targetEAClothEntry != null
                    ? "Target loaded with existing cloth. Select template mesh."
                    : "Target loaded (no cloth found). Select template mesh.";

                UpdateGenerateButton();
            }
            catch (Exception ex)
            {
                FrostyMessageBox.Show($"Error loading target: {ex.Message}", "Load Error", MessageBoxButton.OK);
                StatusText.Text = "Error loading target";
            }
        }

        private void LoadTargetMeshData()
        {
            var asset = App.AssetManager.GetEbx(_targetMeshAssetEntry);
            dynamic meshAsset = asset.RootObject;

            _meshBundles.Clear();
            _meshBundles.AddRange(_targetMeshAssetEntry.Bundles);
            if (_targetMeshAssetEntry.AddedBundles != null)
                _meshBundles.AddRange(_targetMeshAssetEntry.AddedBundles);

            ulong resRid = meshAsset.MeshSetResource;
            var resEntry = App.AssetManager.GetResEntry(resRid);

            if (resEntry == null)
                throw new Exception("Could not find mesh set resource");

            _targetMeshSet = App.AssetManager.GetResAs<MeshSetPlugin.Resources.MeshSet>(resEntry);
            _targetMeshData = ExtractMeshData(_targetMeshSet);
        }

        private MeshData ExtractMeshData(MeshSetPlugin.Resources.MeshSet meshSet)
        {
            var meshData = new MeshData();

            if (meshSet.Lods == null || meshSet.Lods.Count == 0)
                throw new Exception("No LODs found");

            var lod = meshSet.Lods[0];
            int vertexCount = 0;
            int indexCount = 0;

            foreach (var section in lod.Sections)
            {
                vertexCount += (int)section.VertexCount;
                // PrimitiveCount is number of triangles, multiply by 3 for indices
                indexCount += (int)section.PrimitiveCount * 3;
            }

            meshData.Vertices = new Math.Vector3d[vertexCount];
            meshData.Indices = new int[indexCount]; // Initialize indices array for proper TriangleCount
            return meshData;
        }

        /// <summary>
        /// Auto-detect cloth resources for the TARGET mesh
        /// Uses linked assets first, then name scoring as fallback.
        /// </summary>
        private void AutoDetectTargetClothResources(EbxAssetEntry meshEntry)
        {
            _targetClothWrappingEntry = null;
            _targetEAClothEntry = null;

            if (meshEntry == null || string.IsNullOrEmpty(meshEntry.Name))
                return;

            string meshAssetPath = meshEntry.Name;

            // Get parent path for search scope
            int lastSlash = meshAssetPath.LastIndexOf('/');
            string parentPath = lastSlash > 0 ? meshAssetPath.Substring(0, lastSlash).ToLower() : "";

            var clothWrappings = new List<ResAssetEntry>();
            var eaCloths = new List<ResAssetEntry>();

            foreach (var resEntry in App.AssetManager.EnumerateRes())
            {
                string resName = resEntry.Name.ToLower();

                // Must be in same directory tree
                if (!resName.StartsWith(parentPath)) continue;

                if (ClothAssetNaming.IsClothWrappingAsset(resName))
                {
                    clothWrappings.Add(resEntry);
                }
                else if (ClothAssetNaming.IsClothAsset(resName))
                {
                    eaCloths.Add(resEntry);
                }
            }

            string meshStem = GetMeshStemForMatching(meshAssetPath);
            var linkedClothWrappings = new List<ResAssetEntry>();
            var linkedEaCloths = new List<ResAssetEntry>();
            CollectLinkedClothResources(meshEntry, linkedClothWrappings, linkedEaCloths);

            var linkedCwNames = new HashSet<string>(linkedClothWrappings.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var linkedEcNames = new HashSet<string>(linkedEaCloths.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            var pairClothWrappings = MergeCandidates(linkedClothWrappings, clothWrappings);
            var pairEaCloths = MergeCandidates(linkedEaCloths, eaCloths);

            if (TrySelectBestPair(meshStem, pairClothWrappings, pairEaCloths,
                out var bestCw, out var bestEc, out int bestScore, out string bestCwStem, out string bestEcStem))
            {
                _targetClothWrappingEntry = bestCw;
                _targetEAClothEntry = bestEc;

                string cwSource = linkedCwNames.Contains(bestCw.Name) ? "linked" : "name-fallback";
                string ecSource = linkedEcNames.Contains(bestEc.Name) ? "linked" : "name-fallback";

                ClothLogger.LogDebug($"Auto-detected target ClothWrapping: {bestCw.Name} (stem={bestCwStem}, score={bestScore}, source={cwSource})");
                ClothLogger.LogDebug($"Auto-detected target EACloth: {bestEc.Name} (stem={bestEcStem}, score={bestScore}, source={ecSource})");
                return;
            }

            _targetClothWrappingEntry = FindBestByStem(meshStem, pairClothWrappings, GetClothWrappingStem);
            _targetEAClothEntry = FindBestByStem(meshStem, pairEaCloths, GetEAClothStem);

            if (_targetClothWrappingEntry != null)
            {
                string source = linkedCwNames.Contains(_targetClothWrappingEntry.Name) ? "linked" : "name-fallback";
                ClothLogger.LogDebug($"Auto-detected target ClothWrapping (single): {_targetClothWrappingEntry.Name} (source={source})");
            }
            if (_targetEAClothEntry != null)
            {
                string source = linkedEcNames.Contains(_targetEAClothEntry.Name) ? "linked" : "name-fallback";
                ClothLogger.LogDebug($"Auto-detected target EACloth (single): {_targetEAClothEntry.Name} (source={source})");
            }
        }

        private void UpdateTargetResourcesUI()
        {
            TargetClothWrappingText.Text = _targetClothWrappingEntry?.Name ?? "(Will create new)";
            TargetEAClothText.Text = _targetEAClothEntry?.Name ?? "(Will create new)";
        }

        /// <summary>
        /// Finds other mesh assets in the same parent folder that resolve to the
        /// same EACloth res entry as the target. Returns empty list if not shared.
        ///
        /// A sibling is only counted as a sharer if:
        ///   1. It has its OWN ClothWrappingAsset that stem-matches its mesh name
        ///      (CW is strictly per-mesh — no matching CW = the sibling has no cloth).
        ///   2. That sibling's CW pairs with the target's EACloth by name-stem, OR
        ///      the folder contains exactly one EACloth (the shared one), so any
        ///      sibling with a CW implicitly references it.
        /// </summary>
        private List<EbxAssetEntry> DetectSharedEAClothUsers(ResAssetEntry sharedEACloth, EbxAssetEntry targetMesh)
        {
            var sharers = new List<EbxAssetEntry>();
            if (sharedEACloth == null || targetMesh == null) return sharers;

            string targetPath = targetMesh.Name.ToLower();
            int lastSlash = targetPath.LastIndexOf('/');
            string parentPath = lastSlash > 0 ? targetPath.Substring(0, lastSlash) : "";

            // Collect all cloth res entries in the parent folder once.
            var folderClothWrappings = new List<ResAssetEntry>();
            var folderEACloths = new List<ResAssetEntry>();
            foreach (var resEntry in App.AssetManager.EnumerateRes())
            {
                string resName = resEntry.Name.ToLower();
                if (!resName.StartsWith(parentPath)) continue;
                if (ClothAssetNaming.IsClothWrappingAsset(resName))
                    folderClothWrappings.Add(resEntry);
                else if (ClothAssetNaming.IsClothAsset(resName))
                    folderEACloths.Add(resEntry);
            }

            string sharedFileStem = FileStem(ClothAssetNaming.StripClothSuffix(sharedEACloth.Name.ToLower()));

            foreach (var ebx in App.AssetManager.EnumerateEbx())
            {
                if (ebx == targetMesh) continue;
                if (ebx.Type == null || !ebx.Type.Contains("MeshAsset")) continue;

                string siblingPath = ebx.Name.ToLower();
                int siblingSlash = siblingPath.LastIndexOf('/');
                string siblingParent = siblingSlash > 0 ? siblingPath.Substring(0, siblingSlash) : "";
                if (!string.Equals(siblingParent, parentPath, StringComparison.OrdinalIgnoreCase)) continue;

                string siblingFile = siblingPath.Substring(siblingSlash + 1);
                string siblingStem = siblingFile.EndsWith("_mesh")
                    ? siblingFile.Substring(0, siblingFile.Length - "_mesh".Length)
                    : siblingFile;

                // Step 1: sibling must own a ClothWrappingAsset that stem-matches its mesh.
                ResAssetEntry siblingCW = null;
                foreach (var cw in folderClothWrappings)
                {
                    string cwFile = FileStem(cw.Name.ToLower().Replace("_clothwrappingasset", ""));
                    if (cwFile == siblingStem || cwFile.EndsWith(siblingStem) || siblingStem.EndsWith(cwFile))
                    {
                        siblingCW = cw;
                        break;
                    }
                }
                if (siblingCW == null) continue; // No CW → no cloth at all.

                // Step 2: does this sibling's CW pair with the target's EACloth?
                string siblingCwFile = FileStem(siblingCW.Name.ToLower().Replace("_clothwrappingasset", ""));

                bool paired = false;
                // Exact stem-pair: sibling CW stem matches shared EACloth stem.
                if (siblingCwFile == sharedFileStem
                    || siblingCwFile.EndsWith(sharedFileStem)
                    || sharedFileStem.EndsWith(siblingCwFile))
                {
                    paired = true;
                }
                // Fallback: if the folder has exactly one EACloth and it's the shared one,
                // every CW in the folder implicitly references it (ARC kama pattern).
                else if (folderEACloths.Count == 1 && folderEACloths[0] == sharedEACloth)
                {
                    paired = true;
                }

                if (paired) sharers.Add(ebx);
            }
            return sharers;
        }

        private static string FileStem(string pathLower)
        {
            int s = pathLower.LastIndexOf('/');
            return s >= 0 ? pathLower.Substring(s + 1) : pathLower;
        }

        private void UpdateSharedClothWarning()
        {
            _sharedEAClothUsers.Clear();
            if (_targetEAClothEntry == null || _targetMeshAssetEntry == null)
            {
                SharedClothWarningPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _sharedEAClothUsers = DetectSharedEAClothUsers(_targetEAClothEntry, _targetMeshAssetEntry);
            if (_sharedEAClothUsers.Count == 0)
            {
                SharedClothWarningPanel.Visibility = Visibility.Collapsed;
                return;
            }

            string firstFew = string.Join(", ", _sharedEAClothUsers.Take(3).Select(m => m.Filename));
            string suffix = _sharedEAClothUsers.Count > 3
                ? $" (+{_sharedEAClothUsers.Count - 3} more)"
                : "";
            SharedClothWarningText.Text =
                $"Shared with {_sharedEAClothUsers.Count} other mesh(es): {firstFew}{suffix}. " +
                "Replacing it will affect every mesh that references it.";
            SharedClothWarningPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Loads and parses the target's existing ClothWrappingAsset.
        /// This provides normals/tangents already in the correct cloth encoding,
        /// which avoids shadow artifacts from encoding mismatches.
        /// </summary>
        private void LoadTargetClothWrapping()
        {
            _targetClothWrappingParsed = null;
            
            try
            {
                byte[] targetClothBytes;
                using (var stream = App.AssetManager.GetRes(_targetClothWrappingEntry))
                {
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        targetClothBytes = ms.ToArray();
                    }
                }
                
                _targetClothWrappingParsed = new ClothWrappingAssetParsed();
                _targetClothWrappingParsed.Read(targetClothBytes);
                ClothLogger.LogDebug($"Parsed target ClothWrapping: {_targetClothWrappingParsed.LodCount} LODs, {_targetClothWrappingParsed.MeshSections[0].VertexCount} vertices in LOD0 (will use for normals/tangents)");
            }
            catch (Exception ex)
            {
                ClothLogger.LogWarning($"Could not parse target ClothWrapping: {ex.Message} (will fall back to mesh vertex normals)");
                _targetClothWrappingParsed = null;
            }
        }

        #endregion

        #region Template Selection

        /// <summary>
        /// Browse for a mesh asset to use as template
        /// </summary>
        private void BrowseTemplateMesh_Click(object sender, RoutedEventArgs e)
        {
            List<EbxAssetEntry> meshAssets = null;
            
            FrostyTaskWindow.Show("Finding Meshes with Cloth", "", (task) =>
            {
                task.Update("Scanning cloth resources...");
                
                // First pass: collect all parent paths that have cloth resources
                var pathsWithCloth = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                foreach (var resEntry in App.AssetManager.EnumerateRes())
                {
                    string resName = resEntry.Name.ToLower();
                    if (ClothAssetNaming.IsClothWrappingAsset(resName) || ClothAssetNaming.IsClothAsset(resName))
                    {
                        // Extract parent path
                        int lastSlash = resEntry.Name.LastIndexOf('/');
                        if (lastSlash > 0)
                        {
                            // Go up one more level for cloth resources in subdirs like /cloth/
                            string parentPath = resEntry.Name.Substring(0, lastSlash);
                            pathsWithCloth.Add(parentPath.ToLower());
                            
                            // Also add grandparent for cases like mesh/cloth/eacloth
                            int secondLastSlash = parentPath.LastIndexOf('/');
                            if (secondLastSlash > 0)
                            {
                                pathsWithCloth.Add(parentPath.Substring(0, secondLastSlash).ToLower());
                            }
                        }
                    }
                }
                
                task.Update($"Found {pathsWithCloth.Count} paths with cloth. Scanning meshes...");
                
                // Second pass: find mesh assets in those paths
                meshAssets = new List<EbxAssetEntry>();
                
                foreach (var ebxEntry in App.AssetManager.EnumerateEbx())
                {
                    if (ebxEntry.Type != null && ebxEntry.Type.Contains("SkinnedMeshAsset"))
                    {
                        string meshPath = ebxEntry.Name.ToLower();
                        int lastSlash = meshPath.LastIndexOf('/');
                        string parentPath = lastSlash > 0 ? meshPath.Substring(0, lastSlash) : meshPath;
                        
                        if (pathsWithCloth.Contains(parentPath))
                        {
                            meshAssets.Add(ebxEntry);
                        }
                    }
                }
                
                meshAssets.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            });
            
            if (meshAssets == null || meshAssets.Count == 0)
            {
                FrostyMessageBox.Show("No mesh assets with cloth data found.", "No Templates", MessageBoxButton.OK);
                return;
            }
            
            var dialog = new MeshBrowserDialog(meshAssets, "Select Template Mesh (with cloth)");
            if (dialog.ShowDialog() == true && dialog.SelectedMesh != null)
            {
                LoadTemplateMesh(dialog.SelectedMesh);
            }
        }

        private void LoadTemplateMesh(EbxAssetEntry meshEntry)
        {
            _templateMeshAssetEntry = meshEntry;
            _templateClothWrappingEntry = null;
            _templateEAClothEntry = null;
            _templateClothWrappingBytes = null;
            _templateEAClothBytes = null;
            _templateClothWrappingParsed = null;
            _templateMeshSet = null;
            
            try
            {
                FrostyTaskWindow.Show("Loading Template", "", (task) =>
                {
                    task.Update("Loading template mesh...");
                    LoadTemplateMeshSet(meshEntry);
                    
                    task.Update("Finding cloth resources...");
                    AutoDetectTemplateClothResources(meshEntry);
                    
                    if (_templateClothWrappingEntry != null)
                    {
                        task.Update("Loading ClothWrapping...");
                        LoadTemplateClothWrapping();
                    }
                    
                    if (_templateEAClothEntry != null)
                    {
                        task.Update("Loading EACloth...");
                        LoadTemplateEACloth();
                    }
                });
                
                // Update UI
                TemplateClothWrappingText.Text = _templateClothWrappingEntry?.Name ?? "(Not found)";
                TemplateEAClothText.Text = _templateEAClothEntry?.Name ?? "(Not found)";
                
                if (_templateClothWrappingBytes != null && _templateEAClothBytes != null)
                {
                    StatusText.Text = $"Template loaded: {meshEntry.Name}";
                }
                else
                {
                    StatusText.Text = "Warning: Could not load all template cloth data";
                }
                
                UpdateGenerateButton();
            }
            catch (Exception ex)
            {
                FrostyMessageBox.Show($"Error loading template: {ex.Message}", "Error", MessageBoxButton.OK);
                StatusText.Text = "Error loading template";
            }
        }

        private void LoadTemplateMeshSet(EbxAssetEntry meshEntry)
        {
            var asset = App.AssetManager.GetEbx(meshEntry);
            dynamic meshAsset = asset.RootObject;

            ulong resRid = meshAsset.MeshSetResource;
            var resEntry = App.AssetManager.GetResEntry(resRid);

            if (resEntry != null)
            {
                _templateMeshSet = App.AssetManager.GetResAs<MeshSetPlugin.Resources.MeshSet>(resEntry);
                ClothLogger.LogDebug($"Loaded template MeshSet: {meshEntry.Name}");
            }
        }

        /// <summary>
        /// Builds a comparable stem from a mesh path.
        /// Example: ".../obiwan_01_sleeves_cloth_mesh" -> "obiwan_01_sleeves"
        /// </summary>
        private static string GetMeshStemForMatching(string meshAssetPath)
        {
            string meshStem = FileStem(meshAssetPath.ToLower());
            if (meshStem.EndsWith("_mesh"))
                meshStem = meshStem.Substring(0, meshStem.Length - "_mesh".Length);
            return ClothAssetNaming.StripClothSuffix(meshStem);
        }

        /// <summary>
        /// Builds a comparable stem from a ClothWrapping resource name.
        /// Example: ".../obiwan_01_sleeves_clothwrappingasset" -> "obiwan_01_sleeves"
        /// </summary>
        private static string GetClothWrappingStem(string resNameLower)
        {
            string stem = resNameLower;
            if (stem.EndsWith("_clothwrappingasset"))
                stem = stem.Substring(0, stem.Length - "_clothwrappingasset".Length);
            else
                stem = stem.Replace("clothwrappingasset", "");
            return FileStem(stem);
        }

        /// <summary>
        /// Builds a comparable stem from an EACloth/Cloth resource name.
        /// Example: ".../cloth/obiwan_01_sleeves_eacloth" -> "obiwan_01_sleeves"
        /// </summary>
        private static string GetEAClothStem(string resNameLower)
        {
            return FileStem(ClothAssetNaming.StripClothSuffix(resNameLower));
        }

        /// <summary>
        /// Tokenizes a stem for similarity scoring, removing generic cloth words.
        /// </summary>
        private static string[] TokenizeStem(string stem)
        {
            if (string.IsNullOrEmpty(stem))
                return Array.Empty<string>();

            return stem.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t =>
                    !string.Equals(t, "mesh", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(t, "cloth", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(t, "eacloth", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(t, "clothwrappingasset", StringComparison.OrdinalIgnoreCase) &&
                    !t.StartsWith("lod", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        /// <summary>
        /// Scores how similar two stems are.
        /// Higher score means stronger confidence they're the same cloth "part".
        /// </summary>
        private static int ScoreStemSimilarity(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
                return 0;

            int score = 0;

            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                score += 500;

            if (left.EndsWith(right, StringComparison.OrdinalIgnoreCase)
                || right.EndsWith(left, StringComparison.OrdinalIgnoreCase)
                || left.Contains(right)
                || right.Contains(left))
            {
                score += 200;
            }

            string[] leftTokens = TokenizeStem(left);
            string[] rightTokens = TokenizeStem(right);
            if (leftTokens.Length == 0 || rightTokens.Length == 0)
                return score;

            var rightTokenSet = new HashSet<string>(rightTokens, StringComparer.OrdinalIgnoreCase);
            int overlap = leftTokens.Count(t => rightTokenSet.Contains(t));
            score += overlap * 30;

            if (string.Equals(leftTokens[leftTokens.Length - 1], rightTokens[rightTokens.Length - 1], StringComparison.OrdinalIgnoreCase))
                score += 120;

            return score;
        }

        /// <summary>
        /// Scores a CW/EA pair against the selected template mesh.
        /// </summary>
        private static int ScoreTemplatePair(string meshStem, string cwStem, string ecStem)
        {
            int cwToMesh = ScoreStemSimilarity(cwStem, meshStem);
            int ecToMesh = ScoreStemSimilarity(ecStem, meshStem);
            int cwToEc = ScoreStemSimilarity(cwStem, ecStem);
            return (cwToMesh * 2) + (ecToMesh * 2) + (cwToEc * 3);
        }

        /// <summary>
        /// Merges preferred + fallback candidate lists while preserving preferred order.
        /// </summary>
        private static List<ResAssetEntry> MergeCandidates(IEnumerable<ResAssetEntry> preferred, IEnumerable<ResAssetEntry> fallback)
        {
            var result = new List<ResAssetEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRange(IEnumerable<ResAssetEntry> source)
            {
                if (source == null) return;
                foreach (var entry in source)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
                    if (!seen.Add(entry.Name)) continue;
                    result.Add(entry);
                }
            }

            AddRange(preferred);
            AddRange(fallback);
            return result;
        }

        /// <summary>
        /// Picks the best CW/EA pair for a mesh stem using similarity scoring.
        /// </summary>
        private static bool TrySelectBestPair(
            string meshStem,
            IEnumerable<ResAssetEntry> clothWrappings,
            IEnumerable<ResAssetEntry> eaCloths,
            out ResAssetEntry bestCw,
            out ResAssetEntry bestEc,
            out int bestScore,
            out string bestCwStem,
            out string bestEcStem)
        {
            bestCw = null;
            bestEc = null;
            bestScore = int.MinValue;
            bestCwStem = null;
            bestEcStem = null;

            var cwList = clothWrappings?.Where(c => c != null).ToList() ?? new List<ResAssetEntry>();
            var ecList = eaCloths?.Where(c => c != null).ToList() ?? new List<ResAssetEntry>();
            if (cwList.Count == 0 || ecList.Count == 0)
                return false;

            foreach (var cw in cwList)
            {
                string cwStem = GetClothWrappingStem(cw.Name.ToLower());
                foreach (var ec in ecList)
                {
                    string ecStem = GetEAClothStem(ec.Name.ToLower());
                    int pairScore = ScoreTemplatePair(meshStem, cwStem, ecStem);
                    if (pairScore > bestScore)
                    {
                        bestScore = pairScore;
                        bestCw = cw;
                        bestEc = ec;
                        bestCwStem = cwStem;
                        bestEcStem = ecStem;
                    }
                }
            }

            return bestCw != null && bestEc != null;
        }

        /// <summary>
        /// Picks the single best candidate for the mesh stem.
        /// </summary>
        private static ResAssetEntry FindBestByStem(
            string meshStem,
            IEnumerable<ResAssetEntry> candidates,
            Func<string, string> stemSelector)
        {
            if (candidates == null) return null;

            return candidates
                .Where(c => c != null)
                .OrderByDescending(c => ScoreStemSimilarity(stemSelector(c.Name.ToLower()), meshStem))
                .FirstOrDefault();
        }

        /// <summary>
        /// Collects cloth candidates from linked assets for the given mesh.
        /// This is preferred over name search because links are explicit relationships.
        /// </summary>
        private void CollectLinkedClothResources(
            EbxAssetEntry meshEntry,
            List<ResAssetEntry> clothWrappings,
            List<ResAssetEntry> eaCloths)
        {
            if (meshEntry == null) return;

            var seenResNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(ResAssetEntry resEntry)
            {
                if (resEntry == null || string.IsNullOrEmpty(resEntry.Name)) return;
                if (!seenResNames.Add(resEntry.Name)) return;

                string resName = resEntry.Name.ToLower();
                if (ClothAssetNaming.IsClothWrappingAsset(resName))
                    clothWrappings.Add(resEntry);
                else if (ClothAssetNaming.IsClothAsset(resName))
                    eaCloths.Add(resEntry);
            }

            void CollectFromLinkedList(IEnumerable<AssetEntry> linkedAssets)
            {
                if (linkedAssets == null) return;

                foreach (var linked in linkedAssets)
                {
                    if (linked is ResAssetEntry linkedRes)
                    {
                        AddCandidate(linkedRes);

                        // One extra hop catches cases where cloth sits behind an intermediate linked res.
                        if (linkedRes.LinkedAssets != null)
                        {
                            foreach (var child in linkedRes.LinkedAssets)
                            {
                                if (child is ResAssetEntry childRes)
                                    AddCandidate(childRes);
                            }
                        }
                    }
                    else if (linked is EbxAssetEntry linkedEbx)
                    {
                        // Some links are EBX wrappers; resolve by name if there is a same-path res.
                        try
                        {
                            AddCandidate(App.AssetManager.GetResEntry(linkedEbx.Name));
                        }
                        catch { }
                    }
                }
            }

            CollectFromLinkedList(meshEntry.LinkedAssets);

            try
            {
                var meshAsset = App.AssetManager.GetEbx(meshEntry);
                dynamic root = meshAsset.RootObject;
                ulong meshSetResRid = root.MeshSetResource;
                var meshSetRes = App.AssetManager.GetResEntry(meshSetResRid);
                AddCandidate(meshSetRes);
                CollectFromLinkedList(meshSetRes?.LinkedAssets);
            }
            catch (Exception ex)
            {
                ClothLogger.LogDebug($"Linked cloth detection skipped (mesh resource lookup failed): {ex.Message}");
            }
        }

        /// <summary>
        /// Auto-detect cloth resources for the TEMPLATE mesh
        /// Chooses the best ClothWrapping + EACloth pair for the selected template mesh.
        /// Uses similarity scoring to avoid cross-mesh pair mixes (e.g. skirt + flaps).
        /// </summary>
        private void AutoDetectTemplateClothResources(EbxAssetEntry meshEntry)
        {
            if (meshEntry == null || string.IsNullOrEmpty(meshEntry.Name))
                return;

            string meshAssetPath = meshEntry.Name;
            string meshPathLower = meshAssetPath.ToLower();
            int lastSlash = meshPathLower.LastIndexOf('/');
            string parentPath = lastSlash > 0 ? meshPathLower.Substring(0, lastSlash) : "";
            
            // Collect all cloth resources for this mesh
            var clothWrappings = new List<ResAssetEntry>();
            var eaCloths = new List<ResAssetEntry>();
            
            foreach (var resEntry in App.AssetManager.EnumerateRes())
            {
                string resName = resEntry.Name.ToLower();
                
                if (!resName.StartsWith(parentPath)) continue;
                
                if (ClothAssetNaming.IsClothWrappingAsset(resName))
                {
                    clothWrappings.Add(resEntry);
                }
                else if (ClothAssetNaming.IsClothAsset(resName))
                {
                    eaCloths.Add(resEntry);
                }
            }

            string meshStem = GetMeshStemForMatching(meshAssetPath);

            var linkedClothWrappings = new List<ResAssetEntry>();
            var linkedEaCloths = new List<ResAssetEntry>();
            CollectLinkedClothResources(meshEntry, linkedClothWrappings, linkedEaCloths);

            var linkedCwNames = new HashSet<string>(linkedClothWrappings.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var linkedEcNames = new HashSet<string>(linkedEaCloths.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

            var pairClothWrappings = MergeCandidates(linkedClothWrappings, clothWrappings);
            var pairEaCloths = MergeCandidates(linkedEaCloths, eaCloths);

            if (TrySelectBestPair(meshStem, pairClothWrappings, pairEaCloths,
                out var bestCw, out var bestEc, out int bestScore, out string bestCwStem, out string bestEcStem))
            {
                _templateClothWrappingEntry = bestCw;
                _templateEAClothEntry = bestEc;

                string cwSource = linkedCwNames.Contains(bestCw.Name) ? "linked" : "name-fallback";
                string ecSource = linkedEcNames.Contains(bestEc.Name) ? "linked" : "name-fallback";

                ClothLogger.LogDebug("Found template pair by scoring:");
                ClothLogger.LogDebug($"  MeshStem: {meshStem}");
                ClothLogger.LogDebug($"  ClothWrapping: {bestCw.Name} (stem={bestCwStem}, source={cwSource})");
                ClothLogger.LogDebug($"  EACloth: {bestEc.Name} (stem={bestEcStem}, source={ecSource})");
                ClothLogger.LogDebug($"  PairScore: {bestScore}");
                return;
            }

            _templateClothWrappingEntry = FindBestByStem(meshStem, pairClothWrappings, GetClothWrappingStem);
            _templateEAClothEntry = FindBestByStem(meshStem, pairEaCloths, GetEAClothStem);

            if (_templateClothWrappingEntry != null)
            {
                string source = linkedCwNames.Contains(_templateClothWrappingEntry.Name) ? "linked" : "name-fallback";
                ClothLogger.LogDebug($"Found template ClothWrapping (single): {_templateClothWrappingEntry.Name} (source={source})");
            }
            if (_templateEAClothEntry != null)
            {
                string source = linkedEcNames.Contains(_templateEAClothEntry.Name) ? "linked" : "name-fallback";
                ClothLogger.LogDebug($"Found template EACloth (single): {_templateEAClothEntry.Name} (source={source})");
            }
        }

        private void LoadTemplateClothWrapping()
        {
            using (var stream = App.AssetManager.GetRes(_templateClothWrappingEntry))
            {
                _templateClothWrappingBytes = ReadAllBytes(stream);
            }
            ClothLogger.LogDebug($"Loaded ClothWrapping: {_templateClothWrappingBytes.Length} bytes");
            
            // Parse the cloth wrapping data for adaptation
            try
            {
                _templateClothWrappingParsed = new ClothWrappingAssetParsed();
                _templateClothWrappingParsed.Read(_templateClothWrappingBytes);
                ClothLogger.LogDebug($"Parsed ClothWrapping: {_templateClothWrappingParsed.LodCount} LODs, {_templateClothWrappingParsed.MeshSections.Length} sections, {_templateClothWrappingParsed.MeshSections[0].VertexCount} vertices in LOD0");
            }
            catch (Exception ex)
            {
                ClothLogger.LogWarning($"Could not parse ClothWrapping: {ex.Message}");
                _templateClothWrappingParsed = null;
            }
        }

        private void LoadTemplateEACloth()
        {
            using (var stream = App.AssetManager.GetRes(_templateEAClothEntry))
            {
                _templateEAClothBytes = ReadAllBytes(stream);
            }
            ClothLogger.LogDebug($"Loaded EACloth: {_templateEAClothBytes.Length} bytes");
        }

        private byte[] ReadAllBytes(Stream stream)
        {
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }

        #endregion

        #region Manual Target Browsing

        private void BrowseTargetClothWrapping_Click(object sender, RoutedEventArgs e)
        {
            var entry = BrowseForClothResource("Select ClothWrapping to Replace", ClothAssetNaming.IsClothWrappingAsset, "ClothWrapping");
            if (entry != null)
            {
                _targetClothWrappingEntry = entry;
                TargetClothWrappingText.Text = entry.Name;
                StatusText.Text = $"Will replace: {entry.Name}";
            }
        }

        private void BrowseTargetEACloth_Click(object sender, RoutedEventArgs e)
        {
            var entry = BrowseForClothResource("Select EACloth / Cloth to Replace", ClothAssetNaming.IsClothAsset, "EACloth/Cloth");
            if (entry != null)
            {
                _targetEAClothEntry = entry;
                TargetEAClothText.Text = entry.Name;
                StatusText.Text = $"Will replace: {entry.Name}";
                UpdateSharedClothWarning();
            }
        }

        private void ClearTargetClothWrapping_Click(object sender, RoutedEventArgs e)
        {
            _targetClothWrappingEntry = null;
            _targetClothWrappingParsed = null;
            TargetClothWrappingText.Text = "(Will create new)";
            StatusText.Text = "Cleared target ClothWrapping";
        }

        private void ClearTargetEACloth_Click(object sender, RoutedEventArgs e)
        {
            _targetEAClothEntry = null;
            TargetEAClothText.Text = "(Will create new)";
            StatusText.Text = "Cleared target EACloth";
            UpdateSharedClothWarning();
        }

        private ResAssetEntry BrowseForClothResource(string title, Func<string, bool> predicate, string kindLabel)
        {
            var resources = new List<ResAssetEntry>();

            foreach (var resEntry in App.AssetManager.EnumerateRes())
            {
                if (predicate(resEntry.Name.ToLower()))
                {
                    resources.Add(resEntry);
                }
            }

            if (resources.Count == 0)
            {
                FrostyMessageBox.Show($"No {kindLabel} resources found.", "No Resources", MessageBoxButton.OK);
                return null;
            }

            resources.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var dialog = new ResourceBrowserDialog(resources, title);
            if (dialog.ShowDialog() == true)
            {
                return dialog.SelectedResource;
            }

            return null;
        }

        #endregion

        private void UpdateGenerateButton()
        {
            generateButton.IsEnabled = _templateClothWrappingBytes != null && _templateEAClothBytes != null;
        }

        /// <summary>
        /// Prepends the 16-byte header to content bytes
        /// Header: [size (4 bytes)] [12 zeros]
        /// This is required because GetRes() strips the header but ModifyRes() expects it
        /// </summary>
        private byte[] PrependHeader(byte[] contentBytes)
        {
            byte[] result = new byte[contentBytes.Length + 16];
            
            // Size field = content length (little-endian)
            uint size = (uint)contentBytes.Length;
            result[0] = (byte)(size & 0xFF);
            result[1] = (byte)((size >> 8) & 0xFF);
            result[2] = (byte)((size >> 16) & 0xFF);
            result[3] = (byte)((size >> 24) & 0xFF);
            
            // Bytes 4-15 are zeros (already initialized)
            
            // Copy content at offset 16
            Array.Copy(contentBytes, 0, result, 16, contentBytes.Length);
            
            return result;
        }

        /// <summary>
        /// Builds updated ResMeta by preserving the existing metadata bytes and only
        /// updating ByteSize (first 4 bytes) to the new content length.
        /// </summary>
        private static byte[] BuildUpdatedResMeta(ResAssetEntry entry, int contentLength)
        {
            byte[] meta = null;

            if (entry?.ResMeta != null && entry.ResMeta.Length > 0)
            {
                meta = (byte[])entry.ResMeta.Clone();
            }
            else
            {
                meta = new byte[16];
            }

            if (meta.Length < 4)
            {
                var expanded = new byte[16];
                Array.Copy(meta, expanded, meta.Length);
                meta = expanded;
            }

            BitConverter.GetBytes((uint)contentLength).CopyTo(meta, 0);
            return meta;
        }

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_templateClothWrappingBytes == null || _templateEAClothBytes == null)
            {
                FrostyMessageBox.Show("Please select a template mesh first.", "Template Required", MessageBoxButton.OK);
                return;
            }

            if (_targetMeshSet == null)
            {
                FrostyMessageBox.Show("Target mesh not loaded.", "Error", MessageBoxButton.OK);
                return;
            }

            if (_targetEAClothEntry != null && _sharedEAClothUsers != null && _sharedEAClothUsers.Count > 0)
            {
                string list = string.Join("\n  • ", _sharedEAClothUsers.Select(m => m.Name));
                var result = FrostyMessageBox.Show(
                    $"The EACloth resource you're about to replace is shared with {_sharedEAClothUsers.Count} other mesh(es):\n\n  • {list}\n\n" +
                    "These meshes will all receive the new cloth data. Continue?",
                    "Shared Cloth Asset",
                    MessageBoxButton.YesNo);
                if (result != MessageBoxResult.Yes) return;
            }

            // Writing to the resource we just read the template from destroys the template. The
            // run still "succeeds", but every later run reads back the corrupted data, so the
            // damage compounds silently. This happens whenever a duplicated mesh still points at
            // the source mesh's cloth resources.
            if (_targetClothWrappingEntry != null && _templateClothWrappingEntry != null &&
                string.Equals(_targetClothWrappingEntry.Name, _templateClothWrappingEntry.Name, StringComparison.OrdinalIgnoreCase))
            {
                var shared = FrostyMessageBox.Show(
                    $"The target and template share one ClothWrapping resource:\n\n  {_targetClothWrappingEntry.Name}\n\n" +
                    "Generating overwrites the template with the generated data, so every later run against this " +
                    "template reads back the new data instead of the original. This usually means the target is a " +
                    "duplicated asset that still references the source mesh's cloth.\n\n" +
                    "Continue anyway?",
                    "Target and template share a resource", MessageBoxButton.YesNo);
                ClothLogger.LogWarning(
                    $"Target and template both resolve to ClothWrapping '{_targetClothWrappingEntry.Name}' " +
                    $"(user chose {(shared == MessageBoxResult.Yes ? "continue" : "cancel")}).");
                if (shared != MessageBoxResult.Yes) return;
            }

            if (_targetEAClothEntry != null && _templateEAClothEntry != null &&
                string.Equals(_targetEAClothEntry.Name, _templateEAClothEntry.Name, StringComparison.OrdinalIgnoreCase))
            {
                var sharedEc = FrostyMessageBox.Show(
                    $"The target and template share one EACloth resource:\n\n  {_targetEAClothEntry.Name}\n\n" +
                    "Generating overwrites the template's copy. Continue anyway?",
                    "Target and template share a resource", MessageBoxButton.YesNo);
                ClothLogger.LogWarning(
                    $"Target and template both resolve to EACloth '{_targetEAClothEntry.Name}' " +
                    $"(user chose {(sharedEc == MessageBoxResult.Yes ? "continue" : "cancel")}).");
                if (sharedEc != MessageBoxResult.Yes) return;
            }

            string newResourceName = NewResourceNameText.Text.Trim().ToLower();
            int precision = (int)PrecisionSlider.Value;
            ClothLogger.DebugMode = DebugCheckBox.IsChecked == true;
            var targetClothWrappingEntry = _targetClothWrappingEntry;
            var targetEAClothEntry = _targetEAClothEntry;
            var meshBundles = _meshBundles.ToArray();
            
            byte[] adaptedClothWrappingBytes = null;
            byte[] eaClothBytes = null;

            try
            {
                FrostyTaskWindow.Show("Generating Cloth Data", "", (task) =>
                {
                    // Step 1: Extract target mesh vertices for all LODs
                    task.Update("Extracting target mesh vertices...");
                    int targetLodCount = _targetMeshSet.Lods.Count;
                    int templateLodCount = _templateClothWrappingParsed.MeshSections.Length;
                    int lodCount = targetLodCount;
                    var targetLodVertexData = new ExtractedVertexData[lodCount][];

                    if (templateLodCount < targetLodCount)
                    {
                        ClothLogger.LogWarning($"Template cloth has fewer LODs ({templateLodCount}) than target mesh ({targetLodCount}). Higher target LODs will reuse the highest available template LOD metadata.");
                    }

                    for (int lod = 0; lod < lodCount; lod++)
                    {
                        try
                        {
                            targetLodVertexData[lod] = ClothDataAdapter.ExtractMeshVertices(_targetMeshSet, lod);
                            ClothLogger.LogDebug($"Target mesh LOD{lod}: {targetLodVertexData[lod].Length} vertices");
                        }
                        catch (Exception ex)
                        {
                            ClothLogger.LogWarning($"Could not extract target LOD{lod}: {ex.Message}");
                            targetLodVertexData[lod] = null;
                        }
                    }

                    // Step 2: Extract template mesh vertices (positions + normals for matching)
                    // If template and target are the same asset, the mesh has been replaced by import
                    // so mesh vertices no longer match the cloth wrapping — skip extraction and
                    // let the adapter match directly against cloth wrapping positions instead
                    task.Update("Extracting template mesh vertices...");
                    ExtractedVertexData[] templateVertexData = null;
                    bool isSameAsset = _templateMeshAssetEntry != null && _targetMeshAssetEntry != null
                        && _templateMeshAssetEntry.Name == _targetMeshAssetEntry.Name;

                    if (isSameAsset)
                    {
                        ClothLogger.Log("Template and target are the same asset — matching against cloth wrapping positions directly");
                    }
                    else if (_templateMeshSet != null)
                    {
                        templateVertexData = ClothDataAdapter.ExtractMeshVertices(_templateMeshSet);
                        ClothLogger.LogDebug($"Template mesh: {templateVertexData.Length} vertices");
                    }

                    // Step 3: Adapt cloth wrapping data for all LODs
                    task.Update("Adapting cloth data to target mesh...");
                    var adapterSettings = new ClothAdapterSettings { Precision = precision };
                    var adaptedClothWrapping = ClothDataAdapter.AdaptClothWrapping(targetLodVertexData, templateVertexData, _templateClothWrappingParsed, adapterSettings);
                    
                    // Step 3: Write adapted cloth wrapping to bytes (content only, starts at BNRY)
                    task.Update("Writing ClothWrappingAsset...");
                    adaptedClothWrappingBytes = adaptedClothWrapping.Write();
                    ClothLogger.LogDebug($"Generated ClothWrapping: {adaptedClothWrappingBytes.Length} bytes");
                    
                    // Step 4: EACloth copied from template
                    eaClothBytes = _templateEAClothBytes;
                    ClothLogger.LogDebug($"EACloth: {eaClothBytes.Length} bytes (copied from template)");
                    
                    // Step 5: Write to Frosty project
                    task.Update("Saving to project...");
                    
                    if (targetClothWrappingEntry != null)
                    {
                        byte[] cwMeta = BuildUpdatedResMeta(targetClothWrappingEntry, adaptedClothWrappingBytes.Length);
                        App.AssetManager.ModifyRes(targetClothWrappingEntry.ResRid, adaptedClothWrappingBytes, cwMeta);
                        ClothLogger.Log($"Replaced ClothWrapping: {targetClothWrappingEntry.Name}");
                    }
                    else
                    {
                        string resourceName = newResourceName + "_clothwrappingasset";
                        App.AssetManager.AddRes(resourceName, ResourceType.EAClothAssetData, null, adaptedClothWrappingBytes, meshBundles);
                        ClothLogger.Log($"Created ClothWrapping: {resourceName}");
                    }
                    
                    if (targetEAClothEntry != null)
                    {
                        byte[] eaMeta = BuildUpdatedResMeta(targetEAClothEntry, eaClothBytes.Length);
                        App.AssetManager.ModifyRes(targetEAClothEntry.ResRid, eaClothBytes, eaMeta);
                        ClothLogger.Log($"Replaced EACloth: {targetEAClothEntry.Name}");
                    }
                    else
                    {
                        string resourceName = newResourceName + "_eacloth";
                        App.AssetManager.AddRes(resourceName, ResourceType.EAClothData, null, eaClothBytes, meshBundles);
                        ClothLogger.Log($"Created EACloth: {resourceName}");
                    }

                    task.Update("Complete!");
                });

                StatusText.Text = "Complete! Save your project.";
                FrostyMessageBox.Show(
                    "Cloth data generated!\n\n" +
                    $"ClothWrapping: {adaptedClothWrappingBytes?.Length ?? 0} bytes\n" +
                    $"EACloth: {eaClothBytes?.Length ?? 0} bytes\n\n" +
                    "Save your project to apply changes.",
                    "Complete", MessageBoxButton.OK);
                
                // Close the generator window after successful generation
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                FrostyMessageBox.Show($"Error: {ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK);
                StatusText.Text = "Error during generation";
                ClothLogger.LogError($"Generation error: {ex}");
            }
        }

        private void PrecisionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PrecisionValueText != null)
                PrecisionValueText.Text = ((int)e.NewValue).ToString();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AssetPathText_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {

        }
    }
}
