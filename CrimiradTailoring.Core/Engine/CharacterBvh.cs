using System;
using System.Collections.Generic;
using CrimiradTailoring.Core.Models;

namespace CrimiradTailoring.Core.Engine
{
    public struct BvhRayHit
    {
        public bool Hit;
        public int TriangleIndex;
        public float Distance;
        public Vec3 Point;
        public Vec3 Normal;
        public Vec3 Barycentric;
    }

    public struct BvhClosestHit
    {
        public bool Found;
        public int TriangleIndex;
        public float DistanceSq;
        public Vec3 Point;
        public Vec3 Normal;
        public Vec3 Barycentric;
    }

    /// <summary>
    /// Cây BVH AABB tam giác hiệu năng cao cho Model Nhân vật và các Layer vải dưới.
    /// Hỗ trợ Raycast (chọn điểm neo / Ruler / Culling) và ClosestPoint (Hugging / Fitting) thời gian thực.
    /// </summary>
    public sealed class CharacterBvh
    {
        private struct Node
        {
            public Vec3 Min;
            public Vec3 Max;
            public int LeftOrFirst;
            public int RightOrCount;
            public bool IsLeaf;
        }

        private readonly IReadOnlyList<Vec3> _verts;
        private readonly IReadOnlyList<int> _tris;
        private readonly IReadOnlyList<Vec3>? _normals;
        private readonly int _triCount;
        private readonly int[] _triIndices;
        private readonly Vec3[] _triMin;
        private readonly Vec3[] _triMax;
        private readonly Vec3[] _triCentroids;
        private readonly Node[] _nodes;
        private int _nodeCount;

        public int TriangleCount => _triCount;

        private CharacterBvh(IReadOnlyList<Vec3> verts, IReadOnlyList<int> tris, IReadOnlyList<Vec3>? normals)
        {
            _verts = verts;
            _tris = tris;
            _normals = normals;
            _triCount = tris.Count / 3;
            _triIndices = new int[_triCount];
            _triMin = new Vec3[_triCount];
            _triMax = new Vec3[_triCount];
            _triCentroids = new Vec3[_triCount];

            for (int t = 0; t < _triCount; t++)
            {
                _triIndices[t] = t;
                int ia = tris[t * 3];
                int ib = tris[t * 3 + 1];
                int ic = tris[t * 3 + 2];
                Vec3 a = verts[ia];
                Vec3 b = verts[ib];
                Vec3 c = verts[ic];
                _triMin[t] = new Vec3(
                    Math.Min(a.X, Math.Min(b.X, c.X)),
                    Math.Min(a.Y, Math.Min(b.Y, c.Y)),
                    Math.Min(a.Z, Math.Min(b.Z, c.Z)));
                _triMax[t] = new Vec3(
                    Math.Max(a.X, Math.Max(b.X, c.X)),
                    Math.Max(a.Y, Math.Max(b.Y, c.Y)),
                    Math.Max(a.Z, Math.Max(b.Z, c.Z)));
                _triCentroids[t] = (a + b + c) * (1f / 3f);
            }

            _nodes = new Node[Math.Max(1, _triCount * 2)];
            if (_triCount > 0)
                BuildNode(0, _triCount);
        }

        public static CharacterBvh Build(IReadOnlyList<Vec3> verts, IReadOnlyList<int> tris, IReadOnlyList<Vec3>? normals = null)
        {
            return new CharacterBvh(verts, tris, normals);
        }

        private int BuildNode(int start, int end)
        {
            int nodeIdx = _nodeCount++;
            Vec3 bMin = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vec3 bMax = new Vec3(float.MinValue, float.MinValue, float.MinValue);

            for (int i = start; i < end; i++)
            {
                int t = _triIndices[i];
                Vec3 mn = _triMin[t];
                Vec3 mx = _triMax[t];
                if (mn.X < bMin.X) bMin.X = mn.X;
                if (mn.Y < bMin.Y) bMin.Y = mn.Y;
                if (mn.Z < bMin.Z) bMin.Z = mn.Z;
                if (mx.X > bMax.X) bMax.X = mx.X;
                if (mx.Y > bMax.Y) bMax.Y = mx.Y;
                if (mx.Z > bMax.Z) bMax.Z = mx.Z;
            }

            int count = end - start;
            if (count <= 4)
            {
                _nodes[nodeIdx] = new Node
                {
                    Min = bMin,
                    Max = bMax,
                    LeftOrFirst = start,
                    RightOrCount = count,
                    IsLeaf = true
                };
                return nodeIdx;
            }

            Vec3 extent = bMax - bMin;
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : (extent.Y >= extent.Z ? 1 : 2);
            float splitPos = axis == 0 ? (bMin.X + bMax.X) * 0.5f : (axis == 1 ? (bMin.Y + bMax.Y) * 0.5f : (bMin.Z + bMax.Z) * 0.5f);

            int mid = start;
            for (int i = start; i < end; i++)
            {
                int t = _triIndices[i];
                Vec3 cen = _triCentroids[t];
                float v = axis == 0 ? cen.X : (axis == 1 ? cen.Y : cen.Z);
                if (v < splitPos)
                {
                    (_triIndices[i], _triIndices[mid]) = (_triIndices[mid], _triIndices[i]);
                    mid++;
                }
            }

            if (mid == start || mid == end)
                mid = start + (count >> 1);

            int left = BuildNode(start, mid);
            int right = BuildNode(mid, end);
            _nodes[nodeIdx] = new Node
            {
                Min = bMin,
                Max = bMax,
                LeftOrFirst = left,
                RightOrCount = right,
                IsLeaf = false
            };
            return nodeIdx;
        }

