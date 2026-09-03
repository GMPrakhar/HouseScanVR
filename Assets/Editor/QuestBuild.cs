using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace HouseScan.EditorTools
{
    /// <summary>
    /// Configures and builds the Meta Quest (Android/OpenXR) APK.
    ///
    /// Requires Unity's Android Build Support module, which cannot be installed on
    /// every platform. <see cref="ConfigureQuest"/> is deliberately separate from
    /// <see cref="BuildQuestApk"/> so the configuration can be applied and
    /// inspected on a machine that cannot build.
    /// </summary>
    public static class QuestBuild
    {
        const string kXrDir = "Assets/XR";
        const string kPackageName = "com.gmprakhar.housescanvr";

        /// <summary>
        /// Applies every Quest-specific player and XR setting. Safe to run on a
        /// machine without Android Build Support.
        /// </summary>
        public static void ConfigureQuest()
        {
            Directory.CreateDirectory(kXrDir);

            bool androidSupported = BuildPipeline.IsBuildTargetSupported(
                BuildTargetGroup.Android, BuildTarget.Android);

            ConfigurePlayerSettings();
            ConfigureOpenXr();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (!androidSupported)
            {
                Debug.LogWarning(
                    "[QuestBuild] Android Build Support is NOT installed. Player settings " +
                    "were written, but OpenXR's Android feature set and render mode could " +
                    "not be configured because Unity only creates them for installed " +
                    "build targets. Install Android Build Support and re-run " +
                    "QuestBuild.ConfigureQuest before building, or the APK may default to " +
                    "SinglePassInstanced and render incorrectly.");
            }

            Debug.Log($"[QuestBuild] Quest configuration applied (androidSupported={androidSupported}).");
        }

        static void ConfigurePlayerSettings()
        {
            var android = NamedBuildTarget.Android;

            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // Quest 3 runs Android 12L; API 32 is Meta's documented minimum for
            // current store submissions.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;

            // The splat renderer is compute-based; Vulkan only, no GLES fallback.
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,
                new[] { GraphicsDeviceType.Vulkan });

            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.gpuSkinning = true;
            PlayerSettings.SetApplicationIdentifier(android, kPackageName);
            PlayerSettings.productName = "HouseScanVR";
            PlayerSettings.companyName = "GMPrakhar";

            // A headset app is never portrait and never rotates.
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;

            // Reading scans from persistentDataPath needs no permission on the
            // app-private external directory, but the write permission keeps
            // sideloaded workflows simple.
            PlayerSettings.Android.forceInternetPermission = false;
            PlayerSettings.Android.forceSDCardPermission = true;

            PlayerSettings.SetScriptingDefineSymbols(android,
                AddDefine(PlayerSettings.GetScriptingDefineSymbols(android), "HOUSESCAN_QUEST"));
        }

        static string AddDefine(string defines, string add)
        {
            var parts = defines.Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (!parts.Contains(add))
                parts.Add(add);
            return string.Join(";", parts);
        }

        static void ConfigureOpenXr()
        {
            var perTarget = GetOrCreatePerBuildTargetSettings();
            var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                settings.name = "Android Settings";
                AssetDatabase.AddObjectToAsset(settings, perTarget);
                perTarget.SetSettingsForBuildTarget(BuildTargetGroup.Android, settings);
            }

            if (settings.Manager == null)
            {
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                manager.name = "Android Providers";
                AssetDatabase.AddObjectToAsset(manager, perTarget);
                settings.Manager = manager;
            }
            settings.InitManagerOnStart = true;

            bool assigned = XRPackageMetadataStore.AssignLoader(
                settings.Manager, "OpenXRLoader", BuildTargetGroup.Android);
            Debug.Log($"[QuestBuild] OpenXRLoader assigned: {assigned}");

            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            EnableFeature(MetaQuestFeature.featureId);
            EnableFeature(OculusTouchControllerProfile.featureId);
            EnableFeature(MetaQuestTouchPlusControllerProfile.featureId);

            var openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (openXr == null)
            {
                Debug.LogError("[QuestBuild] No OpenXR settings for Android.");
                return;
            }

            // OpenXR defaults to SinglePassInstanced. The Gaussian splat renderer
            // only consults XRSettings.eyeTextureWidth and has no instancing-aware
            // path, so single-pass would render incorrectly. This must stay
            // MultiPass unless the renderer gains that support.
            openXr.renderMode = OpenXRSettings.RenderMode.MultiPass;
            EditorUtility.SetDirty(openXr);

            Debug.Log($"[QuestBuild] OpenXR renderMode={openXr.renderMode}");

            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(perTarget);

            if (settings.Manager.activeLoaders.Count == 0)
                Debug.LogError("[QuestBuild] no XR loader active for Android; APK would run flat.");
        }

        static void EnableFeature(string featureId)
        {
            var f = FeatureHelpers.GetFeatureWithIdForBuildTarget(BuildTargetGroup.Android, featureId);
            if (f == null)
            {
                Debug.LogWarning($"[QuestBuild] feature not found: {featureId}");
                return;
            }
            f.enabled = true;
            EditorUtility.SetDirty(f);
            Debug.Log($"[QuestBuild] enabled feature {featureId}");
        }

        static XRGeneralSettingsPerBuildTarget GetOrCreatePerBuildTargetSettings()
        {
            EditorBuildSettings.TryGetConfigObject(
                XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget);

            if (perTarget == null)
            {
                perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(
                    kXrDir + "/XRGeneralSettingsPerBuildTarget.asset");
            }

            if (perTarget == null)
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, kXrDir + "/XRGeneralSettingsPerBuildTarget.asset");
            }

            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            return perTarget;
        }

        /// <summary>
        /// Builds the APK. Fails with a clear message if Android Build Support is
        /// not installed, rather than emitting an opaque build error.
        /// </summary>
        public static void BuildQuestApk()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            {
                Debug.LogError(
                    "[QuestBuild] Android Build Support is not installed for this Unity " +
                    "version. Install it via Unity Hub > Installs > Add Modules > " +
                    "Android Build Support (including OpenJDK and Android SDK & NDK Tools), " +
                    "then re-run this command.");
                EditorApplication.Exit(2);
                return;
            }

            // Switch first: OpenXR only materialises its Android settings and
            // feature set for the active/installed target, so configuring before
            // the switch silently does nothing.
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(NamedBuildTarget.Android, BuildTarget.Android);

            ConfigureQuest();

            string outDir = System.Environment.GetEnvironmentVariable("QUEST_BUILD_DIR")
                            ?? Path.GetFullPath("Build/Quest");
            Directory.CreateDirectory(outDir);
            string apk = Path.Combine(outDir, "HouseScanVR.apk");

            bool dev = System.Environment.GetEnvironmentVariable("QUEST_DEV_BUILD") == "1";
            var options = BuildOptions.None;
            if (dev)
                options |= BuildOptions.Development | BuildOptions.AllowDebugging;

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { ProjectSetup.kScenePath },
                locationPathName = apk,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = options,
            };

            var summary = BuildPipeline.BuildPlayer(opts).summary;
            Debug.Log($"[QuestBuild] result={summary.result} " +
                      $"size={summary.totalSize / (1024 * 1024)} MB " +
                      $"errors={summary.totalErrors} output={apk}");

            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
