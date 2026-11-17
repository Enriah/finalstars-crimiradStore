using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CrimiradTailoring.Core.Models;

namespace CrimiradTailoring.Core.Engine
{
    /// <summary>
    /// Bộ máy xử lý cho CharacterViewport:
    /// - Dựng Mannequin giải phẫu chuẩn (có vùng lồi/lõm tự nhiên để test Ruler, Hugging, Fitting)
    /// - Đo dây Ruler ôm cong bề mặt (Geodesic Surface Projection)
    /// - Gắn mảnh vải vào Anchor Point + giải Form-Hugging / Form-Fitting
    /// - Phân tầng Layers chống clip xuyên lớp & Occlusion Culling tối ưu GPU Shader
    /// </summary>
    public static class CharacterEngine
    {
        public static CharacterModelEntity CreateDefaultMannequin()
        {
            var mesh = new MeshData();

            // 1. Thân người (từ đáy chậu Y=0.78 -> đầu Y=1.68) với 44 góc ngang x 38 vòng quanh trục Y
            // Tạo dáng giải phẫu mượt mà có vùng lồi/lõm rõ rệt (eo thon, 2 bầu ngực + rãnh ngực giữa, vai)
            const int torsoSegU = 44;
            const int torsoRings = 38;
            float yMin = 0.78f, yMax = 1.68f;

            int[,] torsoGrid = new int[torsoRings, torsoSegU];
            for (int r = 0; r < torsoRings; r++)
            {
                float t = r / (float)(torsoRings - 1); // 0=đáy chậu -> 1=đỉnh đầu
                float y = yMin + (yMax - yMin) * t;
                GetTorsoProfile(t, out float rx, out float rzFront, out float rzBack, out float zOffset, out float bustBump, out float cleavageDip);

                for (int u = 0; u < torsoSegU; u++)
                {
                    float ang = u * (float)(Math.PI * 2.0 / torsoSegU);
                    float cosA = (float)Math.Cos(ang); // +X phải, -X trái
                    float sinA = (float)Math.Sin(ang); // +Z trước ngực, -Z sau lưng

                    float rz = sinA >= 0f ? rzFront : rzBack;
                    float x = cosA * rx;
                    float z = sinA * rz + zOffset;

                    // Hai đỉnh ngực lồi (+Z) & khe lõm xương ức ở giữa (sinA > 0)
                    if (sinA > 0f && bustBump > 1e-4f)
                    {
                        float lobeL = (float)Math.Exp(-Math.Pow((cosA + 0.46f) / 0.38f, 2.0));
                        float lobeR = (float)Math.Exp(-Math.Pow((cosA - 0.46f) / 0.38f, 2.0));
                        float frontFactor = (float)Math.Pow(sinA, 1.3);
                        float midDip = (float)Math.Exp(-Math.Pow(cosA / 0.24f, 2.0));
                        z += frontFactor * (bustBump * (lobeL + lobeR) - cleavageDip * midDip);
                    }

                    // Hõm thắt lưng & rãnh cột sống phía sau (sinA < 0)
                    if (sinA < -0.2f && t > 0.14f && t < 0.62f)
                    {
                        float spineHollow = (float)Math.Exp(-Math.Pow(cosA / 0.20f, 2.0)) * (-sinA);
                        z += spineHollow * 0.014f; // hơi lõm vào trong (+Z) ở rãnh lưng
                    }

                    torsoGrid[r, u] = mesh.Vertices.Count;
                    mesh.Vertices.Add(new Vec3(x, y, z));
                    mesh.UVs.Add(new Vec2(u / (float)torsoSegU, t));
                }
            }

            for (int r = 0; r < torsoRings - 1; r++)
            {
                for (int u = 0; u < torsoSegU; u++)
                {
                    int un = (u + 1) % torsoSegU;
                    int a = torsoGrid[r, u];
                    int b = torsoGrid[r, un];
                    int c = torsoGrid[r + 1, un];
                    int d = torsoGrid[r + 1, u];
                    // Đảo thứ tự để pháp tuyến (b-a)x(c-a) hướng RA NGOÀI (+Z phía ngực)
                    mesh.Faces.Add(new List<int> { a, d, c, b });
                }
            }

            // Nắp đáy chậu & đỉnh đầu (pháp tuyến hướng xuống -Y ở đáy, hướng lên +Y ở đỉnh)
            int pelvisCenter = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vec3(0f, yMin - 0.015f, 0f));
            mesh.UVs.Add(new Vec2(0.5f, 0f));
            int crownCenter = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vec3(0f, yMax + 0.005f, 0.01f));
            mesh.UVs.Add(new Vec2(0.5f, 1f));
            for (int u = 0; u < torsoSegU; u++)
            {
                int un = (u + 1) % torsoSegU;
                mesh.Triangles.Add(pelvisCenter); mesh.Triangles.Add(torsoGrid[0, u]); mesh.Triangles.Add(torsoGrid[0, un]);
                mesh.Triangles.Add(crownCenter); mesh.Triangles.Add(torsoGrid[torsoRings - 1, un]); mesh.Triangles.Add(torsoGrid[torsoRings - 1, u]);
            }

            // 2. Hai cánh tay dáng chữ A tự nhiên
            AppendTaperedLimb(mesh,
                new Vec3[] { new Vec3(0.155f, 1.36f, -0.008f), new Vec3(0.22f, 1.24f, -0.01f), new Vec3(0.28f, 1.07f, 0.01f), new Vec3(0.33f, 0.90f, 0.04f), new Vec3(0.35f, 0.80f, 0.055f) },
                new float[] { 0.058f, 0.046f, 0.038f, 0.031f, 0.024f }, 16);
            AppendTaperedLimb(mesh,
                new Vec3[] { new Vec3(-0.155f, 1.36f, -0.008f), new Vec3(-0.22f, 1.24f, -0.01f), new Vec3(-0.28f, 1.07f, 0.01f), new Vec3(-0.33f, 0.90f, 0.04f), new Vec3(-0.35f, 0.80f, 0.055f) },
                new float[] { 0.058f, 0.046f, 0.038f, 0.031f, 0.024f }, 16);

