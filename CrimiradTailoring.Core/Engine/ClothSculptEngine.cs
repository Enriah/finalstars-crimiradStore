using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrimiradTailoring.Core.Models;

namespace CrimiradTailoring.Core.Engine
{
    public static class ClothSculptEngine
    {
        private static readonly ColorRgba[] Palette = new[]
        {
            new ColorRgba(156, 162, 172), // Xám trung tính mặc định (Clay/Neutral Gray)
            new ColorRgba(68, 124, 186),  // Xanh Denim sáng
            new ColorRgba(178, 62, 76),   // Đỏ Crimson
            new ColorRgba(74, 148, 118),  // Xanh Ngọc Lục Bảo
            new ColorRgba(156, 108, 182), // Tím Oải Hương
            new ColorRgba(204, 152, 72),  // Vàng Hổ Phách
            new ColorRgba(195, 186, 168)  // Kem Linen
        };

        /// <summary>
        /// Spawn một tấm vải phẳng cơ bản hình vuông lớn với lưới Tứ giác (Quad Mesh) chuẩn Blender.
        /// </summary>
        public static ClothEntity CreateSquareClothSheet(
            int index,
            float sizeMeters = 0.84f,
            int resolution = 24,
            Vec3 centerOffset = default)
        {
            var color = Palette[(index - 1) % Palette.Length];
            var cloth = new ClothEntity
            {
                Name = $"Mảnh Vải #{index}",
                ClothColor = color,
                WireColor = new ColorRgba(
                    (byte)Math.Min(255, color.R + 55),
                    (byte)Math.Min(255, color.G + 55),
                    (byte)Math.Min(255, color.B + 55))
            };

            var mesh = new MeshData { Name = cloth.Name };
            float half = sizeMeters * 0.5f;

            for (int iy = 0; iy <= resolution; iy++)
            {
                float v = (float)iy / resolution;
                float y = half - v * sizeMeters; // v=0 ở đỉnh trên (+half), v=1 ở đáy dưới (-half)

                for (int ix = 0; ix <= resolution; ix++)
                {
                    float u = (float)ix / resolution;
                    float x = -half + u * sizeMeters;

                    mesh.Vertices.Add(new Vec3(x, y, 0f) + centerOffset);
                    mesh.Normals.Add(Vec3.Forward);
                    mesh.UVs.Add(new Vec2(u, v));
                }
            }

            int stride = resolution + 1;
            for (int iy = 0; iy < resolution; iy++)
            {
                for (int ix = 0; ix < resolution; ix++)
                {
                    int i00 = iy * stride + ix;
                    int i10 = i00 + 1;
                    int i01 = (iy + 1) * stride + ix;
                    int i11 = i01 + 1;

                    // Lưới Tứ giác mặc định (CCW nhìn từ hướng +Z)
                    mesh.Faces.Add(new List<int> { i00, i01, i11, i10 });
                }
            }

            mesh.RebuildTrianglesFromFaces();
            mesh.RecalculateNormals();
            EnsureConsistentNormals(mesh);
            mesh.RestVertices = new List<Vec3>(mesh.Vertices);
            cloth.Mesh = mesh;
            RebuildDisplayMesh(cloth);
            return cloth;
        }

        /// <summary>
        /// Chọn vùng (Region Selection) trên mảnh vải theo 3 hình dạng: Vuông (Box), Tròn (Circle), Nón/Hình quạt (Cone).
        /// </summary>
        public static void SelectRegionOnCloth(
            ClothEntity cloth,
            RegionShapeMode shapeMode,
            Vec3 startPt,
            Vec3 dragPt,
            float coneHalfAngleDeg = 28f,
            bool additive = false)
        {
            if (!additive)
            {
                cloth.SelectedFaces.Clear();
                cloth.SelectedTriangles.Clear();
                cloth.SelectedVertices.Clear();
            }

            if (cloth.Mesh.Faces.Count == 0 && cloth.Mesh.Triangles.Count > 0)
            {
                for (int t = 0; t + 2 < cloth.Mesh.Triangles.Count; t += 3)
                {
                    cloth.Mesh.Faces.Add(new List<int>
                    {
                        cloth.Mesh.Triangles[t],
                        cloth.Mesh.Triangles[t + 1],
                        cloth.Mesh.Triangles[t + 2]
                    });
                }
                cloth.Mesh.RebuildTrianglesFromFaces();
            }

            float radius = Vec3.Distance(startPt, dragPt);
            if (radius < 0.005f)
            {
                cloth.ActiveRegion = null;
                return;
            }

            // Lưu thông số vùng chọn chính xác để khi bấm Cut tự tạo bù cạnh & điểm bo tròn
            cloth.ActiveRegion = new RegionSelectionSpec
            {
                Shape = shapeMode,
                StartPoint = startPt,
                DragPoint = dragPt,
                PlaneNormal = EstimateAverageNormalNear(cloth.Mesh, startPt),
                ConeHalfAngleDeg = coneHalfAngleDeg,
                IsActive = true
            };

            BuildRegionFrameAndPolygon(
                cloth.ActiveRegion,
                out Vec3 origin,
                out Vec3 uAxis,
                out Vec3 vAxis,
                out List<Vec2> clipPoly2D);

            var verts = cloth.Mesh.Vertices;
            for (int f = 0; f < cloth.Mesh.Faces.Count; f++)
            {
                var face = cloth.Mesh.Faces[f];
                if (face.Count < 3) continue;

                // Tính tâm của mặt tứ giác
                Vec3 center3D = Vec3.Zero;
                for (int k = 0; k < face.Count; k++)
                {
                    center3D = center3D + verts[face[k]];
                }
                center3D = center3D / face.Count;
                Vec2 center2D = ProjectToPlane2D(center3D, origin, uAxis, vAxis);

                if (IsPointInPolygon2D(center2D, clipPoly2D))
                {
                    cloth.SelectedFaces.Add(f);
                    for (int k = 0; k < face.Count; k++)
                        cloth.SelectedVertices.Add(face[k]);
                }
            }

            // Đồng bộ danh sách tam giác tương ứng với SelectedFaces để tô vàng trên 3D Viewport
            for (int t = 0; t < cloth.Mesh.TriangleToFace.Count; t++)
            {
                if (cloth.SelectedFaces.Contains(cloth.Mesh.TriangleToFace[t]))
                    cloth.SelectedTriangles.Add(t);
            }
        }

        /// <summary>
        /// Thực hiện cắt (Cut) vùng vải đang được chọn:
        /// Tạo lưới Tứ giác sạch chuẩn Topology cho cả mảnh cắt mới (Squircle/Y-Sector) lẫn mảnh vải gốc (O-Grid Ring + Smooth Relaxation).
        /// </summary>
        public static bool CutSelectedRegion(
            ClothEntity sourceCloth,
            CutActionMode mode,
            int nextClothNumber,
            out ClothEntity? newExtractedCloth)
        {
            newExtractedCloth = null;
            if (sourceCloth.SelectedFaces.Count == 0 && sourceCloth.SelectedTriangles.Count == 0)
                return false;

            int totalFaces = sourceCloth.Mesh.Faces.Count;
            if (totalFaces > 0 && sourceCloth.SelectedFaces.Count >= totalFaces)
                return false;

            var activeRegion = sourceCloth.ActiveRegion;
            bool isCurvedCut = activeRegion != null &&
                               activeRegion.IsActive &&
                               (activeRegion.Shape == RegionShapeMode.Circle || activeRegion.Shape == RegionShapeMode.Cone);

            var cleanSelection = new HashSet<int>(sourceCloth.SelectedFaces);
            if (isCurvedCut)
            {
                SanitizeSelectionForCleanTopology(sourceCloth.Mesh, cleanSelection);
                if (cleanSelection.Count == 0 || cleanSelection.Count >= totalFaces)
                    return false;
            }

            // Tìm các đỉnh ranh giới chia sẻ giữa vùng chọn và vùng còn lại trước khi tách
            var selectedVerts = new HashSet<int>();
            var unselectedVerts = new HashSet<int>();
            for (int f = 0; f < sourceCloth.Mesh.Faces.Count; f++)
            {
                var face = sourceCloth.Mesh.Faces[f];
                var targetSet = cleanSelection.Contains(f) ? selectedVerts : unselectedVerts;
                for (int k = 0; k < face.Count; k++)
                    targetSet.Add(face[k]);
            }

            var sharedSeamOldVerts = new HashSet<int>(selectedVerts);
            sharedSeamOldVerts.IntersectWith(unselectedVerts);

            MeshData? extractedMesh = null;
            Dictionary<int, int>? extractedVertMap = null;
            if (mode == CutActionMode.ExtractPiece)
            {
                if (isCurvedCut && activeRegion != null)
                {
                    // Thử tạo lưới Tứ giác sạch hoàn hảo (Squircle cho Tròn, 3-Block Y-Sector cho Nón) bám theo bề mặt vải gốc
                    extractedMesh = TryBuildCleanCurvedExtractedQuadMesh(sourceCloth.Mesh, activeRegion);
                }

                if (extractedMesh == null)
                {
                    extractedMesh = BuildSubMeshFromFaces(
                        sourceCloth.Mesh,
                        cleanSelection,
                        keepSelected: true,
                        out extractedVertMap);

                    if (isCurvedCut && activeRegion != null)
                    {
                        var extBoundaryVerts = new HashSet<int>();
                        foreach (int oldV in sharedSeamOldVerts)
                            if (extractedVertMap.TryGetValue(oldV, out int newV))
                                extBoundaryVerts.Add(newV);

                        ApplyOGridCleanCutBoundary(extractedMesh, extBoundaryVerts, activeRegion, isExtractedPiece: true);
                    }
                }
            }

            MeshData remainingMesh = BuildSubMeshFromFaces(
                sourceCloth.Mesh,
                cleanSelection,
                keepSelected: false,
                out Dictionary<int, int> remainVertMap);

            if (isCurvedCut && activeRegion != null)
            {
                var remBoundaryVerts = new HashSet<int>();
                foreach (int oldV in sharedSeamOldVerts)
                    if (remainVertMap.TryGetValue(oldV, out int newV))
                        remBoundaryVerts.Add(newV);

                ApplyOGridCleanCutBoundary(remainingMesh, remBoundaryVerts, activeRegion, isExtractedPiece: false);
            }

            if (mode == CutActionMode.ExtractPiece && extractedMesh != null && extractedMesh.Faces.Count > 0)
            {
                var color = Palette[(nextClothNumber - 1) % Palette.Length];
                newExtractedCloth = new ClothEntity
                {
                    Name = $"Mảnh Cắt #{nextClothNumber}",
                    ClothColor = color,
                    WireColor = new ColorRgba(
                        (byte)Math.Min(255, color.R + 55),
                        (byte)Math.Min(255, color.G + 55),
                        (byte)Math.Min(255, color.B + 55)),
                    Mesh = extractedMesh
                };

                // Dịch nhẹ mảnh vải vừa cắt ra phía trước +Z một chút (0.045m) để thấy rõ đã tách rời
                for (int i = 0; i < newExtractedCloth.Mesh.Vertices.Count; i++)
                {
                    newExtractedCloth.Mesh.Vertices[i] = newExtractedCloth.Mesh.Vertices[i] + new Vec3(0.03f, 0.02f, 0.045f);
                }
                newExtractedCloth.Mesh.RestVertices.Clear();
                newExtractedCloth.Mesh.EnsureRestVerticesInitialized();
                newExtractedCloth.SubdivisionLevel = sourceCloth.SubdivisionLevel;
                newExtractedCloth.EqualizeGridOnBend = sourceCloth.EqualizeGridOnBend;
                RebuildDisplayMesh(newExtractedCloth);
            }

            sourceCloth.Mesh = remainingMesh;
            sourceCloth.Mesh.EnsureRestVerticesInitialized();
            UpdateControlPointsAfterCut(sourceCloth, remainVertMap);

            sourceCloth.SelectedFaces.Clear();
            sourceCloth.SelectedTriangles.Clear();
            sourceCloth.SelectedVertices.Clear();
            sourceCloth.ActiveRegion = null;
            sourceCloth.CachedBorders = null;
            RebuildDisplayMesh(sourceCloth);
            return true;
        }

