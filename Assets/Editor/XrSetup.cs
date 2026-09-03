using System.IO;
using System.Linq;
using Unity.XR.MockHMD;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.XR.Management;

namespace HouseScan.EditorTools
{
    /// <summary>
    /// Configures XR Management with the Mock HMD provider so stereo rendering can
    /// be exercised in a built player without a headset, and builds that player.
    ///
    /// Mock HMD ships native plugins for Windows/macOS/Android but not for Linux
    /// desktop, so the stereo player is built for Windows and run under Wine.
    /// </summary>
    public static class XrSetup
    {
        const string kXrDir = "Assets/XR";
        const string kLoaderPath = kXrDir + "/MockHMDLoader.asset";
        const string kGeneralPath = kXrDir + "/XRGeneralSettings.asset";
        const string kMockSettingsPath = kXrDir + "/MockHMDBuildSettings.asset";
        const string kPerTargetPath = kXrDir + "/XRGeneralSettingsPerBuildTarget.asset";

        public static void SetupMockHmd()
        {
            Directory.CreateDirectory(kXrDir);

            var perTarget = GetOrCreatePerBuildTargetSettings();
            var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Standalone);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                settings.name = "Standalone Settings";
                AssetDatabase.AddObjectToAsset(settings, perTarget);
                perTarget.SetSettingsForBuildTarget(BuildTargetGroup.Standalone, settings);
            }

            if (settings.Manager == null)
            {
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = "Standalone Providers";
                AssetDatabase.AddObjectToAsset(manager, perTarget);
                settings.Manager = manager;
            }

            settings.InitManagerOnStart = true;

            // Registering through the metadata store is what writes the provider
            // into the build, rather than merely referencing the loader asset.
            bool added = XRPackageMetadataStore.AssignLoader(
                settings.Manager, nameof(MockHMDLoader), BuildTargetGroup.Standalone);
            Debug.Log($"[XrSetup] MockHMDLoader assigned: {added}");

            var mockSettings = AssetDatabase.LoadAssetAtPath<MockHMDBuildSettings>(kMockSettingsPath);
            if (mockSettings == null)
            {
                mockSettings = ScriptableObject.CreateInstance<MockHMDBuildSettings>();
                AssetDatabase.CreateAsset(mockSettings, kMockSettingsPath);
            }
            mockSettings.renderMode = MockHMDBuildSettings.RenderMode.MultiPass;
            EditorUtility.SetDirty(mockSettings);
            EditorBuildSettings.AddConfigObject(
                MockHMDBuildSettings.BuildSettingsKey, mockSettings, true);

            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(perTarget);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var loaders = settings.Manager.activeLoaders;
            Debug.Log($"[XrSetup] active loaders: {loaders.Count}");
            foreach (var l in loaders)
                Debug.Log($"[XrSetup]   {l.name} ({l.GetType().FullName})");

            if (loaders.Count == 0)
                Debug.LogError("[XrSetup] no XR loader registered; the player will not run in stereo.");
        }

        static XRGeneralSettingsPerBuildTarget GetOrCreatePerBuildTargetSettings()
        {
            EditorBuildSettings.TryGetConfigObject(
                XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget);

            if (perTarget == null)
                perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(kPerTargetPath);

            if (perTarget == null)
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, kPerTargetPath);
            }

            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            return perTarget;
        }

        /// <summary>
        /// Builds the Windows x64 player used for stereo verification under Wine.
        /// </summary>
        public static void BuildStereoPlayer()
        {
            SetupMockHmd();

            string outDir = System.Environment.GetEnvironmentVariable("STEREO_BUILD_DIR")
                            ?? "/home/prak/vr-work/stereo-build";
            Directory.CreateDirectory(outDir);

            // Vulkan goes straight through winevulkan to the host GPU, avoiding a
            // D3D translation layer that this box does not have.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
                new[] { UnityEngine.Rendering.GraphicsDeviceType.Vulkan });
            PlayerSettings.runInBackground = true;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.defaultScreenWidth = 1024;
            PlayerSettings.defaultScreenHeight = 1024;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;

            PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.Standalone, out var defines);
            if (!defines.Contains("HOUSESCAN_MOCKHMD"))
            {
                defines = defines.Append("HOUSESCAN_MOCKHMD").ToArray();
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, defines);
            }

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ProjectSetup.kScenePath },
                locationPathName = Path.Combine(outDir, "HouseScanVR.exe"),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.Development,
            };

            var summary = BuildPipeline.BuildPlayer(opts).summary;
            Debug.Log($"[XrSetup] build result={summary.result} " +
                      $"size={summary.totalSize / (1024 * 1024)} MB errors={summary.totalErrors}");

            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
