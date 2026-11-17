using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CrimiradTailoring.Core.Engine;
using CrimiradTailoring.Core.Models;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace CrimiradTailoring.Studio.Rendering
{
    public enum ViewportShadingMode
    {
        WireframeOnly,
        SolidClay,
        TexturedGrid
    }

    /// <summary>
    /// Bộ render 3D GPU sử dụng OpenGL 3.3 Core (tương tự kiến trúc Viewport của Blender).
    /// Quản lý VAO/VBO/EBO theo từng ClothEntity và tái sử dụng primitive cầu/trụ/mũi tên để đạt 60+ FPS mượt mà.
    /// </summary>
    public sealed partial class GlClothViewportRenderer : IDisposable
    {
        private bool _initialized;
        private bool _disposed;

        // Shader Programs
        private int _clothProgram;
        private int _solidProgram;
        private int _lineProgram;

        // Shared Static VAOs
        private int _floorGridVao, _floorGridVbo, _floorGridCount;
        private int _axisXVao, _axisXVbo;
        private int _axisZVao, _axisZVbo;

        private int _unitSphereVao, _unitSphereVbo, _unitSphereEbo, _unitSphereIdxCount;
        private int _unitCylinderVao, _unitCylinderVbo, _unitCylinderEbo, _unitCylinderIdxCount;
        private int _unitArrowVao, _unitArrowVbo, _unitArrowEbo, _unitArrowIdxCount;
        private int _unitRingVao, _unitRingVbo, _unitRingVertCount;
        private int _dynamicOutlineVao, _dynamicOutlineVbo;

        private sealed class ClothGpuCache : IDisposable
        {
            public int Vao;
            public int Vbo;
            public int UnselectedEbo;
            public int SelectedEbo;
            public int WireVao;
            public int WireVbo;
            public int WireEbo;
            public int NormalsVao;
            public int NormalsVbo;

            public int UnselectedIndexCount;
            public int SelectedIndexCount;
            public int WireIndexCount;
            public int NormalsVertexCount;

            public bool MeshDirty = true;
            public bool WireDirty = true;
            public bool NormalsDirty = true;
            public bool SelectionDirty = true;
            public bool WireEboValid = false;
            public bool SelectionEboValid = false;
            public MeshShadingSmoothMode BuiltSmoothMode;
            public float BuiltAutoSmoothDeg;
            public int BuiltVertCount;
            public int BuiltTriCount;
            public int BuiltSubdivLevel;
            public bool BuiltUsedSharedVertices;
            public float[]? VboScratch;
            public float[]? WireScratch;

            // Map tam giác gốc -> danh sách index đã split (nếu AutoSmooth/Flat phải tách đỉnh) hoặc 1:1 với Triangles
            public int[]? TriCornerMap;
            public int[][]? VertToTrisFlat;
            public bool VertToTrisValid = false;
            public Vec3[]? RawTriNormalsScratch;
            public Vec3[]? UnitTriNormalsScratch;
            public Vec3[]? FaceNormSumScratch;

            public void Dispose()
            {
                if (Vao != 0) GL.DeleteVertexArray(Vao);
                if (Vbo != 0) GL.DeleteBuffer(Vbo);
                if (UnselectedEbo != 0) GL.DeleteBuffer(UnselectedEbo);
                if (SelectedEbo != 0) GL.DeleteBuffer(SelectedEbo);
                if (WireVao != 0) GL.DeleteVertexArray(WireVao);
                if (WireVbo != 0) GL.DeleteBuffer(WireVbo);
                if (WireEbo != 0) GL.DeleteBuffer(WireEbo);
                if (NormalsVao != 0) GL.DeleteVertexArray(NormalsVao);
                if (NormalsVbo != 0) GL.DeleteBuffer(NormalsVbo);
                Vao = Vbo = UnselectedEbo = SelectedEbo = WireVao = WireVbo = WireEbo = NormalsVao = NormalsVbo = 0;
            }
        }

        private readonly Dictionary<Guid, ClothGpuCache> _clothCaches = new();

        public void EnsureInitialized()
        {
            if (_initialized || _disposed) return;

            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Disable(EnableCap.CullFace);

            CompileShaders();
            BuildFloorGridAndAxes();
            BuildUnitSphere(10, 8);
            BuildUnitCylinder(10);
            BuildUnitArrow(10);
            BuildUnitRing(32);

            _dynamicOutlineVao = GL.GenVertexArray();
            _dynamicOutlineVbo = GL.GenBuffer();

            _initialized = true;
        }

        public void MarkClothDirty(Guid clothId)
        {
            if (_clothCaches.TryGetValue(clothId, out var cache))
            {
                cache.MeshDirty = true;
                cache.WireDirty = true;
                cache.NormalsDirty = true;
            }
            MarkCharacterClothDirty(clothId);
        }

        public void MarkClothSelectionDirty(Guid clothId)
        {
            if (_clothCaches.TryGetValue(clothId, out var cache))
            {
                cache.SelectionDirty = true;
                cache.SelectionEboValid = false;
            }
            _subVpDirty = true;
        }

        public void MarkAllDirty()
        {
            foreach (var c in _clothCaches.Values)
            {
                c.MeshDirty = true;
                c.WireDirty = true;
                c.NormalsDirty = true;
                c.SelectionDirty = true;
                c.SelectionEboValid = false;
            }
            MarkAllCharacterClothsDirty();
        }

        public void MarkAllSelectionDirty()
        {
            foreach (var c in _clothCaches.Values)
            {
                c.SelectionDirty = true;
                c.SelectionEboValid = false;
            }
        }

        public void PruneUnusedCloths(List<ClothEntity> activeCloths)
        {
            if (_clothCaches.Count == activeCloths.Count)
            {
                bool allPresent = true;
                for (int i = 0; i < activeCloths.Count; i++)
                {
                    if (!_clothCaches.ContainsKey(activeCloths[i].Id))
                    {
                        allPresent = false;
                        break;
                    }
                }
                if (allPresent) return;
            }

            var activeIds = new HashSet<Guid>();
            for (int i = 0; i < activeCloths.Count; i++)
                activeIds.Add(activeCloths[i].Id);

            var toRemove = new List<Guid>();
            foreach (var kv in _clothCaches)
            {
                if (!activeIds.Contains(kv.Key))
                    toRemove.Add(kv.Key);
            }

            foreach (var id in toRemove)
            {
                _clothCaches[id].Dispose();
                _clothCaches.Remove(id);
            }
        }

        public void RenderFrame(
            int pixelWidth,
            int pixelHeight,
            double camYawDeg,
            double camPitchDeg,
            double camDistance,
            Vec3 camTarget,
            List<ClothEntity> cloths,
            List<ClothConnection> connections,
            ClothEntity? selectedCloth,
            ControlPoint? selectedPoint,
            ControlEdge? selectedEdge,
            ControlPoint? connectFirstPoint,
            ControlEdge? connectFirstEdge,
            HashSet<Guid>? selectedClothIds,
            HashSet<Guid>? selectedPointIds,
            HashSet<Guid>? selectedEdgeIds,
            GizmoAxis activeGizmoAxis,
            ViewportShadingMode shadingMode,
            bool showWireOverlay,
            bool showNormals,
            bool showFloorGrid,
            bool showElements,
            bool showInfluence,
            bool showMoveGizmo,
            IReadOnlyList<ClothBorderEdge>? selectedClothBorders = null,
            ClothBorderEdge? selectedBorderEdge = null,
            bool highlightAllBorders = false)
        {
            EnsureInitialized();
            if (!_initialized) return;

            int w = Math.Max(1, pixelWidth);
            int h = Math.Max(1, pixelHeight);
            GL.Viewport(0, 0, w, h);

            GL.ClearColor(0.114f, 0.114f, 0.118f, 1.0f); // #1D1D1E xám đen
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            ComputeCameraMatrices(w, h, camYawDeg, camPitchDeg, camDistance, camTarget, out Vector3 camPos, out Matrix4 viewProj);

            // 1. Lưới sàn Blender & trục X/Z
            if (showFloorGrid)
                DrawFloorGridAndAxes(viewProj);

            PruneUnusedCloths(cloths);

            // 2. Các mảnh vải & Overlays (Wireframe, Normals, Selection Region Outline)
            foreach (var cloth in cloths)
            {
                if (!cloth.IsVisible) continue;
                if (!_clothCaches.TryGetValue(cloth.Id, out var cache))
                {
                    cache = new ClothGpuCache();
                    _clothCaches[cloth.Id] = cache;
                }

                var activeMesh = cloth.RenderSubdividedMesh ?? cloth.Mesh;
                int activeRenderVerts = activeMesh.Vertices.Count;
                int activeRenderTris = activeMesh.Triangles.Count / 3;
                if (cache.BuiltSmoothMode != cloth.SmoothMode ||
                    Math.Abs(cache.BuiltAutoSmoothDeg - cloth.AutoSmoothAngleDeg) > 0.1f ||
                    cache.BuiltVertCount != activeRenderVerts ||
                    cache.BuiltTriCount != activeRenderTris ||
                    cache.BuiltSubdivLevel != cloth.SubdivisionLevel)
                {
                    if (cache.BuiltVertCount != activeRenderVerts ||
                        cache.BuiltTriCount != activeRenderTris ||
                        cache.BuiltSubdivLevel != cloth.SubdivisionLevel)
                    {
                        cache.WireEboValid = false;
                        cache.SelectionEboValid = false;
                        cache.VertToTrisValid = false;
                    }
                    cache.MeshDirty = true;
                    cache.WireDirty = true;
                    cache.NormalsDirty = true;
                    cache.SelectionDirty = true;
                }

                if (cache.MeshDirty)
                    UploadClothMeshAndWireToGpu(cloth, cache);

                bool needWire = showWireOverlay || shadingMode == ViewportShadingMode.WireframeOnly;
                if (needWire && (cache.WireDirty || cache.WireIndexCount == 0))
                    UploadClothWireToGpu(cloth, cache);

                if (showNormals && (cache.NormalsDirty || cache.NormalsVertexCount == 0))
                    UploadClothNormalsToGpu(cloth, cache);

                if (cache.SelectionDirty)
                    UploadClothSelectionIndicesToGpu(cloth, cache);

                bool isClothSelected = (selectedCloth != null && selectedCloth.Id == cloth.Id) ||
                                       (selectedClothIds != null && selectedClothIds.Contains(cloth.Id));

                if (shadingMode != ViewportShadingMode.WireframeOnly && cache.Vao != 0)
                {
                    GL.UseProgram(_clothProgram);
                    GL.UniformMatrix4(GL.GetUniformLocation(_clothProgram, "uViewProj"), false, ref viewProj);
                    GL.Uniform3(GL.GetUniformLocation(_clothProgram, "uCamPos"), camPos);
                    GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uShadingMode"), shadingMode == ViewportShadingMode.SolidClay ? 1 : 2);
                    GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uIsSelectedCloth"), isClothSelected ? 1.0f : 0.0f);

                    Vector3 baseRgb = new(cloth.ClothColor.R / 255f, cloth.ClothColor.G / 255f, cloth.ClothColor.B / 255f);
                    Vector3 selRgb = new(0.96f, 0.76f, 0.25f);

                    GL.BindVertexArray(cache.Vao);

                    if (cache.UnselectedIndexCount > 0)
                    {
                        GL.Uniform3(GL.GetUniformLocation(_clothProgram, "uBaseColor"), baseRgb);
                        GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uIsSelectedRegion"), 0.0f);
                        GL.BindBuffer(BufferTarget.ElementArrayBuffer, cache.UnselectedEbo);
                        GL.DrawElements(PrimitiveType.Triangles, cache.UnselectedIndexCount, DrawElementsType.UnsignedInt, 0);
                    }

                    if (cache.SelectedIndexCount > 0)
                    {
                        GL.Uniform3(GL.GetUniformLocation(_clothProgram, "uBaseColor"), selRgb);
                        GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uIsSelectedRegion"), 1.0f);
                        GL.BindBuffer(BufferTarget.ElementArrayBuffer, cache.SelectedEbo);
                        GL.DrawElements(PrimitiveType.Triangles, cache.SelectedIndexCount, DrawElementsType.UnsignedInt, 0);
                    }
                }

                // Wireframe Quad Overlay hoặc Wireframe Only
                if ((showWireOverlay || shadingMode == ViewportShadingMode.WireframeOnly) && cache.WireVao != 0 && cache.WireIndexCount > 0)
                {
                    GL.UseProgram(_lineProgram);
                    GL.UniformMatrix4(GL.GetUniformLocation(_lineProgram, "uViewProj"), false, ref viewProj);
                    GL.Uniform1(GL.GetUniformLocation(_lineProgram, "uDepthBias"), shadingMode == ViewportShadingMode.WireframeOnly ? 0f : -0.00045f);
                    GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), new Vector3(0.82f, 0.90f, 1.0f));
                    GL.LineWidth(shadingMode == ViewportShadingMode.WireframeOnly ? 1.5f : 1.15f);
                    GL.BindVertexArray(cache.WireVao);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, cache.WireEbo);
                    GL.DrawElements(PrimitiveType.Lines, cache.WireIndexCount, DrawElementsType.UnsignedInt, 0);
                }

                // Normals Overlay
                if (showNormals && cache.NormalsVao != 0 && cache.NormalsVertexCount > 0)
                {
                    GL.UseProgram(_lineProgram);
                    GL.UniformMatrix4(GL.GetUniformLocation(_lineProgram, "uViewProj"), false, ref viewProj);
                    GL.Uniform1(GL.GetUniformLocation(_lineProgram, "uDepthBias"), -0.0005f);
                    GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), new Vector3(0.32f, 0.92f, 1.0f));
                    GL.LineWidth(1.25f);
                    GL.BindVertexArray(cache.NormalsVao);
                    GL.DrawArrays(PrimitiveType.Lines, 0, cache.NormalsVertexCount);
                }

                // Active Region Outline
                if (cloth.ActiveRegion != null && cloth.ActiveRegion.IsActive)
                {
                    DrawRegionOutline(cloth.ActiveRegion, viewProj);
                }
            }

            // 3. Điểm điều khiển, Cạnh điều khiển & Vùng Radius
            if (showElements)
            {
                GL.UseProgram(_solidProgram);
                GL.UniformMatrix4(GL.GetUniformLocation(_solidProgram, "uViewProj"), false, ref viewProj);
                GL.Uniform3(GL.GetUniformLocation(_solidProgram, "uCamPos"), camPos);
                GL.Uniform1(GL.GetUniformLocation(_solidProgram, "uUnlit"), 0.0f);

                foreach (var cloth in cloths)
                {
                    if (!cloth.IsVisible) continue;
                    foreach (var edge in cloth.Edges)
                    {
                        var pA = cloth.FindPoint(edge.PointAId);
                        var pB = cloth.FindPoint(edge.PointBId);
                        if (pA == null || pB == null) continue;

                        bool isEdgeSel = (selectedEdge != null && selectedEdge.Id == edge.Id) ||
                                         (connectFirstEdge != null && connectFirstEdge.Id == edge.Id) ||
                                         (selectedEdgeIds != null && selectedEdgeIds.Contains(edge.Id));

                        float cylRadius = isEdgeSel ? 0.0055f : 0.0038f;
                        Vector3 edgeCol = isEdgeSel ? new Vector3(1.0f, 0.65f, 0.18f) : new Vector3(0.92f, 0.94f, 0.98f);
                        DrawCylinderSegment(pA.Position, pB.Position, cylRadius, edgeCol);
                    }

                    foreach (var pt in cloth.Points)
                    {
                        bool isPtSel = (selectedPoint != null && selectedPoint.Id == pt.Id) ||
                                       (connectFirstPoint != null && connectFirstPoint.Id == pt.Id) ||
                                       (selectedPointIds != null && selectedPointIds.Contains(pt.Id));

                        float sphereR = isPtSel ? 0.010f : 0.0072f;
                        Vector3 ptCol = isPtSel ? new Vector3(1.0f, 0.84f, 0.22f) : new Vector3(0.25f, 0.80f, 0.98f);
                        DrawSphere(pt.Position, sphereR, ptCol);
                    }
                }

                // Dây Hang
                foreach (var conn in connections)
                {
                    if (conn.Mode != ConnectMode.Hang) continue;
                    var src = cloths.Find(c => c.Id == conn.SourceClothId);
                    var dst = cloths.Find(c => c.Id == conn.TargetClothId);
                    if (src == null || dst == null) continue;

                    if (!conn.IsEdgeConnection)
                    {
                        var sPt = src.FindPoint(conn.SourceElementId);
                        var dPt = dst.FindPoint(conn.TargetElementId);
                        if (sPt != null && dPt != null)
                            DrawCylinderSegment(sPt.Position, dPt.Position, 0.0028f, new Vector3(0.96f, 0.75f, 0.31f));
                    }
                }

                // Vòng ảnh hưởng Radius
                if (showInfluence)
                {
                    foreach (var cloth in cloths)
                    {
                        if (!cloth.IsVisible) continue;
                        foreach (var edge in cloth.Edges)
                        {
                            bool isEdgeSel = (selectedEdge != null && selectedEdge.Id == edge.Id) ||
                                             (connectFirstEdge != null && connectFirstEdge.Id == edge.Id) ||
                                             (selectedEdgeIds != null && selectedEdgeIds.Contains(edge.Id));
                            if (!isEdgeSel) continue;
                            var pA = cloth.FindPoint(edge.PointAId);
                            var pB = cloth.FindPoint(edge.PointBId);
                            if (pA == null || pB == null) continue;

                            Vec3 axis = pB.Position - pA.Position;
                            Vec3 mid = (pA.Position + pB.Position) * 0.5f;
                            DrawRadiusRing(pA.Position, axis, edge.Radius, viewProj);
                            DrawRadiusRing(mid, axis, edge.Radius, viewProj);
                            DrawRadiusRing(pB.Position, axis, edge.Radius, viewProj);
                        }

                        foreach (var pt in cloth.Points)
                        {
                            bool isPtSel = (selectedPoint != null && selectedPoint.Id == pt.Id) ||
                                           (connectFirstPoint != null && connectFirstPoint.Id == pt.Id) ||
                                           (selectedPointIds != null && selectedPointIds.Contains(pt.Id));
                            if (!isPtSel) continue;
                            DrawRadiusRing(pt.Position, Vec3.Forward, pt.Radius, viewProj);
                        }
                    }
                }
            }

            // 3b. Điểm Origin (tâm xoay / phóng to thu nhỏ) màu cam kiểu Blender & các Cạnh Biên Vải
            if (selectedCloth != null && selectedCloth.IsVisible && selectedCloth.Mesh.Vertices.Count > 0)
            {
                if (highlightAllBorders && selectedClothBorders != null)
                {
                    foreach (var border in selectedClothBorders)
                    {
                        bool isSel = selectedBorderEdge != null && selectedBorderEdge.Index == border.Index;
                        if (!isSel)
                            DrawBorderPolyline(selectedCloth.Mesh.Vertices, border, new Vector3(0.38f, 0.78f, 0.88f), 2.2f, viewProj);
                    }
                }

                if (selectedBorderEdge != null)
                {
                    DrawBorderPolyline(selectedCloth.Mesh.Vertices, selectedBorderEdge, new Vector3(1.0f, 0.78f, 0.22f), 4.0f, viewProj);
                    GL.UseProgram(_solidProgram);
                    GL.UniformMatrix4(GL.GetUniformLocation(_solidProgram, "uViewProj"), false, ref viewProj);
                    GL.Uniform3(GL.GetUniformLocation(_solidProgram, "uCamPos"), camPos);
                    GL.Uniform1(GL.GetUniformLocation(_solidProgram, "uUnlit"), 1.0f);
                    DrawSphere(selectedBorderEdge.Midpoint, 0.010f, new Vector3(1.0f, 0.80f, 0.22f));
                }
            }

            foreach (var cloth in cloths)
            {
                if (!cloth.IsVisible) continue;
                bool isSelCloth = selectedCloth != null && selectedCloth.Id == cloth.Id;
                if ((cloth.CustomOrigin.HasValue || isSelCloth) && cloth.Mesh.Vertices.Count > 0)
                {
                    Vec3 origin = ClothSculptEngine.GetClothOrigin(cloth);
                    GL.UseProgram(_solidProgram);
                    GL.UniformMatrix4(GL.GetUniformLocation(_solidProgram, "uViewProj"), false, ref viewProj);
                    GL.Uniform3(GL.GetUniformLocation(_solidProgram, "uCamPos"), camPos);
                    GL.Uniform1(GL.GetUniformLocation(_solidProgram, "uUnlit"), 1.0f);
                    DrawSphere(origin, cloth.CustomOrigin.HasValue ? 0.0095f : 0.0070f, new Vector3(1.0f, 0.56f, 0.08f));
                }
            }

            // 4. Move Gizmo 3D (luôn nổi trên cùng để dễ nắm kéo!)
            if (showMoveGizmo && selectedCloth != null && selectedCloth.IsVisible)
            {
                Vec3? pivot = GetGizmoPivot(selectedCloth, selectedPoint, selectedEdge, selectedBorderEdge);
                if (pivot.HasValue)
                {
                    GL.Clear(ClearBufferMask.DepthBufferBit);
                    DrawMoveGizmo(pivot.Value, (float)Math.Max(0.14, Math.Min(0.28, camDistance * 0.14)), activeGizmoAxis, viewProj, camPos);
                }
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        public static Vec3? GetGizmoPivot(
            ClothEntity? selectedCloth,
            ControlPoint? selectedPoint,
            ControlEdge? selectedEdge,
            ClothBorderEdge? selectedBorderEdge = null)
        {
            if (selectedCloth == null) return null;
            if (selectedPoint != null) return selectedPoint.Position;
            if (selectedEdge != null)
            {
                var pA = selectedCloth.FindPoint(selectedEdge.PointAId);
                var pB = selectedCloth.FindPoint(selectedEdge.PointBId);
                if (pA != null && pB != null)
                    return (pA.Position + pB.Position) * 0.5f;
            }
            if (selectedBorderEdge != null)
                return selectedBorderEdge.Midpoint;
            if (selectedCloth.Mesh.Vertices.Count > 0)
                return ClothSculptEngine.GetClothOrigin(selectedCloth);
            return null;
        }

        private static void ComputeCameraMatrices(
            int width,
            int height,
            double camYawDeg,
            double camPitchDeg,
            double camDistance,
            Vec3 camTarget,
            out Vector3 camPos,
            out Matrix4 viewProj)
        {
            double yawRad = camYawDeg * Math.PI / 180.0;
            double pitchRad = camPitchDeg * Math.PI / 180.0;
            double cosPitch = Math.Cos(pitchRad);

            camPos = new Vector3(
                (float)(camTarget.X + camDistance * cosPitch * Math.Sin(yawRad)),
                (float)(camTarget.Y + camDistance * Math.Sin(pitchRad)),
                (float)(camTarget.Z + camDistance * cosPitch * Math.Cos(yawRad)));

            Vector3 target = new(camTarget.X, camTarget.Y, camTarget.Z);
            Matrix4 view = Matrix4.LookAt(camPos, target, Vector3.UnitY);
            float aspect = Math.Max(0.1f, (float)width / Math.Max(1, height));
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(44f), aspect, 0.05f, 25f);
            viewProj = view * proj;
        }

        public static void ComputeWorldRay(
            double mouseX,
            double mouseY,
            double viewportW,
            double viewportH,
            double camYawDeg,
            double camPitchDeg,
            double camDistance,
            Vec3 camTarget,
            out Vec3 rayOrigin,
            out Vec3 rayDir)
        {
            ComputeCameraMatrices(
                (int)Math.Max(1, viewportW),
                (int)Math.Max(1, viewportH),
                camYawDeg,
                camPitchDeg,
                camDistance,
                camTarget,
                out Vector3 camPos,
                out Matrix4 viewProj);

            float nx = (float)((mouseX / Math.Max(1.0, viewportW)) * 2.0 - 1.0);
            float ny = (float)(1.0 - (mouseY / Math.Max(1.0, viewportH)) * 2.0);

            Matrix4 invVP = viewProj.Inverted();
            Vector4 nearPt = new Vector4(nx, ny, -1f, 1f) * invVP;
            Vector4 farPt = new Vector4(nx, ny, 1f, 1f) * invVP;
            nearPt /= nearPt.W;
            farPt /= farPt.W;

            Vector3 dir = Vector3.Normalize(farPt.Xyz - nearPt.Xyz);
            rayOrigin = new Vec3(camPos.X, camPos.Y, camPos.Z);
            rayDir = new Vec3(dir.X, dir.Y, dir.Z);
        }

        /// <summary>
        /// Raycaster toán học thuần CPU tốc độ cao (<0.03ms), hoạt động chính xác cho cả chế độ Wireframe lẫn Solid.
        /// </summary>
        public static void RaycastSceneAnalytic(
            double mouseX,
            double mouseY,
            double viewportW,
            double viewportH,
            double camYawDeg,
            double camPitchDeg,
            double camDistance,
            Vec3 camTarget,
            List<ClothEntity> cloths,
            ClothEntity? selectedCloth,
            ControlPoint? selectedPoint,
            ControlEdge? selectedEdge,
            bool showMoveGizmo,
            bool showElements,
            out GizmoAxis hitGizmoAxis,
            out ClothEntity? hitCloth,
            out ControlPoint? hitPoint,
            out ControlEdge? hitEdge,
            out Vec3 hitWorldPos)
        {
            RaycastSceneAnalytic(
                mouseX, mouseY, viewportW, viewportH,
                camYawDeg, camPitchDeg, camDistance, camTarget,
                cloths, selectedCloth, selectedPoint, selectedEdge,
                showMoveGizmo, showElements,
                out hitGizmoAxis, out hitCloth, out hitPoint, out hitEdge, out hitWorldPos,
                null, null, false, out _);
        }

        /// <summary>
        /// Raycaster toán học thuần CPU tốc độ cao (<0.03ms), hoạt động chính xác cho cả chế độ Wireframe lẫn Solid (hỗ trợ cả Biên vải).
        /// </summary>
        public static void RaycastSceneAnalytic(
            double mouseX,
            double mouseY,
            double viewportW,
            double viewportH,
            double camYawDeg,
            double camPitchDeg,
            double camDistance,
            Vec3 camTarget,
            List<ClothEntity> cloths,
            ClothEntity? selectedCloth,
            ControlPoint? selectedPoint,
            ControlEdge? selectedEdge,
            bool showMoveGizmo,
            bool showElements,
            out GizmoAxis hitGizmoAxis,
            out ClothEntity? hitCloth,
            out ControlPoint? hitPoint,
            out ControlEdge? hitEdge,
            out Vec3 hitWorldPos,
            IReadOnlyList<ClothBorderEdge>? selectedClothBorders,
            ClothBorderEdge? selectedBorderEdge,
            bool allowBorderPicking,
            out ClothBorderEdge? hitBorderEdge)
        {
            ComputeWorldRay(mouseX, mouseY, viewportW, viewportH, camYawDeg, camPitchDeg, camDistance, camTarget, out Vec3 ro, out Vec3 rd);

            hitGizmoAxis = GizmoAxis.None;
            hitCloth = null;
            hitPoint = null;
            hitEdge = null;
            hitBorderEdge = null;
            hitWorldPos = Vec3.Zero;

            // 1. Kiểm tra Move Gizmo trước tiên
            if (showMoveGizmo && selectedCloth != null)
            {
                Vec3? pivot = GetGizmoPivot(selectedCloth, selectedPoint, selectedEdge, selectedBorderEdge);
                if (pivot.HasValue && TryHitMoveGizmo(ro, rd, pivot.Value, camDistance, out GizmoAxis gAx, out Vec3 gWorld))
                {
                    hitGizmoAxis = gAx;
                    hitWorldPos = gWorld;
                    return;
                }
            }

            // 2. Điểm và Cạnh điều khiển
            float bestElemT = float.MaxValue;
            if (showElements)
            {
                foreach (var cloth in cloths)
                {
                    if (!cloth.IsVisible) continue;
                    foreach (var pt in cloth.Points)
                    {
                        if (RaySphereDistance(ro, rd, pt.Position, 0.020f, out float tHit) && tHit < bestElemT)
                        {
                            bestElemT = tHit;
                            hitCloth = cloth;
                            hitPoint = pt;
                            hitEdge = null;
                            hitWorldPos = pt.Position;
                        }
                    }

                    if (hitPoint == null)
                    {
                        foreach (var edge in cloth.Edges)
                        {
                            var pA = cloth.FindPoint(edge.PointAId);
                            var pB = cloth.FindPoint(edge.PointBId);
                            if (pA == null || pB == null) continue;

                            float dist = RaySegmentDistance(ro, rd, pA.Position, pB.Position, out float tHit);
                            if (dist < 0.012f && tHit < bestElemT)
                            {
                                bestElemT = tHit;
                                hitCloth = cloth;
                                hitEdge = edge;
                                hitWorldPos = ro + rd * tHit;
                            }
                        }
                    }
                }
            }

            if (hitPoint != null || hitEdge != null)
                return;

            // 2b. Cạnh Biên Vải (MeshEdge) trên tất cả mảnh vải đang hiển thị
            if (allowBorderPicking)
            {
                float bestBorderT = float.MaxValue;
                foreach (var cloth in cloths)
                {
                    if (!cloth.IsVisible) continue;
                    var borders = (cloth == selectedCloth && selectedClothBorders != null && selectedClothBorders.Count > 0)
                        ? selectedClothBorders
                        : (cloth.CachedBorders ??= ClothSculptEngine.DetectClothBorderEdges(cloth));
                    if (borders == null || borders.Count == 0) continue;

                    var cVerts = cloth.Mesh.Vertices;
                    foreach (var border in borders)
                    {
                        var chain = border.VertexIndices;
                        int segCount = border.IsClosedLoop ? chain.Count : (chain.Count - 1);
                        for (int s = 0; s < segCount; s++)
                        {
                            int ia = chain[s];
                            int ib = chain[(s + 1) % chain.Count];
                            if (ia < 0 || ib < 0 || ia >= cVerts.Count || ib >= cVerts.Count) continue;
                            float dSeg = RaySegmentDistance(ro, rd, cVerts[ia], cVerts[ib], out float tHit);
                            if (dSeg < 0.015f && tHit < bestBorderT)
                            {
                                bestBorderT = tHit;
                                hitCloth = cloth;
                                hitBorderEdge = border;
                                hitWorldPos = ro + rd * tHit;
                            }
                        }
                    }
                }
                if (hitBorderEdge != null)
                    return;
            }

            // 3. Mặt vải (dùng lưới gốc khi mật độ hiển thị cao > 6000 tris để pick cực nhanh + không tốn CPU)
            float bestClothT = float.MaxValue;
            foreach (var cloth in cloths)
            {
                if (!cloth.IsVisible) continue;
                var dispMesh = cloth.RenderSubdividedMesh ?? cloth.Mesh;
                var pickMesh = dispMesh.Triangles.Count > 6000 ? cloth.Mesh : dispMesh;
                var verts = pickMesh.Vertices;
                var tris = pickMesh.Triangles;
                int triCount = tris.Count / 3;

                for (int t = 0; t < triCount; t++)
                {
                    Vec3 v0 = verts[tris[t * 3]];
                    Vec3 v1 = verts[tris[t * 3 + 1]];
                    Vec3 v2 = verts[tris[t * 3 + 2]];

                    if (RayTriangleIntersect(ro, rd, v0, v1, v2, out float tHit) && tHit < bestClothT)
                    {
                        bestClothT = tHit;
                        hitCloth = cloth;
                        hitWorldPos = ro + rd * tHit;
                    }
                }
            }
        }

        private static bool RaySphereDistance(Vec3 ro, Vec3 rd, Vec3 center, float radius, out float tHit)
        {
            Vec3 oc = ro - center;
            float b = Vec3.Dot(oc, rd);
            float c = Vec3.Dot(oc, oc) - radius * radius;
            float disc = b * b - c;
            if (disc < 0f)
            {
                tHit = 0f;
                return false;
            }
            float t = -b - (float)Math.Sqrt(disc);
            if (t < 0.01f) t = -b + (float)Math.Sqrt(disc);
            tHit = t;
            return t > 0.01f;
        }

        private static float RaySegmentDistance(Vec3 ro, Vec3 rd, Vec3 a, Vec3 b, out float tRay)
        {
            Vec3 u = rd;
            Vec3 v = b - a;
            Vec3 w = ro - a;
            float aa = Vec3.Dot(u, u);
            float ab = Vec3.Dot(u, v);
            float bb = Vec3.Dot(v, v);
            float au = Vec3.Dot(u, w);
            float av = Vec3.Dot(v, w);
            float denom = aa * bb - ab * ab;

            float sc, tc;
            if (denom < 1e-6f)
            {
                sc = 0f;
                tc = bb > 1e-6f ? Math.Clamp(av / bb, 0f, 1f) : 0f;
            }
            else
            {
                sc = (ab * av - bb * au) / denom;
                tc = (aa * av - ab * au) / denom;
                if (tc < 0f)
                {
                    tc = 0f;
                    sc = -au / aa;
                }
                else if (tc > 1f)
                {
                    tc = 1f;
                    sc = (ab - au) / aa;
                }
            }

            sc = Math.Max(0.01f, sc);
            tRay = sc;
            Vec3 dP = w + u * sc - v * tc;
            return dP.Length;
        }

        private static bool RayTriangleIntersect(Vec3 ro, Vec3 rd, Vec3 v0, Vec3 v1, Vec3 v2, out float tHit)
        {
            tHit = 0f;
            Vec3 e1 = v1 - v0;
            Vec3 e2 = v2 - v0;
            Vec3 h = Vec3.Cross(rd, e2);
            float a = Vec3.Dot(e1, h);
            if (Math.Abs(a) < 1e-7f) return false;

            float f = 1f / a;
            Vec3 s = ro - v0;
            float u = f * Vec3.Dot(s, h);
            if (u < -0.001f || u > 1.001f) return false;

            Vec3 q = Vec3.Cross(s, e1);
            float v = f * Vec3.Dot(rd, q);
            if (v < -0.001f || u + v > 1.002f) return false;

            float t = f * Vec3.Dot(e2, q);
            if (t > 0.01f)
            {
                tHit = t;
                return true;
            }
            return false;
        }

        private void UploadClothMeshAndWireToGpu(ClothEntity cloth, ClothGpuCache cache)
        {
            var data = cloth.RenderSubdividedMesh ?? cloth.Mesh;
            if (data.Vertices.Count == 0 || data.Triangles.Count < 3)
            {
                cache.UnselectedIndexCount = 0;
                cache.SelectedIndexCount = 0;
                cache.WireIndexCount = 0;
                cache.NormalsVertexCount = 0;
                cache.MeshDirty = false;
                return;
            }

            if (cache.Vao == 0) cache.Vao = GL.GenVertexArray();
            if (cache.Vbo == 0) cache.Vbo = GL.GenBuffer();
            if (cache.UnselectedEbo == 0) cache.UnselectedEbo = GL.GenBuffer();
            if (cache.SelectedEbo == 0) cache.SelectedEbo = GL.GenBuffer();
            if (cache.WireVao == 0) cache.WireVao = GL.GenVertexArray();
            if (cache.WireVbo == 0) cache.WireVbo = GL.GenBuffer();
            if (cache.WireEbo == 0) cache.WireEbo = GL.GenBuffer();
            if (cache.NormalsVao == 0) cache.NormalsVao = GL.GenVertexArray();
            if (cache.NormalsVbo == 0) cache.NormalsVbo = GL.GenBuffer();

            bool useSharedVertices = cloth.SmoothMode == MeshShadingSmoothMode.Smooth;
            int triCount = data.Triangles.Count / 3;
            Vec3[]? unitTriNormals = null;
            Vec3[]? rawTriNormals = null;
            int[][]? vertToTrisFlat = null;
            float cosThreshold = (float)Math.Cos(cloth.AutoSmoothAngleDeg * Math.PI / 180.0);

            if (!useSharedVertices)
            {
                if (cache.RawTriNormalsScratch == null || cache.RawTriNormalsScratch.Length != triCount)
                    cache.RawTriNormalsScratch = new Vec3[triCount];
                if (cache.UnitTriNormalsScratch == null || cache.UnitTriNormalsScratch.Length != triCount)
                    cache.UnitTriNormalsScratch = new Vec3[triCount];
                rawTriNormals = cache.RawTriNormalsScratch;
                unitTriNormals = cache.UnitTriNormalsScratch;

                for (int t = 0; t < triCount; t++)
                {
                    Vec3 v0 = data.Vertices[data.Triangles[t * 3]];
                    Vec3 v1 = data.Vertices[data.Triangles[t * 3 + 1]];
                    Vec3 v2 = data.Vertices[data.Triangles[t * 3 + 2]];
                    Vec3 cross = Vec3.Cross(v1 - v0, v2 - v0);
                    rawTriNormals[t] = cross;
                    unitTriNormals[t] = cross.LengthSquared > 1e-10f ? cross.Normalized : Vec3.Forward;
                }

                if (data.TriangleToFace != null && data.TriangleToFace.Count == triCount && data.Faces != null && data.Faces.Count > 0)
                {
                    int fCount = data.Faces.Count;
                    if (cache.FaceNormSumScratch == null || cache.FaceNormSumScratch.Length != fCount)
                        cache.FaceNormSumScratch = new Vec3[fCount];
                    else
                        Array.Clear(cache.FaceNormSumScratch, 0, fCount);
                    var faceNormSum = cache.FaceNormSumScratch;

                    for (int t = 0; t < triCount; t++)
                    {
                        int fIdx = data.TriangleToFace[t];
                        if (fIdx >= 0 && fIdx < fCount)
                            faceNormSum[fIdx] = faceNormSum[fIdx] + rawTriNormals[t];
                    }
                    for (int t = 0; t < triCount; t++)
                    {
                        int fIdx = data.TriangleToFace[t];
                        if (fIdx >= 0 && fIdx < fCount && faceNormSum[fIdx].LengthSquared > 1e-10f)
                            unitTriNormals[t] = faceNormSum[fIdx].Normalized;
                    }
                }

                if (cloth.SmoothMode == MeshShadingSmoothMode.AutoSmooth)
                {
                    if (!cache.VertToTrisValid || cache.VertToTrisFlat == null || cache.VertToTrisFlat.Length != data.Vertices.Count)
                    {
                        var tmp = new List<int>[data.Vertices.Count];
                        for (int t = 0; t < triCount; t++)
                        {
                            for (int k = 0; k < 3; k++)
                            {
                                int vi = data.Triangles[t * 3 + k];
                                tmp[vi] ??= new List<int>(6);
                                tmp[vi].Add(t);
                            }
                        }
                        cache.VertToTrisFlat = new int[data.Vertices.Count][];
                        for (int v = 0; v < data.Vertices.Count; v++)
                            cache.VertToTrisFlat[v] = tmp[v] != null ? tmp[v].ToArray() : Array.Empty<int>();
                        cache.VertToTrisValid = true;
                    }
                    vertToTrisFlat = cache.VertToTrisFlat;

                    bool hasCrease = false;
                    for (int v = 0; v < vertToTrisFlat.Length && !hasCrease; v++)
                    {
                        int[] adj = vertToTrisFlat[v];
                        if (adj.Length <= 1) continue;
                        Vec3 refN = unitTriNormals[adj[0]];
                        for (int i = 1; i < adj.Length; i++)
                        {
                            if (Vec3.Dot(refN, unitTriNormals[adj[i]]) < cosThreshold)
                            {
                                hasCrease = true;
                                break;
                            }
                        }
                    }
                    if (!hasCrease)
                        useSharedVertices = true;
                }
            }

            int neededFloats = useSharedVertices ? data.Vertices.Count * 8 : triCount * 3 * 8;
            if (cache.VboScratch == null || cache.VboScratch.Length != neededFloats)
                cache.VboScratch = new float[neededFloats];
            float[] vboData = cache.VboScratch;

            if (useSharedVertices)
            {
                int vc = data.Vertices.Count;
                for (int i = 0; i < vc; i++)
                {
                    Vec3 p = data.Vertices[i];
                    Vec3 n = i < data.Normals.Count ? data.Normals[i] : Vec3.Forward;
                    Vec2 uv = i < data.UVs.Count ? data.UVs[i] : new Vec2(0.5f, 0.5f);
                    int o = i * 8;
                    vboData[o + 0] = p.X; vboData[o + 1] = p.Y; vboData[o + 2] = p.Z;
                    vboData[o + 3] = n.X; vboData[o + 4] = n.Y; vboData[o + 5] = n.Z;
                    vboData[o + 6] = uv.X; vboData[o + 7] = uv.Y;
                }
                cache.TriCornerMap = null;
            }
            else
            {
                int cornerCount = triCount * 3;
                if (cache.TriCornerMap == null || cache.TriCornerMap.Length != cornerCount)
                    cache.TriCornerMap = new int[cornerCount];

                for (int t = 0; t < triCount; t++)
                {
                    Vec3 baseNorm = unitTriNormals![t];
                    for (int k = 0; k < 3; k++)
                    {
                        int cIdx = t * 3 + k;
                        int vIdx = data.Triangles[cIdx];
                        Vec3 p = data.Vertices[vIdx];
                        Vec2 uv = vIdx < data.UVs.Count ? data.UVs[vIdx] : new Vec2(0.5f, 0.5f);

                        Vec3 finalN = baseNorm;
                        if (cloth.SmoothMode == MeshShadingSmoothMode.AutoSmooth && vertToTrisFlat != null)
                        {
                            Vec3 sum = Vec3.Zero;
                            int[] adj = vertToTrisFlat[vIdx];
                            for (int a = 0; a < adj.Length; a++)
                            {
                                int adjT = adj[a];
                                if (Vec3.Dot(baseNorm, unitTriNormals[adjT]) >= cosThreshold)
                                    sum = sum + rawTriNormals![adjT];
                            }
                            if (sum.LengthSquared > 1e-10f)
                                finalN = sum.Normalized;
                        }

                        int o = cIdx * 8;
                        vboData[o + 0] = p.X; vboData[o + 1] = p.Y; vboData[o + 2] = p.Z;
                        vboData[o + 3] = finalN.X; vboData[o + 4] = finalN.Y; vboData[o + 5] = finalN.Z;
                        vboData[o + 6] = uv.X; vboData[o + 7] = uv.Y;
                        cache.TriCornerMap[cIdx] = cIdx;
                    }
                }
            }

            GL.BindVertexArray(cache.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, cache.Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vboData.Length * sizeof(float), vboData, BufferUsageHint.StreamDraw);

            int stride = 8 * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));

            bool topologyChanged = cache.BuiltVertCount != data.Vertices.Count ||
                                   cache.BuiltTriCount != triCount ||
                                   cache.BuiltSubdivLevel != cloth.SubdivisionLevel ||
                                   cache.BuiltUsedSharedVertices != useSharedVertices ||
                                   !cache.SelectionEboValid;

            cache.BuiltSmoothMode = cloth.SmoothMode;
            cache.BuiltAutoSmoothDeg = cloth.AutoSmoothAngleDeg;
            cache.BuiltVertCount = data.Vertices.Count;
            cache.BuiltTriCount = triCount;
            cache.BuiltSubdivLevel = cloth.SubdivisionLevel;
            cache.BuiltUsedSharedVertices = useSharedVertices;
            cache.MeshDirty = false;
            if (topologyChanged)
                cache.SelectionDirty = true;
        }

        private static void UploadClothWireToGpu(ClothEntity cloth, ClothGpuCache cache)
        {
            var data = cloth.RenderSubdividedMesh ?? cloth.Mesh;
            int vCount = data.Vertices.Count;
            int triCount = data.Triangles.Count / 3;
            if (vCount == 0) { cache.WireIndexCount = 0; cache.WireDirty = false; return; }

            if (cache.WireScratch == null || cache.WireScratch.Length != vCount * 3)
                cache.WireScratch = new float[vCount * 3];
            float[] wirePos = cache.WireScratch;
            for (int i = 0; i < vCount; i++)
            {
                Vec3 p = data.Vertices[i];
                wirePos[i * 3 + 0] = p.X;
                wirePos[i * 3 + 1] = p.Y;
                wirePos[i * 3 + 2] = p.Z;
            }

            GL.BindVertexArray(cache.WireVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, cache.WireVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, wirePos.Length * sizeof(float), wirePos, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);

            if (!cache.WireEboValid)
            {
                var drawnEdges = new HashSet<long>();
                var wireIndices = new List<uint>(vCount * 4);
                void AddEdge(int a, int b)
                {
                    int min = Math.Min(a, b);
                    int max = Math.Max(a, b);
                    long key = ((long)min << 32) | (uint)max;
                    if (drawnEdges.Add(key))
                    {
                        wireIndices.Add((uint)a);
                        wireIndices.Add((uint)b);
                    }
                }

                if (data.Faces != null && data.Faces.Count > 0)
                {
                    for (int f = 0; f < data.Faces.Count; f++)
                    {
                        var face = data.Faces[f];
                        if (face == null || face.Count < 3) continue;
                        for (int k = 0; k < face.Count; k++)
                            AddEdge(face[k], face[(k + 1) % face.Count]);
                    }
                }
                else
                {
                    for (int t = 0; t < triCount; t++)
                    {
                        int i0 = data.Triangles[t * 3];
                        int i1 = data.Triangles[t * 3 + 1];
                        int i2 = data.Triangles[t * 3 + 2];
                        AddEdge(i0, i1); AddEdge(i1, i2); AddEdge(i2, i0);
                    }
                }

                var wireIdxArr = wireIndices.ToArray();
                cache.WireIndexCount = wireIdxArr.Length;
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, cache.WireEbo);
                GL.BufferData(BufferTarget.ElementArrayBuffer, wireIdxArr.Length * sizeof(uint), wireIdxArr, BufferUsageHint.DynamicDraw);
                cache.WireEboValid = true;
            }

            cache.WireDirty = false;
        }

        private static void UploadClothNormalsToGpu(ClothEntity cloth, ClothGpuCache cache)
        {
            var data = cloth.RenderSubdividedMesh ?? cloth.Mesh;
            var normLines = new List<float>(data.Vertices.Count * 3);
            for (int i = 0; i < data.Vertices.Count && i < data.Normals.Count; i += 2)
            {
                Vec3 p0 = data.Vertices[i];
                Vec3 p1 = p0 + data.Normals[i] * 0.032f;
                normLines.Add(p0.X); normLines.Add(p0.Y); normLines.Add(p0.Z);
                normLines.Add(p1.X); normLines.Add(p1.Y); normLines.Add(p1.Z);
            }
            var normArr = normLines.ToArray();
            cache.NormalsVertexCount = normArr.Length / 3;
            GL.BindVertexArray(cache.NormalsVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, cache.NormalsVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, normArr.Length * sizeof(float), normArr, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            cache.NormalsDirty = false;
        }

        private static void UploadClothSelectionIndicesToGpu(ClothEntity cloth, ClothGpuCache cache)
        {
            var data = cloth.RenderSubdividedMesh ?? cloth.Mesh;
            var tris = data.Triangles;
            int triCount = tris.Count / 3;
            bool hasSubdiv = cloth.RenderSubdividedMesh != null && data.SubdivParentFaces != null;
            bool hasFaceSel = cloth.SelectedFaces != null && cloth.SelectedFaces.Count > 0;
            bool hasTriSel = cloth.SelectedTriangles != null && cloth.SelectedTriangles.Count > 0;

            var unSel = new List<uint>(triCount * 3);
            var sel = (hasFaceSel || hasTriSel) ? new List<uint>(triCount) : null;

            for (int t = 0; t < triCount; t++)
            {
                bool isSel = false;
                if (hasSubdiv)
                {
                    if (hasFaceSel && data.TriangleToFace != null && t < data.TriangleToFace.Count)
                    {
                        int subFace = data.TriangleToFace[t];
                        if (subFace >= 0 && subFace < data.SubdivParentFaces!.Length)
                            isSel = cloth.SelectedFaces!.Contains(data.SubdivParentFaces[subFace]);
                    }
                }
                else
                {
                    isSel = hasTriSel && cloth.SelectedTriangles!.Contains(t);
                }

                var dst = isSel ? sel! : unSel;
                if (cache.TriCornerMap == null)
                {
                    dst.Add((uint)tris[t * 3]);
                    dst.Add((uint)tris[t * 3 + 1]);
                    dst.Add((uint)tris[t * 3 + 2]);
                }
                else
                {
                    dst.Add((uint)cache.TriCornerMap[t * 3]);
                    dst.Add((uint)cache.TriCornerMap[t * 3 + 1]);
                    dst.Add((uint)cache.TriCornerMap[t * 3 + 2]);
                }
            }

            var unSelArr = unSel.ToArray();
            cache.UnselectedIndexCount = unSelArr.Length;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, cache.UnselectedEbo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, unSelArr.Length * sizeof(uint), unSelArr, BufferUsageHint.DynamicDraw);

            if (sel != null && sel.Count > 0)
            {
                var selArr = sel.ToArray();
                cache.SelectedIndexCount = selArr.Length;
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, cache.SelectedEbo);
                GL.BufferData(BufferTarget.ElementArrayBuffer, selArr.Length * sizeof(uint), selArr, BufferUsageHint.DynamicDraw);
            }
            else
            {
                cache.SelectedIndexCount = 0;
            }

            cache.SelectionDirty = false;
            cache.SelectionEboValid = true;
        }

        public static bool TryHitMoveGizmo(Vec3 ro, Vec3 rd, Vec3 p, double camDistance, out GizmoAxis hitAxis, out Vec3 hitWorldPos)
        {
            hitAxis = GizmoAxis.None;
            hitWorldPos = Vec3.Zero;
            float arrowLen = (float)Math.Max(0.14, Math.Min(0.28, camDistance * 0.14));

            if (RaySphereDistance(ro, rd, p, 0.028f, out _))
            {
                hitAxis = GizmoAxis.FreeCenter;
                hitWorldPos = p;
                return true;
            }

            float bestDist = float.MaxValue;
            GizmoAxis bestAx = GizmoAxis.None;
            Vec3 bestPos = Vec3.Zero;
            void TestAxis(GizmoAxis ax, Vec3 dir)
            {
                float d = RaySegmentDistance(ro, rd, p + dir * 0.02f, p + dir * arrowLen, out float tRay);
                if (d < 0.022f && tRay < bestDist)
                {
                    bestDist = tRay;
                    bestAx = ax;
                    bestPos = ro + rd * tRay;
                }
            }

            TestAxis(GizmoAxis.X, Vec3.Right);
            TestAxis(GizmoAxis.Y, Vec3.Up);
            TestAxis(GizmoAxis.Z, Vec3.Forward);

            if (bestAx != GizmoAxis.None)
            {
                hitAxis = bestAx;
                hitWorldPos = bestPos;
                return true;
            }
            return false;
        }

        private void DrawSphere(Vec3 center, float radius, Vector3 color)
        {
            Matrix4 model = Matrix4.CreateScale(radius) * Matrix4.CreateTranslation(center.X, center.Y, center.Z);
            GL.UniformMatrix4(GL.GetUniformLocation(_solidProgram, "uModel"), false, ref model);
            GL.Uniform3(GL.GetUniformLocation(_solidProgram, "uColor"), color);
            GL.BindVertexArray(_unitSphereVao);
            GL.DrawElements(PrimitiveType.Triangles, _unitSphereIdxCount, DrawElementsType.UnsignedInt, 0);
        }

        private void DrawCylinderSegment(Vec3 a, Vec3 b, float radius, Vector3 color)
        {
            Vec3 diff = b - a;
            float len = diff.Length;
            if (len < 1e-5f) return;

            Vec3 dir = diff / len;
            Matrix4 rot = RotationFromUpTo(new Vector3(dir.X, dir.Y, dir.Z));
            Matrix4 model = Matrix4.CreateScale(radius, len, radius) * rot * Matrix4.CreateTranslation(a.X, a.Y, a.Z);

            GL.UniformMatrix4(GL.GetUniformLocation(_solidProgram, "uModel"), false, ref model);
            GL.Uniform3(GL.GetUniformLocation(_solidProgram, "uColor"), color);
            GL.BindVertexArray(_unitCylinderVao);
            GL.DrawElements(PrimitiveType.Triangles, _unitCylinderIdxCount, DrawElementsType.UnsignedInt, 0);
        }

        private void DrawMoveGizmo(Vec3 pivot, float arrowLen, GizmoAxis activeAxis, Matrix4 viewProj, Vector3 camPos)
        {
            GL.UseProgram(_solidProgram);
            GL.UniformMatrix4(GL.GetUniformLocation(_solidProgram, "uViewProj"), false, ref viewProj);
            GL.Uniform3(GL.GetUniformLocation(_solidProgram, "uCamPos"), camPos);
            GL.Uniform1(GL.GetUniformLocation(_solidProgram, "uUnlit"), 1.0f);

            Vector3 activeCol = new(1.0f, 0.88f, 0.25f);
            void DrawAxis(GizmoAxis ax, Vec3 dir, Vector3 col)
            {
                Matrix4 rot = RotationFromUpTo(new Vector3(dir.X, dir.Y, dir.Z));
                Matrix4 model = Matrix4.CreateScale(arrowLen) * rot * Matrix4.CreateTranslation(pivot.X, pivot.Y, pivot.Z);
                GL.UniformMatrix4(GL.GetUniformLocation(_solidProgram, "uModel"), false, ref model);
                GL.Uniform3(GL.GetUniformLocation(_solidProgram, "uColor"), activeAxis == ax ? activeCol : col);
                GL.BindVertexArray(_unitArrowVao);
                GL.DrawElements(PrimitiveType.Triangles, _unitArrowIdxCount, DrawElementsType.UnsignedInt, 0);
            }

            DrawAxis(GizmoAxis.X, Vec3.Right, new Vector3(0.92f, 0.31f, 0.35f));
            DrawAxis(GizmoAxis.Y, Vec3.Up, new Vector3(0.47f, 0.84f, 0.37f));
            DrawAxis(GizmoAxis.Z, Vec3.Forward, new Vector3(0.29f, 0.65f, 0.96f));

            DrawSphere(pivot, 0.022f, activeAxis == GizmoAxis.FreeCenter ? activeCol : new Vector3(0.96f, 0.96f, 0.98f));
        }

        private void DrawRadiusRing(Vec3 center, Vec3 normalDir, float radius, Matrix4 viewProj)
        {
            if (radius < 0.005f) return;
            Vec3 n = normalDir.LengthSquared > 1e-6f ? normalDir.Normalized : Vec3.Forward;
            Matrix4 rot = RotationFromUpTo(new Vector3(n.X, n.Y, n.Z));
            Matrix4 model = Matrix4.CreateScale(radius) * rot * Matrix4.CreateTranslation(center.X, center.Y, center.Z);
            Matrix4 mvp = model * viewProj;

            GL.UseProgram(_lineProgram);
            GL.UniformMatrix4(GL.GetUniformLocation(_lineProgram, "uViewProj"), false, ref mvp);
            GL.Uniform1(GL.GetUniformLocation(_lineProgram, "uDepthBias"), -0.0003f);
            GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), new Vector3(0.37f, 0.88f, 0.53f));
            GL.LineWidth(1.8f);
            GL.BindVertexArray(_unitRingVao);
            GL.DrawArrays(PrimitiveType.LineLoop, 0, _unitRingVertCount);
        }

        private void DrawRegionOutline(RegionSelectionSpec region, Matrix4 viewProj)
        {
            Vec3 origin = region.StartPoint;
            Vec3 n = region.PlaneNormal.LengthSquared > 1e-6f ? region.PlaneNormal.Normalized : Vec3.Forward;
            Vec3 upRef = Math.Abs(Vec3.Dot(n, Vec3.Up)) > 0.9f ? Vec3.Right : Vec3.Up;
            Vec3 uAxis = Vec3.Cross(upRef, n).Normalized;
            Vec3 vAxis = Vec3.Cross(n, uAxis).Normalized;

            Vec3 rel = region.DragPoint - origin;
            Vec2 d2 = new Vec2(Vec3.Dot(rel, uAxis), Vec3.Dot(rel, vAxis));
            float radius = d2.Length;
            if (radius < 0.005f) return;

            var pts = new List<Vec3>();
            Vec3 lift = n * 0.006f;

            switch (region.Shape)
            {
                case RegionShapeMode.Box:
                    float minX = Math.Min(0f, d2.X), maxX = Math.Max(0f, d2.X);
                    float minY = Math.Min(0f, d2.Y), maxY = Math.Max(0f, d2.Y);
                    pts.Add(origin + uAxis * minX + vAxis * minY + lift);
                    pts.Add(origin + uAxis * maxX + vAxis * minY + lift);
                    pts.Add(origin + uAxis * maxX + vAxis * maxY + lift);
                    pts.Add(origin + uAxis * minX + vAxis * maxY + lift);
                    break;
                case RegionShapeMode.Circle:
                    for (int i = 0; i < 48; i++)
                    {
                        float a = 2f * (float)Math.PI * i / 48f;
                        pts.Add(origin + uAxis * ((float)Math.Cos(a) * radius) + vAxis * ((float)Math.Sin(a) * radius) + lift);
                    }
                    break;
                case RegionShapeMode.Cone:
                    float baseAng = (float)Math.Atan2(d2.Y, d2.X);
                    float halfRad = region.ConeHalfAngleDeg * (float)Math.PI / 180f;
                    pts.Add(origin + lift);
                    for (int i = 0; i <= 24; i++)
                    {
                        float a = baseAng - halfRad + ((float)i / 24f) * (2f * halfRad);
                        pts.Add(origin + uAxis * ((float)Math.Cos(a) * radius) + vAxis * ((float)Math.Sin(a) * radius) + lift);
                    }
                    break;
            }

            if (pts.Count < 2) return;
            var arr = new float[pts.Count * 3];
            for (int i = 0; i < pts.Count; i++)
            {
                arr[i * 3 + 0] = pts[i].X;
                arr[i * 3 + 1] = pts[i].Y;
                arr[i * 3 + 2] = pts[i].Z;
            }

            GL.UseProgram(_lineProgram);
            GL.UniformMatrix4(GL.GetUniformLocation(_lineProgram, "uViewProj"), false, ref viewProj);
            GL.Uniform1(GL.GetUniformLocation(_lineProgram, "uDepthBias"), -0.0008f);
            GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), new Vector3(1.0f, 0.88f, 0.31f));
            GL.LineWidth(2.4f);

            GL.BindVertexArray(_dynamicOutlineVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _dynamicOutlineVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.DrawArrays(PrimitiveType.LineLoop, 0, pts.Count);
        }

        private void DrawBorderPolyline(IReadOnlyList<Vec3> verts, ClothBorderEdge border, Vector3 color, float lineWidth, Matrix4 viewProj)
        {
            var chain = border.VertexIndices;
            if (chain.Count < 2) return;
            var arr = new float[chain.Count * 3];
            for (int i = 0; i < chain.Count; i++)
            {
                int vi = chain[i];
                if (vi < 0 || vi >= verts.Count) return;
                arr[i * 3 + 0] = verts[vi].X;
                arr[i * 3 + 1] = verts[vi].Y;
                arr[i * 3 + 2] = verts[vi].Z;
            }

            GL.UseProgram(_lineProgram);
            GL.UniformMatrix4(GL.GetUniformLocation(_lineProgram, "uViewProj"), false, ref viewProj);
            GL.Uniform1(GL.GetUniformLocation(_lineProgram, "uDepthBias"), -0.0012f);
            GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), color);
            GL.LineWidth(lineWidth);

            GL.BindVertexArray(_dynamicOutlineVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _dynamicOutlineVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.DrawArrays(border.IsClosedLoop ? PrimitiveType.LineLoop : PrimitiveType.LineStrip, 0, chain.Count);
            GL.LineWidth(1.0f);
        }

        private static Matrix4 RotationFromUpTo(Vector3 targetDir)
        {
            Vector3 up = Vector3.UnitY;
            Vector3 d = Vector3.Normalize(targetDir);
            float dot = Vector3.Dot(up, d);
            if (dot > 0.9999f) return Matrix4.Identity;
            if (dot < -0.9999f) return Matrix4.CreateRotationX((float)Math.PI);

            Vector3 axis = Vector3.Normalize(Vector3.Cross(up, d));
            float angle = (float)Math.Acos(Math.Clamp(dot, -1f, 1f));
            return Matrix4.CreateFromAxisAngle(axis, angle);
        }

        #region Shader & Primitive Setup

        private void CompileShaders()
        {
            const string clothVs = @"
#version 330 core
layout (location = 0) in vec3 aPos;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aUv;

uniform mat4 uViewProj;

out vec3 vWorldPos;
out vec3 vNormal;
out vec2 vUv;

void main()
{
    vWorldPos = aPos;
    vNormal = aNormal;
    vUv = aUv;
    gl_Position = uViewProj * vec4(aPos, 1.0);
}";

            const string clothFs = @"
#version 330 core
in vec3 vWorldPos;
in vec3 vNormal;
in vec2 vUv;

uniform vec3 uCamPos;
uniform vec3 uBaseColor;
uniform float uIsSelectedCloth;
uniform float uIsSelectedRegion;
uniform int uShadingMode;

out vec4 FragColor;

void main()
{
    vec3 N = normalize(vNormal);
    if (!gl_FrontFacing) N = -N;

    vec3 col = uBaseColor;

    // Procedural anti-aliased grid (Blender-style cloth grid texture on GPU)
    if (uShadingMode == 2)
    {
        vec2 gUv = vUv * 8.0;
        vec2 fw = max(fwidth(gUv), vec2(0.0005));
        vec2 distToEdge = min(fract(gUv), 1.0 - fract(gUv));
        vec2 lineAlpha = 1.0 - smoothstep(vec2(0.0), fw * 1.4, distToEdge);
        float majorGrid = max(lineAlpha.x, lineAlpha.y);

        float border = (vUv.x < 0.012 || vUv.x > 0.988 || vUv.y < 0.012 || vUv.y > 0.988) ? 1.0 : 0.0;
        float weave = sin((vUv.x + vUv.y) * 280.0) * 0.025;

        col *= (1.0 + weave);
        col = mix(col, min(col * 1.24 + vec3(0.06), vec3(1.0)), majorGrid * 0.78);
        col = mix(col, min(col * 1.35 + vec3(0.08), vec3(1.0)), border * 0.85);
    }

    vec3 L1 = normalize(vec3(0.40, 0.50, 0.75));
    vec3 L2 = normalize(vec3(-0.50, -0.30, -0.70));
    float diff1 = max(dot(N, L1), 0.0);
    float diff2 = max(dot(N, L2), 0.0) * 0.45;
    float hemi = 0.5 + 0.5 * N.y;
    vec3 ambient = mix(vec3(0.32, 0.35, 0.42), vec3(0.46, 0.50, 0.56), hemi);

    vec3 lit = col * (ambient + diff1 * 0.72 + diff2 * 0.48);

    if (uShadingMode == 1) // SolidClay soft highlight
    {
        vec3 V = normalize(uCamPos - vWorldPos);
        vec3 H = normalize(L1 + V);
        float spec = pow(max(dot(N, H), 0.0), 36.0) * 0.22;
        lit += vec3(spec);
    }

    if (uIsSelectedRegion > 0.5)
        lit += vec3(0.24, 0.18, 0.03);
    else if (uIsSelectedCloth > 0.5)
        lit += vec3(0.05, 0.08, 0.13);

    FragColor = vec4(clamp(lit, 0.0, 1.0), 1.0);
}";

            const string solidVs = @"
#version 330 core
layout (location = 0) in vec3 aPos;
layout (location = 1) in vec3 aNormal;

uniform mat4 uViewProj;
uniform mat4 uModel;

out vec3 vNormal;

void main()
{
    vNormal = mat3(uModel) * aNormal;
    gl_Position = uViewProj * uModel * vec4(aPos, 1.0);
}";

            const string solidFs = @"
#version 330 core
in vec3 vNormal;

uniform vec3 uColor;
uniform float uUnlit;

out vec4 FragColor;

void main()
{
    vec3 N = normalize(vNormal);
    float diff = max(dot(N, normalize(vec3(0.40, 0.50, 0.75))), 0.0);
    vec3 lit = uUnlit > 0.5
        ? uColor * (0.72 + 0.28 * diff)
        : uColor * (0.45 + 0.58 * diff);
    FragColor = vec4(clamp(lit, 0.0, 1.0), 1.0);
}";

            const string lineVs = @"
#version 330 core
layout (location = 0) in vec3 aPos;

uniform mat4 uViewProj;
uniform float uDepthBias;

void main()
{
    vec4 p = uViewProj * vec4(aPos, 1.0);
    p.z += uDepthBias * p.w;
    gl_Position = p;
}";

            const string lineFs = @"
#version 330 core
uniform vec3 uColor;
out vec4 FragColor;
void main() { FragColor = vec4(uColor, 1.0); }";

            _clothProgram = LinkProgram(clothVs, clothFs);
            _solidProgram = LinkProgram(solidVs, solidFs);
            _lineProgram = LinkProgram(lineVs, lineFs);
        }

        private static int LinkProgram(string vsSrc, string fsSrc)
        {
            int vs = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vs, vsSrc);
            GL.CompileShader(vs);

            int fs = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fs, fsSrc);
            GL.CompileShader(fs);

            int prog = GL.CreateProgram();
            GL.AttachShader(prog, vs);
            GL.AttachShader(prog, fs);
            GL.LinkProgram(prog);

            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
            return prog;
        }

        private void BuildFloorGridAndAxes()
        {
            int halfLines = 8;
            float step = 0.14f;
            float extent = halfLines * step;
            float y = -0.48f;

            var grid = new List<float>();
            for (int i = -halfLines; i <= halfLines; i++)
            {
                if (i == 0) continue;
                float p = i * step;
                grid.Add(-extent); grid.Add(y); grid.Add(p);
                grid.Add(extent); grid.Add(y); grid.Add(p);
                grid.Add(p); grid.Add(y); grid.Add(-extent);
                grid.Add(p); grid.Add(y); grid.Add(extent);
            }

            _floorGridCount = grid.Count / 3;
            _floorGridVao = UploadLineBuffer(grid.ToArray(), out _floorGridVbo);
            _axisXVao = UploadLineBuffer(new[] { -extent, y, 0f, extent, y, 0f }, out _axisXVbo);
            _axisZVao = UploadLineBuffer(new[] { 0f, y, -extent, 0f, y, extent }, out _axisZVbo);
        }

        private static int UploadLineBuffer(float[] verts, out int vbo)
        {
            int vao = GL.GenVertexArray();
            vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            return vao;
        }

        private void BuildUnitSphere(int slices, int stacks)
        {
            var verts = new List<float>();
            var idx = new List<uint>();

            for (int stack = 0; stack <= stacks; stack++)
            {
                float phi = (float)Math.PI * stack / stacks;
                float y = (float)Math.Cos(phi);
                float r = (float)Math.Sin(phi);
                for (int slice = 0; slice <= slices; slice++)
                {
                    float theta = 2f * (float)Math.PI * slice / slices;
                    float x = r * (float)Math.Sin(theta);
                    float z = r * (float)Math.Cos(theta);
                    verts.Add(x); verts.Add(y); verts.Add(z);
                    verts.Add(x); verts.Add(y); verts.Add(z);
                }
            }

            int stride = slices + 1;
            for (int stack = 0; stack < stacks; stack++)
            {
                for (int slice = 0; slice < slices; slice++)
                {
                    uint i0 = (uint)(stack * stride + slice);
                    uint i1 = i0 + 1;
                    uint i2 = (uint)((stack + 1) * stride + slice);
                    uint i3 = i2 + 1;
                    idx.Add(i0); idx.Add(i2); idx.Add(i1);
                    idx.Add(i1); idx.Add(i2); idx.Add(i3);
                }
            }

            UploadSolidVao(verts.ToArray(), idx.ToArray(), out _unitSphereVao, out _unitSphereVbo, out _unitSphereEbo, out _unitSphereIdxCount);
        }

        private void BuildUnitCylinder(int segments)
        {
            var verts = new List<float>();
            var idx = new List<uint>();

            for (int ring = 0; ring <= 1; ring++)
            {
                float y = ring == 0 ? 0f : 1f;
                for (int s = 0; s <= segments; s++)
                {
                    float ang = 2f * (float)Math.PI * s / segments;
                    float x = (float)Math.Cos(ang);
                    float z = (float)Math.Sin(ang);
                    verts.Add(x); verts.Add(y); verts.Add(z);
                    verts.Add(x); verts.Add(0f); verts.Add(z);
                }
            }

            int stride = segments + 1;
            for (int s = 0; s < segments; s++)
            {
                uint i0 = (uint)s, i1 = i0 + 1;
                uint i2 = (uint)(stride + s), i3 = i2 + 1;
                idx.Add(i0); idx.Add(i2); idx.Add(i1);
                idx.Add(i1); idx.Add(i2); idx.Add(i3);
            }

            UploadSolidVao(verts.ToArray(), idx.ToArray(), out _unitCylinderVao, out _unitCylinderVbo, out _unitCylinderEbo, out _unitCylinderIdxCount);
        }

        private void BuildUnitArrow(int segments)
        {
            // Normalized unit arrow along +Y (total length = 1.0)
            var verts = new List<float>();
            var idx = new List<uint>();
            float shaftR = 0.028f;
            float headR = 0.082f;
            float shaftH = 0.78f;

            // Shaft
            for (int ring = 0; ring <= 1; ring++)
            {
                float y = ring == 0 ? 0f : shaftH;
                for (int s = 0; s <= segments; s++)
                {
                    float ang = 2f * (float)Math.PI * s / segments;
                    float x = (float)Math.Cos(ang), z = (float)Math.Sin(ang);
                    verts.Add(x * shaftR); verts.Add(y); verts.Add(z * shaftR);
                    verts.Add(x); verts.Add(0f); verts.Add(z);
                }
            }
            int stride = segments + 1;
            for (int s = 0; s < segments; s++)
            {
                uint i0 = (uint)s, i1 = i0 + 1, i2 = (uint)(stride + s), i3 = i2 + 1;
                idx.Add(i0); idx.Add(i2); idx.Add(i1);
                idx.Add(i1); idx.Add(i2); idx.Add(i3);
            }

            // Cone Head
            uint tipIdx = (uint)(verts.Count / 6);
            verts.Add(0f); verts.Add(1.0f); verts.Add(0f);
            verts.Add(0f); verts.Add(1f); verts.Add(0f);

            uint ringBase = (uint)(verts.Count / 6);
            for (int s = 0; s <= segments; s++)
            {
                float ang = 2f * (float)Math.PI * s / segments;
                float x = (float)Math.Cos(ang), z = (float)Math.Sin(ang);
                verts.Add(x * headR); verts.Add(shaftH); verts.Add(z * headR);
                verts.Add(x); verts.Add(0.3f); verts.Add(z);
            }
            for (int s = 0; s < segments; s++)
            {
                idx.Add(ringBase + (uint)s);
                idx.Add(tipIdx);
                idx.Add(ringBase + (uint)s + 1);
            }

            UploadSolidVao(verts.ToArray(), idx.ToArray(), out _unitArrowVao, out _unitArrowVbo, out _unitArrowEbo, out _unitArrowIdxCount);
        }

        private void BuildUnitRing(int segments)
        {
            var pts = new float[segments * 3];
            for (int i = 0; i < segments; i++)
            {
                float a = 2f * (float)Math.PI * i / segments;
                pts[i * 3 + 0] = (float)Math.Cos(a);
                pts[i * 3 + 1] = 0f;
                pts[i * 3 + 2] = (float)Math.Sin(a);
            }
            _unitRingVertCount = segments;
            _unitRingVao = UploadLineBuffer(pts, out _unitRingVbo);
        }

        private void DrawFloorGridAndAxes(Matrix4 viewProj)
        {
            GL.UseProgram(_lineProgram);
            GL.UniformMatrix4(GL.GetUniformLocation(_lineProgram, "uViewProj"), false, ref viewProj);
            GL.Uniform1(GL.GetUniformLocation(_lineProgram, "uDepthBias"), 0f);
            GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), new Vector3(0.26f, 0.26f, 0.28f));
            GL.LineWidth(1.0f);
            GL.BindVertexArray(_floorGridVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, _floorGridCount);

            GL.LineWidth(2.0f);
            GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), new Vector3(0.84f, 0.28f, 0.31f));
            GL.BindVertexArray(_axisXVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, 2);

            GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), new Vector3(0.28f, 0.65f, 0.43f));
            GL.BindVertexArray(_axisZVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, 2);
            GL.LineWidth(1.0f);
        }

        private static void UploadSolidVao(float[] verts, uint[] indices, out int vao, out int vbo, out int ebo, out int idxCount)
        {
            vao = GL.GenVertexArray();
            vbo = GL.GenBuffer();
            ebo = GL.GenBuffer();
            idxCount = indices.Length;

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

            int stride = 6 * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var c in _clothCaches.Values)
                c.Dispose();
            _clothCaches.Clear();
            DisposeCharacterResources();

            if (_initialized)
            {
                GL.DeleteProgram(_clothProgram);
                GL.DeleteProgram(_solidProgram);
                GL.DeleteProgram(_lineProgram);
                GL.DeleteVertexArray(_floorGridVao); GL.DeleteBuffer(_floorGridVbo);
                GL.DeleteVertexArray(_axisXVao); GL.DeleteBuffer(_axisXVbo);
                GL.DeleteVertexArray(_axisZVao); GL.DeleteBuffer(_axisZVbo);
                GL.DeleteVertexArray(_unitSphereVao); GL.DeleteBuffer(_unitSphereVbo); GL.DeleteBuffer(_unitSphereEbo);
                GL.DeleteVertexArray(_unitCylinderVao); GL.DeleteBuffer(_unitCylinderVbo); GL.DeleteBuffer(_unitCylinderEbo);
                GL.DeleteVertexArray(_unitArrowVao); GL.DeleteBuffer(_unitArrowVbo); GL.DeleteBuffer(_unitArrowEbo);
                GL.DeleteVertexArray(_unitRingVao); GL.DeleteBuffer(_unitRingVbo);
                GL.DeleteVertexArray(_dynamicOutlineVao); GL.DeleteBuffer(_dynamicOutlineVbo);
            }
        }
    }
}
