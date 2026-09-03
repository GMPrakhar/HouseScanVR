using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;

namespace HouseScan
{
    /// <summary>
    /// Runtime stereo verification that runs inside a built player with an XR
    /// runtime active (Mock HMD). Unlike the editor probe, this exercises the real
    /// XR display subsystem: the camera is driven by the XR device, eye textures
    /// are allocated by the provider, and each eye is captured through
    /// <see cref="ScreenCapture"/> rather than being simulated by moving a camera.
    ///
    /// Writes a report and quits with a non-zero exit code on failure, so it can
    /// gate CI without a headset.
    /// </summary>
    public class StereoProbe : MonoBehaviour
    {
        public HouseScanLoader m_Loader;

        const int kEyeWidth = 1024;
        const int kEyeHeight = 1024;

        StringBuilder m_Report = new();
        bool m_Ok = true;

        void Start()
        {
            if (m_Loader == null)
                m_Loader = FindFirstObjectByType<HouseScanLoader>();
            StartCoroutine(RunProbe());
        }

        static string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; ++i)
                if (args[i] == name)
                    return args[i + 1];
            return fallback;
        }

        IEnumerator RunProbe()
        {
            string outPath = Arg("-report", "stereo_report.txt");
            string scanPath = Arg("-scan", null);

            Line($"device={SystemInfo.graphicsDeviceName}");
            Line($"api={SystemInfo.graphicsDeviceType}");
            Line($"compute={SystemInfo.supportsComputeShaders}");

            yield return WaitForXr();

            if (!m_Ok)
            {
                Finish(outPath);
                yield break;
            }

            // Ask the mock provider for a known eye resolution and multi-pass, which
            // is the mode the splat renderer supports.
            MockHmdConfigure();

            Line($"xr.enabled={XRSettings.enabled}");
            Line($"xr.device={XRSettings.loadedDeviceName}");
            Line($"xr.stereo_mode={XRSettings.stereoRenderingMode}");
            Line($"xr.eye_width={XRSettings.eyeTextureWidth}");
            Line($"xr.eye_height={XRSettings.eyeTextureHeight}");

            if (!XRSettings.enabled)
            {
                Fail("XR did not enable; stereo was never exercised");
                Finish(outPath);
                yield break;
            }

            if (XRSettings.eyeTextureWidth <= 0 || XRSettings.eyeTextureHeight <= 0)
            {
                Fail($"XR reported a degenerate eye texture " +
                     $"{XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}");
            }

            // Single-pass instanced would break the splat renderer, which only
            // consults the eye texture width.
            if (XRSettings.stereoRenderingMode != XRSettings.StereoRenderingMode.MultiPass)
            {
                Fail($"stereo mode is {XRSettings.stereoRenderingMode}, expected MultiPass");
            }

            if (!LoadScan(scanPath))
            {
                Finish(outPath);
                yield break;
            }

            var cam = Camera.main;
            if (cam == null)
            {
                Fail("no main camera to render from");
                Finish(outPath);
                yield break;
            }

            Line($"cam.stereo_enabled={cam.stereoEnabled}");
            Line($"cam.stereo_target={cam.stereoTargetEye}");
            if (!cam.stereoEnabled)
                Fail("main camera is not rendering in stereo");

            // Let the XR display settle and the splat renderer build its resources.
            for (int i = 0; i < 10; ++i)
                yield return new WaitForEndOfFrame();

            yield return CaptureAndCompare(outPath);

            Finish(outPath);
        }

        IEnumerator WaitForXr()
        {
            var settings = XRGeneralSettings.Instance;
            if (settings == null || settings.Manager == null)
            {
                Fail("XRGeneralSettings/Manager missing; XR was not configured for this build");
                yield break;
            }

            var mgr = settings.Manager;
            if (mgr.activeLoader == null)
            {
                yield return mgr.InitializeLoader();
                mgr.StartSubsystems();
            }

            // Initialisation is asynchronous; give it a bounded number of frames
            // rather than hanging a CI run forever.
            for (int i = 0; i < 300 && !XRSettings.enabled; ++i)
                yield return null;

            Line($"xr.active_loader={(mgr.activeLoader != null ? mgr.activeLoader.name : "<none>")}");
            if (mgr.activeLoader == null)
                Fail("no XR loader became active");
        }

        void MockHmdConfigure()
        {
#if HOUSESCAN_MOCKHMD
            try
            {
                Unity.XR.MockHMD.MockHMD.SetEyeResolution(kEyeWidth, kEyeHeight);
                Unity.XR.MockHMD.MockHMD.SetRenderMode(
                    Unity.XR.MockHMD.MockHMDBuildSettings.RenderMode.MultiPass);
                Unity.XR.MockHMD.MockHMD.SetMirrorViewCrop(0f);
            }
            catch (Exception e)
            {
                Line($"note: mock hmd configuration failed: {e.Message}");
            }
#endif
        }

        bool LoadScan(string scanPath)
        {
            if (m_Loader == null)
            {
                Fail("HouseScanLoader missing from scene");
                return false;
            }

            if (!string.IsNullOrEmpty(scanPath))
                m_Loader.m_ScanPath = scanPath;

            if (!m_Loader.isLoaded && !m_Loader.Load())
            {
                Fail($"scan load failed: {m_Loader.lastError}");
                return false;
            }

            Line($"scan={m_Loader.ResolvePath()}");
            Line($"splats={m_Loader.loadedSplatCount}");
            Line($"load_ms={m_Loader.loadMilliseconds:F0}");

            var r = m_Loader.GetComponent<GaussianSplatRenderer>();
            Line($"has_valid_asset={(r != null && r.HasValidAsset)}");
            if (r == null || !r.HasValidAsset)
            {
                Fail("splat renderer has no valid asset in the player");
                return false;
            }
            return true;
        }

        IEnumerator CaptureAndCompare(string outPath)
        {
            yield return new WaitForEndOfFrame();
            var left = ScreenCapture.CaptureScreenshotAsTexture(
                ScreenCapture.StereoScreenCaptureMode.LeftEye);

            yield return new WaitForEndOfFrame();
            var right = ScreenCapture.CaptureScreenshotAsTexture(
                ScreenCapture.StereoScreenCaptureMode.RightEye);

            if (left == null || right == null)
            {
                Fail("failed to capture eye images");
                yield break;
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            WritePng(left, Path.Combine(dir, "player_left.png"));
            WritePng(right, Path.Combine(dir, "player_right.png"));

            Line($"capture.size={left.width}x{left.height}");
            if (left.width != right.width || left.height != right.height)
                Fail("eye captures differ in size");

            float covL = Coverage(left, out Color meanL);
            float covR = Coverage(right, out Color meanR);
            Line($"left.coverage={F(covL)}");
            Line($"right.coverage={F(covR)}");
            Line($"left.mean_rgb={F(meanL.r)},{F(meanL.g)},{F(meanL.b)}");
            Line($"right.mean_rgb={F(meanR.r)},{F(meanR.g)},{F(meanR.b)}");

            // Both eyes must actually contain the scan, not a black or empty frame.
            if (covL < 0.30f) Fail($"left eye coverage {covL:F3} below 0.30");
            if (covR < 0.30f) Fail($"right eye coverage {covR:F3} below 0.30");

            float chroma = ChromaDistance(meanL, meanR);
            Line($"stereo.eye_chroma_delta={chroma.ToString("F4", CultureInfo.InvariantCulture)}");
            if (chroma > 0.02f)
                Fail($"eyes disagree on colour (chroma delta {chroma:F4})");

            // The two eyes are rendered from different positions, so a meaningful
            // number of pixels must differ. Identical eyes would mean the provider
            // is handing the same view to both, and the scene would look flat.
            var a = left.GetPixels();
            var b = right.GetPixels();
            int changed = 0;
            double diffSum = 0;
            int n = Mathf.Min(a.Length, b.Length);
            for (int i = 0; i < n; ++i)
            {
                float d = Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) +
                          Mathf.Abs(a[i].b - b[i].b);
                diffSum += d;
                if (d > 0.02f) changed++;
            }
            float parallax = changed / (float)n;
            float meanDiff = (float)(diffSum / n);
            Line($"stereo.parallax_pixel_fraction={parallax.ToString("F4", CultureInfo.InvariantCulture)}");
            Line($"stereo.mean_abs_diff={meanDiff.ToString("F4", CultureInfo.InvariantCulture)}");

            if (parallax < 0.02f)
                Fail($"no stereo parallax: only {parallax:P2} of pixels differ between eyes");
            if (meanDiff > 0.25f)
                Fail($"eyes differ too much (mean abs diff {meanDiff:F4})");
        }

        static void WritePng(Texture2D t, string path)
        {
            try { File.WriteAllBytes(path, t.EncodeToPNG()); }
            catch (Exception e) { Debug.LogWarning($"[StereoProbe] png write failed: {e.Message}"); }
        }

        static float Coverage(Texture2D t, out Color mean)
        {
            var px = t.GetPixels();
            int lit = 0;
            Color sum = Color.black;
            foreach (var p in px)
            {
                if (p.r + p.g + p.b > 0.12f) { lit++; sum += p; }
            }
            mean = lit > 0 ? sum / lit : Color.black;
            return lit / (float)px.Length;
        }

        static Color Normalize(Color c)
        {
            float s = c.r + c.g + c.b;
            return s < 1e-5f ? new Color(0.333f, 0.333f, 0.333f) : new Color(c.r / s, c.g / s, c.b / s);
        }

        static float ChromaDistance(Color a, Color b)
        {
            Color x = Normalize(a), y = Normalize(b);
            return Mathf.Sqrt((x.r - y.r) * (x.r - y.r) + (x.g - y.g) * (x.g - y.g) +
                              (x.b - y.b) * (x.b - y.b));
        }

        static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        void Line(string s)
        {
            m_Report.AppendLine(s);
            Debug.Log($"[StereoProbe] {s}");
        }

        void Fail(string why)
        {
            m_Report.AppendLine($"FAIL: {why}");
            Debug.LogError($"[StereoProbe] FAIL: {why}");
            m_Ok = false;
        }

        void Finish(string outPath)
        {
            m_Report.AppendLine(m_Ok ? "RESULT=PASS" : "RESULT=FAIL");
            try
            {
                File.WriteAllText(outPath, m_Report.ToString());
                Debug.Log($"[StereoProbe] report written to {outPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[StereoProbe] could not write report: {e}");
            }
            Application.Quit(m_Ok ? 0 : 1);
        }
    }
}
