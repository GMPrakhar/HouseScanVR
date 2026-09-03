// SPDX-License-Identifier: MIT
// Runtime reader for INRIA-format 3D Gaussian Splatting .ply files, i.e. what
// Polycam / Scaniverse / Splatware / the reference trainer export.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public static class GaussianPlyRuntimeReader
    {
        /// <summary>
        /// Rigid transform applied while loading, to bring scanner output into the
        /// game's Y-up world. These are rotations, never mirrors, so gaussian
        /// covariance stays valid.
        /// </summary>
        public enum SourceConvention
        {
            /// Data is already authored Y-up (our synthetic fixtures, some exporters).
            AsAuthored,
            /// COLMAP / INRIA convention: Y points down, Z forward.
            ThreeDGS_YDown,
        }

        public class Header
        {
            public int vertexCount;
            public int headerBytes;
            public int strideBytes;
            public Dictionary<string, int> propertyOffsets = new();
            public int shCoeffCount; // per colour channel, 0..15
        }

        static readonly string[] kRequired =
        {
            "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2",
            "opacity", "scale_0", "scale_1", "scale_2",
            "rot_0", "rot_1", "rot_2", "rot_3"
        };

        public static Header ReadHeader(Stream stream)
        {
            var text = new StringBuilder();
            var line = new StringBuilder();
            int b;
            int consumed = 0;
            var props = new List<string>();
            bool binaryLE = false;
            int vertexCount = -1;
            bool inVertexElement = false;

            while ((b = stream.ReadByte()) != -1)
            {
                consumed++;
                if (b != '\n')
                {
                    if (b != '\r')
                        line.Append((char)b);
                    continue;
                }

                string l = line.ToString().Trim();
                line.Clear();
                text.AppendLine(l);

                if (l == "end_header")
                    break;
                if (l.StartsWith("format "))
                    binaryLE = l.Contains("binary_little_endian");
                else if (l.StartsWith("element "))
                {
                    var parts = l.Split(' ');
                    inVertexElement = parts.Length >= 3 && parts[1] == "vertex";
                    if (inVertexElement)
                        vertexCount = int.Parse(parts[2]);
                }
                else if (l.StartsWith("property ") && inVertexElement)
                {
                    var parts = l.Split(' ');
                    if (parts.Length >= 3)
                    {
                        if (parts[1] != "float" && parts[1] != "float32")
                            throw new NotSupportedException(
                                $"Only float32 splat properties are supported, found '{parts[1]}'.");
                        props.Add(parts[2]);
                    }
                }
            }

            if (!binaryLE)
                throw new NotSupportedException("Only binary_little_endian .ply files are supported.");
            if (vertexCount <= 0)
                throw new InvalidDataException("PLY has no vertex element.");

            var header = new Header
            {
                vertexCount = vertexCount,
                headerBytes = consumed,
                strideBytes = props.Count * 4,
            };
            for (int i = 0; i < props.Count; ++i)
                header.propertyOffsets[props[i]] = i;

            foreach (var r in kRequired)
            {
                if (!header.propertyOffsets.ContainsKey(r))
                    throw new InvalidDataException($"PLY is missing required splat property '{r}'.");
            }

            int rest = 0;
            while (header.propertyOffsets.ContainsKey($"f_rest_{rest}"))
                rest++;
            // 45 rest coefficients == 15 per channel (SH degree 3).
            header.shCoeffCount = rest / 3;
            return header;
        }

        public static NativeArray<RuntimeSplatData> Load(string path, SourceConvention convention,
            int maxSplats = 0)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var header = ReadHeader(fs);

            int count = header.vertexCount;
            int stride = header.strideBytes;

            long payload = (long)count * stride;
            if (fs.Length - header.headerBytes < payload)
                throw new InvalidDataException(
                    $"PLY truncated: expected {payload} payload bytes, file has {fs.Length - header.headerBytes}.");

            // Subsampling keeps huge whole-house captures inside a memory budget.
            int stepNumerator = 1, keep = count;
            if (maxSplats > 0 && count > maxSplats)
            {
                keep = maxSplats;
                stepNumerator = count;
            }

            var result = new NativeArray<RuntimeSplatData>(keep, Allocator.Persistent);

            int oX = header.propertyOffsets["x"];
            int oDc = header.propertyOffsets["f_dc_0"];
            int oOp = header.propertyOffsets["opacity"];
            int oSc = header.propertyOffsets["scale_0"];
            int oRot = header.propertyOffsets["rot_0"];
            bool hasRest = header.propertyOffsets.ContainsKey("f_rest_0");
            int oRest = hasRest ? header.propertyOffsets["f_rest_0"] : -1;
            int shPerChannel = header.shCoeffCount;

            quaternion pre = convention == SourceConvention.ThreeDGS_YDown
                ? quaternion.RotateX(math.PI)
                : quaternion.identity;
            bool applyPre = convention != SourceConvention.AsAuthored;

            var row = new byte[stride];
            var floats = new float[stride / 4];
            long basePos = header.headerBytes;

            for (int outIdx = 0; outIdx < keep; ++outIdx)
            {
                int srcIdx = stepNumerator == 1
                    ? outIdx
                    : (int)((long)outIdx * stepNumerator / keep);

                if (stepNumerator != 1)
                    fs.Seek(basePos + (long)srcIdx * stride, SeekOrigin.Begin);

                int got = 0;
                while (got < stride)
                {
                    int n = fs.Read(row, got, stride - got);
                    if (n <= 0)
                        throw new EndOfStreamException("Unexpected end of PLY payload.");
                    got += n;
                }
                Buffer.BlockCopy(row, 0, floats, 0, stride);

                var s = new RuntimeSplatData();

                float3 p = new float3(floats[oX], floats[oX + 1], floats[oX + 2]);
                float4 q = new float4(floats[oRot + 1], floats[oRot + 2], floats[oRot + 3], floats[oRot]);
                q = math.normalize(q);

                if (applyPre)
                {
                    p = math.mul(pre, p);
                    q = math.mul(pre, new quaternion(q)).value;
                }

                s.pos = p;
                s.rot = new Quaternion(q.x, q.y, q.z, q.w);

                s.dc0 = (Vector3)(float3)GaussianUtils.SH0ToColor(
                    new float3(floats[oDc], floats[oDc + 1], floats[oDc + 2]));
                s.opacity = GaussianUtils.Sigmoid(floats[oOp]);
                s.scale = (Vector3)(float3)GaussianUtils.LinearScale(
                    new float3(floats[oSc], floats[oSc + 1], floats[oSc + 2]));

                if (hasRest && shPerChannel > 0)
                    ReadSH(ref s, floats, oRest, shPerChannel);

                result[outIdx] = s;
            }

            return result;
        }

        // .ply stores SH rest coefficients channel-major: all R coefficients, then
        // all G, then all B. The renderer wants them interleaved as float3 per band.
        static void ReadSH(ref RuntimeSplatData s, float[] f, int o, int n)
        {
            Vector3 Get(int i)
            {
                if (i >= n) return Vector3.zero;
                return new Vector3(f[o + i], f[o + i + n], f[o + i + n * 2]);
            }

            s.sh1 = Get(0); s.sh2 = Get(1); s.sh3 = Get(2); s.sh4 = Get(3);
            s.sh5 = Get(4); s.sh6 = Get(5); s.sh7 = Get(6); s.sh8 = Get(7);
            s.sh9 = Get(8); s.shA = Get(9); s.shB = Get(10); s.shC = Get(11);
            s.shD = Get(12); s.shE = Get(13); s.shF = Get(14);
        }
    }
}