        /// <summary>
        /// Xóa dải ô tứ giác dọc theo cạnh biên MeshEdge được chọn (vẫn giữ lưới tứ giác sạch).
        /// </summary>
        public static bool DeleteBorderEdgeStrip(ClothEntity cloth, ClothBorderEdge border)
        {
            var chain = border.VertexIndices;
            if (chain.Count < 2 || cloth.Mesh.Faces.Count <= 1) return false;

            var borderEdgeKeys = new HashSet<long>(chain.Count);
            int segCount = border.IsClosedLoop ? chain.Count : (chain.Count - 1);
            for (int s = 0; s < segCount; s++)
            {
                int a = chain[s];
                int b = chain[(s + 1) % chain.Count];
                borderEdgeKeys.Add(((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b));
            }

            var facesToRemove = new HashSet<int>();
            for (int f = 0; f < cloth.Mesh.Faces.Count; f++)
            {
                var face = cloth.Mesh.Faces[f];
                for (int k = 0; k < face.Count; k++)
                {
                    int a = face[k];
                    int b = face[(k + 1) % face.Count];
                    long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    if (borderEdgeKeys.Contains(key))
                    {
                        facesToRemove.Add(f);
                        break;
                    }
                }
            }

            if (facesToRemove.Count == 0 || facesToRemove.Count >= cloth.Mesh.Faces.Count)
                return false;

            MeshData remainingMesh = BuildSubMeshFromFaces(
                cloth.Mesh,
                facesToRemove,
                keepSelected: false,
                out Dictionary<int, int> remainVertMap);

            cloth.Mesh = remainingMesh;
            cloth.Mesh.EnsureRestVerticesInitialized();
            UpdateControlPointsAfterCut(cloth, remainVertMap);
            cloth.SelectedFaces.Clear();
            cloth.SelectedTriangles.Clear();
            cloth.SelectedVertices.Clear();
            cloth.ActiveRegion = null;
            cloth.CachedBorders = null;
            RebuildDisplayMesh(cloth);
            return true;
        }

        /// <summary>
        /// Loại bỏ các ô đơn lẻ lồi quá 3 cạnh ra ngoài ranh giới để tránh tạo cầu mảnh 1 ô hoặc vết cắt rách trước khi tạo O-Grid.
        /// </summary>
        private static void SanitizeSelectionForCleanTopology(MeshData mesh, HashSet<int> selectedFaces)
        {
            int faceCount = mesh.Faces.Count;
            if (faceCount < 4) return;

            // Xây dựng kề cạnh giữa các mặt (EdgeKey -> list face indices)
            var edgeFaces = new Dictionary<long, List<int>>(faceCount * 2);
            var faceNeighbors = new List<int>[faceCount];
            for (int f = 0; f < faceCount; f++)
            {
                faceNeighbors[f] = new List<int>(4);
                var face = mesh.Faces[f];
                for (int k = 0; k < face.Count; k++)
                {
                    int a = face[k];
                    int b = face[(k + 1) % face.Count];
                    long eKey = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    if (!edgeFaces.TryGetValue(eKey, out var list))
                    {
                        list = new List<int>(2);
                        edgeFaces[eKey] = list;
                    }
                    list.Add(f);
                }
            }

            foreach (var list in edgeFaces.Values)
            {
                if (list.Count == 2)
                {
                    faceNeighbors[list[0]].Add(list[1]);
                    faceNeighbors[list[1]].Add(list[0]);
                }
            }

            for (int pass = 0; pass < 3; pass++)
            {
                bool changed = false;
                for (int f = 0; f < faceCount; f++)
                {
                    var nbrs = faceNeighbors[f];
                    if (nbrs.Count < 3) continue;

                    bool inSel = selectedFaces.Contains(f);
                    int oppCount = 0;
                    for (int i = 0; i < nbrs.Count; i++)
                    {
                        if (selectedFaces.Contains(nbrs[i]) != inSel)
                            oppCount++;
                    }

                    if (oppCount >= 3)
                    {
                        if (inSel) selectedFaces.Remove(f);
                        else selectedFaces.Add(f);
                        changed = true;
                    }
                }
                if (!changed) break;
            }
        }

        /// <summary>
        /// Tạo lưới Tứ giác sạch hoàn hảo cho mảnh Tròn (Squircle Grid) hoặc mảnh Nón (3-Block Y-Sector Patch),
        /// chiếu trực tiếp lên bề mặt 3D + UV của mảnh vải gốc (giữ nguyên độ cong 3D và UV nếu vải đang được uốn).
        /// Trả về null nếu vùng chọn trồi ra ngoài biên tấm vải gốc (khi đó dùng O-Grid bảo toàn mép ngoài).
        /// </summary>
        private static MeshData? TryBuildCleanCurvedExtractedQuadMesh(MeshData sourceMesh, RegionSelectionSpec region)
        {
            if (sourceMesh.Vertices.Count == 0 || sourceMesh.Triangles.Count < 3)
                return null;

            BuildRegionFrameAndPolygon(
                region,
                out Vec3 origin,
                out Vec3 uAxis,
                out Vec3 vAxis,
                out _);

            Vec2 drag2D = ProjectToPlane2D(region.DragPoint, origin, uAxis, vAxis);
            float radius = drag2D.Length;
            if (radius < 0.015f)
                return null;

            // Ước lượng kích thước 1 ô lưới của vải gốc
            float avgEdge = EstimateAverageEdgeLength(sourceMesh);
            if (avgEdge < 1e-4f) avgEdge = 0.035f;

            // Pre-project tam giác của sourceMesh lên mặt phẳng (uAxis, vAxis) để kiểm tra phủ & nội suy 3D/UV nhanh
            int triCount = sourceMesh.Triangles.Count / 3;
            var tri2D = new Vec2[triCount * 3];
            for (int t = 0; t < triCount; t++)
            {
                tri2D[t * 3 + 0] = ProjectToPlane2D(sourceMesh.Vertices[sourceMesh.Triangles[t * 3 + 0]], origin, uAxis, vAxis);
                tri2D[t * 3 + 1] = ProjectToPlane2D(sourceMesh.Vertices[sourceMesh.Triangles[t * 3 + 1]], origin, uAxis, vAxis);
                tri2D[t * 3 + 2] = ProjectToPlane2D(sourceMesh.Vertices[sourceMesh.Triangles[t * 3 + 2]], origin, uAxis, vAxis);
            }

            bool LiftPoint2D(Vec2 p2, out Vec3 pos3D, out Vec2 uv)
            {
                for (int t = 0; t < triCount; t++)
                {
                    Vec2 a = tri2D[t * 3 + 0];
                    Vec2 b = tri2D[t * 3 + 1];
                    Vec2 c = tri2D[t * 3 + 2];

                    if (TryBarycentric2D(p2, a, b, c, out float w0, out float w1, out float w2))
                    {
                        int i0 = sourceMesh.Triangles[t * 3 + 0];
                        int i1 = sourceMesh.Triangles[t * 3 + 1];
                        int i2 = sourceMesh.Triangles[t * 3 + 2];

                        pos3D = sourceMesh.Vertices[i0] * w0 + sourceMesh.Vertices[i1] * w1 + sourceMesh.Vertices[i2] * w2;
                        Vec2 uv0 = i0 < sourceMesh.UVs.Count ? sourceMesh.UVs[i0] : new Vec2(0.5f, 0.5f);
                        Vec2 uv1 = i1 < sourceMesh.UVs.Count ? sourceMesh.UVs[i1] : new Vec2(0.5f, 0.5f);
                        Vec2 uv2 = i2 < sourceMesh.UVs.Count ? sourceMesh.UVs[i2] : new Vec2(0.5f, 0.5f);
                        uv = uv0 * w0 + uv1 * w1 + uv2 * w2;
                        return true;
                    }
                }

                pos3D = origin + uAxis * p2.X + vAxis * p2.Y;
                uv = new Vec2(0.5f, 0.5f);
                return false;
            }

            if (region.Shape == RegionShapeMode.Circle)
            {
                // Kiểm tra đường tròn có nằm trọn trên bề mặt vải gốc không (cho phép sai số nhỏ ở rìa)
                int insideSamples = 0;
                const int ringChecks = 24;
                for (int i = 0; i < ringChecks; i++)
                {
                    float ang = 2f * (float)Math.PI * i / ringChecks;
                    Vec2 samplePt = new Vec2((float)Math.Cos(ang) * radius * 0.94f, (float)Math.Sin(ang) * radius * 0.94f);
                    if (LiftPoint2D(samplePt, out _, out _))
                        insideSamples++;
                }
                if (insideSamples < ringChecks - 2)
                    return null;

                // Dựng lưới Tứ giác Hình Tròn chuẩn 5-Block O-Grid Disk (lõi vuông + vành tỏa tròn):
                // Mọi góc tứ giác đều nằm quanh 65°..135°, 0 ô dẹt 180°, giống lưới Grid Fill chuẩn CAD/Blender!
                int k = Math.Max(4, Math.Min(12, (int)Math.Round((1.05f * radius) / avgEdge)));
                if (k % 2 != 0) k++;
                int mRings = Math.Max(2, Math.Min(6, (int)Math.Round((0.46f * radius) / avgEdge)));
                float halfInner = radius * 0.54f;

                var dst = new MeshData { Name = sourceMesh.Name };
                int AddPt(Vec2 p2)
                {
                    LiftPoint2D(p2, out Vec3 p3, out Vec2 uv);
                    int idx = dst.Vertices.Count;
                    dst.Vertices.Add(p3);
                    dst.Normals.Add(Vec3.Forward);
                    dst.UVs.Add(uv);
                    return idx;
                }

                // 1. Lưới lõi vuông (k x k) ở giữa
                var innerIdx = new int[k + 1, k + 1];
                for (int iy = 0; iy <= k; iy++)
                {
                    float y = -halfInner + (2f * halfInner * iy / k);
                    for (int ix = 0; ix <= k; ix++)
                    {
                        float x = -halfInner + (2f * halfInner * ix / k);
                        innerIdx[ix, iy] = AddPt(new Vec2(x, y));
                    }
                }
                for (int iy = 0; iy < k; iy++)
                {
                    for (int ix = 0; ix < k; ix++)
                    {
                        dst.Faces.Add(new List<int>(4)
                        {
                            innerIdx[ix, iy],
                            innerIdx[ix + 1, iy],
                            innerIdx[ix + 1, iy + 1],
                            innerIdx[ix, iy + 1]
                        });
                    }
                }

                // 2. Lấy vòng biên của lõi vuông theo thứ tự ngược chiều kim đồng hồ (4 * k đỉnh):
                // Right (iy: 0..k) -> Top (ix: k-1..0) -> Left (iy: k-1..0) -> Bottom (ix: 1..k-1)
                int perimCount = 4 * k;
                var rings = new int[mRings + 1, perimCount];
                var innerPos2D = new Vec2[perimCount];
                int p = 0;
                for (int iy = 0; iy <= k; iy++) { rings[0, p] = innerIdx[k, iy]; innerPos2D[p++] = new Vec2(halfInner, -halfInner + 2f * halfInner * iy / k); }
                for (int ix = k - 1; ix >= 0; ix--) { rings[0, p] = innerIdx[ix, k]; innerPos2D[p++] = new Vec2(-halfInner + 2f * halfInner * ix / k, halfInner); }
                for (int iy = k - 1; iy >= 0; iy--) { rings[0, p] = innerIdx[0, iy]; innerPos2D[p++] = new Vec2(-halfInner, -halfInner + 2f * halfInner * iy / k); }
                for (int ix = 1; ix < k; ix++) { rings[0, p] = innerIdx[ix, 0]; innerPos2D[p++] = new Vec2(-halfInner + 2f * halfInner * ix / k, -halfInner); }

                for (int rIdx = 1; rIdx <= mRings; rIdx++)
                {
                    float tRad = (float)rIdx / mRings;
                    for (int i = 0; i < perimCount; i++)
                    {
                        float ang = -(float)Math.PI * 0.25f + (2f * (float)Math.PI * i / perimCount);
                        Vec2 outerPt = new Vec2((float)Math.Cos(ang) * radius, (float)Math.Sin(ang) * radius);
                        Vec2 pt2 = Vec2.Lerp(innerPos2D[i], outerPt, tRad);
                        rings[rIdx, i] = AddPt(pt2);
                    }
                }

                for (int rIdx = 0; rIdx < mRings; rIdx++)
                {
                    for (int i = 0; i < perimCount; i++)
                    {
                        int iNext = (i + 1) % perimCount;
                        dst.Faces.Add(new List<int>(4)
                        {
                            rings[rIdx, i],
                            rings[rIdx + 1, i],
                            rings[rIdx + 1, iNext],
                            rings[rIdx, iNext]
                        });
                    }
                }

                dst.RebuildTrianglesFromFaces();
                dst.RecalculateNormals();
                EnsureConsistentNormals(dst);
                return dst;
            }
            else if (region.Shape == RegionShapeMode.Cone)
            {
                float baseAngle = (float)Math.Atan2(drag2D.Y, drag2D.X);
                float halfRad = region.ConeHalfAngleDeg * (float)Math.PI / 180f;

                Vec2 PtOnRay(float ang, float r) => new Vec2((float)Math.Cos(ang) * r, (float)Math.Sin(ang) * r);

                // Kiểm tra hình nón nằm trọn trên vải gốc
                int totalChecks = 16;
                int insideSamples = 0;
                for (int i = 0; i <= totalChecks; i++)
                {
                    float a = baseAngle - halfRad + ((float)i / totalChecks) * (2f * halfRad);
                    if (LiftPoint2D(PtOnRay(a, radius * 0.94f), out _, out _))
                        insideSamples++;
                }
                if (!LiftPoint2D(Vec2.Zero, out _, out _) || insideSamples < totalChecks)
                    return null;

                // Dựng lưới Nón All-Quad theo cấu trúc 3 Block hội tụ tại điểm giữa C (Y-Pole Quad Sector):
                // Đảm bảo CHÓP NÓN là Tứ giác sắc nét, 2 cạnh bên thẳng tắp, cung đáy tròn mượt mà, 0% tam giác!
                int m = Math.Max(4, Math.Min(10, (int)Math.Round((radius * 0.55f) / avgEdge)));

                Vec2 pTip = Vec2.Zero;
                Vec2 pSide1Mid = PtOnRay(baseAngle - halfRad, radius * 0.52f);
                Vec2 pSide1End = PtOnRay(baseAngle - halfRad, radius);
                Vec2 pSide2Mid = PtOnRay(baseAngle + halfRad, radius * 0.52f);
                Vec2 pSide2End = PtOnRay(baseAngle + halfRad, radius);
                Vec2 pArcMid = PtOnRay(baseAngle, radius);
                Vec2 pCenterPole = PtOnRay(baseAngle, radius * 0.54f);

                var dst = new MeshData { Name = sourceMesh.Name };
                var vertMap = new Dictionary<(int x, int y), int>();

                // Ta ghép 3 lưới (m x m) trên mặt phẳng chỉ số hợp nhất:
                // - Block 0 (Chóp nón): i in [0..m], j in [0..m] -> [pTip, pSide1Mid, pCenterPole, pSide2Mid]
                // - Block 1 (Cánh 1 + nửa cung 1): i in [m..2m], j in [0..m] -> [pSide1Mid, pSide1End, pArcMid, pCenterPole]
                // - Block 2 (Cánh 2 + nửa cung 2): i in [0..m], j in [m..2m] -> [pSide2Mid, pCenterPole, pArcMid, pSide2End]
                int AddOrGetGridVert(int gx, int gy, Vec2 p2)
                {
                    if (vertMap.TryGetValue((gx, gy), out int existing))
                        return existing;

                    LiftPoint2D(p2, out Vec3 p3, out Vec2 uv);
                    int idx = dst.Vertices.Count;
                    dst.Vertices.Add(p3);
                    dst.Normals.Add(Vec3.Forward);
                    dst.UVs.Add(uv);
                    vertMap[(gx, gy)] = idx;
                    return idx;
                }

                // Tọa độ nội suy bilinear có uốn cong mép ngoài theo cung tròn thật
                for (int gy = 0; gy <= m; gy++)
                {
                    float v = (float)gy / m;
                    for (int gx = 0; gx <= m; gx++)
                    {
                        float u = (float)gx / m;
                        Vec2 p2 = Vec2.Lerp(Vec2.Lerp(pTip, pSide1Mid, u), Vec2.Lerp(pSide2Mid, pCenterPole, u), v);
                        AddOrGetGridVert(gx, gy, p2);
                    }
                }

                // Block 1: cạnh ngoài (u=1) ôm sát cung tròn từ (baseAngle - halfRad) -> (baseAngle)
                for (int gy = 0; gy <= m; gy++)
                {
                    float v = (float)gy / m;
                    Vec2 arcPt = PtOnRay(baseAngle - halfRad + v * halfRad, radius);
                    Vec2 innerEdge = Vec2.Lerp(pSide1Mid, pCenterPole, v);
                    for (int gx = m + 1; gx <= 2 * m; gx++)
                    {
                        float u = (float)(gx - m) / m;
                        Vec2 straightLerp = Vec2.Lerp(innerEdge, Vec2.Lerp(pSide1End, pArcMid, v), u);
                        Vec2 curvedLerp = Vec2.Lerp(innerEdge, arcPt, u);
                        Vec2 p2 = Vec2.Lerp(straightLerp, curvedLerp, u);
                        AddOrGetGridVert(gx, gy, p2);
                    }
                }

                // Block 2: cạnh ngoài (v=1) ôm sát cung tròn từ (baseAngle + halfRad) -> (baseAngle)
                for (int gx = 0; gx <= m; gx++)
                {
                    float u = (float)gx / m;
                    Vec2 arcPt = PtOnRay(baseAngle + halfRad - u * halfRad, radius);
                    Vec2 innerEdge = Vec2.Lerp(pSide2Mid, pCenterPole, u);
                    for (int gy = m + 1; gy <= 2 * m; gy++)
                    {
                        float v = (float)(gy - m) / m;
                        Vec2 straightLerp = Vec2.Lerp(innerEdge, Vec2.Lerp(pSide2End, pArcMid, u), v);
                        Vec2 curvedLerp = Vec2.Lerp(innerEdge, arcPt, v);
                        Vec2 p2 = Vec2.Lerp(straightLerp, curvedLerp, v);
                        AddOrGetGridVert(gx, gy, p2);
                    }
                }

                void AddCellQuad(int gx, int gy)
                {
                    int i0 = vertMap[(gx, gy)];
                    int i1 = vertMap[(gx + 1, gy)];
                    int i2 = vertMap[(gx + 1, gy + 1)];
                    int i3 = vertMap[(gx, gy + 1)];
                    dst.Faces.Add(new List<int>(4) { i0, i1, i2, i3 });
                }

                for (int gy = 0; gy < m; gy++)
                    for (int gx = 0; gx < m; gx++)
                        AddCellQuad(gx, gy);

                for (int gy = 0; gy < m; gy++)
                    for (int gx = m; gx < 2 * m; gx++)
                        AddCellQuad(gx, gy);

                for (int gy = m; gy < 2 * m; gy++)
                    for (int gx = 0; gx < m; gx++)
                        AddCellQuad(gx, gy);

                dst.RebuildTrianglesFromFaces();
                dst.RecalculateNormals();
                EnsureConsistentNormals(dst);
                return dst;
            }

            return null;
        }

        private static bool TryBarycentric2D(Vec2 p, Vec2 a, Vec2 b, Vec2 c, out float w0, out float w1, out float w2)
        {
            Vec2 v0 = b - a;
            Vec2 v1 = c - a;
            Vec2 v2 = p - a;
            float denom = v0.X * v1.Y - v1.X * v0.Y;
            if (Math.Abs(denom) < 1e-9f)
            {
                w0 = w1 = w2 = 0f;
                return false;
            }
            float inv = 1f / denom;
            w1 = (v2.X * v1.Y - v1.X * v2.Y) * inv;
            w2 = (v0.X * v2.Y - v2.X * v0.Y) * inv;
            w0 = 1f - w1 - w2;
            const float eps = -1e-3f;
            return w0 >= eps && w1 >= eps && w2 >= eps;
        }

        private static float EstimateAverageEdgeLength(MeshData mesh)
        {
            if (mesh.Faces.Count == 0) return 0.035f;
            double sum = 0.0;
            int cnt = 0;
            int step = Math.Max(1, mesh.Faces.Count / 60);
            for (int f = 0; f < mesh.Faces.Count; f += step)
            {
                var face = mesh.Faces[f];
                for (int k = 0; k < face.Count; k++)
                {
                    Vec3 a = mesh.Vertices[face[k]];
                    Vec3 b = mesh.Vertices[face[(k + 1) % face.Count]];
                    sum += (b - a).Length;
                    cnt++;
                }
            }
            return cnt > 0 ? (float)(sum / cnt) : 0.035f;
        }

        /// <summary>
        /// Tạo vòng đệm O-Grid Tứ giác (1 vòng quad mịn bao quanh vết cắt) + làm mượt Laplacian 3 vòng đỉnh lân cận:
        /// Ngăn hoàn toàn hiện tượng các ô tứ giác bậc thang bị gập 180° thành hình tam giác quanh miệng lỗ Tròn/Nón!
        /// </summary>
        private static void ApplyOGridCleanCutBoundary(
            MeshData mesh,
            HashSet<int> cutBoundaryVerts,
            RegionSelectionSpec region,
            bool isExtractedPiece)
        {
            if (cutBoundaryVerts.Count < 3 || mesh.Faces.Count == 0)
                return;

            BuildRegionFrameAndPolygon(
                region,
                out Vec3 origin,
                out Vec3 uAxis,
                out Vec3 vAxis,
                out List<Vec2> regionPoly2D);

            var neighbors = new Dictionary<int, HashSet<int>>(mesh.Vertices.Count);
            // EdgeKey -> danh sách cạnh có hướng (faceA, faceB) thuộc các mặt hiện có
            var edgeRefs = new Dictionary<long, List<(int a, int b)>>();

            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var face = mesh.Faces[f];
                for (int k = 0; k < face.Count; k++)
                {
                    int a = face[k];
                    int b = face[(k + 1) % face.Count];
                    if (!neighbors.TryGetValue(a, out var setA)) { setA = new HashSet<int>(); neighbors[a] = setA; }
                    if (!neighbors.TryGetValue(b, out var setB)) { setB = new HashSet<int>(); neighbors[b] = setB; }
                    setA.Add(b);
                    setB.Add(a);

                    long eKey = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                    if (!edgeRefs.TryGetValue(eKey, out var refs))
                    {
                        refs = new List<(int a, int b)>(2);
                        edgeRefs[eKey] = refs;
                    }
                    refs.Add((a, b));
                }
            }

            // Phân loại cạnh biên: cạnh thuộc vết cắt (cả 2 đầu thuộc cutBoundaryVerts) vs cạnh mép ngoài gốc của tấm vải
            var seamBoundaryEdges = new List<(int a, int b)>();
            var allBoundaryVerts = new HashSet<int>();
            var outerNonCutNeighbors = new Dictionary<int, List<int>>();
            var seamVertsActive = new HashSet<int>();
            var seamAdj = new Dictionary<int, List<int>>();

            foreach (var kv in edgeRefs)
            {
                if (kv.Value.Count != 1) continue;
                var (ea, eb) = kv.Value[0];
                allBoundaryVerts.Add(ea);
                allBoundaryVerts.Add(eb);

                if (cutBoundaryVerts.Contains(ea) && cutBoundaryVerts.Contains(eb))
                {
                    seamBoundaryEdges.Add((ea, eb));
                    seamVertsActive.Add(ea);
                    seamVertsActive.Add(eb);

                    if (!seamAdj.TryGetValue(ea, out var la)) { la = new List<int>(2); seamAdj[ea] = la; }
                    if (!seamAdj.TryGetValue(eb, out var lb)) { lb = new List<int>(2); seamAdj[eb] = lb; }
                    la.Add(eb);
                    lb.Add(ea);
                }
                else
                {
                    if (cutBoundaryVerts.Contains(ea))
                    {
                        if (!outerNonCutNeighbors.TryGetValue(ea, out var l)) { l = new List<int>(2); outerNonCutNeighbors[ea] = l; }
                        l.Add(eb);
                    }
                    if (cutBoundaryVerts.Contains(eb))
                    {
                        if (!outerNonCutNeighbors.TryGetValue(eb, out var l)) { l = new List<int>(2); outerNonCutNeighbors[eb] = l; }
                        l.Add(ea);
                    }
                }
            }

            if (seamBoundaryEdges.Count < 3)
                return;

            Vec2 drag2D = ProjectToPlane2D(region.DragPoint, origin, uAxis, vAxis);
            float radius = Math.Max(0.01f, drag2D.Length);
            float avgEdge = EstimateAverageEdgeLength(mesh);
            float ringOffset = Math.Max(0.008f, avgEdge * 0.42f);

            // 1. Tạo lớp đỉnh vòng đệm ngoài/trong mới (C_v) nằm đúng trên đường cong Tròn/Nón,
            //    đồng thời lùi nhẹ đỉnh cũ (v) về phía phần thân lưới để mở rộng thành 1 vòng O-Grid tứ giác đều!
            var seamNewVertIdx = new Dictionary<int, int>(seamVertsActive.Count);
            foreach (int vIdx in seamVertsActive)
            {
                Vec3 origP = mesh.Vertices[vIdx];
                Vec2 p2 = ProjectToPlane2D(origP, origin, uAxis, vAxis);
                Vec2 curve2D = region.Shape == RegionShapeMode.Circle
                    ? ProjectPointToCircle2D(p2, radius)
                    : ProjectPointToPolygonBoundary2D(p2, regionPoly2D);

                Vec2 deltaCurve = curve2D - p2;
                Vec3 curve3D = origP + uAxis * deltaCurve.X + vAxis * deltaCurve.Y;

                // Tính vị trí mới cho đỉnh vòng đệm cũ vIdx (lùi về phía nội thất lưới, tránh dẫm lên curve3D)
                if (outerNonCutNeighbors.TryGetValue(vIdx, out var outerNbrs) && outerNbrs.Count > 0)
                {
                    // Đầu mút vết cắt chạm mép ngoài tấm vải -> trượt vIdx lùi lại dọc theo mép ngoài gốc
                    Vec3 outerNbr = mesh.Vertices[outerNbrs[0]];
                    mesh.Vertices[vIdx] = Vec3.Lerp(origP, outerNbr, 0.36f);
                }
                else
                {
                    Vec3 interiorPull = Vec3.Zero;
                    int intCount = 0;
                    if (neighbors.TryGetValue(vIdx, out var allN))
                    {
                        foreach (int nb in allN)
                        {
                            if (!seamVertsActive.Contains(nb))
                            {
                                interiorPull = interiorPull + mesh.Vertices[nb];
                                intCount++;
                            }
                        }
                    }

                    if (intCount > 0)
                    {
                        Vec3 avgInt = interiorPull / intCount;
                        Vec3 dirInward = avgInt - curve3D;
                        if (dirInward.LengthSquared > 1e-7f)
                            mesh.Vertices[vIdx] = curve3D + dirInward.Normalized * ringOffset;
                    }
                    else
                    {
                        Vec2 away2D = p2;
                        if ((away2D.X * away2D.X + away2D.Y * away2D.Y) < 1e-8f) away2D = new Vec2(1f, 0f);
                        float sign = isExtractedPiece ? -1f : 1f;
                        Vec2 dir2D = away2D.Normalized * sign;
                        mesh.Vertices[vIdx] = curve3D + uAxis * (dir2D.X * ringOffset) + vAxis * (dir2D.Y * ringOffset);
                    }
                }

                int newIdx = mesh.Vertices.Count;
                mesh.Vertices.Add(curve3D);
                mesh.Normals.Add(vIdx < mesh.Normals.Count ? mesh.Normals[vIdx] : Vec3.Forward);
                mesh.UVs.Add(vIdx < mesh.UVs.Count ? mesh.UVs[vIdx] : new Vec2(0.5f, 0.5f));
                seamNewVertIdx[vIdx] = newIdx;
            }

            // 2. Làm mượt dọc theo chuỗi biên mới seamNewVertIdx để khoảng cách điểm trên đường Tròn/Nón thật đều
            for (int iter = 0; iter < 4; iter++)
            {
                var nextPos = new Dictionary<int, Vec3>(seamVertsActive.Count);
                foreach (int vOld in seamVertsActive)
                {
                    if (!seamAdj.TryGetValue(vOld, out var adjOld) || adjOld.Count != 2)
                        continue;

                    int cSelf = seamNewVertIdx[vOld];
                    int cPrev = seamNewVertIdx[adjOld[0]];
                    int cNext = seamNewVertIdx[adjOld[1]];
                    Vec3 avg = (mesh.Vertices[cPrev] + mesh.Vertices[cNext]) * 0.5f;
                    Vec3 blended = Vec3.Lerp(mesh.Vertices[cSelf], avg, 0.45f);

                    Vec2 p2 = ProjectToPlane2D(blended, origin, uAxis, vAxis);
                    Vec2 target2D = region.Shape == RegionShapeMode.Circle
                        ? ProjectPointToCircle2D(p2, radius)
                        : ProjectPointToPolygonBoundary2D(p2, regionPoly2D);

                    Vec2 d2 = target2D - p2;
                    nextPos[cSelf] = blended + uAxis * d2.X + vAxis * d2.Y;
                }

                foreach (var kv in nextPos)
                    mesh.Vertices[kv.Key] = kv.Value;
            }

            // 3. Nối thêm 1 vòng O-Grid Tứ giác quanh vết cắt: với mỗi cạnh biên (ea -> eb),
            //    mặt mới ngược chiều sẽ là [eb, ea, C_ea, C_eb] để đảm bảo 100% cùng hướng pháp tuyến & 4 đỉnh rời!
            foreach (var (ea, eb) in seamBoundaryEdges)
            {
                int ca = seamNewVertIdx[ea];
                int cb = seamNewVertIdx[eb];
                if (ea != eb && ca != cb && ea != ca && eb != cb)
                {
                    mesh.Faces.Add(new List<int>(4) { eb, ea, ca, cb });
                    if (!neighbors.TryGetValue(ca, out var setCa)) { setCa = new HashSet<int>(); neighbors[ca] = setCa; }
                    if (!neighbors.TryGetValue(cb, out var setCb)) { setCb = new HashSet<int>(); neighbors[cb] = setCb; }
                    setCa.Add(ea); setCa.Add(cb);
                    setCb.Add(eb); setCb.Add(ca);
                    neighbors[ea].Add(ca);
                    neighbors[eb].Add(cb);
                }
            }

            // 4. Làm mượt Laplacian cho vòng đệm vOld (nếu không nằm trên mép ngoài gốc) + 2 vòng đỉnh lân cận
            var relaxVerts = new HashSet<int>();
            foreach (int vOld in seamVertsActive)
            {
                if (!outerNonCutNeighbors.ContainsKey(vOld))
                    relaxVerts.Add(vOld);

                if (!neighbors.TryGetValue(vOld, out var nbrs)) continue;
                foreach (int n1 in nbrs)
                {
                    if (!allBoundaryVerts.Contains(n1) && !seamNewVertIdx.ContainsValue(n1))
                    {
                        relaxVerts.Add(n1);
                        if (neighbors.TryGetValue(n1, out var nbrs2))
                        {
                            foreach (int n2 in nbrs2)
                            {
                                if (!allBoundaryVerts.Contains(n2) && !seamNewVertIdx.ContainsValue(n2))
                                    relaxVerts.Add(n2);
                            }
                        }
                    }
                }
            }

            for (int iter = 0; iter < 5; iter++)
            {
                var updated = new Dictionary<int, Vec3>(relaxVerts.Count);
                foreach (int vIdx in relaxVerts)
                {
                    if (!neighbors.TryGetValue(vIdx, out var nbrs) || nbrs.Count == 0)
                        continue;

                    Vec3 sum = Vec3.Zero;
                    foreach (int nIdx in nbrs)
                        sum = sum + mesh.Vertices[nIdx];
                    Vec3 avg = sum / nbrs.Count;
                    updated[vIdx] = Vec3.Lerp(mesh.Vertices[vIdx], avg, 0.45f);
                }

                foreach (var kv in updated)
                    mesh.Vertices[kv.Key] = kv.Value;
            }

            mesh.RebuildTrianglesFromFaces();
            mesh.RecalculateNormals();
            EnsureConsistentNormals(mesh);
        }