        public bool Raycast(Vec3 ro, Vec3 rd, out BvhRayHit hit, float maxDist = float.MaxValue)
        {
            hit = default;
            if (_triCount == 0) return false;

            Vec3 invD = new Vec3(
                Math.Abs(rd.X) > 1e-8f ? 1f / rd.X : 1e8f,
                Math.Abs(rd.Y) > 1e-8f ? 1f / rd.Y : 1e8f,
                Math.Abs(rd.Z) > 1e-8f ? 1f / rd.Z : 1e8f);

            float bestT = maxDist;
            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;

            while (sp > 0)
            {
                ref readonly Node node = ref _nodes[stack[--sp]];
                if (!RayAabb(ro, invD, node.Min, node.Max, bestT))
                    continue;

                if (node.IsLeaf)
                {
                    int end = node.LeftOrFirst + node.RightOrCount;
                    for (int i = node.LeftOrFirst; i < end; i++)
                    {
                        int t = _triIndices[i];
                        int ia = _tris[t * 3];
                        int ib = _tris[t * 3 + 1];
                        int ic = _tris[t * 3 + 2];
                        Vec3 a = _verts[ia];
                        Vec3 b = _verts[ib];
                        Vec3 c = _verts[ic];
                        if (RayTriangle(ro, rd, a, b, c, out float tHit, out float u, out float v) && tHit < bestT)
                        {
                            bestT = tHit;
                            float w = 1f - u - v;
                            hit.Hit = true;
                            hit.TriangleIndex = t;
                            hit.Distance = tHit;
                            hit.Point = ro + rd * tHit;
                            hit.Barycentric = new Vec3(w, u, v);
                            hit.Normal = ComputeInterpolatedNormal(ia, ib, ic, a, b, c, w, u, v);
                        }
                    }
                }
                else
                {
                    if (sp < 62)
                    {
                        stack[sp++] = node.LeftOrFirst;
                        stack[sp++] = node.RightOrCount;
                    }
                }
            }

            return hit.Hit;
        }

        public bool ClosestPoint(Vec3 query, out BvhClosestHit hit, float maxDist = float.MaxValue)
        {
            hit = default;
            hit.DistanceSq = maxDist < float.MaxValue ? maxDist * maxDist : float.MaxValue;
            if (_triCount == 0) return false;

            Span<int> stack = stackalloc int[64];
            int sp = 0;
            stack[sp++] = 0;

            while (sp > 0)
            {
                ref readonly Node node = ref _nodes[stack[--sp]];
                float boxDistSq = AabbDistanceSq(query, node.Min, node.Max);
                if (boxDistSq > hit.DistanceSq)
                    continue;

                if (node.IsLeaf)
                {
                    int end = node.LeftOrFirst + node.RightOrCount;
                    for (int i = node.LeftOrFirst; i < end; i++)
                    {
                        int t = _triIndices[i];
                        int ia = _tris[t * 3];
                        int ib = _tris[t * 3 + 1];
                        int ic = _tris[t * 3 + 2];
                        Vec3 a = _verts[ia];
                        Vec3 b = _verts[ib];
                        Vec3 c = _verts[ic];

                        Vec3 p = ClosestPointOnTriangle(query, a, b, c, out float w, out float u, out float v);
                        float d2 = (query - p).LengthSquared;
                        if (d2 < hit.DistanceSq)
                        {
                            hit.Found = true;
                            hit.DistanceSq = d2;
                            hit.TriangleIndex = t;
                            hit.Point = p;
                            hit.Barycentric = new Vec3(w, u, v);
                            hit.Normal = ComputeInterpolatedNormal(ia, ib, ic, a, b, c, w, u, v);
                        }
                    }
                }
                else
                {
                    int l = node.LeftOrFirst;
                    int r = node.RightOrCount;
                    float dl = AabbDistanceSq(query, _nodes[l].Min, _nodes[l].Max);
                    float dr = AabbDistanceSq(query, _nodes[r].Min, _nodes[r].Max);
                    if (dl < dr)
                    {
                        if (dr <= hit.DistanceSq && sp < 63) stack[sp++] = r;
                        if (dl <= hit.DistanceSq && sp < 63) stack[sp++] = l;
                    }
                    else
                    {
                        if (dl <= hit.DistanceSq && sp < 63) stack[sp++] = l;
                        if (dr <= hit.DistanceSq && sp < 63) stack[sp++] = r;
                    }
                }
            }

            return hit.Found;
        }

