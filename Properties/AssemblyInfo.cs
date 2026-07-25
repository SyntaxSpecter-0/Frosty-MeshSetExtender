using MeshSetExtender.ContextMenus;
using MeshSetExtender.Patches;
using MeshSetExtender.Settings;
using MeshSetExtender.Extensions;
using Frosty.Core.Attributes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: ComVisible(false)]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]

[assembly: Guid("f1e2d3c4-b5a6-7890-abcd-ef1234567890")]

[assembly: PluginDisplayName("MeshSet Extender")]
[assembly: PluginAuthor("Claymaver")]
[assembly: PluginVersion("1.0.1.9")]

// ============================================
// MeshSet Exporter
// ============================================

// Context menu item (right-click in data explorer)
[assembly: RegisterDataExplorerContextMenu(typeof(ExportMeshResFilesContextMenu))]

// Harmony patches (toolbar buttons in mesh/texture editors)
[assembly: RegisterStartupAction(typeof(MeshSetExporterHarmonyInit))]

// ============================================
// Auto LOD Generator
// ============================================

// Harmony startup action (injects Auto LOD Import + Export Res/Chunk toolbar buttons
// into the mesh editor, and Auto LOD settings into the Mesh Options page)
[assembly: RegisterStartupAction(typeof(AutoLodHarmonyInit))]

// Standalone options page (kept as fallback, removed from sidebar by Harmony patch)
[assembly: RegisterOptionsExtension(typeof(AutoLodConfig))]

// ============================================
// Cloth Data Generator
// ============================================

// Context menu extension for mesh assets
[assembly: RegisterDataExplorerContextMenu(typeof(ClothDataContextMenuExtension))]

// Asset icons for cloth types in the data explorer
[assembly: RegisterAssetDefinition("ClothWrappingAsset", typeof(ClothWrappingAssetDefinition))]
[assembly: RegisterAssetDefinition("ClothAsset", typeof(ClothAssetDefinition))]
