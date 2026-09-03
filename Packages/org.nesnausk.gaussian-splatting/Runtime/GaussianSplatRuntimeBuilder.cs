// SPDX-License-Identifier: MIT
// Runtime construction of GaussianSplatAsset from splat data loaded at play time.
//
// The upstream package only builds assets in the editor importer, which writes
// TextAsset blobs. A game that ingests a player's own house scan has to do the
// same encoding while running, so the encoding jobs are reproduced here against
// in-memory NativeArrays. Encoding must stay byte-identical to the editor path,
// because the shaders and compute kernels read these buffers directly.

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>Decoded, linearised splat as read from a scan file.</summary>
    public struct RuntimeSplatData
    {
        public Vector3 pos;
        public Vector3 nor;
        public Vector3 dc0;
        public Vector3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }

    public static class GaussianSplatRuntimeBuilder
    {
        // Lossless "VeryHigh" formats. These are the only combination for which the
        // renderer does not need chunk normalisation data, which keeps runtime
        // construction simple and exact.
        const GaussianSplatAsset.VectorFormat kPosFormat = GaussianSplatAsset.VectorFormat.Float32;
        const GaussianSplatAsset.VectorFormat kScaleFormat = GaussianSplatAsset.VectorFormat.Float32;
        const GaussianSplatAsset.ColorFormat kColorFormat = GaussianSplatAsset.ColorFormat.Float32x4;
        const GaussianSplatAsset.SHFormat kSHFormat = GaussianSplatAsset.SHFormat.Float32;

        public static long EstimateGpuBytes(int splatCount)
        {
            return GaussianSplatAsset.CalcPosDataSize(splatCount, kPosFormat)
                 + GaussianSplatAsset.CalcOtherDataSize(splatCount, kScaleFormat)
                 + GaussianSplatAsset.CalcColorDataSize(splatCount, kColorFormat)
                 + GaussianSplatAsset.CalcSHDataSize(splatCount, kSHFormat);
        }

        static int NextMultipleOf(int size, int multipleOf)
        {
            return (size + multipleOf - 1) / multipleOf * multipleOf;
        }

        /// <summary>
        /// Builds a renderable asset. Ownership of the returned asset's native
        /// buffers belongs to the asset; destroying it releases them.
        /// </summary>
        public static GaussianSplatAsset Build(NativeArray<RuntimeSplatData> splats, string name)
        {
            int splatCount = splats.Length;
            if (splatCount <= 0 || splatCount > GaussianSplatAsset.kMaxSplats)
            {
                Debug.LogError($"GaussianSplatRuntimeBuilder: invalid splat count {splatCount}");
                return null;
            }

            var bounds = CalcBounds(splats);

            // Morton reordering keeps splats that are near each other in space also
            // near each other in memory, which is what makes the GPU radix sort and
            // the tiled colour texture behave. The editor importer does the same.
            var ordered = ReorderMorton(splats, bounds.min, bounds.max);

            var posData = BuildPositions(ordered);
            var otherData = BuildOther(ordered);
            var colorData = BuildColor(ordered);
            var shData = BuildSH(ordered);

            if (ordered.IsCreated)
                ordered.Dispose();

            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.name = name;
            asset.Initialize(splatCount, kPosFormat, kScaleFormat, kColorFormat, kSHFormat,
                bounds.min, bounds.max, new GaussianSplatAsset.CameraInfo[0]);
            asset.SetRuntimeData(posData, otherData, colorData, shData, default);
            return asset;
        }

        static (Vector3 min, Vector3 max) CalcBounds(NativeArray<RuntimeSplatData> splats)
        {
            float3 lo = float.PositiveInfinity;
            float3 hi = float.NegativeInfinity;
            for (int i = 0; i < splats.Length; ++i)
            {
                float3 p = splats[i].pos;
                lo = math.min(lo, p);
                hi = math.max(hi, p);
            }
            return (lo, hi);
        }

        [BurstCompile]
        struct MortonCodeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<RuntimeSplatData> splats;
            public float3 boundsMin, invBoundsSize;
            [NativeDisableParallelForRestriction] public NativeArray<ulong> codes;

            public void Execute(int index)
            {
                float3 pos = ((float3)splats[index].pos - boundsMin) * invBoundsSize;
                uint3 ipos = (uint3)(math.saturate(pos) * 2097151.0f);
                codes[index] = GaussianUtils.MortonEncode3(ipos);
            }
        }

        static NativeArray<RuntimeSplatData> ReorderMorton(NativeArray<RuntimeSplatData> splats, float3 boundsMin, float3 boundsMax)
        {
            int n = splats.Length;
            float3 size = boundsMax - boundsMin;
            float3 invSize = new float3(
                size.x > 1e-9f ? 1.0f / size.x : 0.0f,
                size.y > 1e-9f ? 1.0f / size.y : 0.0f,
                size.z > 1e-9f ? 1.0f / size.z : 0.0f);

            var codes = new NativeArray<ulong>(n, Allocator.TempJob);
            new MortonCodeJob { splats = splats, boundsMin = boundsMin, invBoundsSize = invSize, codes = codes }
                .Schedule(n, 8192).Complete();

            var order = new NativeArray<int>(n, Allocator.Temp);
            for (int i = 0; i < n; ++i)
                order[i] = i;
            var keys = new ulong[n];
            var idx = new int[n];
            codes.CopyTo(keys);
            order.CopyTo(idx);
            System.Array.Sort(keys, idx);

            var result = new NativeArray<RuntimeSplatData>(n, Allocator.Persistent);
            for (int i = 0; i < n; ++i)
                result[i] = splats[idx[i]];

            order.Dispose();
            codes.Dispose();
            return result;
        }

        [BurstCompile]
        struct CreatePositionsDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<RuntimeSplatData> input;
            [NativeDisableParallelForRestriction] public NativeArray<byte> output;

            public unsafe void Execute(int index)
            {
                byte* dst = (byte*)output.GetUnsafePtr() + index * 12;
                float3 v = input[index].pos;
                *(float*)dst = v.x;
                *(float*)(dst + 4) = v.y;
                *(float*)(dst + 8) = v.z;
            }
        }

        static NativeArray<byte> BuildPositions(NativeArray<RuntimeSplatData> splats)
        {
            int len = NextMultipleOf(splats.Length * 12, 8);
            var data = new NativeArray<byte>(len, Allocator.Persistent);
            new CreatePositionsDataJob { input = splats, output = data }.Schedule(splats.Length, 8192).Complete();
            return data;
        }

        static uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
        {
            return (uint)(v.x * 1023.5f) | ((uint)(v.y * 1023.5f) << 10) | ((uint)(v.z * 1023.5f) << 20) | ((uint)(v.w * 3.5f) << 30);
        }

        [BurstCompile]
        struct CreateOtherDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<RuntimeSplatData> input;
            public int formatSize;
            [NativeDisableParallelForRestriction] public NativeArray<byte> output;

            public unsafe void Execute(int index)
            {
                byte* dst = (byte*)output.GetUnsafePtr() + index * formatSize;

                Quaternion rotQ = input[index].rot;
                *(uint*)dst = EncodeQuatToNorm10(new float4(rotQ.x, rotQ.y, rotQ.z, rotQ.w));
                dst += 4;

                float3 s = input[index].scale;
                *(float*)dst = s.x;
                *(float*)(dst + 4) = s.y;
                *(float*)(dst + 8) = s.z;
            }
        }

        static NativeArray<byte> BuildOther(NativeArray<RuntimeSplatData> splats)
        {
            int formatSize = GaussianSplatAsset.GetOtherSizeNoSHIndex(kScaleFormat);
            int len = NextMultipleOf(splats.Length * formatSize, 8);
            var data = new NativeArray<byte>(len, Allocator.Persistent);
            new CreateOtherDataJob { input = splats, formatSize = formatSize, output = data }
                .Schedule(splats.Length, 8192).Complete();
            return data;
        }

        static int SplatIndexToTextureIndex(uint idx)
        {
            uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
            uint width = GaussianSplatAsset.kTextureWidth / 16;
            idx >>= 8;
            uint x = (idx % width) * 16 + xy.x;
            uint y = (idx / width) * 16 + xy.y;
            return (int)(y * GaussianSplatAsset.kTextureWidth + x);
        }

        [BurstCompile]
        struct CreateColorDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<RuntimeSplatData> input;
            [NativeDisableParallelForRestriction] public NativeArray<float4> output;

            public void Execute(int index)
            {
                var splat = input[index];
                output[SplatIndexToTextureIndex((uint)index)] =
                    new float4(splat.dc0.x, splat.dc0.y, splat.dc0.z, splat.opacity);
            }
        }

        static NativeArray<byte> BuildColor(NativeArray<RuntimeSplatData> splats)
        {
            var (width, height) = GaussianSplatAsset.CalcTextureSize(splats.Length);
            var pixels = new NativeArray<float4>(width * height, Allocator.TempJob);
            new CreateColorDataJob { input = splats, output = pixels }.Schedule(splats.Length, 8192).Complete();

            // Float32x4 is stored verbatim, so the pixel buffer is already the blob.
            var bytes = new NativeArray<byte>(width * height * 16, Allocator.Persistent);
            pixels.Reinterpret<byte>(16).CopyTo(bytes);
            pixels.Dispose();
            return bytes;
        }

        [BurstCompile]
        struct CreateSHDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<RuntimeSplatData> input;
            [NativeDisableParallelForRestriction] public NativeArray<byte> output;

            public unsafe void Execute(int index)
            {
                var splat = input[index];
                GaussianSplatAsset.SHTableItemFloat32 res;
                res.sh1 = splat.sh1; res.sh2 = splat.sh2; res.sh3 = splat.sh3;
                res.sh4 = splat.sh4; res.sh5 = splat.sh5; res.sh6 = splat.sh6;
                res.sh7 = splat.sh7; res.sh8 = splat.sh8; res.sh9 = splat.sh9;
                res.shA = splat.shA; res.shB = splat.shB; res.shC = splat.shC;
                res.shD = splat.shD; res.shE = splat.shE; res.shF = splat.shF;
                res.shPadding = default;
                ((GaussianSplatAsset.SHTableItemFloat32*)output.GetUnsafePtr())[index] = res;
            }
        }

        static NativeArray<byte> BuildSH(NativeArray<RuntimeSplatData> splats)
        {
            int itemSize = UnsafeUtility.SizeOf<GaussianSplatAsset.SHTableItemFloat32>();
            int len = NextMultipleOf(splats.Length * itemSize, 8);
            var data = new NativeArray<byte>(len, Allocator.Persistent);
            new CreateSHDataJob { input = splats, output = data }.Schedule(splats.Length, 8192).Complete();
            return data;
        }
    }
}
