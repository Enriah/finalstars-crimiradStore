using System;
using System.Collections.Generic;
using CrimiradTailoring.Core.Engine;
using CrimiradTailoring.Core.Models;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace CrimiradTailoring.Studio.Rendering
{
    public sealed partial class GlClothViewportRenderer
    {
        // Offscreen Sub-Viewport (PiP) Framebuffer
        private int _subVpFbo;
        private int _subVpColorTex;
        private int _subVpDepthRb;
        private int _subVpW;
        private int _subVpH;
        private bool _subVpDirty = true;

        // Character Model GPU Cache
        private int _charVao;
        private int _charVbo;
        private int _charEbo;
        private int _charIndexCount;
        private bool _charMeshDirty = true;

        // Character-Space Attached Cloths GPU Cache (riêng với Fabric để hỗ trợ Occlusion Culling EBO)
        private sealed class CharClothGpuCache : IDisposable
        {
            public int Vao, Vbo, Ebo;
            public int WireVao, WireVbo, WireEbo;
            public int VisibleIndexCount;
            public int WireIndexCount;
            public bool Dirty = true;

            public void Dispose()
            {
                if (Vao != 0) GL.DeleteVertexArray(Vao);
                if (Vbo != 0) GL.DeleteBuffer(Vbo);
                if (Ebo != 0) GL.DeleteBuffer(Ebo);
                if (WireVao != 0) GL.DeleteVertexArray(WireVao);
                if (WireVbo != 0) GL.DeleteBuffer(WireVbo);
                if (WireEbo != 0) GL.DeleteBuffer(WireEbo);
                Vao = Vbo = Ebo = WireVao = WireVbo = WireEbo = 0;
            }
        }

        private readonly Dictionary<Guid, CharClothGpuCache> _charClothCaches = new();

        public IntPtr SubViewportTextureId => (IntPtr)_subVpColorTex;

        public void MarkCharacterModelDirty()
        {
            _charMeshDirty = true;
            _subVpDirty = true;
        }

        public void MarkCharacterClothDirty(Guid clothId, bool markSubViewport = false)
        {
            if (_charClothCaches.TryGetValue(clothId, out var c))
                c.Dirty = true;
            if (markSubViewport) _subVpDirty = true;
        }

        public void MarkAllCharacterClothsDirty(bool markSubViewport = false)
        {
            foreach (var c in _charClothCaches.Values)
                c.Dirty = true;
            if (markSubViewport) _subVpDirty = true;
        }

        public void MarkSubViewportDirty() => _subVpDirty = true;

        public void EnsureSubViewportRendered(
            int texWidth,
            int texHeight,
            WorkspaceViewportKind activeMainViewport,
            double fabricYaw, double fabricPitch, double fabricDist, Vec3 fabricTarget,
            double charYaw, double charPitch, double charDist, Vec3 charTarget,
            CharacterModelEntity? characterModel,
            IReadOnlyList<CharacterAnchorPoint> anchors,
            IReadOnlyList<RulerMeasurement> rulers,
            RulerMeasurement? activeDraftRuler,
            CharacterAnchorPoint? selectedAnchor,
            RulerMeasurement? selectedRuler,
            List<ClothEntity> cloths,
            List<ClothConnection> connections,
            ClothEntity? selectedCloth,
            ControlPoint? selectedPoint,
            ControlEdge? selectedEdge,
            ViewportShadingMode shadingMode,
            bool showWireOverlay,
            bool showFloorGrid,
            bool showElements)
        {
            EnsureInitialized();
            if (!_initialized) return;

            int tw = Math.Max(64, texWidth);
            int th = Math.Max(64, texHeight);
            bool resized = tw != _subVpW || th != _subVpH || _subVpFbo == 0;
            if (resized)
                EnsureSubViewportFbo(tw, th);

            if (!_subVpDirty && !resized)
                return;

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _subVpFbo);
            if (activeMainViewport == WorkspaceViewportKind.FabricViewport)
            {
                // Sub-Viewport đang preview CharacterViewport
                RenderCharacterFrame(
                    tw, th, charYaw, charPitch, charDist, charTarget,
                    characterModel, anchors, rulers, activeDraftRuler, selectedAnchor, selectedRuler,
                    cloths, selectedCloth, selectedPoint, selectedEdge,
                    GizmoAxis.None, shadingMode, showWireOverlay: false, showFloorGrid, showElements, showMoveGizmo: false,
                    clearSubViewportColor: true);
            }
            else
            {
                // Sub-Viewport đang preview FabricViewport
                RenderFrame(
                    tw, th, fabricYaw, fabricPitch, fabricDist, fabricTarget,
                    cloths, connections, selectedCloth, selectedPoint, selectedEdge,
                    null, null, null, null, null, GizmoAxis.None,
                    shadingMode, showWireOverlay, showNormals: false, showFloorGrid, showElements, showInfluence: false, showMoveGizmo: false);
            }
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _subVpDirty = false;
        }

        private void EnsureSubViewportFbo(int w, int h)
        {
            if (_subVpFbo != 0)
            {
                GL.DeleteFramebuffer(_subVpFbo);
                GL.DeleteTexture(_subVpColorTex);
                GL.DeleteRenderbuffer(_subVpDepthRb);
            }

            _subVpW = w;
            _subVpH = h;
            _subVpFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _subVpFbo);

            _subVpColorTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _subVpColorTex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _subVpColorTex, 0);

            _subVpDepthRb = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _subVpDepthRb);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _subVpDepthRb);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        public void RenderCharacterFrame(
            int pixelWidth,
            int pixelHeight,
            double camYawDeg,
            double camPitchDeg,
            double camDistance,
            Vec3 camTarget,
            CharacterModelEntity? characterModel,
            IReadOnlyList<CharacterAnchorPoint> anchors,
            IReadOnlyList<RulerMeasurement> rulers,
            RulerMeasurement? activeDraftRuler,
            CharacterAnchorPoint? selectedAnchor,
            RulerMeasurement? selectedRuler,
            List<ClothEntity> cloths,
            ClothEntity? selectedCloth,
            ControlPoint? selectedPoint,
            ControlEdge? selectedEdge,
            GizmoAxis activeGizmoAxis,
            ViewportShadingMode shadingMode,
            bool showWireOverlay,
            bool showFloorGrid,
            bool showElements,
            bool showMoveGizmo,
            bool clearSubViewportColor = false)
        {
            EnsureInitialized();
            if (!_initialized) return;

            int w = Math.Max(1, pixelWidth);
            int h = Math.Max(1, pixelHeight);
            GL.Viewport(0, 0, w, h);

            if (clearSubViewportColor)
                GL.ClearColor(0.095f, 0.095f, 0.100f, 1.0f);
            else
                GL.ClearColor(0.114f, 0.114f, 0.118f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            ComputeCameraMatrices(w, h, camYawDeg, camPitchDeg, camDistance, camTarget, out Vector3 camPos, out Matrix4 viewProj);

            if (showFloorGrid)
                DrawFloorGridAndAxes(viewProj);

            // 1. Render Model Nhân Vật
            if (characterModel != null && characterModel.IsVisible && characterModel.Mesh.Vertices.Count > 0)
            {
                if (_charMeshDirty || _charVao == 0)
                    UploadCharacterModelToGpu(characterModel);

                if (_charVao != 0 && _charIndexCount > 0)
                {
                    GL.UseProgram(_clothProgram);
                    GL.UniformMatrix4(GL.GetUniformLocation(_clothProgram, "uViewProj"), false, ref viewProj);
                    GL.Uniform3(GL.GetUniformLocation(_clothProgram, "uCamPos"), camPos);
                    GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uShadingMode"), 1); // Clay mượt cho cơ thể
                    GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uIsSelectedCloth"), 0.0f);
                    GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uIsSelectedRegion"), 0.0f);
                    GL.Uniform3(GL.GetUniformLocation(_clothProgram, "uBaseColor"), new Vector3(0.70f, 0.66f, 0.63f));

                    GL.BindVertexArray(_charVao);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, _charEbo);
                    GL.DrawElements(PrimitiveType.Triangles, _charIndexCount, DrawElementsType.UnsignedInt, 0);
                }
            }

            // 2. Render các mảnh vải đã gắn lên CharacterViewport (tôn trọng Layers & Occlusion Culling!)
            foreach (var cloth in cloths)
            {
                if (!cloth.IsVisible || !cloth.CharacterState.IsAttached || cloth.CharacterFittedMesh == null)
                    continue;

                if (!_charClothCaches.TryGetValue(cloth.Id, out var cCache))
                {
                    cCache = new CharClothGpuCache();
                    _charClothCaches[cloth.Id] = cCache;
                }

                if (cCache.Dirty)
                    UploadFittedClothToGpu(cloth, cCache);

                bool isSel = selectedCloth != null && selectedCloth.Id == cloth.Id;
                if (shadingMode != ViewportShadingMode.WireframeOnly && cCache.Vao != 0 && cCache.VisibleIndexCount > 0)
                {
                    GL.UseProgram(_clothProgram);
                    GL.UniformMatrix4(GL.GetUniformLocation(_clothProgram, "uViewProj"), false, ref viewProj);
                    GL.Uniform3(GL.GetUniformLocation(_clothProgram, "uCamPos"), camPos);
                    GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uShadingMode"), shadingMode == ViewportShadingMode.SolidClay ? 1 : 2);
                    GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uIsSelectedCloth"), isSel ? 1.0f : 0.0f);
                    GL.Uniform1(GL.GetUniformLocation(_clothProgram, "uIsSelectedRegion"), 0.0f);
                    GL.Uniform3(GL.GetUniformLocation(_clothProgram, "uBaseColor"),
                        new Vector3(cloth.ClothColor.R / 255f, cloth.ClothColor.G / 255f, cloth.ClothColor.B / 255f));

                    GL.BindVertexArray(cCache.Vao);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, cCache.Ebo);
                    GL.DrawElements(PrimitiveType.Triangles, cCache.VisibleIndexCount, DrawElementsType.UnsignedInt, 0);
                }

                if ((showWireOverlay || shadingMode == ViewportShadingMode.WireframeOnly) && cCache.WireVao != 0 && cCache.WireIndexCount > 0)
                {
                    GL.UseProgram(_lineProgram);
                    GL.UniformMatrix4(GL.GetUniformLocation(_lineProgram, "uViewProj"), false, ref viewProj);
                    GL.Uniform1(GL.GetUniformLocation(_lineProgram, "uDepthBias"), -0.0005f);
                    GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), new Vector3(0.84f, 0.88f, 0.94f));
                    GL.LineWidth(1.1f);
                    GL.BindVertexArray(cCache.WireVao);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, cCache.WireEbo);
                    GL.DrawElements(PrimitiveType.Lines, cCache.WireIndexCount, DrawElementsType.UnsignedInt, 0);
                }
            }

            // 3. Điểm Neo (Anchor Points) & Dây thước đo Ruler trên Model
            if (showElements)
            {
                GL.UseProgram(_solidProgram);
                GL.UniformMatrix4(GL.GetUniformLocation(_solidProgram, "uViewProj"), false, ref viewProj);
                GL.Uniform3(GL.GetUniformLocation(_solidProgram, "uCamPos"), camPos);
                GL.Uniform1(GL.GetUniformLocation(_solidProgram, "uUnlit"), 0.0f);

                for (int i = 0; i < anchors.Count; i++)
                {
                    var a = anchors[i];
                    bool sel = selectedAnchor != null && selectedAnchor.Id == a.Id;
                    DrawSphere(a.Position, sel ? 0.0115f : 0.0080f, sel ? new Vector3(1.0f, 0.82f, 0.22f) : new Vector3(0.95f, 0.56f, 0.16f));
                }

                void DrawRulerPoints(RulerMeasurement r, bool highlight)
                {
                    if (!r.IsVisible) return;
                    Vector3 ptCol = highlight ? new Vector3(1.0f, 0.86f, 0.26f) : new Vector3(0.30f, 0.94f, 0.65f);
                    for (int k = 0; k < r.ControlPoints.Count; k++)
                        DrawSphere(r.ControlPoints[k], highlight ? 0.0075f : 0.0058f, ptCol);
                }

                for (int i = 0; i < rulers.Count; i++)
                    DrawRulerPoints(rulers[i], selectedRuler != null && selectedRuler.Id == rulers[i].Id);
                if (activeDraftRuler != null)
                    DrawRulerPoints(activeDraftRuler, highlight: true);

                // Vẽ đường thước cong ôm sát bề mặt
                for (int i = 0; i < rulers.Count; i++)
                {
                    var r = rulers[i];
                    if (!r.IsVisible || r.SurfacePolyline.Count < 2) continue;
                    bool sel = selectedRuler != null && selectedRuler.Id == r.Id;
                    DrawLineStrip(r.SurfacePolyline, sel ? new Vector3(1.0f, 0.86f, 0.26f) : new Vector3(0.28f, 0.92f, 0.64f), sel ? 3.2f : 2.5f, viewProj);
                }
                if (activeDraftRuler != null && activeDraftRuler.SurfacePolyline.Count >= 2)
                    DrawLineStrip(activeDraftRuler.SurfacePolyline, new Vector3(1.0f, 0.88f, 0.28f), 3.0f, viewProj);
            }

            // 4. Gizmo Transform trong CharacterViewport
            if (showMoveGizmo && selectedCloth != null && selectedCloth.IsVisible && selectedCloth.CharacterState.IsAttached && selectedCloth.CharacterFittedMesh != null)
            {
                Vec3 piv = ComputeClothCharacterPivot(selectedCloth);
                float gizmoLen = (float)Math.Max(0.14, Math.Min(0.28, camDistance * 0.14));
                DrawMoveGizmo(piv, gizmoLen, activeGizmoAxis, viewProj, camPos);
            }
        }

        public static Vec3 ComputeClothCharacterPivot(ClothEntity cloth)
        {
            var m = cloth.CharacterFittedMesh;
            if (m != null && m.Vertices.Count > 0)
            {
                Vec3 sum = Vec3.Zero;
                for (int i = 0; i < m.Vertices.Count; i++) sum = sum + m.Vertices[i];
                return sum / m.Vertices.Count;
            }
            return new Vec3(0f, 1.15f, 0.20f);
        }

        public static bool ProjectWorldToScreen(
            Vec3 worldPos,
            int pixelWidth,
            int pixelHeight,
            double camYawDeg,
            double camPitchDeg,
            double camDistance,
            Vec3 camTarget,
            out System.Numerics.Vector2 screenPos)
        {
            screenPos = System.Numerics.Vector2.Zero;
            ComputeCameraMatrices(pixelWidth, pixelHeight, camYawDeg, camPitchDeg, camDistance, camTarget, out _, out Matrix4 viewProj);
            Vector4 clip = new Vector4(worldPos.X, worldPos.Y, worldPos.Z, 1f) * viewProj;
            if (clip.W <= 1e-4f) return false;
            Vector3 ndc = clip.Xyz / clip.W;
            if (ndc.Z < -1f || ndc.Z > 1f) return false;
            screenPos = new System.Numerics.Vector2(
                (ndc.X * 0.5f + 0.5f) * pixelWidth,
                (1f - (ndc.Y * 0.5f + 0.5f)) * pixelHeight);
            return true;
        }

        private void DrawLineStrip(IReadOnlyList<Vec3> pts, Vector3 color, float lineWidth, Matrix4 viewProj)
        {
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
            GL.Uniform1(GL.GetUniformLocation(_lineProgram, "uDepthBias"), -0.0014f);
            GL.Uniform3(GL.GetUniformLocation(_lineProgram, "uColor"), color);
            GL.LineWidth(lineWidth);

            GL.BindVertexArray(_dynamicOutlineVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _dynamicOutlineVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.DrawArrays(PrimitiveType.LineStrip, 0, pts.Count);
            GL.LineWidth(1.0f);
        }

        private void UploadCharacterModelToGpu(CharacterModelEntity model)
        {
            var mesh = model.Mesh;
            int vCount = mesh.Vertices.Count;
            if (vCount == 0 || mesh.Triangles.Count == 0) return;

            if (_charVao == 0)
            {
                _charVao = GL.GenVertexArray();
                _charVbo = GL.GenBuffer();
                _charEbo = GL.GenBuffer();
            }

            var vbo = new float[vCount * 8];
            for (int i = 0; i < vCount; i++)
            {
                Vec3 p = mesh.Vertices[i];
                Vec3 n = i < mesh.Normals.Count ? mesh.Normals[i] : Vec3.Up;
                Vec2 uv = i < mesh.UVs.Count ? mesh.UVs[i] : new Vec2(0f, 0f);
                int o = i * 8;
                vbo[o + 0] = p.X; vbo[o + 1] = p.Y; vbo[o + 2] = p.Z;
                vbo[o + 3] = n.X; vbo[o + 4] = n.Y; vbo[o + 5] = n.Z;
                vbo[o + 6] = uv.X; vbo[o + 7] = uv.Y;
            }

            var indices = new uint[mesh.Triangles.Count];
            for (int i = 0; i < mesh.Triangles.Count; i++) indices[i] = (uint)mesh.Triangles[i];
            _charIndexCount = indices.Length;

            GL.BindVertexArray(_charVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _charVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vbo.Length * sizeof(float), vbo, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _charEbo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

            int stride = 8 * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));

            _charMeshDirty = false;
        }

        private static void UploadFittedClothToGpu(ClothEntity cloth, CharClothGpuCache cache)
        {
            var mesh = cloth.CharacterFittedMesh;
            if (mesh == null || mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0)
            {
                cache.VisibleIndexCount = 0;
                cache.Dirty = false;
                return;
            }

            if (cache.Vao == 0)
            {
                cache.Vao = GL.GenVertexArray();
                cache.Vbo = GL.GenBuffer();
                cache.Ebo = GL.GenBuffer();
                cache.WireVao = GL.GenVertexArray();
                cache.WireVbo = GL.GenBuffer();
                cache.WireEbo = GL.GenBuffer();
            }

            int vCount = mesh.Vertices.Count;
            var vbo = new float[vCount * 8];
            var wireVbo = new float[vCount * 3];
            for (int i = 0; i < vCount; i++)
            {
                Vec3 p = mesh.Vertices[i];
                Vec3 n = i < mesh.Normals.Count ? mesh.Normals[i] : Vec3.Forward;
                Vec2 uv = i < mesh.UVs.Count ? mesh.UVs[i] : new Vec2(0.5f, 0.5f);
                int o = i * 8;
                vbo[o + 0] = p.X; vbo[o + 1] = p.Y; vbo[o + 2] = p.Z;
                vbo[o + 3] = n.X; vbo[o + 4] = n.Y; vbo[o + 5] = n.Z;
                vbo[o + 6] = uv.X; vbo[o + 7] = uv.Y;

                wireVbo[i * 3 + 0] = p.X;
                wireVbo[i * 3 + 1] = p.Y;
                wireVbo[i * 3 + 2] = p.Z;
            }

            // Chỉ đưa các tam giác KHÔNG bị Layer ngoài che khuất vào EBO -> GPU bỏ qua hoàn toàn shader vùng khuất
            int triCount = mesh.Triangles.Count / 3;
            var visList = new List<uint>(mesh.Triangles.Count);
            var occ = cloth.CharacterOccludedTriangles;
            for (int t = 0; t < triCount; t++)
            {
                if (occ != null && t < occ.Length && occ[t]) continue;
                visList.Add((uint)mesh.Triangles[t * 3]);
                visList.Add((uint)mesh.Triangles[t * 3 + 1]);
                visList.Add((uint)mesh.Triangles[t * 3 + 2]);
            }
            cache.VisibleIndexCount = visList.Count;

            GL.BindVertexArray(cache.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, cache.Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vbo.Length * sizeof(float), vbo, BufferUsageHint.StreamDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, cache.Ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, visList.Count * sizeof(uint), visList.ToArray(), BufferUsageHint.StreamDraw);

            int stride = 8 * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));

            // Wireframe lines cho lưới đã ép
            var wIdx = new List<uint>(mesh.Faces.Count * 8);
            for (int f = 0; f < mesh.Faces.Count; f++)
            {
                var face = mesh.Faces[f];
                for (int k = 0; k < face.Count; k++)
                {
                    wIdx.Add((uint)face[k]);
                    wIdx.Add((uint)face[(k + 1) % face.Count]);
                }
            }
            cache.WireIndexCount = wIdx.Count;
            GL.BindVertexArray(cache.WireVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, cache.WireVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, wireVbo.Length * sizeof(float), wireVbo, BufferUsageHint.StreamDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, cache.WireEbo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, wIdx.Count * sizeof(uint), wIdx.ToArray(), BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);

            cache.Dirty = false;
        }

        private void DisposeCharacterResources()
        {
            foreach (var c in _charClothCaches.Values) c.Dispose();
            _charClothCaches.Clear();

            if (_charVao != 0)
            {
                GL.DeleteVertexArray(_charVao);
                GL.DeleteBuffer(_charVbo);
                GL.DeleteBuffer(_charEbo);
                _charVao = _charVbo = _charEbo = 0;
            }
            if (_subVpFbo != 0)
            {
                GL.DeleteFramebuffer(_subVpFbo);
                GL.DeleteTexture(_subVpColorTex);
                GL.DeleteRenderbuffer(_subVpDepthRb);
                _subVpFbo = _subVpColorTex = _subVpDepthRb = 0;
            }
        }
    }
}