            // 3. Hai chân xuống mặt sàn Y = 0.00
            AppendTaperedLimb(mesh,
                new Vec3[] { new Vec3(0.088f, 0.84f, 0.00f), new Vec3(0.088f, 0.62f, 0.005f), new Vec3(0.084f, 0.43f, 0.012f), new Vec3(0.082f, 0.24f, -0.008f), new Vec3(0.082f, 0.05f, 0.00f), new Vec3(0.082f, 0.00f, 0.03f) },
                new float[] { 0.086f, 0.068f, 0.052f, 0.054f, 0.036f, 0.038f }, 18);
            AppendTaperedLimb(mesh,
                new Vec3[] { new Vec3(-0.088f, 0.84f, 0.00f), new Vec3(-0.088f, 0.62f, 0.005f), new Vec3(-0.084f, 0.43f, 0.012f), new Vec3(-0.082f, 0.24f, -0.008f), new Vec3(-0.082f, 0.05f, 0.00f), new Vec3(-0.082f, 0.00f, 0.03f) },
                new float[] { 0.086f, 0.068f, 0.052f, 0.054f, 0.036f, 0.038f }, 18);

            // Chuyển toàn bộ Faces sang Triangles (giữ kèm Triangles từ nắp)
            var extraTris = new List<int>(mesh.Triangles);
            mesh.RebuildTrianglesFromFaces();
            mesh.Triangles.AddRange(extraTris);

            mesh.EnsureRestVerticesInitialized();
            RecomputeSmoothNormals(mesh);

