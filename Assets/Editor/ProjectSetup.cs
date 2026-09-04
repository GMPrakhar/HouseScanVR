using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using GaussianSplatting.Runtime;

namespace HouseScan.EditorTools
{
    /// <summary>
    /// Headless project bootstrap. Everything here is callable through
    /// -executeMethod so the project can be set up and validated on a machine with
    /// no GUI and no headset attached.
    /// </summary>
    public static class ProjectSetup
    {
        const string kSettingsDir = "Assets/Settings";
        const string kRendererPath = kSettingsDir + "/HouseScanRenderer.asset";
        const string kUrpPath = kSettingsDir + "/HouseScanURP.asset";
        public const string kScenePath = "Assets/Scenes/HouseScanTest.unity";

        /// <summary>Adds the define that enables the package's URP integration.</summary>
        public static void SetDefines()
        {
            foreach (var target in new[] { NamedBuildTarget.Standalone, NamedBuildTarget.Android })
            {
                PlayerSettings.GetScriptingDefineSymbols(target, out var defines);
                if (defines != null && defines.Contains("GS_ENABLE_URP"))
                    continue;
                var list = (defines ?? new string[0]).ToList();
                list.Add("GS_ENABLE_URP");
                PlayerSettings.SetScriptingDefineSymbols(target, list.ToArray());
                Debug.Log($"[Setup] Added GS_ENABLE_URP for {target}");
            }
            AssetDatabase.SaveAssets();
        }

        public static void SetupRenderPipeline()
        {
            Directory.CreateDirectory(kSettingsDir);

            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            rendererData.name = "HouseScanRenderer";
            AssetDatabase.CreateAsset(rendererData, kRendererPath);

            var feature = ScriptableObject.CreateInstance<GaussianSplatURPFeature>();
            feature.name = "GaussianSplatFeature";
            rendererData.rendererFeatures.Add(feature);
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            EditorUtility.SetDirty(rendererData);

            var urp = UniversalRenderPipelineAsset.Create(rendererData);
            urp.name = "HouseScanURP";
            AssetDatabase.CreateAsset(urp, kUrpPath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            GraphicsSettings.defaultRenderPipeline = urp;
            QualitySettings.renderPipeline = urp;

            AssetDatabase.SaveAssets();
            Debug.Log("[Setup] URP asset created with Gaussian splat render feature.");
        }

        public static void BuildScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.02f, 0.02f, 0.03f, 1f);
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 60f;
            cam.fieldOfView = 70f;
            // Standing eye height in the living room, facing the blue feature wall
            // so the probe has a known target to assert against.
            camGo.transform.position = new Vector3(-1.0f, 1.6f, 0.0f);
            camGo.transform.rotation = Quaternion.Euler(0f, 90f, 0f);

            var scanGo = new GameObject("HouseScan");
            var splatRenderer = scanGo.AddComponent<GaussianSplatRenderer>();
            AssignShaders(splatRenderer);

            var loader = scanGo.AddComponent<HouseScanLoader>();
            // The probes and the player choose the scan explicitly, so nothing is
            // loaded until a path is known to exist.
            loader.m_LoadOnStart = false;
            loader.m_ScanPath = "house.ply";
            loader.m_Convention = GaussianPlyRuntimeReader.SourceConvention.AsAuthored;

            var probeGo = new GameObject("StereoProbe");
            var stereoProbe = probeGo.AddComponent<StereoProbe>();
            stereoProbe.m_Loader = loader;

            // The rig is the floor-level tracking origin the headset is tracked
            // relative to, so the camera lives underneath it.
            var playerGo = new GameObject("Player");
            camGo.transform.SetParent(playerGo.transform, worldPositionStays: true);
            var rig = playerGo.AddComponent<ScanPlayerRig>();
            rig.m_Loader = loader;
            rig.m_Camera = cam;

            var gameGo = new GameObject("GameDirector");
            var director = gameGo.AddComponent<GameDirector>();
            director.m_Loader = loader;
            director.m_Rig = rig;

            EditorSceneManager.SaveScene(scene, kScenePath);
            Debug.Log($"[Setup] Scene written to {kScenePath}");
        }

        public static void AssignShaders(GaussianSplatRenderer r)
        {
            const string root = "Packages/org.nesnausk.gaussian-splatting/Shaders/";
            r.m_ShaderSplats = AssetDatabase.LoadAssetAtPath<Shader>(root + "RenderGaussianSplats.shader");
            r.m_ShaderComposite = AssetDatabase.LoadAssetAtPath<Shader>(root + "GaussianComposite.shader");
            r.m_ShaderDebugPoints = AssetDatabase.LoadAssetAtPath<Shader>(root + "GaussianDebugRenderPoints.shader");
            r.m_ShaderDebugBoxes = AssetDatabase.LoadAssetAtPath<Shader>(root + "GaussianDebugRenderBoxes.shader");
            r.m_CSSplatUtilities = AssetDatabase.LoadAssetAtPath<ComputeShader>(root + "SplatUtilities.compute");

            if (r.m_ShaderSplats == null || r.m_ShaderComposite == null || r.m_CSSplatUtilities == null)
                Debug.LogError("[Setup] Failed to resolve Gaussian splat shaders.");
        }

        public static void SetupAll()
        {
            SetupRenderPipeline();
            BuildScene();
        }
    }
}