        private static Vec2 ProjectPointToCircle2D(Vec2 p, float radius)
        {
            float len = p.Length;
            if (len < 1e-6f) return new Vec2(radius, 0f);
            return p * (radius / len);
        }

        private static Vec2 ProjectPointToPolygonBoundary2D(Vec2 p, List<Vec2> poly)
        {
            Vec2 best = p;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                Vec2 a = poly[i];
                Vec2 b = poly[(i + 1) % poly.Count];
                Vec2 ab = b - a;
                float lenSq = ab.X * ab.X + ab.Y * ab.Y;
                float t = lenSq > 1e-8f ? Clamp01(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / lenSq) : 0f;
                Vec2 proj = a + ab * t;
                Vec2 d = p - proj;
                float dSq = d.X * d.X + d.Y * d.Y;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = proj;
                }
            }
            return best;
        }

        private static void UpdateControlPointsAfterCut(ClothEntity sourceCloth, Dictionary<int, int> remainVertMap)
        {
            for (int i = sourceCloth.Points.Count - 1; i >= 0; i--)
            {
                var pt = sourceCloth.Points[i];
                if (remainVertMap.TryGetValue(pt.AttachedVertexIndex, out int newVIdx))
                {
                    pt.AttachedVertexIndex = newVIdx;
                    pt.Position = sourceCloth.Mesh.Vertices[newVIdx];
                }
                else
                {
                    Guid removedId = pt.Id;
                    sourceCloth.Edges.RemoveAll(e => e.PointAId == removedId || e.PointBId == removedId);
                    sourceCloth.Points.RemoveAt(i);
                }
            }
        }

        private static void BuildRegionFrameAndPolygon(
            RegionSelectionSpec region,
            out Vec3 origin,
            out Vec3 uAxis,
            out Vec3 vAxis,
            out List<Vec2> poly2D)
        {
            origin = region.StartPoint;
            Vec3 n = region.PlaneNormal.LengthSquared > 1e-6f ? region.PlaneNormal.Normalized : Vec3.Forward;
            Vec3 upRef = Math.Abs(Vec3.Dot(n, Vec3.Up)) > 0.9f ? Vec3.Right : Vec3.Up;
            uAxis = Vec3.Cross(upRef, n).Normalized;
            vAxis = Vec3.Cross(n, uAxis).Normalized;

            poly2D = new List<Vec2>();
            Vec2 d2 = ProjectToPlane2D(region.DragPoint, origin, uAxis, vAxis);
            float radius = d2.Length;
            if (radius < 1e-4f) radius = 0.01f;

            switch (region.Shape)
            {
                case RegionShapeMode.Box:
                    float minX = Math.Min(0f, d2.X);
                    float maxX = Math.Max(0f, d2.X);
                    float minY = Math.Min(0f, d2.Y);
                    float maxY = Math.Max(0f, d2.Y);
                    poly2D.Add(new Vec2(minX, minY));
                    poly2D.Add(new Vec2(maxX, minY));
                    poly2D.Add(new Vec2(maxX, maxY));
                    poly2D.Add(new Vec2(minX, maxY));
                    break;

                case RegionShapeMode.Circle:
                    int circleSegments = 48;
                    for (int i = 0; i < circleSegments; i++)
                    {
                        float a = 2f * (float)Math.PI * i / circleSegments;
                        poly2D.Add(new Vec2((float)Math.Cos(a) * radius, (float)Math.Sin(a) * radius));
                    }
                    break;

                case RegionShapeMode.Cone:
                    float baseAngle = (float)Math.Atan2(d2.Y, d2.X);
                    float halfRad = region.ConeHalfAngleDeg * (float)Math.PI / 180f;
                    int arcSegments = 24;
                    poly2D.Add(Vec2.Zero); // Đỉnh nón
                    for (int i = 0; i <= arcSegments; i++)
                    {
                        float t = (float)i / arcSegments;
                        float a = baseAngle - halfRad + t * (2f * halfRad);
                        poly2D.Add(new Vec2((float)Math.Cos(a) * radius, (float)Math.Sin(a) * radius));
                    }
                    break;
            }
        }

        private static Vec3 EstimateAverageNormalNear(MeshData mesh, Vec3 point)
        {
            int idx = FindClosestVertexIndex(mesh, point);
            if (idx >= 0 && idx < mesh.Normals.Count && mesh.Normals[idx].LengthSquared > 1e-5f)
                return mesh.Normals[idx].Normalized;
            return Vec3.Forward;
        }

        private static Vec2 ProjectToPlane2D(Vec3 p, Vec3 origin, Vec3 uAxis, Vec3 vAxis)
        {
            Vec3 rel = p - origin;
            return new Vec2(Vec3.Dot(rel, uAxis), Vec3.Dot(rel, vAxis));
        }

        private static bool IsPointInPolygon2D(Vec2 p, List<Vec2> poly)
        {
            bool inside = false;
            int n = poly.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vec2 vi = poly[i];
                Vec2 vj = poly[j];
                if (((vi.Y > p.Y) != (vj.Y > p.Y)) &&
                    (p.X < (vj.X - vi.X) * (p.Y - vi.Y) / (vj.Y - vi.Y + 1e-8f) + vi.X))
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        private static MeshData BuildSubMeshFromFaces(
            MeshData src,
            HashSet<int> selectedFaces,
            bool keepSelected,
            out Dictionary<int, int> oldToNewVertIndex)
        {
            var dst = new MeshData { Name = src.Name };
            oldToNewVertIndex = new Dictionary<int, int>();

            for (int f = 0; f < src.Faces.Count; f++)
            {
                bool isSelected = selectedFaces.Contains(f);
                if (isSelected != keepSelected)
                    continue;

                var oldFace = src.Faces[f];
                var newFace = new List<int>(oldFace.Count);
                for (int k = 0; k < oldFace.Count; k++)
                {
                    newFace.Add(MapVertex(src, dst, oldFace[k], oldToNewVertIndex));
                }
                dst.Faces.Add(newFace);
            }

            dst.RebuildTrianglesFromFaces();
            dst.RecalculateNormals();
            EnsureConsistentNormals(dst);
            return dst;
        }

        private static int MapVertex(MeshData src, MeshData dst, int oldIdx, Dictionary<int, int> map)
        {
            if (map.TryGetValue(oldIdx, out int existing))
                return existing;

            int newIdx = dst.Vertices.Count;
            dst.Vertices.Add(src.Vertices[oldIdx]);
            dst.RestVertices.Add(oldIdx < src.RestVertices.Count ? src.RestVertices[oldIdx] : src.Vertices[oldIdx]);
            dst.Normals.Add(oldIdx < src.Normals.Count ? src.Normals[oldIdx] : Vec3.Forward);
            dst.UVs.Add(oldIdx < src.UVs.Count ? src.UVs[oldIdx] : new Vec2(0.5f, 0.5f));
            map[oldIdx] = newIdx;
            return newIdx;
        }

        /// <summary>
        /// Tạo một Điểm điều khiển (ControlPoint) mới trên bề mặt mảnh vải tại vị trí click.
        /// </summary>
        public static ControlPoint CreatePointOnClothSurface(ClothEntity cloth, Vec3 worldHitPoint)
        {
            int bestVertIdx = FindClosestVertexIndex(cloth.Mesh, worldHitPoint);
            Vec3 pos = bestVertIdx >= 0 ? cloth.Mesh.Vertices[bestVertIdx] : worldHitPoint;
            Vec2 uv = (bestVertIdx >= 0 && bestVertIdx < cloth.Mesh.UVs.Count)
                ? cloth.Mesh.UVs[bestVertIdx]
                : new Vec2(0.5f, 0.5f);

            int nextNum = cloth.Points.Count + 1;
            var pt = new ControlPoint
            {
                Name = $"P{nextNum}",
                OwnerClothId = cloth.Id,
                AttachedVertexIndex = bestVertIdx,
                SurfaceUV = uv,
                Position = pos,
                Radius = 0.24f,
                Bending = 0.65f
            };

            cloth.Points.Add(pt);
            return pt;
        }

        /// <summary>
        /// Tạo một Điểm nằm ngay trên một Cạnh đã có -> tự động chia Cạnh đó thành 2 Cạnh nối tiếp nhau!
        /// </summary>
        public static ControlPoint CreatePointOnExistingEdge(
            ClothEntity cloth,
            ControlEdge existingEdge,
            Vec3 clickWorldPoint,
            out ControlEdge secondEdge)
        {
            ControlPoint? pA = cloth.FindPoint(existingEdge.PointAId);
            ControlPoint? pB = cloth.FindPoint(existingEdge.PointBId);

            Vec3 targetPos = clickWorldPoint;
            if (pA != null && pB != null)
            {
                targetPos = ClosestPointOnSegment3D(clickWorldPoint, pA.Position, pB.Position, out _);
            }

            ControlPoint midPoint = CreatePointOnClothSurface(cloth, targetPos);
            midPoint.Radius = existingEdge.Radius;
            midPoint.Bending = existingEdge.Bending;

            Guid oldBId = existingEdge.PointBId;
            existingEdge.PointBId = midPoint.Id;
            if (pA != null)
                existingEdge.Name = $"E ({pA.Name}-{midPoint.Name})";

            secondEdge = new ControlEdge
            {
                Name = pB != null ? $"E ({midPoint.Name}-{pB.Name})" : $"E{cloth.Edges.Count + 1}",
                OwnerClothId = cloth.Id,
                PointAId = midPoint.Id,
                PointBId = oldBId,
                Radius = existingEdge.Radius,
                Bending = existingEdge.Bending
            };

            cloth.Edges.Add(secondEdge);
            return midPoint;
        }

        /// <summary>
        /// Mode 1 của công cụ Nối: Tạo Cạnh điều khiển (ControlEdge) từ 2 Điểm trên cùng một mảnh vải.
        /// </summary>
        public static ControlEdge? ConnectPointsToEdge(ClothEntity cloth, ControlPoint p1, ControlPoint p2)
        {
            if (p1.Id == p2.Id || p1.OwnerClothId != cloth.Id || p2.OwnerClothId != cloth.Id)
                return null;

            // Kiểm tra xem cạnh nối 2 điểm này đã tồn tại chưa
            foreach (var e in cloth.Edges)
            {
                if ((e.PointAId == p1.Id && e.PointBId == p2.Id) ||
                    (e.PointAId == p2.Id && e.PointBId == p1.Id))
                {
                    return e;
                }
            }

            var edge = new ControlEdge
            {
                Name = $"E{cloth.Edges.Count + 1} ({p1.Name}-{p2.Name})",
                OwnerClothId = cloth.Id,
                PointAId = p1.Id,
                PointBId = p2.Id,
                Radius = Math.Max(p1.Radius, p2.Radius),
                Bending = (p1.Bending + p2.Bending) * 0.5f
            };

            cloth.Edges.Add(edge);
            return edge;
        }

        private static Vec3 GetPointRestPosition(MeshData mesh, ControlPoint pt)
        {
            mesh.EnsureRestVerticesInitialized();
            if (pt.AttachedVertexIndex >= 0 && pt.AttachedVertexIndex < mesh.RestVertices.Count)
                return mesh.RestVertices[pt.AttachedVertexIndex];
            return pt.Position;
        }

        private static float EvaluateSymmetricAxialT(float t, float bending01)
        {
            float clampedT = Clamp01(t);
            float smooth = 0.5f * (1f - (float)Math.Cos(clampedT * Math.PI));
            float blend = Clamp01(bending01 * 2f);
            return clampedT + (smooth - clampedT) * blend;
        }

        private static Vec3 AddOmegaPleatLateralSwell(
            Vec3 ctrlDisp,
            Vec3 radialVec,
            float radialDist,
            float normalizedS,
            float radius,
            float bending,
            float w)
        {
            if (bending <= 0.55f || radialDist <= 1e-5f || normalizedS <= 1e-4f || normalizedS >= 0.9999f)
                return ctrlDisp;

            float dispLen = ctrlDisp.Length;
            if (dispLen <= 1e-4f)
                return ctrlDisp;

            Vec3 radDir = radialVec / radialDist;
            Vec3 dispDir = ctrlDisp / dispLen;
            Vec3 tangentOut = radDir - dispDir * Vec3.Dot(radDir, dispDir);
            float tLen = tangentOut.Length;
            if (tLen <= 1e-3f)
                return ctrlDisp;

            tangentOut = tangentOut / tLen;
            float omegaBase = (bending - 0.55f) / 0.45f;
            float omegaStrength = omegaBase * omegaBase;
            float amp = omegaStrength * 0.33f * Math.Min(dispLen * 1.30f, radius);
            float wave = (float)Math.Sin(2.0 * Math.PI * normalizedS);
            float scale = (amp * wave) / Math.Max(0.45f, w);
            return ctrlDisp + tangentOut * scale;
        }

        private static void CollectActiveControls(
            ClothEntity cloth,
            MeshData mesh,
            out List<(ControlEdge edge, Vec3 aRest, Vec3 bRest, Vec3 dispA, Vec3 dispB, bool sameDelta)> activeEdges,
            out List<(ControlPoint pt, Vec3 ptRest, Vec3 disp)> activePoints,
            out HashSet<Guid> pointsOnActiveEdges)
        {
            activeEdges = new List<(ControlEdge edge, Vec3 aRest, Vec3 bRest, Vec3 dispA, Vec3 dispB, bool sameDelta)>();
            activePoints = new List<(ControlPoint pt, Vec3 ptRest, Vec3 disp)>();
            pointsOnActiveEdges = new HashSet<Guid>();
            var pointsOnAnyEdge = new HashSet<Guid>();

            foreach (var edge in cloth.Edges)
            {
                var pA = cloth.FindPoint(edge.PointAId);
                var pB = cloth.FindPoint(edge.PointBId);
                if (pA == null || pB == null) continue;

                pointsOnAnyEdge.Add(pA.Id);
                pointsOnAnyEdge.Add(pB.Id);

                Vec3 aRest = GetPointRestPosition(mesh, pA);
                Vec3 bRest = GetPointRestPosition(mesh, pB);
                Vec3 dispA = pA.Position - aRest;
                Vec3 dispB = pB.Position - bRest;

                bool isActive = edge.IsEdited || pA.IsEdited || pB.IsEdited ||
                                dispA.LengthSquared > 1e-8f || dispB.LengthSquared > 1e-8f;
                if (!isActive) continue;

                pointsOnActiveEdges.Add(pA.Id);
                pointsOnActiveEdges.Add(pB.Id);
                activeEdges.Add((edge, aRest, bRest, dispA, dispB, (dispA - dispB).LengthSquared < 1e-10f));
            }

            foreach (var pt in cloth.Points)
            {
                if (pointsOnAnyEdge.Contains(pt.Id)) continue;
                Vec3 ptRest = GetPointRestPosition(mesh, pt);
                Vec3 disp = pt.Position - ptRest;
                if (!pt.IsEdited && disp.LengthSquared <= 1e-8f) continue;
                activePoints.Add((pt, ptRest, disp));
            }
        }

        private static void ApplyControlsToVertices(
            IReadOnlyList<Vec3> restVerts,
            List<Vec3> verts,
            IReadOnlyList<(ControlEdge edge, Vec3 aRest, Vec3 bRest, Vec3 dispA, Vec3 dispB, bool sameDelta)> activeEdges,
            IReadOnlyList<(ControlPoint pt, Vec3 ptRest, Vec3 disp)> activePoints,
            ref float[]? peakWeights)
        {
            int count = restVerts.Count;
            if (peakWeights == null || peakWeights.Length != count)
                peakWeights = new float[count];
            else
                Array.Clear(peakWeights, 0, count);

            while (verts.Count < count) verts.Add(Vec3.Zero);
            for (int i = 0; i < count; i++)
                verts[i] = restVerts[i];

            if (activeEdges.Count == 0 && activePoints.Count == 0)
                return;

            for (int i = 0; i < count; i++)
            {
                Vec3 pRest = restVerts[i];
                float prodOneMinusW = 1f;
                float sumAlpha = 0f;
                Vec3 weightedDisp = Vec3.Zero;

                for (int eIdx = 0; eIdx < activeEdges.Count; eIdx++)
                {
                    var item = activeEdges[eIdx];
                    float radius = Math.Max(0.01f, item.edge.Radius);
                    float bending = Clamp01(item.edge.Bending);

                    Vec3 closest = ClosestPointOnSegment3D(pRest, item.aRest, item.bRest, out float t);
                    Vec3 radialVec = pRest - closest;
                    float d = radialVec.Length;
                    if (d > radius) continue;

                    float s = d / radius;
                    float w = EvaluateBendingWeight(s, bending);
                    if (w <= 1e-5f) continue;

                    float smoothT = item.sameDelta ? t : EvaluateSymmetricAxialT(t, bending);
                    Vec3 ctrlDisp = Vec3.Lerp(item.dispA, item.dispB, smoothT);
                    ctrlDisp = AddOmegaPleatLateralSwell(ctrlDisp, radialVec, d, s, radius, bending, w);

                    float wClamped = w > 1f ? 1f : w;
                    prodOneMinusW *= (1f - wClamped);
                    float alpha = (wClamped * wClamped) / Math.Max(0.004f, 1f - 0.996f * wClamped);
                    sumAlpha += alpha;
                    weightedDisp = weightedDisp + ctrlDisp * alpha;
                }

                for (int pIdx = 0; pIdx < activePoints.Count; pIdx++)
                {
                    var item = activePoints[pIdx];
                    float radius = Math.Max(0.01f, item.pt.Radius);
                    float bending = Clamp01(item.pt.Bending);

                    Vec3 radialVec = pRest - item.ptRest;
                    float d = radialVec.Length;
                    if (d > radius) continue;

                    float s = d / radius;
                    float w = EvaluateBendingWeight(s, bending);
                    if (w <= 1e-5f) continue;

                    Vec3 ctrlDisp = AddOmegaPleatLateralSwell(item.disp, radialVec, d, s, radius, bending, w);

                    float wClamped = w > 1f ? 1f : w;
                    prodOneMinusW *= (1f - wClamped);
                    float alpha = (wClamped * wClamped) / Math.Max(0.004f, 1f - 0.996f * wClamped);
                    sumAlpha += alpha;
                    weightedDisp = weightedDisp + ctrlDisp * alpha;
                }

                if (sumAlpha > 1e-7f)
                {
                    float combinedWeight = 1f - prodOneMinusW;
                    peakWeights[i] = combinedWeight;
                    verts[i] = pRest + (weightedDisp / sumAlpha) * combinedWeight;
                }
            }
        }

        /// <summary>
        /// Chia mịn một cấp Catmull-Clark toàn tứ giác (mỗi ô -> 4 ô tứ giác), giữ sắc góc biên (>= 32°) và làm mượt C² cạnh cong & bề mặt.
        /// </summary>
        private static MeshData SubdivideCatmullClarkOnce(MeshData src, bool preserveParentMapping)
        {
            src.EnsureRestVerticesInitialized();
            int vCount = src.RestVertices.Count;
            var srcFaces = new List<List<int>>();
            if (src.Faces.Count > 0)
            {
                srcFaces.AddRange(src.Faces);
            }
            else
            {
                for (int t = 0; t + 2 < src.Triangles.Count; t += 3)
                    srcFaces.Add(new List<int> { src.Triangles[t], src.Triangles[t + 1], src.Triangles[t + 2] });
            }

            int fCount = srcFaces.Count;
            var dst = new MeshData { Name = src.Name };
            if (vCount < 3 || fCount == 0) return dst;

            // 1. Face Points
            var facePos = new Vec3[fCount];
            var faceUv = new Vec2[fCount];
            var vertAdjFaces = new List<int>[vCount];
            for (int i = 0; i < vCount; i++) vertAdjFaces[i] = new List<int>(4);

            var edgeMap = new Dictionary<long, int>(fCount * 2);
            var edges = new List<(int a, int b, List<int> adjFaces)>(fCount * 2);
            var vertEdges = new List<int>[vCount];
            for (int i = 0; i < vCount; i++) vertEdges[i] = new List<int>(4);

            for (int f = 0; f < fCount; f++)
            {
                var poly = srcFaces[f];
                Vec3 sumP = Vec3.Zero;
                Vec2 sumUv = Vec2.Zero;
                for (int k = 0; k < poly.Count; k++)
                {
                    int vi = poly[k];
                    sumP = sumP + src.RestVertices[vi];
                    sumUv = sumUv + (vi < src.UVs.Count ? src.UVs[vi] : new Vec2(0.5f, 0.5f));
                    vertAdjFaces[vi].Add(f);

                    int vj = poly[(k + 1) % poly.Count];
                    long key = vi < vj ? (((long)vi << 32) | (uint)vj) : (((long)vj << 32) | (uint)vi);
                    if (!edgeMap.TryGetValue(key, out int eIdx))
                    {
                        eIdx = edges.Count;
                        edgeMap[key] = eIdx;
                        edges.Add((Math.Min(vi, vj), Math.Max(vi, vj), new List<int>(2)));
                        vertEdges[vi].Add(eIdx);
                        vertEdges[vj].Add(eIdx);
                    }
                    edges[eIdx].adjFaces.Add(f);
                }
                facePos[f] = sumP / poly.Count;
                faceUv[f] = sumUv * (1f / poly.Count);
            }

            // 2. Edge Points
            int eCount = edges.Count;
            var edgePos = new Vec3[eCount];
            var edgeUv = new Vec2[eCount];
            for (int e = 0; e < eCount; e++)
            {
                var (va, vb, adjF) = edges[e];
                Vec3 pA = src.RestVertices[va];
                Vec3 pB = src.RestVertices[vb];
                Vec2 uvA = va < src.UVs.Count ? src.UVs[va] : new Vec2(0.5f, 0.5f);
                Vec2 uvB = vb < src.UVs.Count ? src.UVs[vb] : new Vec2(0.5f, 0.5f);

                if (adjF.Count == 2)
                {
                    edgePos[e] = (pA + pB + facePos[adjF[0]] + facePos[adjF[1]]) * 0.25f;
                    edgeUv[e] = (uvA + uvB + faceUv[adjF[0]] + faceUv[adjF[1]]) * 0.25f;
                }
                else
                {
                    edgePos[e] = (pA + pB) * 0.5f;
                    edgeUv[e] = (uvA + uvB) * 0.5f;
                }
            }

            // 3. New Vertex Points (ghim cố định các góc biên sắc nhọn, uốn C² cạnh mượt & mặt trong)
            var newVertPos = new Vec3[vCount];
            var newVertUv = new Vec2[vCount];
            var boundaryNbs = new List<int>(4);
            for (int v = 0; v < vCount; v++)
            {
                Vec3 oldP = src.RestVertices[v];
                newVertUv[v] = v < src.UVs.Count ? src.UVs[v] : new Vec2(0.5f, 0.5f);

                boundaryNbs.Clear();
                foreach (int eIdx in vertEdges[v])
                {
                    var ed = edges[eIdx];
                    if (ed.adjFaces.Count == 1)
                        boundaryNbs.Add(ed.a == v ? ed.b : ed.a);
                }

                if (boundaryNbs.Count > 0)
                {
                    if (boundaryNbs.Count == 2)
                    {
                        Vec3 p0 = src.RestVertices[boundaryNbs[0]];
                        Vec3 p1 = src.RestVertices[boundaryNbs[1]];
                        Vec3 dIn = (oldP - p0).Normalized;
                        Vec3 dOut = (p1 - oldP).Normalized;
                        // Nếu đỉnh gập góc >= 32° (cos <= 0.848) -> ghim giữ nguyên góc nhọn của mảnh vải
                        newVertPos[v] = Vec3.Dot(dIn, dOut) < 0.848f
                            ? oldP
                            : oldP * 0.75f + (p0 + p1) * 0.125f;
                    }
                    else
                    {
                        newVertPos[v] = oldP;
                    }
                    continue;
                }

                int valence = vertAdjFaces[v].Count;
                if (valence < 3)
                {
                    newVertPos[v] = oldP;
                    continue;
                }

                Vec3 fAvg = Vec3.Zero;
                foreach (int fi in vertAdjFaces[v]) fAvg = fAvg + facePos[fi];
                fAvg = fAvg / valence;

                Vec3 rAvg = Vec3.Zero;
                foreach (int eIdx in vertEdges[v])
                {
                    var ed = edges[eIdx];
                    rAvg = rAvg + (src.RestVertices[ed.a] + src.RestVertices[ed.b]) * 0.5f;
                }
                rAvg = rAvg / Math.Max(1, vertEdges[v].Count);

                newVertPos[v] = (fAvg + rAvg * 2f + oldP * (valence - 3)) / valence;
            }

            // 4. Đóng gói danh sách đỉnh + mặt tứ giác chuẩn
            int totalVerts = vCount + eCount + fCount;
            dst.RestVertices = new List<Vec3>(totalVerts);
            dst.UVs = new List<Vec2>(totalVerts);
            for (int i = 0; i < vCount; i++) { dst.RestVertices.Add(newVertPos[i]); dst.UVs.Add(newVertUv[i]); }
            for (int e = 0; e < eCount; e++) { dst.RestVertices.Add(edgePos[e]); dst.UVs.Add(edgeUv[e]); }
            for (int f = 0; f < fCount; f++) { dst.RestVertices.Add(facePos[f]); dst.UVs.Add(faceUv[f]); }
            dst.Vertices = new List<Vec3>(dst.RestVertices);

            var parentMapList = new List<int>(fCount * 4);
            for (int f = 0; f < fCount; f++)
            {
                var poly = srcFaces[f];
                int facePtIdx = vCount + eCount + f;
                int parentF = (preserveParentMapping && src.SubdivParentFaces != null && f < src.SubdivParentFaces.Length)
                    ? src.SubdivParentFaces[f]
                    : f;

                for (int k = 0; k < poly.Count; k++)
                {
                    int vPrev = poly[(k - 1 + poly.Count) % poly.Count];
                    int vCurr = poly[k];
                    int vNext = poly[(k + 1) % poly.Count];

                    long kNext = vCurr < vNext ? (((long)vCurr << 32) | (uint)vNext) : (((long)vNext << 32) | (uint)vCurr);
                    long kPrev = vPrev < vCurr ? (((long)vPrev << 32) | (uint)vCurr) : (((long)vCurr << 32) | (uint)vPrev);
                    int eNextIdx = vCount + edgeMap[kNext];
                    int ePrevIdx = vCount + edgeMap[kPrev];

                    dst.Faces.Add(new List<int> { vCurr, eNextIdx, facePtIdx, ePrevIdx });
                    parentMapList.Add(parentF);
                }
            }

            dst.SubdivParentFaces = parentMapList.ToArray();
            dst.RebuildTrianglesFromFaces();
            return dst;
        }

        private static float ComputeRestChecksum(IReadOnlyList<Vec3> verts)
        {
            float sum = verts.Count * 1.13f;
            int step = Math.Max(1, verts.Count / 48);
            for (int i = 0; i < verts.Count; i += step)
            {
                Vec3 v = verts[i];
                sum += v.X * (i + 1) * 0.17f + v.Y * (i + 3) * 0.31f + v.Z * (i + 7) * 0.43f;
            }
            if (verts.Count > 0)
            {
                Vec3 last = verts[verts.Count - 1];
                sum += last.X * 1.7f + last.Y * 2.3f + last.Z * 3.1f;
            }
            return sum;
        }

        private static void BuildStaticTopologyData(MeshData mesh, out int[][] flatAdj, out bool[] isBoundary)
        {
            int n = mesh.Vertices.Count;
            var adjLists = BuildVertexAdjacency(mesh);
            flatAdj = new int[n][];
            for (int i = 0; i < n; i++)
                flatAdj[i] = adjLists[i].ToArray();

            isBoundary = new bool[n];
            var edgeCounts = new Dictionary<long, int>(n * 2);
            void Tally(int a, int b)
            {
                long k = a < b ? (((long)a << 32) | (uint)b) : (((long)b << 32) | (uint)a);
                edgeCounts.TryGetValue(k, out int c);
                edgeCounts[k] = c + 1;
            }
            if (mesh.Faces.Count > 0)
            {
                foreach (var f in mesh.Faces)
                    for (int k = 0; k < f.Count; k++) Tally(f[k], f[(k + 1) % f.Count]);
            }
            else
            {
                for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
                {
                    Tally(mesh.Triangles[t], mesh.Triangles[t + 1]);
                    Tally(mesh.Triangles[t + 1], mesh.Triangles[t + 2]);
                    Tally(mesh.Triangles[t + 2], mesh.Triangles[t]);
                }
            }
            foreach (var kv in edgeCounts)
            {
                if (kv.Value == 1)
                {
                    int a = (int)(kv.Key >> 32);
                    int b = (int)(uint)kv.Key;
                    if (a >= 0 && a < n) isBoundary[a] = true;
                    if (b >= 0 && b < n) isBoundary[b] = true;
                }
            }
        }

        /// <summary>
        /// Dàn đều ô lưới dọc theo mặt phẳng tiếp tuyến (Tangent-Plane Laplacian Relaxation) sử dụng mảng đệm tĩnh không cấp phát rác bộ nhớ:
        /// Giúp các ô lưới dốc khi uốn mạnh trở nên đều đặn tuyệt đối mà KHÔNG làm xẹp chiều cao hay độ phồng của nếp uốn.
        /// </summary>
        private static void EqualizeGridTangentially(
            MeshData mesh,
            int[][] adj,
            bool[] isBoundary,
            float[]? peakWeights,
            ref Vec3[]? nextVerts,
            int iterations = 6,
            float strength = 0.36f)
        {
            var verts = mesh.Vertices;
            int n = verts.Count;
            if (n < 4 || adj.Length != n || isBoundary.Length != n) return;

            if (nextVerts == null || nextVerts.Length != n)
                nextVerts = new Vec3[n];

            for (int iter = 0; iter < iterations; iter++)
            {
                if (iter == 0 || iter == iterations / 2)
                {
                    mesh.RecalculateNormals();
                    EnsureConsistentNormals(mesh);
                }

                for (int i = 0; i < n; i++)
                {
                    int[] nbs = adj[i];
                    if (isBoundary[i] || nbs.Length == 0)
                    {
                        nextVerts[i] = verts[i];
                        continue;
                    }

                    float pin = 0f;
                    if (peakWeights != null && i < peakWeights.Length)
                    {
                        float pw = peakWeights[i];
                        if (pw >= 0.96f) { nextVerts[i] = verts[i]; continue; }
                        if (pw > 0.84f) pin = Clamp01((pw - 0.84f) / 0.12f);
                    }

                    Vec3 sum = Vec3.Zero;
                    for (int j = 0; j < nbs.Length; j++)
                        sum = sum + verts[nbs[j]];
                    Vec3 avg = sum / nbs.Length;

                    Vec3 lap = avg - verts[i];
                    Vec3 nrm = i < mesh.Normals.Count ? mesh.Normals[i] : Vec3.Forward;
                    Vec3 tangentStep = lap - nrm * Vec3.Dot(lap, nrm);
                    nextVerts[i] = verts[i] + tangentStep * (strength * (1f - pin));
                }

                for (int i = 0; i < n; i++)
                    verts[i] = nextVerts[i];
            }
        }

        /// <summary>
        /// Tạo lưới hiển thị mịn (Catmull-Clark 4x/16x + Dàn đều tiếp tuyến) trên nền lưới gốc.
        /// Tái sử dụng SubdivTopologyCache để khi kéo Điểm/Cạnh/Slider không tốn CPU chia lưới lại từ đầu.
        /// </summary>
        public static void RebuildDisplayMesh(ClothEntity cloth)
        {
            int level = Math.Max(0, Math.Min(2, cloth.SubdivisionLevel));
            cloth.Mesh.EnsureRestVerticesInitialized();

            if (level == 0 && !cloth.EqualizeGridOnBend)
            {
                cloth.RenderSubdividedMesh = null;
                cloth.SubdivCache = null;
                return;
            }

            int baseV = cloth.Mesh.RestVertices.Count;
            int baseF = cloth.Mesh.Faces.Count;
            float restChecksum = ComputeRestChecksum(cloth.Mesh.RestVertices);

            var cache = cloth.SubdivCache;
            bool cacheHit = cache != null &&
                            cache.Level == level &&
                            cache.BaseVertCount == baseV &&
                            cache.BaseFaceCount == baseF &&
                            Math.Abs(cache.RestChecksum - restChecksum) < 1e-4f &&
                            cache.TemplateMesh.RestVertices.Count > 0;

            if (!cacheHit)
            {
                MeshData template;
                if (level == 0)
                {
                    template = new MeshData
                    {
                        Name = cloth.Mesh.Name,
                        Vertices = new List<Vec3>(cloth.Mesh.Vertices),
                        RestVertices = new List<Vec3>(cloth.Mesh.RestVertices),
                        Normals = new List<Vec3>(cloth.Mesh.Normals),
                        UVs = new List<Vec2>(cloth.Mesh.UVs),
                        Triangles = new List<int>(cloth.Mesh.Triangles),
                        TriangleToFace = new List<int>(cloth.Mesh.TriangleToFace),
                        Faces = cloth.Mesh.Faces.Select(f => new List<int>(f)).ToList()
                    };
                }
                else
                {
                    template = SubdivideCatmullClarkOnce(cloth.Mesh, preserveParentMapping: false);
                    for (int s = 1; s < level; s++)
                        template = SubdivideCatmullClarkOnce(template, preserveParentMapping: true);
                }

                BuildStaticTopologyData(template, out var flatAdj, out var isBoundary);
                cloth.CachedBorders = null;
                cache = new SubdivTopologyCache
                {
                    Level = level,
                    BaseVertCount = baseV,
                    BaseFaceCount = baseF,
                    RestChecksum = restChecksum,
                    TemplateMesh = template,
                    Adjacency = flatAdj,
                    IsBoundary = isBoundary
                };
                cloth.SubdivCache = cache;
            }

            var tmpl = cache!.TemplateMesh;
            MeshData work = cloth.RenderSubdividedMesh != null &&
                            cloth.RenderSubdividedMesh.Vertices.Count == tmpl.RestVertices.Count &&
                            cloth.RenderSubdividedMesh.Faces.Count == tmpl.Faces.Count
                ? cloth.RenderSubdividedMesh
                : new MeshData
                {
                    Name = tmpl.Name,
                    Vertices = new List<Vec3>(tmpl.RestVertices),
                    RestVertices = tmpl.RestVertices,
                    Normals = new List<Vec3>(tmpl.RestVertices.Count),
                    UVs = tmpl.UVs,
                    Triangles = tmpl.Triangles,
                    TriangleToFace = tmpl.TriangleToFace,
                    Faces = tmpl.Faces,
                    SubdivParentFaces = tmpl.SubdivParentFaces
                };

            CollectActiveControls(cloth, cloth.Mesh, out var activeEdges, out var activePoints, out _);
            ApplyControlsToVertices(tmpl.RestVertices, work.Vertices, activeEdges, activePoints, ref cache.ScratchPeakWeights);

            if (cloth.EqualizeGridOnBend && (activeEdges.Count > 0 || activePoints.Count > 0))
                EqualizeGridTangentially(work, cache.Adjacency, cache.IsBoundary, cache.ScratchPeakWeights, ref cache.ScratchNextVerts, iterations: 6, strength: 0.36f);

            work.RecalculateNormals();
            EnsureConsistentNormals(work);
            cloth.RenderSubdividedMesh = work;
        }

        /// <summary>
        /// Chia mịn vĩnh viễn lưới gốc của mảnh vải thêm 1 cấp Catmull-Clark (x4 số ô tứ giác).
        /// </summary>
        public static void SubdivideBaseClothMesh(ClothEntity cloth)
        {
            cloth.Mesh.EnsureRestVerticesInitialized();
            if (cloth.Mesh.Faces.Count == 0 && cloth.Mesh.Triangles.Count == 0) return;

            var sub = SubdivideCatmullClarkOnce(cloth.Mesh, preserveParentMapping: false);
            sub.SubdivParentFaces = null;
            cloth.Mesh = sub;
            cloth.SubdivCache = null;
            cloth.SelectedFaces.Clear();
            cloth.SelectedTriangles.Clear();
            cloth.SelectedVertices.Clear();
            cloth.ActiveRegion = null;
            cloth.CachedBorders = null;
            RebuildClothDeformationFromControls(cloth);
        }

        /// <summary>
        /// Tái tạo toàn bộ biến dạng của mảnh vải từ RestVertices dựa trên tập hợp tất cả ControlEdge và ControlPoint đang hoạt động.
        /// Cho phép mỗi cạnh/điểm gần nhau giữ nguyên thông số Độ uốn (Bending) & Bán kính riêng biệt và cập nhật trực tiếp khi kéo slider.
        /// </summary>
        public static void RebuildClothDeformationFromControls(ClothEntity cloth)
        {
            var mesh = cloth.Mesh;
            mesh.EnsureRestVerticesInitialized();
            var restVerts = mesh.RestVertices;
            var verts = mesh.Vertices;
            if (verts.Count == 0 || restVerts.Count != verts.Count)
                return;

            CollectActiveControls(cloth, mesh, out var activeEdges, out var activePoints, out var pointsOnActiveEdges);
            float[]? dummyPeaks = null;
            ApplyControlsToVertices(restVerts, verts, activeEdges, activePoints, ref dummyPeaks);

            for (int i = 0; i < cloth.Points.Count; i++)
            {
                var pt = cloth.Points[i];
                if (pt.IsEdited || pointsOnActiveEdges.Contains(pt.Id))
                    continue;

                if (pt.AttachedVertexIndex >= 0 && pt.AttachedVertexIndex < verts.Count)
                    pt.Position = verts[pt.AttachedVertexIndex];
            }

            mesh.RecalculateNormals();
            EnsureConsistentNormals(mesh);
            RebuildDisplayMesh(cloth);
        }

        /// <summary>
        /// Di chuyển 1 Điểm điều khiển bằng deltaMove:
        /// - Nếu Điểm thuộc 1 hay nhiều Cạnh (ControlEdge), đánh dấu điểm & các cạnh nối đã chỉnh sửa và tái tạo toàn bộ biến dạng đồng nhất trên lưới.
        /// - Dùng RestVertices làm hệ quy chiếu không phá hủy để mọi nếp uốn liền lạc và giữ đúng Độ uốn riêng của từng cạnh.
        /// </summary>
        public static void MoveControlPoint(ClothEntity cloth, ControlPoint point, Vec3 deltaMove)
        {
            if (deltaMove.LengthSquared < 1e-10f) return;
            cloth.Mesh.EnsureRestVerticesInitialized();

            point.IsEdited = true;
            point.Position = point.Position + deltaMove;
            foreach (var e in cloth.Edges)
            {
                if (e.PointAId == point.Id || e.PointBId == point.Id)
                    e.IsEdited = true;
            }

            cloth.CachedBorders = null;
            RebuildClothDeformationFromControls(cloth);
        }

        /// <summary>
        /// Di chuyển toàn bộ 1 Cạnh điều khiển (cả 2 điểm đầu & cuối cùng dịch chuyển deltaMove)
        /// hoặc dịch chuyển độc lập đầu A (deltaA) và đầu B (deltaB).
        /// </summary>
        public static void MoveControlEdge(ClothEntity cloth, ControlEdge edge, Vec3 deltaA, Vec3 deltaB)
        {
            if (deltaA.LengthSquared < 1e-10f && deltaB.LengthSquared < 1e-10f)
                return;

            DeformClothByEdgeCylinder(cloth, edge, deltaA, deltaB);
        }

        /// <summary>
        /// Uốn Vùng Cầu (Spherical Falloff) xung quanh 1 Điểm theo khoảng cách trên lưới gốc (RestVertices), Radius và Bending [0..1].
        /// </summary>
        public static void DeformClothByPointSphere(ClothEntity cloth, ControlPoint point, Vec3 deltaMove)
        {
            if (deltaMove.LengthSquared < 1e-10f) return;
            cloth.Mesh.EnsureRestVerticesInitialized();
            point.IsEdited = true;
            point.Position = point.Position + deltaMove;
            cloth.CachedBorders = null;
            RebuildClothDeformationFromControls(cloth);
        }

        /// <summary>
        /// Uốn Vùng Trụ (Cylindrical / Capsule Falloff) xung quanh trục Cạnh (PointA - PointB)
        /// sử dụng khoảng cách trên lưới gốc (RestVertices) và hòa trộn Partition-of-Unity giữa các cạnh lân cận.
        /// </summary>
        public static void DeformClothByEdgeCylinder(
            ClothEntity cloth,
            ControlEdge edge,
            Vec3 deltaA,
            Vec3 deltaB)
        {
            ControlPoint? pA = cloth.FindPoint(edge.PointAId);
            ControlPoint? pB = cloth.FindPoint(edge.PointBId);
            if (pA == null || pB == null) return;

            cloth.Mesh.EnsureRestVerticesInitialized();
            edge.IsEdited = true;
            pA.IsEdited = true;
            pB.IsEdited = true;
            pA.Position = pA.Position + deltaA;
            pB.Position = pB.Position + deltaB;

            cloth.CachedBorders = null;
            RebuildClothDeformationFromControls(cloth);
        }

        /// <summary>
        /// Dịch chuyển toàn bộ mảnh vải trong không gian 3D.
        /// </summary>
        public static void TranslateEntireCloth(ClothEntity cloth, Vec3 delta)
        {
            if (delta.LengthSquared < 1e-10f) return;
            cloth.Mesh.EnsureRestVerticesInitialized();
            for (int i = 0; i < cloth.Mesh.Vertices.Count; i++)
            {
                cloth.Mesh.Vertices[i] = cloth.Mesh.Vertices[i] + delta;
                cloth.Mesh.RestVertices[i] = cloth.Mesh.RestVertices[i] + delta;
            }

            for (int i = 0; i < cloth.Points.Count; i++)
                cloth.Points[i].Position = cloth.Points[i].Position + delta;

            if (cloth.CustomOrigin.HasValue)
                cloth.CustomOrigin = cloth.CustomOrigin.Value + delta;

            if (cloth.RenderSubdividedMesh != null)
            {
                var sm = cloth.RenderSubdividedMesh;
                for (int i = 0; i < sm.Vertices.Count; i++) sm.Vertices[i] = sm.Vertices[i] + delta;
                if (cloth.SubdivCache != null && !ReferenceEquals(sm.RestVertices, cloth.SubdivCache.TemplateMesh.RestVertices))
                {
                    for (int i = 0; i < sm.RestVertices.Count; i++) sm.RestVertices[i] = sm.RestVertices[i] + delta;
                }
            }
            if (cloth.SubdivCache != null)
            {
                var tr = cloth.SubdivCache.TemplateMesh.RestVertices;
                for (int i = 0; i < tr.Count; i++) tr[i] = tr[i] + delta;
                cloth.SubdivCache.RestChecksum = ComputeRestChecksum(cloth.Mesh.RestVertices);
            }
            cloth.CachedBorders = null;
        }

        /// <summary>
        /// Trả về tọa độ Origin (tâm xoay / phóng to thu nhỏ) của mảnh vải:
        /// ưu tiên CustomOrigin nếu người dùng đã Set Origin vào điểm, ngược lại dùng tâm hình học.
        /// </summary>
        public static Vec3 GetClothOrigin(ClothEntity cloth) =>
            cloth.CustomOrigin ?? GetClothCentroid(cloth);

        /// <summary>
        /// Phóng to / Thu nhỏ (Scale) mảnh vải quanh Origin hiện tại của nó (CustomOrigin hoặc tâm hình học).
        /// </summary>
        public static void ScaleClothEntity(ClothEntity cloth, float newScaleX, float newScaleY)
        {
            newScaleX = Math.Max(0.2f, Math.Min(3.5f, newScaleX));
            newScaleY = Math.Max(0.2f, Math.Min(3.5f, newScaleY));

            float ratioX = newScaleX / Math.Max(0.01f, cloth.ScaleX);
            float ratioY = newScaleY / Math.Max(0.01f, cloth.ScaleY);
            if (Math.Abs(ratioX - 1f) < 1e-4f && Math.Abs(ratioY - 1f) < 1e-4f)
                return;

            cloth.Mesh.EnsureRestVerticesInitialized();
            Vec3 center = GetClothOrigin(cloth);
            for (int i = 0; i < cloth.Mesh.Vertices.Count; i++)
            {
                Vec3 rel = cloth.Mesh.Vertices[i] - center;
                cloth.Mesh.Vertices[i] = center + new Vec3(rel.X * ratioX, rel.Y * ratioY, rel.Z);

                Vec3 relR = cloth.Mesh.RestVertices[i] - center;
                cloth.Mesh.RestVertices[i] = center + new Vec3(relR.X * ratioX, relR.Y * ratioY, relR.Z);
            }

            cloth.ScaleX = newScaleX;
            cloth.ScaleY = newScaleY;
            SyncAttachedControlPoints(cloth);
            cloth.Mesh.RecalculateNormals();
            EnsureConsistentNormals(cloth.Mesh);
            RebuildDisplayMesh(cloth);
        }

        /// <summary>
        /// Xoay toàn bộ mảnh vải quanh một trục 3D bất kỳ đi qua Origin (hoặc pivot tùy chọn) bằng công thức Rodrigues.
        /// </summary>
        public static void RotateClothAroundAxis(
            ClothEntity cloth,
            Vec3 axis,
            float angleDeg,
            Vec3? customPivot = null)
        {
            if (Math.Abs(angleDeg) < 1e-4f || axis.LengthSquared < 1e-8f || cloth.Mesh.Vertices.Count == 0)
                return;

            cloth.Mesh.EnsureRestVerticesInitialized();
            Vec3 pivot = customPivot ?? GetClothOrigin(cloth);
            Vec3 k = axis.Normalized;
            float rad = angleDeg * (float)Math.PI / 180f;
            float cosA = (float)Math.Cos(rad);
            float sinA = (float)Math.Sin(rad);

            Vec3 RotateVec(Vec3 v)
            {
                Vec3 p = v - pivot;
                Vec3 rot = p * cosA + Vec3.Cross(k, p) * sinA + k * (Vec3.Dot(k, p) * (1f - cosA));
                return pivot + rot;
            }

            for (int i = 0; i < cloth.Mesh.Vertices.Count; i++)
            {
                cloth.Mesh.Vertices[i] = RotateVec(cloth.Mesh.Vertices[i]);
                cloth.Mesh.RestVertices[i] = RotateVec(cloth.Mesh.RestVertices[i]);
            }

            for (int i = 0; i < cloth.Points.Count; i++)
                cloth.Points[i].Position = RotateVec(cloth.Points[i].Position);

            if (cloth.CustomOrigin.HasValue && customPivot.HasValue)
                cloth.CustomOrigin = RotateVec(cloth.CustomOrigin.Value);

            cloth.Mesh.RecalculateNormals();
            EnsureConsistentNormals(cloth.Mesh);
            RebuildDisplayMesh(cloth);
        }

        /// <summary>
        /// Xoay mảnh vải đến góc Euler mục tiêu (RotXDeg, RotYDeg, RotZDeg) quanh Origin hiện tại của mảnh vải.
        /// </summary>
        public static void RotateClothToEuler(
            ClothEntity cloth,
            float targetRotXDeg,
            float targetRotYDeg,
            float targetRotZDeg)
        {
            float dX = targetRotXDeg - cloth.RotXDeg;
            float dY = targetRotYDeg - cloth.RotYDeg;
            float dZ = targetRotZDeg - cloth.RotZDeg;
            if (Math.Abs(dX) < 1e-4f && Math.Abs(dY) < 1e-4f && Math.Abs(dZ) < 1e-4f)
                return;

            Vec3 center = GetClothOrigin(cloth);
            if (Math.Abs(dX) >= 1e-4f) RotateClothAroundAxis(cloth, Vec3.Right, dX, center);
            if (Math.Abs(dY) >= 1e-4f) RotateClothAroundAxis(cloth, Vec3.Up, dY, center);
            if (Math.Abs(dZ) >= 1e-4f) RotateClothAroundAxis(cloth, Vec3.Forward, dZ, center);

            cloth.RotXDeg = targetRotXDeg;
            cloth.RotYDeg = targetRotYDeg;
            cloth.RotZDeg = targetRotZDeg;
        }

        /// <summary>
        /// Thực thi liên kết Nối Mode 2 (Snap: chập khít) hoặc Mode 3 (Hang: treo cách một khoảng HangDistance).
        /// </summary>
        public static void ApplyConnectionConstraint(
            IReadOnlyList<ClothEntity> allCloths,
            ClothConnection conn)
        {
            ClothEntity? srcCloth = null;
            ClothEntity? dstCloth = null;
            for (int i = 0; i < allCloths.Count; i++)
            {
                if (allCloths[i].Id == conn.SourceClothId) srcCloth = allCloths[i];
                if (allCloths[i].Id == conn.TargetClothId) dstCloth = allCloths[i];
            }

            if (srcCloth == null || dstCloth == null) return;

            Vec3 hangOffset = conn.Mode == ConnectMode.Hang
                ? new Vec3(0f, -Math.Max(0.01f, conn.HangDistance), 0f)
                : Vec3.Zero;

            if (!conn.IsEdgeConnection)
            {
                // Nối Điểm -> Điểm
                ControlPoint? srcPt = srcCloth.FindPoint(conn.SourceElementId);
                ControlPoint? dstPt = dstCloth.FindPoint(conn.TargetElementId);
                if (srcPt == null || dstPt == null) return;

                Vec3 desiredPos = dstPt.Position + hangOffset;
                Vec3 delta = desiredPos - srcPt.Position;
                if (delta.LengthSquared > 1e-7f)
                {
                    MoveControlPoint(srcCloth, srcPt, delta);
                }
            }
            else
            {
                // Nối Cạnh -> Cạnh
                ControlEdge? srcEdge = srcCloth.FindEdge(conn.SourceElementId);
                ControlEdge? dstEdge = dstCloth.FindEdge(conn.TargetElementId);
                if (srcEdge == null || dstEdge == null) return;

                ControlPoint? sA = srcCloth.FindPoint(srcEdge.PointAId);
                ControlPoint? sB = srcCloth.FindPoint(srcEdge.PointBId);
                ControlPoint? dA = dstCloth.FindPoint(dstEdge.PointAId);
                ControlPoint? dB = dstCloth.FindPoint(dstEdge.PointBId);
                if (sA == null || sB == null || dA == null || dB == null) return;

                Vec3 targetA = dA.Position + hangOffset;
                Vec3 targetB = dB.Position + hangOffset;

                Vec3 deltaA = targetA - sA.Position;
                Vec3 deltaB = targetB - sB.Position;
                if (deltaA.LengthSquared > 1e-7f || deltaB.LengthSquared > 1e-7f)
                {
                    MoveControlEdge(srcCloth, srcEdge, deltaA, deltaB);
                }
            }
        }

        public static Vec3 GetClothCentroid(ClothEntity cloth)
        {
            var verts = cloth.Mesh.Vertices;
            if (verts.Count == 0) return Vec3.Zero;
            Vec3 sum = Vec3.Zero;
            for (int i = 0; i < verts.Count; i++)
                sum = sum + verts[i];
            return sum / verts.Count;
        }

        private static Vec3 GetClothAverageNormal(MeshData mesh, IReadOnlyList<Vec3> verts)
        {
            Vec3 sum = Vec3.Zero;
            for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
            {
                Vec3 a = verts[mesh.Triangles[t]];
                Vec3 b = verts[mesh.Triangles[t + 1]];
                Vec3 c = verts[mesh.Triangles[t + 2]];
                sum = sum + Vec3.Cross(b - a, c - a);
            }
            return sum.LengthSquared > 1e-10f ? sum.Normalized : Vec3.Forward;
        }

        private static List<int>[] BuildVertexAdjacency(MeshData mesh)
        {
            int n = mesh.Vertices.Count;
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>(6);

            void AddUndir(int a, int b)
            {
                if (a == b || a < 0 || b < 0 || a >= n || b >= n) return;
                if (!adj[a].Contains(b)) adj[a].Add(b);
                if (!adj[b].Contains(a)) adj[b].Add(a);
            }

            if (mesh.Faces.Count > 0)
            {
                foreach (var f in mesh.Faces)
                {
                    for (int k = 0; k < f.Count; k++)
                        AddUndir(f[k], f[(k + 1) % f.Count]);
                }
            }
            else
            {
                for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
                {
                    AddUndir(mesh.Triangles[t], mesh.Triangles[t + 1]);
                    AddUndir(mesh.Triangles[t + 1], mesh.Triangles[t + 2]);
                    AddUndir(mesh.Triangles[t + 2], mesh.Triangles[t]);
                }
            }

            return adj;
        }

        /// <summary>
        /// Phát hiện các cạnh biên thực tế của mảnh vải (chuỗi cạnh chu vi chia tại các góc gãy >= 32°),
        /// hỗ trợ cả mảnh vuông lẫn mảnh đã cắt lỗ / tạo hình thang / uốn cong.
        /// </summary>
        public static List<ClothBorderEdge> DetectClothBorderEdges(ClothEntity cloth, IReadOnlyList<Vec3>? refVerts = null)
        {
            var result = new List<ClothBorderEdge>();
            var mesh = cloth.Mesh;
            var verts = refVerts ?? mesh.Vertices;
            if (verts.Count < 3) return result;

            var edgeCounts = new Dictionary<long, int>();
            var orientedEdges = new List<(int a, int b)>();

            void CountEdge(int a, int b)
            {
                if (a == b) return;
                long key = a < b ? (((long)a << 32) | (uint)b) : (((long)b << 32) | (uint)a);
                edgeCounts.TryGetValue(key, out int c);
                edgeCounts[key] = c + 1;
                orientedEdges.Add((a, b));
            }

            if (mesh.Faces.Count > 0)
            {
                foreach (var f in mesh.Faces)
                {
                    for (int k = 0; k < f.Count; k++)
                        CountEdge(f[k], f[(k + 1) % f.Count]);
                }
            }
            else
            {
                for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
                {
                    CountEdge(mesh.Triangles[t], mesh.Triangles[t + 1]);
                    CountEdge(mesh.Triangles[t + 1], mesh.Triangles[t + 2]);
                    CountEdge(mesh.Triangles[t + 2], mesh.Triangles[t]);
                }
            }

            var outMap = new Dictionary<int, List<int>>();
            var boundaryHalfEdges = new List<(int a, int b)>();
            foreach (var (a, b) in orientedEdges)
            {
                long key = a < b ? (((long)a << 32) | (uint)b) : (((long)b << 32) | (uint)a);
                if (edgeCounts.TryGetValue(key, out int cnt) && cnt == 1)
                {
                    boundaryHalfEdges.Add((a, b));
                    if (!outMap.TryGetValue(a, out var list))
                    {
                        list = new List<int>(2);
                        outMap[a] = list;
                    }
                    list.Add(b);
                }
            }

            if (boundaryHalfEdges.Count < 3) return result;

            var usedHalfEdges = new HashSet<long>();
            var loops = new List<List<int>>();

            foreach (var (startA, startB) in boundaryHalfEdges)
            {
                long firstKey = ((long)startA << 32) | (uint)startB;
                if (!usedHalfEdges.Add(firstKey)) continue;

                var loop = new List<int> { startA };
                int prev = startA;
                int curr = startB;
                int guard = boundaryHalfEdges.Count + 4;

                while (curr != startA && guard-- > 0)
                {
                    loop.Add(curr);
                    if (!outMap.TryGetValue(curr, out var nextCandidates) || nextCandidates.Count == 0)
                        break;

                    int chosen = -1;
                    float bestScore = float.MinValue;
                    Vec3 inDir = (verts[curr] - verts[prev]).Normalized;
                    foreach (int nxt in nextCandidates)
                    {
                        long ek = ((long)curr << 32) | (uint)nxt;
                        if (usedHalfEdges.Contains(ek)) continue;
                        Vec3 outDir = (verts[nxt] - verts[curr]).Normalized;
                        float score = Vec3.Dot(inDir, outDir);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            chosen = nxt;
                        }
                    }
                    if (chosen < 0) break;
                    usedHalfEdges.Add(((long)curr << 32) | (uint)chosen);
                    prev = curr;
                    curr = chosen;
                }

                if (loop.Count >= 3)
                    loops.Add(loop);
            }

            Vec3 meshCenter = Vec3.Zero;
            for (int i = 0; i < verts.Count; i++) meshCenter = meshCenter + verts[i];
            meshCenter = meshCenter / verts.Count;

            string DescribeDirection(Vec3 mid)
            {
                Vec3 off = mid - meshCenter;
                float ax = Math.Abs(off.X), ay = Math.Abs(off.Y), az = Math.Abs(off.Z);
                if (ay >= ax && ay >= az) return off.Y >= 0 ? "Trên" : "Dưới";
                if (ax >= ay && ax >= az) return off.X >= 0 ? "Phải" : "Trái";
                return off.Z >= 0 ? "Trước" : "Sau";
            }

            int borderIdx = 0;
            for (int loopId = 0; loopId < loops.Count; loopId++)
            {
                var loop = loops[loopId];
                int k = loop.Count;
                var cornerDots = new float[k];
                var candidateCorners = new List<int>();

                // Phát hiện đỉnh góc có độ gập >= 32° (cos <= 0.848)
                for (int i = 0; i < k; i++)
                {
                    Vec3 pPrev = verts[loop[(i - 1 + k) % k]];
                    Vec3 pCurr = verts[loop[i]];
                    Vec3 pNext = verts[loop[(i + 1) % k]];
                    Vec3 d1 = (pCurr - pPrev).Normalized;
                    Vec3 d2 = (pNext - pCurr).Normalized;
                    float dot = Vec3.Dot(d1, d2);
                    cornerDots[i] = dot;
                    if (dot < 0.848f)
                        candidateCorners.Add(i);
                }

                // Khử đỉnh góc kề sát nhau để giữ góc nhọn nhất
                var corners = new List<int>();
                for (int c = 0; c < candidateCorners.Count; c++)
                {
                    int idx = candidateCorners[c];
                    int prevIdx = (idx - 1 + k) % k;
                    int nextIdx = (idx + 1) % k;
                    if (k > 6 && ((cornerDots[prevIdx] < cornerDots[idx] && cornerDots[prevIdx] < 0.848f) ||
                                  (cornerDots[nextIdx] < cornerDots[idx] && cornerDots[nextIdx] < 0.848f)))
                        continue;
                    corners.Add(idx);
                }

                if (corners.Count >= 2)
                {
                    for (int c = 0; c < corners.Count; c++)
                    {
                        int sPos = corners[c];
                        int ePos = corners[(c + 1) % corners.Count];
                        var chain = new List<int>();
                        int curPos = sPos;
                        int stepGuard = k + 2;
                        while (stepGuard-- > 0)
                        {
                            chain.Add(loop[curPos]);
                            if (curPos == ePos) break;
                            curPos = (curPos + 1) % k;
                        }
                        if (chain.Count < 2) continue;

                        Vec3 mid = Vec3.Zero;
                        float arcLen = 0f;
                        for (int i = 0; i < chain.Count; i++)
                        {
                            mid = mid + verts[chain[i]];
                            if (i > 0) arcLen += Vec3.Distance(verts[chain[i - 1]], verts[chain[i]]);
                        }
                        mid = mid / chain.Count;
                        float chord = Vec3.Distance(verts[chain[0]], verts[chain[chain.Count - 1]]);

                        result.Add(new ClothBorderEdge
                        {
                            Index = borderIdx,
                            LoopId = loopId,
                            IsClosedLoop = false,
                            VertexIndices = chain,
                            Midpoint = mid,
                            ChordLength = chord,
                            ArcLength = arcLen,
                            Name = $"Biên #{borderIdx + 1} ({DescribeDirection(mid)})"
                        });
                        borderIdx++;
                    }
                }
                else
                {
                    Vec3 mid = Vec3.Zero;
                    float arcLen = 0f;
                    for (int i = 0; i < k; i++)
                    {
                        mid = mid + verts[loop[i]];
                        arcLen += Vec3.Distance(verts[loop[i]], verts[loop[(i + 1) % k]]);
                    }
                    mid = mid / k;
                    result.Add(new ClothBorderEdge
                    {
                        Index = borderIdx,
                        LoopId = loopId,
                        IsClosedLoop = true,
                        VertexIndices = new List<int>(loop),
                        Midpoint = mid,
                        ChordLength = arcLen / (float)Math.PI,
                        ArcLength = arcLen,
                        Name = $"Vòng biên #{borderIdx + 1}"
                    });
                    borderIdx++;
                }
            }

            return result;
        }

        /// <summary>
        /// Đo chiều dài dây cung và độ võng phẳng của cạnh biên MeshEdge hiện tại để Copy/Paste dáng biên.
        /// </summary>
        public static void MeasureBorderCurveProfile(
            ClothEntity cloth,
            ClothBorderEdge border,
            out float chordLen,
            out float arcOutSagitta)
        {
            chordLen = border.ChordLength;
            arcOutSagitta = 0f;
            var verts = cloth.Mesh.Vertices;
            var chain = border.VertexIndices;
            if (chain.Count < 3 || verts.Count == 0) return;

            Vec3 pStart = verts[chain[0]];
            Vec3 pEnd = verts[chain[chain.Count - 1]];
            Vec3 chordVec = pEnd - pStart;
            Vec3 chordDir = chordVec.LengthSquared > 1e-8f ? chordVec.Normalized : Vec3.Right;
            Vec3 midPt = Vec3.Zero;
            for (int i = 0; i < chain.Count; i++) midPt = midPt + verts[chain[i]];
            midPt = midPt / chain.Count;

            Vec3 meshCenter = GetClothCentroid(cloth);
            Vec3 surfNormal = GetClothAverageNormal(cloth.Mesh, verts);
            Vec3 inPlaneOut = Vec3.Cross(chordDir, surfNormal);
            if (inPlaneOut.LengthSquared < 1e-6f) inPlaneOut = Vec3.Up;
            inPlaneOut = inPlaneOut.Normalized;
            if (Vec3.Dot(inPlaneOut, midPt - meshCenter) < 0f) inPlaneOut = -inPlaneOut;

            Vec3 pMid = verts[chain[chain.Count / 2]];
            Vec3 chordMid = (pStart + pEnd) * 0.5f;
            arcOutSagitta = Vec3.Dot(pMid - chordMid, inPlaneOut);
        }

        /// <summary>
        /// Biến dạng mượt toàn tấm vải theo một cạnh biên thực tế (Scale cạnh tạo hình thang, Cong phẳng tạo hình quạt/nón không chóp, Cong 3D, Xoay cạnh, Di chuyển cạnh).
        /// Giải phương trình Laplace (∇²Δ = 0) với nội suy tuyến tính dọc các cạnh biên kề để giữ lưới tứ giác sạch đều tuyệt đối.
        /// </summary>
        public static void ApplyBorderEdgeDeformation(
            ClothEntity cloth,
            IReadOnlyList<ClothBorderEdge> allBorders,
            ClothBorderEdge activeBorder,
            float scaleFactor,
            float arc2DMeters,
            float wrap3DDeg,
            Vec3 rotateAxis,
            float rotateDeg,
            Vec3 moveDelta,
            IReadOnlyList<Vec3>? baseRestVerts = null,
            IReadOnlyList<Vec3>? basePointPositions = null)
        {
            var mesh = cloth.Mesh;
            mesh.EnsureRestVerticesInitialized();
            int n = mesh.RestVertices.Count;
            if (n < 3 || activeBorder.VertexIndices.Count < 2) return;

            var src = baseRestVerts != null && baseRestVerts.Count == n
                ? baseRestVerts
                : new List<Vec3>(mesh.RestVertices);

            bool isIdentity = Math.Abs(scaleFactor - 1f) < 1e-4f &&
                              Math.Abs(arc2DMeters) < 1e-5f &&
                              Math.Abs(wrap3DDeg) < 0.02f &&
                              Math.Abs(rotateDeg) < 0.02f &&
                              moveDelta.LengthSquared < 1e-10f;

            if (isIdentity)
            {
                for (int i = 0; i < n; i++)
                {
                    mesh.RestVertices[i] = src[i];
                    mesh.Vertices[i] = src[i];
                }
                if (basePointPositions != null && basePointPositions.Count == cloth.Points.Count)
                {
                    for (int p = 0; p < cloth.Points.Count; p++)
                        cloth.Points[p].Position = basePointPositions[p];
                }
                RebuildClothDeformationFromControls(cloth);
                return;
            }

            // 1. Tham số hình học cơ bản trên cạnh biên được chọn
            var chain = activeBorder.VertexIndices;
            int m = chain.Count;
            var arcCum = new float[m];
            Vec3 mid = Vec3.Zero;
            for (int i = 0; i < m; i++)
            {
                mid = mid + src[chain[i]];
                if (i > 0)
                    arcCum[i] = arcCum[i - 1] + Vec3.Distance(src[chain[i - 1]], src[chain[i]]);
            }
            mid = mid / m;
            float totalArc = Math.Max(1e-5f, arcCum[m - 1]);

            Vec3 pStart = src[chain[0]];
            Vec3 pEnd = src[chain[m - 1]];
            Vec3 chordVec = pEnd - pStart;
            Vec3 chordDir = chordVec.LengthSquared > 1e-8f ? chordVec.Normalized : Vec3.Right;
            Vec3 surfNormal = GetClothAverageNormal(mesh, src);
            Vec3 meshCenter = Vec3.Zero;
            for (int i = 0; i < n; i++) meshCenter = meshCenter + src[i];
            meshCenter = meshCenter / n;

            Vec3 inPlaneOut = Vec3.Cross(chordDir, surfNormal);
            if (inPlaneOut.LengthSquared < 1e-6f) inPlaneOut = Vec3.Up;
            inPlaneOut = inPlaneOut.Normalized;
            if (Vec3.Dot(inPlaneOut, mid - meshCenter) < 0f)
                inPlaneOut = -inPlaneOut;

            Vec3 rotAx = rotateAxis.LengthSquared > 1e-8f ? rotateAxis.Normalized : surfNormal;
            float rotRad = rotateDeg * (float)Math.PI / 180f;
            float cosR = (float)Math.Cos(rotRad);
            float sinR = (float)Math.Sin(rotRad);

            Vec3 RotateAroundMid(Vec3 pt)
            {
                Vec3 r = pt - mid;
                return mid + r * cosR + Vec3.Cross(rotAx, r) * sinR + rotAx * (Vec3.Dot(rotAx, r) * (1f - cosR));
            }

            float cosPhi = (float)Math.Cos(1.15);
            float denomPhi = Math.Max(1e-4f, 1f - cosPhi);

            // 2. Tính độ dịch chuyển mục tiêu Δ cho các đỉnh trên cạnh biên đang chọn
            var delta = new Vec3[n];
            var isFixed = new bool[n];

            for (int k = 0; k < m; k++)
            {
                int vi = chain[k];
                Vec3 p0 = src[vi];
                float t = arcCum[k] / totalArc; // [0..1]
                Vec3 p = p0;

                // a) Scale cạnh
                if (Math.Abs(scaleFactor - 1f) >= 1e-4f)
                {
                    if (!activeBorder.IsClosedLoop)
                    {
                        float proj = Vec3.Dot(p - mid, chordDir);
                        p = p + chordDir * (proj * (scaleFactor - 1f));
                    }
                    else
                    {
                        p = mid + (p - mid) * scaleFactor;
                    }
                }

                // b) Cong Phẳng (tạo cung quạt / nón cụt trong mặt phẳng vải)
                if (Math.Abs(arc2DMeters) >= 1e-5f)
                {
                    float u = 2f * t - 1f;
                    float profile = ((float)Math.Cos(u * 1.15) - cosPhi) / denomPhi;
                    p = p + inPlaneOut * (arc2DMeters * profile);
                }

                // c) Cong 3D (cuộn vòng cung theo pháp tuyến để uốn thành ống / nón 3D)
                if (!activeBorder.IsClosedLoop && Math.Abs(wrap3DDeg) >= 0.02f)
                {
                    float scaledLen = Math.Max(0.02f, totalArc * scaleFactor);
                    float theta = wrap3DDeg * (float)Math.PI / 180f;
                    float rad = scaledLen / theta;
                    float x = (t - 0.5f) * scaledLen;
                    float ang = x / rad;
                    Vec3 straightRef = mid + chordDir * x;
                    Vec3 localOffset = p - straightRef;
                    p = mid + chordDir * (rad * (float)Math.Sin(ang))
                            + surfNormal * (rad * (1f - (float)Math.Cos(ang)))
                            + localOffset;
                }

                // d) Xoay cạnh quanh trung điểm
                if (Math.Abs(rotateDeg) >= 0.02f)
                    p = RotateAroundMid(p);

                // e) Di chuyển cạnh
                p = p + moveDelta;

                delta[vi] = p - p0;
                isFixed[vi] = true;
            }

            // 3. Cố định các biên neo đối diện (=0) và nội suy tuyến tính 1D dọc 2 biên kề
            var adjSides = new List<ClothBorderEdge>();
            if (!activeBorder.IsClosedLoop && allBorders.Count >= 3)
            {
                int endA = chain[0];
                int endB = chain[m - 1];
                foreach (var b in allBorders)
                {
                    if (b == activeBorder || b.Index == activeBorder.Index || b.IsClosedLoop || b.VertexIndices.Count < 2)
                        continue;

                    bool isAdj = b.LoopId == activeBorder.LoopId &&
                                 (b.VertexIndices[0] == endA || b.VertexIndices[b.VertexIndices.Count - 1] == endA ||
                                  b.VertexIndices[0] == endB || b.VertexIndices[b.VertexIndices.Count - 1] == endB);

                    if (isAdj)
                    {
                        adjSides.Add(b);
                    }
                    else
                    {
                        foreach (int vi in b.VertexIndices)
                        {
                            if (!isFixed[vi])
                            {
                                delta[vi] = Vec3.Zero;
                                isFixed[vi] = true;
                            }
                        }
                    }
                }

                foreach (var adjEdge in adjSides)
                {
                    var ac = adjEdge.VertexIndices;
                    int am = ac.Count;
                    var acArc = new float[am];
                    for (int i = 1; i < am; i++)
                        acArc[i] = acArc[i - 1] + Vec3.Distance(src[ac[i - 1]], src[ac[i]]);
                    float acTotal = Math.Max(1e-5f, acArc[am - 1]);
                    Vec3 d0 = isFixed[ac[0]] ? delta[ac[0]] : Vec3.Zero;
                    Vec3 d1 = isFixed[ac[am - 1]] ? delta[ac[am - 1]] : Vec3.Zero;
                    for (int i = 0; i < am; i++)
                    {
                        int vi = ac[i];
                        if (!isFixed[vi])
                        {
                            delta[vi] = Vec3.Lerp(d0, d1, acArc[i] / acTotal);
                            isFixed[vi] = true;
                        }
                    }
                }
            }
            else if (!activeBorder.IsClosedLoop)
            {
                foreach (var b in allBorders)
                {
                    if (b == activeBorder || b.Index == activeBorder.Index) continue;
                    foreach (int vi in b.VertexIndices)
                    {
                        if (!isFixed[vi])
                        {
                            delta[vi] = Vec3.Zero;
                            isFixed[vi] = true;
                        }
                    }
                }
            }
            else
            {
                // Vòng biên khép kín: neo những đỉnh ở sâu nhất trong phần ruột lưới (gần tâm vùng)
                var adjGraphTmp = BuildVertexAdjacency(mesh);
                var dist = new int[n];
                for (int i = 0; i < n; i++) dist[i] = -1;
                var q = new Queue<int>();
                for (int i = 0; i < n; i++)
                {
                    if (isFixed[i]) { dist[i] = 0; q.Enqueue(i); }
                }
                int maxD = 0;
                while (q.Count > 0)
                {
                    int u = q.Dequeue();
                    foreach (int nb in adjGraphTmp[u])
                    {
                        if (dist[nb] == -1)
                        {
                            dist[nb] = dist[u] + 1;
                            if (dist[nb] > maxD) maxD = dist[nb];
                            q.Enqueue(nb);
                        }
                    }
                }
                int anchorThreshold = Math.Max(1, (int)(maxD * 0.75f));
                for (int i = 0; i < n; i++)
                {
                    if (!isFixed[i] && dist[i] >= anchorThreshold)
                    {
                        delta[i] = Vec3.Zero;
                        isFixed[i] = true;
                    }
                }
            }

            // 4. Giải Laplace (SOR Gauss-Seidel) điền toàn bộ đỉnh bên trong
            var adjGraph = BuildVertexAdjacency(mesh);
            const float omega = 1.35f;
            for (int iter = 0; iter < 48; iter++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (isFixed[i]) continue;
                    var nbs = adjGraph[i];
                    if (nbs.Count == 0) continue;

                    Vec3 sum = Vec3.Zero;
                    for (int j = 0; j < nbs.Count; j++)
                        sum = sum + delta[nbs[j]];
                    Vec3 avg = sum / nbs.Count;
                    delta[i] = delta[i] + (avg - delta[i]) * omega;
                }
            }

            // 5. Ghi vào RestVertices và bảo toàn các điểm điều khiển gắn trên mặt vải
            for (int i = 0; i < n; i++)
            {
                mesh.RestVertices[i] = src[i] + delta[i];
                mesh.Vertices[i] = mesh.RestVertices[i];
            }

            for (int p = 0; p < cloth.Points.Count; p++)
            {
                var pt = cloth.Points[p];
                Vec3 basePos = (basePointPositions != null && p < basePointPositions.Count)
                    ? basePointPositions[p]
                    : pt.Position;
                if (pt.AttachedVertexIndex >= 0 && pt.AttachedVertexIndex < n)
                    pt.Position = basePos + delta[pt.AttachedVertexIndex];
                else
                    pt.Position = basePos;
            }

            cloth.CachedBorders = null;
            RebuildClothDeformationFromControls(cloth);
        }

        /// <summary>
        /// Hàm trọng số Độ Uốn (Bending [0..1]) theo khoảng cách chuẩn hóa s = d / Radius:
        /// - Bending = 0.0: Gập nhọn chữ V (Linear Falloff: 1 - s)
        /// - Bending = 0.5: Uốn cong mượt hình chuông cos (Cosine Bell)
        /// - Bending = 1.0: Mở rộng thành vòm tròn đều chữ U / Omega (Circular Dome Arch)
        /// </summary>
        public static float EvaluateBendingWeight(float normalizedDistS, float bending01)
        {
            float s = Clamp01(normalizedDistS);
            float b = Clamp01(bending01);

            float sharpLinear = 1f - s;
            float smoothCosine = 0.5f * (1f + (float)Math.Cos(s * Math.PI));

            if (b <= 0.5f)
            {
                float t = b * 2f;
                return sharpLinear + (smoothCosine - sharpLinear) * t;
            }

            float u = (b - 0.5f) * 2f;
            float circularDome = (float)Math.Sqrt(Math.Max(0f, 1f - s * s));
            float broadCosine = (float)Math.Cos(s * Math.PI * 0.5);
            float domeArch = broadCosine * 0.35f + circularDome * 0.65f;
            return smoothCosine + (domeArch - smoothCosine) * u;
        }

        private static void SyncAttachedControlPoints(ClothEntity cloth, Guid? skipId1 = null, Guid? skipId2 = null)
        {
            var verts = cloth.Mesh.Vertices;
            for (int i = 0; i < cloth.Points.Count; i++)
            {
                var pt = cloth.Points[i];
                if (pt.Id == skipId1 || pt.Id == skipId2)
                    continue;

                if (pt.AttachedVertexIndex >= 0 && pt.AttachedVertexIndex < verts.Count)
                {
                    pt.Position = verts[pt.AttachedVertexIndex];
                }
            }
        }

        public static int FindClosestVertexIndex(MeshData mesh, Vec3 worldPt)
        {
            int bestIdx = -1;
            float bestDistSq = float.MaxValue;
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                float dSq = (mesh.Vertices[i] - worldPt).LengthSquared;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        public static Vec3 ClosestPointOnSegment3D(Vec3 p, Vec3 a, Vec3 b, out float t)
        {
            Vec3 ab = b - a;
            float lenSq = ab.LengthSquared;
            if (lenSq < 1e-8f)
            {
                t = 0f;
                return a;
            }
            t = Clamp01(Vec3.Dot(p - a, ab) / lenSq);
            return a + ab * t;
        }

        private static void EnsureConsistentNormals(MeshData mesh)
        {
            for (int i = 0; i < mesh.Normals.Count; i++)
            {
                if (mesh.Normals[i].LengthSquared < 1e-6f)
                    mesh.Normals[i] = Vec3.Forward;
            }
        }

        public static void ExportSceneToObj(string filePath, IReadOnlyList<ClothEntity> cloths, bool preferCharacterPose = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Crimirad Cloth Sculpting Export (Unity Ready)");
            int vOffset = 1;

            foreach (var c in cloths)
            {
                var m = (preferCharacterPose && c.CharacterState.IsAttached && c.CharacterFittedMesh != null)
                    ? c.CharacterFittedMesh
                    : (c.RenderSubdividedMesh ?? c.Mesh);
                if (m.Vertices.Count == 0) continue;

                sb.AppendLine($"g {c.Name.Replace(' ', '_')}");
                foreach (var v in m.Vertices)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F5} {1:F5} {2:F5}", v.X, v.Y, v.Z));
                foreach (var uv in m.UVs)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "vt {0:F5} {1:F5}", uv.X, 1f - uv.Y));
                foreach (var n in m.Normals)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "vn {0:F5} {1:F5} {2:F5}", n.X, n.Y, n.Z));

                if (m.Faces != null && m.Faces.Count > 0)
                {
                    foreach (var f in m.Faces)
                    {
                        if (f.Count < 3) continue;
                        sb.Append("f");
                        for (int k = 0; k < f.Count; k++)
                        {
                            int idx = f[k] + vOffset;
                            sb.Append($" {idx}/{idx}/{idx}");
                        }
                        sb.AppendLine();
                    }
                }
                else
                {
                    for (int i = 0; i + 2 < m.Triangles.Count; i += 3)
                    {
                        int i0 = m.Triangles[i] + vOffset;
                        int i1 = m.Triangles[i + 1] + vOffset;
                        int i2 = m.Triangles[i + 2] + vOffset;
                        sb.AppendLine($"f {i0}/{i0}/{i0} {i1}/{i1}/{i1} {i2}/{i2}/{i2}");
                    }
                }
                vOffset += m.Vertices.Count;
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        /// <summary>
        /// Nhập (Import) một file .OBJ bên ngoài thành một ClothEntity để chỉnh sửa, cắt hoặc uốn.
        /// </summary>
        public static ClothEntity ImportClothFromObj(string filePath, int nextIndex)
        {
            var color = Palette[(nextIndex - 1) % Palette.Length];
            var cloth = new ClothEntity
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                ClothColor = color,
                WireColor = new ColorRgba(
                    (byte)Math.Min(255, color.R + 55),
                    (byte)Math.Min(255, color.G + 55),
                    (byte)Math.Min(255, color.B + 55))
            };

            var mesh = new MeshData { Name = cloth.Name };
            var rawVerts = new List<Vec3>();
            var rawUVs = new List<Vec2>();

            foreach (string rawLine in File.ReadLines(filePath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("v "))
                {
                    var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4 &&
                        float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        rawVerts.Add(new Vec3(x, y, z));
                    }
                }
                else if (line.StartsWith("vt "))
                {
                    var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 3 &&
                        float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float u) &&
                        float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    {
                        rawUVs.Add(new Vec2(u, 1f - v));
                    }
                }
                else if (line.StartsWith("f "))
                {
                    var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4)
                    {
                        var faceIdx = new List<int>();
                        for (int i = 1; i < p.Length; i++)
                        {
                            var sub = p[i].Split('/');
                            if (!int.TryParse(sub[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int vIdx))
                                continue;

                            vIdx = vIdx > 0 ? vIdx - 1 : rawVerts.Count + vIdx;
                            vIdx = Math.Max(0, Math.Min(rawVerts.Count - 1, vIdx));

                            Vec2 uv = new Vec2(0.5f, 0.5f);
                            if (sub.Length > 1 && int.TryParse(sub[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int vtIdx))
                            {
                                vtIdx = vtIdx > 0 ? vtIdx - 1 : rawUVs.Count + vtIdx;
                                if (vtIdx >= 0 && vtIdx < rawUVs.Count)
                                    uv = rawUVs[vtIdx];
                            }

                            faceIdx.Add(mesh.Vertices.Count);
                            mesh.Vertices.Add(rawVerts[vIdx]);
                            mesh.UVs.Add(uv);
                            mesh.Normals.Add(Vec3.Forward);
                        }

                        if (faceIdx.Count >= 3)
                            mesh.Faces.Add(faceIdx);
                    }
                }
            }

            mesh.RebuildTrianglesFromFaces();
            mesh.RecalculateNormals();
            EnsureConsistentNormals(mesh);
            mesh.RestVertices = new List<Vec3>(mesh.Vertices);
            cloth.Mesh = mesh;
            RebuildDisplayMesh(cloth);
            return cloth;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