        private Vec3 ComputeInterpolatedNormal(int ia, int ib, int ic, Vec3 a, Vec3 b, Vec3 c, float w, float u, float v)
        {
            if (_normals != null && _normals.Count == _verts.Count)
            {
                Vec3 n = _normals[ia] * w + _normals[ib] * u + _normals[ic] * v;
                if (n.LengthSquared > 1e-8f) return n.Normalized;
            }
            Vec3 fn = Vec3.Cross(b - a, c - a);
            return fn.LengthSquared > 1e-10f ? fn.Normalized : Vec3.Up;
        }

        private static float AabbDistanceSq(Vec3 p, Vec3 min, Vec3 max)
        {
            float dx = p.X < min.X ? min.X - p.X : (p.X > max.X ? p.X - max.X : 0f);
            float dy = p.Y < min.Y ? min.Y - p.Y : (p.Y > max.Y ? p.Y - max.Y : 0f);
            float dz = p.Z < min.Z ? min.Z - p.Z : (p.Z > max.Z ? p.Z - max.Z : 0f);
            return dx * dx + dy * dy + dz * dz;
        }

        private static bool RayAabb(Vec3 ro, Vec3 invD, Vec3 min, Vec3 max, float maxT)
        {
            float t1 = (min.X - ro.X) * invD.X;
            float t2 = (max.X - ro.X) * invD.X;
            float t3 = (min.Y - ro.Y) * invD.Y;
            float t4 = (max.Y - ro.Y) * invD.Y;
            float t5 = (min.Z - ro.Z) * invD.Z;
            float t6 = (max.Z - ro.Z) * invD.Z;

            float tmin = Math.Max(Math.Max(Math.Min(t1, t2), Math.Min(t3, t4)), Math.Min(t5, t6));
            float tmax = Math.Min(Math.Min(Math.Max(t1, t2), Math.Max(t3, t4)), Math.Max(t5, t6));
            return tmax >= 0f && tmin <= tmax && tmin <= maxT;
        }

        private static bool RayTriangle(Vec3 ro, Vec3 rd, Vec3 a, Vec3 b, Vec3 c, out float t, out float u, out float v)
        {
            t = 0f; u = 0f; v = 0f;
            Vec3 e1 = b - a;
            Vec3 e2 = c - a;
            Vec3 h = Vec3.Cross(rd, e2);
            float det = Vec3.Dot(e1, h);
            if (Math.Abs(det) < 1e-7f) return false;

            float invDet = 1f / det;
            Vec3 s = ro - a;
            u = Vec3.Dot(s, h) * invDet;
            if (u < 0f || u > 1f) return false;

            Vec3 q = Vec3.Cross(s, e1);
            v = Vec3.Dot(rd, q) * invDet;
            if (v < 0f || u + v > 1f) return false;

            t = Vec3.Dot(e2, q) * invDet;
            return t > 1e-4f;
        }

        // Christer Ericson - Real-Time Collision Detection: ClosestPointOnTriangle
        private static Vec3 ClosestPointOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c, out float wOut, out float uOut, out float vOut)
        {
            Vec3 ab = b - a;
            Vec3 ac = c - a;
            Vec3 ap = p - a;

            float d1 = Vec3.Dot(ab, ap);
            float d2 = Vec3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) { wOut = 1f; uOut = 0f; vOut = 0f; return a; }

            Vec3 bp = p - b;
            float d3 = Vec3.Dot(ab, bp);
            float d4 = Vec3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) { wOut = 0f; uOut = 1f; vOut = 0f; return b; }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            {
                float v = d1 / (d1 - d3);
                wOut = 1f - v; uOut = v; vOut = 0f;
                return a + ab * v;
            }

            Vec3 cp = p - c;
            float d5 = Vec3.Dot(ab, cp);
            float d6 = Vec3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) { wOut = 0f; uOut = 0f; vOut = 1f; return c; }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            {
                float w = d2 / (d2 - d6);
                wOut = 1f - w; uOut = 0f; vOut = w;
                return a + ac * w;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                wOut = 0f; uOut = 1f - w; vOut = w;
                return b + (c - b) * w;
            }

            float denom = 1f / Math.Max(1e-12f, va + vb + vc);
            float vB = vb * denom;
            float wC = vc * denom;
            wOut = 1f - vB - wC; uOut = vB; vOut = wC;
            return a + ab * vB + ac * wC;
        }
    }
}