            var entity = new CharacterModelEntity
            {
                Name = "Mannequin Mặc Định",
                SourcePath = "",
                IsVisible = true,
                Mesh = mesh
            };
            ComputeBounds(entity);
            return entity;
        }

        private static void GetTorsoProfile(float t, out float rx, out float rzFront, out float rzBack, out float zOff, out float bustBump, out float cleavageDip)
        {
            // Các mốc t từ 0 (đáy chậu Y=0.78) đến 1 (đỉnh đầu Y=1.68)
            // 0.00=0.78, 0.11=0.88(hông), 0.31=1.06(eo), 0.51=1.24(ngực), 0.67=1.38(vai), 0.77=1.47(cổ), 0.90=1.59(đầu)
            bustBump = 0f;
            cleavageDip = 0f;
            zOff = 0f;

            if (t < 0.12f)
            {
                float s = t / 0.12f;
                float roundHip = (float)Math.Sqrt(Math.Clamp(0.25f + 0.75f * s, 0.25f, 1.0f));
                rx = 0.172f * roundHip;
                rzFront = 0.105f * roundHip;
                rzBack = 0.122f * roundHip;
            }
            else if (t < 0.31f) // Hông -> Eo thắt lại
            {
                float s = SmoothStep((t - 0.12f) / 0.19f);
                rx = Lerp(0.172f, 0.116f, s);
                rzFront = Lerp(0.105f, 0.082f, s);
                rzBack = Lerp(0.122f, 0.088f, s);
                zOff = Lerp(0f, 0.008f, s);
            }
            else if (t < 0.52f) // Eo -> Vòng Ngực (2 đỉnh ngực + rãnh giữa)
            {
                float s = SmoothStep((t - 0.31f) / 0.21f);
                rx = Lerp(0.116f, 0.152f, s);
                rzFront = Lerp(0.082f, 0.104f, s);
                rzBack = Lerp(0.088f, 0.105f, s);
                zOff = 0.008f;
                float bustBell = (float)Math.Exp(-Math.Pow((t - 0.50f) / 0.082f, 2.0));
                bustBump = 0.044f * bustBell;
                cleavageDip = 0.019f * bustBell;
            }
            else if (t < 0.67f) // Ngực -> Vai
            {
                float s = SmoothStep((t - 0.52f) / 0.15f);
                rx = Lerp(0.152f, 0.184f, s);
                rzFront = Lerp(0.104f, 0.088f, s);
                rzBack = Lerp(0.105f, 0.096f, s);
                float bustBell = (float)Math.Exp(-Math.Pow((t - 0.50f) / 0.082f, 2.0));
                bustBump = 0.044f * bustBell;
                cleavageDip = 0.019f * bustBell;
                zOff = Lerp(0.008f, 0f, s);
            }
            else if (t < 0.74f) // Vai -> Gốc cổ
            {
                float s = SmoothStep((t - 0.67f) / 0.07f);
                rx = Lerp(0.184f, 0.056f, s);
                rzFront = Lerp(0.088f, 0.056f, s);
                rzBack = Lerp(0.096f, 0.058f, s);
            }
            else if (t < 0.81f) // Cổ
            {
                float s = (t - 0.74f) / 0.07f;
                rx = Lerp(0.056f, 0.050f, s);
                rzFront = Lerp(0.056f, 0.052f, s);
                rzBack = Lerp(0.058f, 0.054f, s);
                zOff = Lerp(0f, 0.006f, s);
            }
            else // Đầu oval tròn mượt (không nhọn đầu)
            {
                float u = Math.Clamp((t - 0.81f) / 0.19f, 0f, 1f);
                float dy = (u - 0.48f) * 2.0f; // [-0.96 .. +1.04]
                float profile = (float)Math.Sqrt(Math.Max(0.08f, 1.0f - Math.Min(0.92f, dy * dy)));
                rx = 0.076f * profile;
                rzFront = 0.088f * profile;
                rzBack = 0.092f * profile;
                zOff = 0.010f;
            }
        }

        private static void AppendTaperedLimb(MeshData mesh, Vec3[] spine, float[] radii, int radialSegs)
        {
            int rings = spine.Length;
            int[,] grid = new int[rings, radialSegs];

            for (int r = 0; r < rings; r++)
            {
                Vec3 dir = r == 0 ? (spine[1] - spine[0]).Normalized
                    : (r == rings - 1 ? (spine[r] - spine[r - 1]).Normalized : (spine[r + 1] - spine[r - 1]).Normalized);
                Vec3 refUp = Math.Abs(dir.Z) < 0.9f ? Vec3.Forward : Vec3.Right;
                Vec3 sideA = Vec3.Cross(dir, refUp).Normalized;
                Vec3 sideB = Vec3.Cross(dir, sideA).Normalized;

                for (int u = 0; u < radialSegs; u++)
                {
                    float ang = u * (float)(Math.PI * 2.0 / radialSegs);
                    Vec3 pos = spine[r] + sideA * ((float)Math.Cos(ang) * radii[r]) + sideB * ((float)Math.Sin(ang) * radii[r]);
                    grid[r, u] = mesh.Vertices.Count;
                    mesh.Vertices.Add(pos);
                    mesh.UVs.Add(new Vec2(u / (float)radialSegs, r / (float)(rings - 1)));
                }
            }

            for (int r = 0; r < rings - 1; r++)
            {
                Vec3 segDir = (spine[r + 1] - spine[r]).Normalized;
                for (int u = 0; u < radialSegs; u++)
                {
                    int un = (u + 1) % radialSegs;
                    int a = grid[r, u], b = grid[r, un], c = grid[r + 1, un], d = grid[r + 1, u];
                    Vec3 radialOut = (mesh.Vertices[a] - spine[r]);
                    Vec3 testNorm = Vec3.Cross(mesh.Vertices[b] - mesh.Vertices[a], mesh.Vertices[c] - mesh.Vertices[a]);
                    if (Vec3.Dot(testNorm, radialOut) >= 0f)
                        mesh.Faces.Add(new List<int> { a, b, c, d });
                    else
                        mesh.Faces.Add(new List<int> { a, d, c, b });
                }
            }

            int capStart = mesh.Vertices.Count;
            mesh.Vertices.Add(spine[0]);
            mesh.UVs.Add(new Vec2(0.5f, 0f));
            int capEnd = mesh.Vertices.Count;
            mesh.Vertices.Add(spine[rings - 1]);
            mesh.UVs.Add(new Vec2(0.5f, 1f));

            for (int u = 0; u < radialSegs; u++)
            {
                int un = (u + 1) % radialSegs;
                mesh.Triangles.Add(capStart); mesh.Triangles.Add(grid[0, un]); mesh.Triangles.Add(grid[0, u]);
                mesh.Triangles.Add(capEnd); mesh.Triangles.Add(grid[rings - 1, u]); mesh.Triangles.Add(grid[rings - 1, un]);
            }
        }

        public static void NormalizeCharacterMesh(CharacterModelEntity model)
        {
            var verts = model.Mesh.Vertices;
            if (verts.Count == 0) return;

            ComputeBounds(model);
            float h = model.HeightMeters;

            // Tự quy đổi cm/mm sang mét nếu model import có chiều cao > 8m
            float scale = h > 800f ? 0.001f : (h > 8f ? 0.01f : 1f);
            Vec3 centerXZ = new Vec3(
                (model.BoundsMin.X + model.BoundsMax.X) * 0.5f,
                model.BoundsMin.Y,
                (model.BoundsMin.Z + model.BoundsMax.Z) * 0.5f);

            if (Math.Abs(scale - 1f) > 1e-4f || Math.Abs(centerXZ.X) > 0.02f || Math.Abs(centerXZ.Y) > 0.02f || Math.Abs(centerXZ.Z) > 0.02f)
            {
                for (int i = 0; i < verts.Count; i++)
                    verts[i] = (verts[i] - centerXZ) * scale;
            }

            model.Mesh.EnsureRestVerticesInitialized();
            RecomputeSmoothNormals(model.Mesh);
            ComputeBounds(model);
        }

        public static void ComputeBounds(CharacterModelEntity model)
        {
            var verts = model.Mesh.Vertices;
            if (verts.Count == 0)
            {
                model.BoundsMin = Vec3.Zero;
                model.BoundsMax = Vec3.Zero;
                model.HeightMeters = 0f;
                return;
            }
            Vec3 mn = new Vec3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vec3 mx = new Vec3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < verts.Count; i++)
            {
                Vec3 v = verts[i];
                if (v.X < mn.X) mn.X = v.X;
                if (v.Y < mn.Y) mn.Y = v.Y;
                if (v.Z < mn.Z) mn.Z = v.Z;
                if (v.X > mx.X) mx.X = v.X;
                if (v.Y > mx.Y) mx.Y = v.Y;
                if (v.Z > mx.Z) mx.Z = v.Z;
            }
            model.BoundsMin = mn;
            model.BoundsMax = mx;
            model.HeightMeters = Math.Max(0f, mx.Y - mn.Y);
        }

        public static void RecomputeSmoothNormals(MeshData mesh)
        {
            int n = mesh.Vertices.Count;
            mesh.Normals.Clear();
            for (int i = 0; i < n; i++) mesh.Normals.Add(Vec3.Zero);

            for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
            {
                int ia = mesh.Triangles[t];
                int ib = mesh.Triangles[t + 1];
                int ic = mesh.Triangles[t + 2];
                if (ia < 0 || ib < 0 || ic < 0 || ia >= n || ib >= n || ic >= n) continue;
                Vec3 fn = Vec3.Cross(mesh.Vertices[ib] - mesh.Vertices[ia], mesh.Vertices[ic] - mesh.Vertices[ia]);
                mesh.Normals[ia] = mesh.Normals[ia] + fn;
                mesh.Normals[ib] = mesh.Normals[ib] + fn;
                mesh.Normals[ic] = mesh.Normals[ic] + fn;
            }

            for (int i = 0; i < n; i++)
                mesh.Normals[i] = mesh.Normals[i].LengthSquared > 1e-10f ? mesh.Normals[i].Normalized : Vec3.Up;
        }

        /// <summary>
        /// Tính đường Ruler ôm cong sát bề mặt model (Surface Geodesic Path) giữa chuỗi điểm ControlPoints.
        /// Không đo đường thẳng xuyên qua thịt mà bám lồi/lõm theo hình dáng model.
        /// </summary>
        public static List<Vec3> BuildGeodesicRulerPath(
            CharacterBvh? bvh,
            IReadOnlyList<Vec3> controlPts,
            bool closedLoop,
            out List<float> segmentLengths,
            out float totalLengthMeters)
        {
            var poly = new List<Vec3>();
            segmentLengths = new List<float>();
            totalLengthMeters = 0f;
            if (controlPts.Count == 0) return poly;
            if (controlPts.Count == 1)
            {
                poly.Add(controlPts[0]);
                return poly;
            }

            int segCount = closedLoop && controlPts.Count >= 3 ? controlPts.Count : (controlPts.Count - 1);
            const float surfaceLift = 0.0022f;

            for (int s = 0; s < segCount; s++)
            {
                Vec3 a = controlPts[s];
                Vec3 b = controlPts[(s + 1) % controlPts.Count];
                float chord = Vec3.Distance(a, b);
                int steps = bvh != null ? Math.Max(4, Math.Min(64, (int)Math.Ceiling(chord / 0.008f))) : 1;

                var segPts = new List<Vec3>(steps + 1);
                for (int k = 0; k <= steps; k++)
                {
                    float u = k / (float)steps;
                    Vec3 p = Vec3.Lerp(a, b, u);
                    if (bvh != null && bvh.ClosestPoint(p, out BvhClosestHit hit))
                    {
                        Vec3 surfP = hit.Point + hit.Normal * surfaceLift;
                        // Điểm giữa các đoạn cong gắt được hiệu chỉnh thêm 1 nhịp theo trung điểm cung
                        if (k > 0 && k < steps && bvh.ClosestPoint(surfP + hit.Normal * 0.006f, out BvhClosestHit hit2))
                            surfP = hit2.Point + hit2.Normal * surfaceLift;
                        segPts.Add(surfP);
                    }
                    else
                    {
                        segPts.Add(p);
                    }
                }

                // Làm mịn nhẹ 1 vòng trên đường cong bề mặt để loại rung tam giác rồi chiếu lại bề mặt
                if (bvh != null && segPts.Count > 3)
                {
                    var smoothed = new List<Vec3>(segPts);
                    for (int pass = 0; pass < 2; pass++)
                    {
                        for (int k = 1; k < segPts.Count - 1; k++)
                        {
                            Vec3 avg = (smoothed[k - 1] + smoothed[k] * 2f + smoothed[k + 1]) * 0.25f;
                            if (bvh.ClosestPoint(avg, out BvhClosestHit h))
                                smoothed[k] = h.Point + h.Normal * surfaceLift;
                        }
                    }
                    segPts = smoothed;
                }

                float segLen = 0f;
                for (int k = 0; k < segPts.Count; k++)
                {
                    if (s == 0 || k > 0) poly.Add(segPts[k]);
                    if (k > 0) segLen += Vec3.Distance(segPts[k - 1], segPts[k]);
                }
                segmentLengths.Add(segLen);
                totalLengthMeters += segLen;
            }

            return poly;
        }

        /// <summary>
        /// Giải toàn bộ hệ thống vải đã gắn trong CharacterViewport theo thứ tự Layer tăng dần (0 -> 1 -> 2...),
        /// đảm bảo liên kết 1-1 từ FabricViewport, áp dụng Hugging / Fitting, chống clip đa tầng và tính Occlusion Culling.
        /// </summary>
        public static void SolveAllCharacterClothLayers(
            CharacterBvh? charBvh,
            IReadOnlyList<CharacterAnchorPoint> anchors,
            IReadOnlyList<ClothEntity> cloths,
            bool interactivePreview = false)
        {
            var attached = new List<ClothEntity>();
            for (int i = 0; i < cloths.Count; i++)
            {
                if (cloths[i].IsVisible && cloths[i].CharacterState.IsAttached)
                    attached.Add(cloths[i]);
                else
                    cloths[i].CharacterFittedMesh = null;
            }

            if (attached.Count == 0) return;
            attached.Sort((a, b) => a.CharacterState.LayerIndex.CompareTo(b.CharacterState.LayerIndex));

            var anchorLookup = new Dictionary<Guid, CharacterAnchorPoint>(anchors.Count);
            for (int i = 0; i < anchors.Count; i++) anchorLookup[anchors[i].Id] = anchors[i];

            var obstacleBvhs = new List<(int layer, CharacterBvh bvh)>();
            if (charBvh != null) obstacleBvhs.Add((-1, charBvh));

            for (int ci = 0; ci < attached.Count; ci++)
            {
                var cloth = attached[ci];
                SolveSingleClothOnCharacter(cloth, anchorLookup, obstacleBvhs, interactivePreview);
                if (cloth.CharacterFittedMesh != null && cloth.CharacterFittedMesh.Triangles.Count >= 3)
                {
                    // Khi đang kéo interactive ở 1 tầng đơn thì không cần build lại BVH cho tầng trên nếu không còn tầng nào phía trên
                    bool hasUpperLayer = false;
                    for (int uj = ci + 1; uj < attached.Count; uj++)
                    {
                        if (attached[uj].CharacterState.LayerIndex > cloth.CharacterState.LayerIndex)
                        {
                            hasUpperLayer = true;
                            break;
                        }
                    }
                    if (hasUpperLayer)
                    {
                        var fittedBvh = CharacterBvh.Build(
                            cloth.CharacterFittedMesh.Vertices,
                            cloth.CharacterFittedMesh.Triangles,
                            cloth.CharacterFittedMesh.Normals);
                        obstacleBvhs.Add((cloth.CharacterState.LayerIndex, fittedBvh));
                    }
                }
            }

            if (!interactivePreview)
            {
                ComputeLayerOcclusionMasks(attached);
            }
            else
            {
                for (int i = 0; i < attached.Count; i++)
                {
                    var fm = attached[i].CharacterFittedMesh;
                    attached[i].CharacterOccludedTriangles = null;
                    attached[i].VisibleCharacterTriangleCount = fm != null ? fm.Triangles.Count : 0;
                }
            }
        }

        private static void SolveSingleClothOnCharacter(
            ClothEntity cloth,
            IReadOnlyDictionary<Guid, CharacterAnchorPoint> anchors,
            IReadOnlyList<(int layer, CharacterBvh bvh)> obstacleBvhs,
            bool interactivePreview)
        {
            var st = cloth.CharacterState;
            // Khi đang kéo/zoom/kéo slider liên tục: giải trên lưới gốc (~625 đỉnh) để đạt >60 FPS, thả chuột sẽ tự nâng cấp lưới mịn
            MeshData srcMesh = (!interactivePreview && cloth.SubdivisionLevel > 0 && cloth.RenderSubdividedMesh != null && cloth.RenderSubdividedMesh.Vertices.Count <= 3000)
                ? cloth.RenderSubdividedMesh
                : cloth.Mesh;

            int vCount = srcMesh.Vertices.Count;
            if (vCount == 0)
            {
                cloth.CharacterFittedMesh = null;
                return;
            }

            // 1. Tạo lưới CharacterFittedMesh từ rập hiện tại ở FabricViewport (giữ liên kết 1-1)
            var fitted = new MeshData();
            fitted.Vertices = new List<Vec3>(vCount);
            fitted.Normals = new List<Vec3>(vCount);
            fitted.UVs = new List<Vec2>(srcMesh.UVs);
            fitted.Faces = new List<List<int>>(srcMesh.Faces.Count);
            for (int f = 0; f < srcMesh.Faces.Count; f++)
                fitted.Faces.Add(new List<int>(srcMesh.Faces[f]));
            fitted.Triangles = new List<int>(srcMesh.Triangles);
            if (fitted.Triangles.Count == 0 && fitted.Faces.Count > 0)
                fitted.RebuildTrianglesFromFaces();

            // 2. Đặt khung tọa độ của mảnh vải vào Anchor Point + Transform trên CharacterViewport
            Vec3 fabricOrigin = ClothSculptEngine.GetClothOrigin(cloth);
            Vec3 anchorPos = new Vec3(0f, 1.15f, 0.20f);
            Vec3 anchorNormal = Vec3.Forward;
            Vec3 anchorTangent = Vec3.Right;

            if (st.AnchorId.HasValue && anchors.TryGetValue(st.AnchorId.Value, out var anch))
            {
                anchorPos = anch.Position;
                anchorNormal = anch.Normal.LengthSquared > 1e-6f ? anch.Normal.Normalized : Vec3.Forward;
                anchorTangent = anch.Tangent.LengthSquared > 1e-6f ? anch.Tangent.Normalized : Vec3.Right;
            }

            // Trục vải ở FabricViewport mặc định hướng pháp tuyến +Z (hoặc trung bình pháp tuyến mặt vải)
            Vec3 baseRight = anchorTangent;
            Vec3 baseUp = Vec3.Cross(anchorNormal, baseRight);
            if (baseUp.LengthSquared < 1e-6f) baseUp = Vec3.Up;
            baseUp = baseUp.Normalized;
            baseRight = Vec3.Cross(baseUp, anchorNormal).Normalized;

            float scale = Math.Clamp(st.UniformScale, 0.1f, 4.0f);
            Vec3 eulerRad = st.WorldRotationEuler * (float)(Math.PI / 180.0);
            Vec3 clothOutDir = ComputeClothOutwardDirection(anchorNormal, st.WorldRotationEuler);

            // Khóa hướng tiếp xúc mặt trước khi Form Snap bật: tránh kéo WorldOffset lùi quá sâu xuyên qua tâm thân sang mặt lưng
            CharacterAnchorPoint? anchorObj = (st.AnchorId.HasValue && anchors.TryGetValue(st.AnchorId.Value, out var foundAnch)) ? foundAnch : null;
            CharacterBvh? charOnlyBvh = (obstacleBvhs.Count > 0 && obstacleBvhs[0].layer < 0) ? obstacleBvhs[0].bvh : null;
            Vec3 effectiveOffset = ClampFrontContactOffset(charOnlyBvh, anchorObj, st);

            Vec3 targetPivot = anchorPos + anchorNormal * Math.Max(0.002f, st.SurfaceOffsetMeters) + effectiveOffset;

            // Lưu độ dịch chuyển điêu khắc gốc của mỗi đỉnh (nếu giữ nếp uốn từ Fabric)
            var sculptOffsets = new float[vCount];
            Vec3 fabricAvgN = GetAverageNormal(srcMesh);

            for (int i = 0; i < vCount; i++)
            {
                Vec3 local = (srcMesh.Vertices[i] - fabricOrigin) * scale;
                if (i < srcMesh.RestVertices.Count && st.PreserveDeformBlend > 1e-3f)
                    sculptOffsets[i] = Vec3.Dot((srcMesh.Vertices[i] - srcMesh.RestVertices[i]) * scale, fabricAvgN);

                Vec3 oriented = baseRight * local.X + baseUp * local.Y + anchorNormal * local.Z;
                oriented = RotateEulerXYZ(oriented, eulerRad);
                fitted.Vertices.Add(targetPivot + oriented);
                fitted.Normals.Add(anchorNormal);
            }

            // 3. Áp dụng chế độ Snap Form (Hugging / Fitting)
            if (st.FormMode != FormSnapMode.Off && obstacleBvhs.Count > 0)
            {
                // Xây danh sách cạnh kề & độ dài nghỉ mục tiêu để giữ kích thước rập khi ôm dáng
                var edges = BuildUniqueMeshEdges(fitted, out List<int>[] adj);
                float[] restEdgeLens = new float[edges.Count];
                for (int e = 0; e < edges.Count; e++)
                    restEdgeLens[e] = Math.Max(1e-4f, Vec3.Distance(fitted.Vertices[edges[e].a], fitted.Vertices[edges[e].b]));

                if (st.FormMode == FormSnapMode.Hugging)
                {
                    SolveHuggingForm(fitted, edges, restEdgeLens, clothOutDir, st, obstacleBvhs, sculptOffsets, interactivePreview);
                }
                else if (st.FormMode == FormSnapMode.Fitting)
                {
                    SolveFittingForm(fitted, edges, restEdgeLens, adj, clothOutDir, st, obstacleBvhs, sculptOffsets, interactivePreview);
                }
            }

            fitted.RestVertices = new List<Vec3>(fitted.Vertices);
            RecomputeSmoothNormals(fitted);
            cloth.CharacterFittedMesh = fitted;
            cloth.VisibleCharacterTriangleCount = fitted.Triangles.Count;
            cloth.CharacterOccludedTriangles = null;
        }

        private static void SolveHuggingForm(
            MeshData mesh,
            List<(int a, int b)> edges,
            float[] restLens,
            Vec3 outwardDir,
            ClothCharacterAttachment st,
            IReadOnlyList<(int layer, CharacterBvh bvh)> obstacleBvhs,
            float[] sculptOffsets,
            bool interactivePreview)
        {
            int n = mesh.Vertices.Count;
            var verts = mesh.Vertices;
            int totalIters = interactivePreview ? 4 : 7;
            Vec3 dirOut = outwardDir.LengthSquared > 1e-6f ? outwardDir.Normalized : Vec3.Forward;

            // Chiếu ôm khít mọi lồi lõm + bảo toàn chiều dài rập (truy vấn BVH đa luồng + khóa hướng mặt trước)
            for (int iter = 0; iter < totalIters; iter++)
            {
                // Bước A: Giữ khoảng cách cạnh rập (PBD Edge Constraints) để vải không bị dãn/co méo
                for (int e = 0; e < edges.Count; e++)
                {
                    int ia = edges[e].a;
                    int ib = edges[e].b;
                    Vec3 diff = verts[ib] - verts[ia];
                    float d = diff.Length;
                    if (d < 1e-6f) continue;
                    float err = (d - restLens[e]) / d;
                    Vec3 corr = diff * (err * 0.42f);
                    verts[ia] = verts[ia] + corr;
                    verts[ib] = verts[ib] - corr;
                }

                // Bước B: Hút sát toàn bộ điểm lên bề mặt da / các layer dưới (chỉ giữ phía trước dirOut)
                float snapAlpha = iter >= totalIters - 2 ? 1.0f : 0.72f;
                int layerIdx = st.LayerIndex;
                float baseOff = st.SurfaceOffsetMeters;
                float keepDeform = st.PreserveDeformBlend;
                Parallel.For(0, n, i =>
                {
                    float extra = keepDeform * Math.Max(0f, sculptOffsets[i]);
                    if (QueryHighestClearanceSurface(verts[i], layerIdx, baseOff + extra, obstacleBvhs, dirOut, out Vec3 goal, out _))
                        verts[i] = Vec3.Lerp(verts[i], goal, snapAlpha);
                });
            }
        }

        private static void SolveFittingForm(
            MeshData mesh,
            List<(int a, int b)> edges,
            float[] restLens,
            List<int>[] adj,
            Vec3 outwardDir,
            ClothCharacterAttachment st,
            IReadOnlyList<(int layer, CharacterBvh bvh)> obstacleBvhs,
            float[] sculptOffsets,
            bool interactivePreview)
        {
            int n = mesh.Vertices.Count;
            var verts = mesh.Vertices;
            Vec3 dirOut = outwardDir.LengthSquared > 1e-6f ? outwardDir.Normalized : Vec3.Forward;
            int layerIdx = st.LayerIndex;
            float baseOff = st.SurfaceOffsetMeters;
            float keepDeform = st.PreserveDeformBlend;

            // Bước khởi tạo: Đẩy các đỉnh xuyên vào trong bề mặt ra phía ngoài dọc hướng phủ (khóa ở mặt trước)
            Parallel.For(0, n, i =>
            {
                float minOff = baseOff + keepDeform * Math.Max(0f, sculptOffsets[i]);
                if (TryGetClosestCombined(verts[i], layerIdx, minOff, obstacleBvhs, dirOut, out Vec3 cPos, out Vec3 cNorm, out float cOff))
                {
                    float signedDist = Vec3.Dot(verts[i] - cPos, cNorm);
                    if (signedDist < cOff)
                    {
                        Vec3 pushVec = Vec3.Dot(cNorm, dirOut) > 0.1f
                            ? (cNorm * 0.35f + dirOut * 0.65f).Normalized
                            : cNorm;
                        float align = Math.Max(0.45f, Vec3.Dot(pushVec, cNorm));
                        float pushDist = (cOff - signedDist) / align;
                        verts[i] = verts[i] + pushVec * pushDist;
                    }
                }
            });

            // Vòng lặp Fitting (Gauss-Seidel bất biến mật độ lưới): căng màng qua vùng lõm & chặn cứng 1 chiều tại đỉnh lồi
            float bridgeWeight = Math.Clamp(st.FittingBridgeStiffness, 0.05f, 0.98f);
            float avgEdgeLen = 0.015f;
            if (restLens.Length > 0)
            {
                float sumE = 0f;
                for (int e = 0; e < restLens.Length; e++) sumE += restLens[e];
                avgEdgeLen = sumE / restLens.Length;
            }
            int fullIters = Math.Clamp((int)MathF.Ceiling(0.75f / Math.Max(0.005f, avgEdgeLen)), 32, 96);
            int totalIters = interactivePreview ? Math.Min(22, fullIters) : fullIters;
            int collisionStride = interactivePreview ? 4 : 3;

            float inRate = 0.14f + 0.56f * (1.0f - bridgeWeight);
            float outRate = 0.92f * bridgeWeight + 0.10f;

            for (int iter = 0; iter < totalIters; iter++)
            {
                bool checkCollision = (iter % collisionStride == 0) || iter == totalIters - 1;

                // 1) Gauss-Seidel Asymmetric Membrane Bridging
                for (int i = 0; i < n; i++)
                {
                    var nbrs = adj[i];
                    if (nbrs.Count == 0) continue;

                    Vec3 avg = Vec3.Zero;
                    for (int k = 0; k < nbrs.Count; k++)
                        avg = avg + verts[nbrs[k]];
                    avg = avg / nbrs.Count;

                    Vec3 diff = avg - verts[i];
                    float dn = Vec3.Dot(diff, dirOut);
                    Vec3 tangDiff = diff - dirOut * dn;

                    float nAlpha = dn >= 0f ? outRate : inRate;
                    verts[i] = verts[i] + tangDiff * 0.22f + dirOut * (dn * nAlpha);
                }

                // 2) Bảo toàn kích thước rập (PBD Edge Constraints)
                if (checkCollision || (iter % 2 == 0))
                {
                    for (int e = 0; e < edges.Count; e++)
                    {
                        int ia = edges[e].a;
                        int ib = edges[e].b;
                        Vec3 diff = verts[ib] - verts[ia];
                        float d = diff.Length;
                        if (d < 1e-6f) continue;
                        float err = (d - restLens[e]) / d;
                        Vec3 corr = diff * (err * 0.22f);
                        verts[ia] = verts[ia] + corr;
                        verts[ib] = verts[ib] - corr;
                    }
                }

                // 3) Kháng va chạm 1 chiều mượt + khóa hướng mặt trước (song song đa luồng)
                if (checkCollision)
                {
                    bool allowSag = bridgeWeight < 0.78f;
                    float sagAllowance = allowSag ? (0.78f - bridgeWeight) * 0.025f : 0f;
                    Parallel.For(0, n, i =>
                    {
                        float minOff = baseOff + keepDeform * Math.Max(0f, sculptOffsets[i]);
                        if (TryGetClosestCombined(verts[i], layerIdx, minOff, obstacleBvhs, dirOut, out Vec3 cPos, out Vec3 cNorm, out float cOff))
                        {
                            float signedDist = Vec3.Dot(verts[i] - cPos, cNorm);
                            if (signedDist < cOff)
                            {
                                Vec3 pushVec = Vec3.Dot(cNorm, dirOut) > 0.1f
                                    ? (cNorm * 0.40f + dirOut * 0.60f).Normalized
                                    : cNorm;
                                float align = Math.Max(0.45f, Vec3.Dot(pushVec, cNorm));
                                float pushDist = (cOff - signedDist) / align;
                                verts[i] = verts[i] + pushVec * pushDist;
                            }
                            else if (allowSag)
                            {
                                verts[i] = Vec3.Lerp(verts[i], cPos + cNorm * cOff, sagAllowance);
                            }
                        }
                    });
                }
            }
        }

        private static bool QueryHighestClearanceSurface(
            Vec3 query,
            int clothLayer,
            float baseOffset,
            IReadOnlyList<(int layer, CharacterBvh bvh)> obstacles,
            Vec3 lockOutDir,
            out Vec3 outPos,
            out Vec3 outNormal)
        {
            return TryGetClosestCombined(query, clothLayer, baseOffset, obstacles, lockOutDir, out outPos, out outNormal, out float effOff)
                && SetOut(ref outPos, outPos + outNormal * effOff);

            static bool SetOut(ref Vec3 dst, Vec3 v) { dst = v; return true; }
        }

        private static bool TryGetClosestCombined(
            Vec3 query,
            int clothLayer,
            float baseOffset,
            IReadOnlyList<(int layer, CharacterBvh bvh)> obstacles,
            Vec3 lockOutDir,
            out Vec3 bestPoint,
            out Vec3 bestNormal,
            out float bestRequiredOffset)
        {
            bool found = false;
            float bestSignedClearance = float.MaxValue;
            bestPoint = query;
            bestNormal = Vec3.Forward;
            bestRequiredOffset = baseOffset;

            for (int k = 0; k < obstacles.Count; k++)
            {
                var (obsLayer, bvh) = obstacles[k];
                if (obsLayer >= clothLayer) continue;

                float reqOff = obsLayer < 0
                    ? baseOffset
                    : Math.Max(0.0045f, 0.005f * (clothLayer - obsLayer));

                if (!bvh.ClosestPoint(query, out BvhClosestHit hit)) continue;

                Vec3 contactPt = hit.Point;
                Vec3 contactNorm = hit.Normal;

                // Khóa hướng mặt trước: nếu điểm vượt qua tâm thân sang vùng quay lưng (Dot < -0.25),
                // chiếu tia từ phía trước vào theo -lockOutDir để giữ vải dừng lại đúng ở mặt trước
                if (Vec3.Dot(contactNorm, lockOutDir) < -0.25f)
                {
                    Vec3 rayOrigin = query + lockOutDir * 0.70f;
                    if (bvh.Raycast(rayOrigin, -lockOutDir, out BvhRayHit frontHit, 1.40f) &&
                        Vec3.Dot(frontHit.Normal, lockOutDir) > 0.02f)
                    {
                        contactPt = frontHit.Point;
                        contactNorm = frontHit.Normal;
                    }
                    else
                    {
                        // Không cho hút ra mặt sau lưng nếu nằm ngoài hình chiếu tia mặt trước
                        continue;
                    }
                }

                Vec3 delta = query - contactPt;
                float proj = Vec3.Dot(delta, contactNorm);
                if (obsLayer >= 0)
                {
                    // Lớp vải dưới là tấm hở mỏng: chỉ va chạm ở vùng thực sự chồng lấn, không hút mép ngoài
                    Vec3 lateral = delta - contactNorm * proj;
                    if (lateral.LengthSquared > 0.018f * 0.018f) continue;
                }
                float signed = proj - reqOff;

                // Ưu tiên vật cản đang vi phạm nhất hoặc gần nhất phía dưới
                if (!found || signed < bestSignedClearance)
                {
                    found = true;
                    bestSignedClearance = signed;
                    bestPoint = contactPt;
                    bestNormal = contactNorm;
                    bestRequiredOffset = reqOff;
                }
            }

            return found;
        }

        /// <summary>
        /// Tính mặt nạ Culling cho các tam giác của Layer dưới khi bị Layer trên che kín hoàn toàn,
        /// giúp GPU bỏ qua không chạy Fragment/Vertex shader ở phần vải khuất bên trong.
        /// </summary>
        public static void ComputeLayerOcclusionMasks(IReadOnlyList<ClothEntity> sortedAttachedCloths)
        {
            if (sortedAttachedCloths.Count <= 1)
            {
                for (int i = 0; i < sortedAttachedCloths.Count; i++)
                {
                    var m = sortedAttachedCloths[i].CharacterFittedMesh;
                    sortedAttachedCloths[i].CharacterOccludedTriangles = null;
                    sortedAttachedCloths[i].VisibleCharacterTriangleCount = m != null ? m.Triangles.Count : 0;
                }
                return;
            }

            var bvhs = new CharacterBvh?[sortedAttachedCloths.Count];
            for (int i = 0; i < sortedAttachedCloths.Count; i++)
            {
                var m = sortedAttachedCloths[i].CharacterFittedMesh;
                if (m != null && m.Triangles.Count >= 3)
                    bvhs[i] = CharacterBvh.Build(m.Vertices, m.Triangles, m.Normals);
            }

            for (int i = 0; i < sortedAttachedCloths.Count; i++)
            {
                var lowCloth = sortedAttachedCloths[i];
                var m = lowCloth.CharacterFittedMesh;
                if (m == null || m.Triangles.Count < 3) continue;

                int triCount = m.Triangles.Count / 3;
                var occluded = new bool[triCount];
                int visibleTris = triCount * 3;

                // Chỉ kiểm tra che khuất bởi các layer nằm trên rõ rệt (LayerIndex > lowCloth.LayerIndex)
                var upperBvhs = new List<CharacterBvh>();
                for (int j = i + 1; j < sortedAttachedCloths.Count; j++)
                {
                    if (sortedAttachedCloths[j].CharacterState.LayerIndex > lowCloth.CharacterState.LayerIndex && bvhs[j] != null)
                        upperBvhs.Add(bvhs[j]!);
                }

                if (upperBvhs.Count > 0)
                {
                    const float maxOcclusionGap = 0.065f;
                    int occludedCount = 0;
                    Parallel.For(0, triCount, t =>
                    {
                        Vec3 va = m.Vertices[m.Triangles[t * 3]];
                        Vec3 vb = m.Vertices[m.Triangles[t * 3 + 1]];
                        Vec3 vc = m.Vertices[m.Triangles[t * 3 + 2]];
                        Vec3 fn = Vec3.Cross(vb - va, vc - va);
                        if (fn.LengthSquared < 1e-12f) return;
                        Vec3 nrm = fn.Normalized;
                        Vec3 cen = (va + vb + vc) * (1f / 3f);

                        // Kiểm tra cả tâm và 3 góc gần trọng tâm đều nằm dưới lớp ngoài để tránh cắt nhầm mép viền
                        Vec3 p0 = Vec3.Lerp(cen, va, 0.72f);
                        Vec3 p1 = Vec3.Lerp(cen, vb, 0.72f);
                        Vec3 p2 = Vec3.Lerp(cen, vc, 0.72f);

                        bool covered = IsCoveredByAnyUpper(upperBvhs, cen, nrm, maxOcclusionGap) &&
                                       IsCoveredByAnyUpper(upperBvhs, p0, nrm, maxOcclusionGap) &&
                                       IsCoveredByAnyUpper(upperBvhs, p1, nrm, maxOcclusionGap) &&
                                       IsCoveredByAnyUpper(upperBvhs, p2, nrm, maxOcclusionGap);
                        if (covered)
                        {
                            occluded[t] = true;
                            Interlocked.Increment(ref occludedCount);
                        }
                    });
                    visibleTris = (triCount - occludedCount) * 3;
                }

                lowCloth.CharacterOccludedTriangles = occluded;
                lowCloth.VisibleCharacterTriangleCount = visibleTris;
            }
        }

        private static bool IsCoveredByAnyUpper(IReadOnlyList<CharacterBvh> upperBvhs, Vec3 pt, Vec3 outwardNorm, float maxGap)
        {
            Vec3 ro = pt + outwardNorm * 0.0005f;
            for (int k = 0; k < upperBvhs.Count; k++)
            {
                if (upperBvhs[k].Raycast(ro, outwardNorm, out BvhRayHit hit, maxGap))
                {
                    if (Vec3.Dot(hit.Normal, outwardNorm) > 0.15f)
                        return true;
                }
            }
            return false;
        }

        private static List<(int a, int b)> BuildUniqueMeshEdges(MeshData mesh, out List<int>[] adj)
        {
            int n = mesh.Vertices.Count;
            var adjArr = new List<int>[n];
            for (int i = 0; i < n; i++) adjArr[i] = new List<int>(6);
            adj = adjArr;

            var edgeSet = new HashSet<long>();
            var list = new List<(int, int)>(n * 2);

            void TryAdd(int a, int b)
            {
                if (a == b || a < 0 || b < 0 || a >= n || b >= n) return;
                int mn = Math.Min(a, b);
                int mx = Math.Max(a, b);
                long key = ((long)mn << 32) | (uint)mx;
                if (edgeSet.Add(key))
                {
                    list.Add((mn, mx));
                    adjArr[mn].Add(mx);
                    adjArr[mx].Add(mn);
                }
            }

            if (mesh.Faces.Count > 0)
            {
                for (int f = 0; f < mesh.Faces.Count; f++)
                {
                    var face = mesh.Faces[f];
                    for (int k = 0; k < face.Count; k++)
                        TryAdd(face[k], face[(k + 1) % face.Count]);
                }
            }
            else
            {
                for (int t = 0; t + 2 < mesh.Triangles.Count; t += 3)
                {
                    TryAdd(mesh.Triangles[t], mesh.Triangles[t + 1]);
                    TryAdd(mesh.Triangles[t + 1], mesh.Triangles[t + 2]);
                    TryAdd(mesh.Triangles[t + 2], mesh.Triangles[t]);
                }
            }

            return list;
        }

        /// <summary>
        /// Tính hướng mặt ngoài hiện tại của mảnh vải sau khi áp dụng xoay WorldRotationEuler.
        /// </summary>
        public static Vec3 ComputeClothOutwardDirection(Vec3 anchorNormal, Vec3 worldRotationEulerDeg)
        {
            Vec3 n = anchorNormal.LengthSquared > 1e-6f ? anchorNormal.Normalized : Vec3.Forward;
            Vec3 rotated = RotateEulerXYZ(n, worldRotationEulerDeg * (float)(Math.PI / 180.0));
            return rotated.LengthSquared > 1e-6f ? rotated.Normalized : n;
        }

        /// <summary>
        /// Kẹp độ sâu lùi WorldOffset theo hướng tiếp xúc mặt trước để vải dừng tại bề mặt da,
        /// không vượt quá điểm trung tâm sang mặt đối diện.
        /// </summary>
        public static Vec3 ClampFrontContactOffset(
            CharacterBvh? charBvh,
            CharacterAnchorPoint? anchor,
            ClothCharacterAttachment st)
        {
            Vec3 offset = st.WorldOffset;
            if (charBvh == null || anchor == null || st.FormMode == FormSnapMode.Off)
                return offset;

            Vec3 anchorPos = anchor.Position;
            Vec3 anchorNormal = anchor.Normal.LengthSquared > 1e-6f ? anchor.Normal.Normalized : Vec3.Forward;
            Vec3 dirOut = ComputeClothOutwardDirection(anchorNormal, st.WorldRotationEuler);

            float depthOffset = Vec3.Dot(offset, dirOut);
            Vec3 lateralOffset = offset - dirOut * depthOffset;
            Vec3 frontProbeStart = anchorPos + lateralOffset + dirOut * 0.55f;
            if (charBvh.Raycast(frontProbeStart, -dirOut, out BvhRayHit frontHit, 1.10f) &&
                Vec3.Dot(frontHit.Normal, dirOut) > -0.15f)
            {
                float surfaceDepth = Vec3.Dot(frontHit.Point - anchorPos, dirOut);
                float minAllowedDepth = surfaceDepth - 0.015f;
                if (depthOffset < minAllowedDepth)
                    return lateralOffset + dirOut * minAllowedDepth;
            }
            return offset;
        }

        private static Vec3 GetAverageNormal(MeshData mesh)
        {
            Vec3 sum = Vec3.Zero;
            for (int i = 0; i < mesh.Normals.Count; i++) sum = sum + mesh.Normals[i];
            return sum.LengthSquared > 1e-6f ? sum.Normalized : Vec3.Forward;
        }

        private static Vec3 RotateEulerXYZ(Vec3 v, Vec3 rad)
        {
            if (Math.Abs(rad.X) > 1e-5f)
            {
                float c = (float)Math.Cos(rad.X), s = (float)Math.Sin(rad.X);
                v = new Vec3(v.X, v.Y * c - v.Z * s, v.Y * s + v.Z * c);
            }
            if (Math.Abs(rad.Y) > 1e-5f)
            {
                float c = (float)Math.Cos(rad.Y), s = (float)Math.Sin(rad.Y);
                v = new Vec3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
            }
            if (Math.Abs(rad.Z) > 1e-5f)
            {
                float c = (float)Math.Cos(rad.Z), s = (float)Math.Sin(rad.Z);
                v = new Vec3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
            }
            return v;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static float SmoothStep(float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
