using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CrimiradTailoring.Core.Engine;
using CrimiradTailoring.Core.Models;
using CrimiradTailoring.Studio.IO;
using CrimiradTailoring.Studio.Platform;
using CrimiradTailoring.Studio.Rendering;
using ImGuiNET;

namespace CrimiradTailoring.Studio
{
    public sealed partial class StudioWindow
    {
        // Active Viewport & Character Camera
        private WorkspaceViewportKind _activeViewport = WorkspaceViewportKind.FabricViewport;
        private double _charCamYawDeg = 18.0;
        private double _charCamPitchDeg = 8.0;
        private double _charCamDistance = 2.30;
        private Vec3 _charCamTarget = new(0f, 0.92f, 0f);

        // Character Model & BVH
        private CharacterModelEntity? _characterModel;
        private CharacterBvh? _characterBvh;

        // Character Anchors & Rulers
        private readonly List<CharacterAnchorPoint> _charAnchors = new();
        private int _anchorCounter = 0;
        private CharacterAnchorPoint? _selectedAnchor;

        private readonly List<RulerMeasurement> _rulers = new();
        private int _rulerCounter = 0;
        private RulerMeasurement? _selectedRuler;
        private RulerMeasurement? _activeDraftRuler;
        private bool _rulerClosedLoop = false;

        // Form Snap Toggle (kiểu nút nam châm Blender: bật/tắt nhanh Hugging & Fitting)
        private bool _charFormSnapEnabled = false;
        private FormSnapMode _charFormSnapMode = FormSnapMode.Fitting;
        private Vector2 _rulerFlyoutPos;
        private Vector2 _formSnapFlyoutPos;

        // Drag-and-Drop từ Outliner vào CharacterViewport + Transform trên Character
        private ClothEntity? _draggedClothFromOutliner;
        private bool _characterSolverDirty = true;
        private bool _wasCharacterInteractivePreview = false;

        private void MarkCharacterSolverDirty()
        {
            _characterSolverDirty = true;
            _glRenderer.MarkAllCharacterClothsDirty(markSubViewport: _activeViewport == WorkspaceViewportKind.FabricViewport);
        }

        private double ActiveCamYaw
        {
            get => _activeViewport == WorkspaceViewportKind.CharacterViewport ? _charCamYawDeg : _camYawDeg;
            set
            {
                if (_activeViewport == WorkspaceViewportKind.CharacterViewport) _charCamYawDeg = value;
                else _camYawDeg = value;
            }
        }

        private double ActiveCamPitch
        {
            get => _activeViewport == WorkspaceViewportKind.CharacterViewport ? _charCamPitchDeg : _camPitchDeg;
            set
            {
                if (_activeViewport == WorkspaceViewportKind.CharacterViewport) _charCamPitchDeg = value;
                else _camPitchDeg = value;
            }
        }

        private double ActiveCamDistance
        {
            get => _activeViewport == WorkspaceViewportKind.CharacterViewport ? _charCamDistance : _camDistance;
            set
            {
                if (_activeViewport == WorkspaceViewportKind.CharacterViewport) _charCamDistance = value;
                else _camDistance = value;
            }
        }

        private Vec3 ActiveCamTarget
        {
            get => _activeViewport == WorkspaceViewportKind.CharacterViewport ? _charCamTarget : _camTarget;
            set
            {
                if (_activeViewport == WorkspaceViewportKind.CharacterViewport) _charCamTarget = value;
                else _camTarget = value;
            }
        }

        private Vec3 DefaultActiveCamTarget()
        {
            if (_activeViewport != WorkspaceViewportKind.CharacterViewport) return Vec3.Zero;
            if (_characterModel != null && _characterModel.HeightMeters > 0.2f)
                return new Vec3(0f, _characterModel.BoundsMin.Y + _characterModel.HeightMeters * 0.52f, 0f);
            return new Vec3(0f, 0.92f, 0f);
        }

        private void FrameCharacterCamera()
        {
            if (_characterModel != null && _characterModel.HeightMeters > 0.2f)
            {
                _charCamTarget = new Vec3(0f, _characterModel.BoundsMin.Y + _characterModel.HeightMeters * 0.52f, 0f);
                _charCamDistance = Math.Max(2.2, _characterModel.HeightMeters * 1.42);
            }
            else
            {
                _charCamTarget = new Vec3(0f, 0.92f, 0f);
                _charCamDistance = 2.30;
            }
        }

        private void InitializeDefaultCharacter()
        {
            string[] candidates =
            {
                System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Girl_Base.fbx"),
                System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "Girl_Base.fbx")),
                System.IO.Path.Combine(Environment.CurrentDirectory, "Assets", "Girl_Base.fbx")
            };

            CharacterModelEntity? loaded = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (System.IO.File.Exists(candidates[i]))
                {
                    try
                    {
                        loaded = CharacterModelImporter.LoadFromFile(candidates[i]);
                        break;
                    }
                    catch
                    {
                        // Fallback sang Mannequin nếu file lỗi
                    }
                }
            }

            _characterModel = loaded ?? CharacterEngine.CreateDefaultMannequin();
            FrameCharacterCamera();
            RebuildCharacterBvh();
            _glRenderer.MarkCharacterModelDirty();
            _characterSolverDirty = true;
        }

        private void SetupCharacterScreenshotDemo()
        {
            _activeViewport = WorkspaceViewportKind.CharacterViewport;
            if (_characterBvh == null || _cloths.Count == 0) return;

            float charMinY = _characterModel?.BoundsMin.Y ?? 0f;
            float charH = (_characterModel != null && _characterModel.HeightMeters > 0.2f) ? _characterModel.HeightMeters : 1.70f;
            float chestY = charMinY + charH * 0.74f;
            float rulerY = charMinY + charH * 0.785f;

            if (_characterBvh.Raycast(new Vec3(0f, chestY, 1.5f), new Vec3(0f, 0f, -1f), out BvhRayHit chestHit))
            {
                var a1 = CreateAnchorAtHit(chestHit);
                _selectedAnchor = a1;
                var innerCloth = _cloths[0];
                innerCloth.CharacterState.IsAttached = true;
                innerCloth.CharacterState.AnchorId = a1.Id;
                innerCloth.CharacterState.UniformScale = 0.34f;
                innerCloth.CharacterState.LayerIndex = 0;
                innerCloth.CharacterState.FormMode = FormSnapMode.Hugging;
                innerCloth.CharacterState.SurfaceOffsetMeters = 0.006f;

                _clothCounter++;
                var outerCloth = ClothSculptEngine.CreateSquareClothSheet(_clothCounter, 0.84f, 24);
                outerCloth.ClothColor = new ColorRgba(76, 102, 138);
                outerCloth.CharacterState.IsAttached = true;
                outerCloth.CharacterState.AnchorId = a1.Id;
                outerCloth.CharacterState.UniformScale = 0.40f;
                outerCloth.CharacterState.LayerIndex = 1;
                outerCloth.CharacterState.FormMode = FormSnapMode.Fitting;
                outerCloth.CharacterState.SurfaceOffsetMeters = 0.012f;
                outerCloth.CharacterState.FittingBridgeStiffness = 0.88f;
                _cloths.Add(outerCloth);
                SelectSingleCloth(outerCloth);
                _charFormSnapEnabled = true;
                _charFormSnapMode = FormSnapMode.Fitting;
            }

            // Thước dây đo cong ngang vòng ngực
            var rPts = new List<Vec3>();
            float[] xs = { -0.14f, -0.07f, 0f, 0.07f, 0.14f };
            for (int i = 0; i < xs.Length; i++)
            {
                if (_characterBvh.Raycast(new Vec3(xs[i], rulerY, 1.2f), new Vec3(0f, 0f, -1f), out BvhRayHit rh))
                    rPts.Add(rh.Point + rh.Normal * 0.003f);
            }
            if (rPts.Count >= 2)
            {
                _rulerCounter++;
                var ruler = new RulerMeasurement
                {
                    Name = "Ruler #1 (Vòng vai-ngực)",
                    ControlPoints = rPts
                };
                RebuildRulerGeometry(ruler);
                _rulers.Add(ruler);
            }

            MarkCharacterSolverDirty();
            EnsureCharacterClothsSolved();
            _statusText = "CharacterViewport";
        }

        private void RebuildCharacterBvh()
        {
            if (_characterModel != null && _characterModel.Mesh.Vertices.Count > 0 && _characterModel.Mesh.Triangles.Count >= 3)
            {
                _characterBvh = CharacterBvh.Build(
                    _characterModel.Mesh.Vertices,
                    _characterModel.Mesh.Triangles,
                    _characterModel.Mesh.Normals);
            }
            else
            {
                _characterBvh = null;
            }
        }

        private bool IsCharacterDraggingNow() =>
            _activeViewport == WorkspaceViewportKind.CharacterViewport &&
            (_isDraggingMoveElement || _isDraggingRotateCloth || _isDraggingScaleElement || ImGui.IsAnyItemActive());

        private void EnsureCharacterClothsSolved(bool forceFullQuality = false)
        {
            bool isDragging = !forceFullQuality && IsCharacterDraggingNow();
            if (!forceFullQuality && _wasCharacterInteractivePreview && !isDragging)
            {
                // Khi vừa thả chuột/slider: nâng cấp lên lưới mịn + Occlusion Culling
                _characterSolverDirty = true;
            }
            if (!_characterSolverDirty) return;

            CharacterEngine.SolveAllCharacterClothLayers(
                _characterBvh,
                _charAnchors,
                _cloths,
                interactivePreview: isDragging);
            _glRenderer.MarkAllCharacterClothsDirty(markSubViewport: _activeViewport == WorkspaceViewportKind.FabricViewport);
            _characterSolverDirty = false;
            _wasCharacterInteractivePreview = isDragging;
        }

        private void ImportCharacterModelDialog()
        {
            string? path = NativeFileDialog.ShowOpenDialog(
                "Nhập Model Nhân Vật (.GLB / .GLTF / .OBJ / .FBX)",
                "3D Character (*.glb;*.gltf;*.obj;*.fbx)\0*.glb;*.gltf;*.obj;*.fbx\0All Files (*.*)\0*.*\0");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var loaded = CharacterModelImporter.LoadFromFile(path);
                _characterModel = loaded;
                _charAnchors.Clear();
                _rulers.Clear();
                _activeDraftRuler = null;
                _selectedAnchor = null;
                _selectedRuler = null;
                FrameCharacterCamera();
                RebuildCharacterBvh();
                _glRenderer.MarkCharacterModelDirty();
                MarkCharacterSolverDirty();
                _activeViewport = WorkspaceViewportKind.CharacterViewport;
                _statusText = $"Đã nạp Model: {loaded.Name}";
            }
            catch (Exception ex)
            {
                _alertMessage = $"Lỗi nạp model nhân vật:\n{ex.Message}";
            }
        }

        private void SwapActiveViewport()
        {
            _activeViewport = _activeViewport == WorkspaceViewportKind.FabricViewport
                ? WorkspaceViewportKind.CharacterViewport
                : WorkspaceViewportKind.FabricViewport;
            _activeDraftRuler = null;
            _glRenderer.MarkSubViewportDirty();
            _statusText = _activeViewport == WorkspaceViewportKind.CharacterViewport ? "CharacterViewport" : "FabricViewport";
        }

        private bool IsPointOverCharacterCanvas(Vector2 mp)
        {
            float w = ClientSize.X;
            float h = ClientSize.Y;
            float rightLimit = _showUiPanels ? (w - 328f - 8f) : w;
            if (mp.X < 0f || mp.X >= rightLimit) return false;
            if (mp.Y <= 32f || mp.Y >= h - 26f) return false;
            if (_showUiPanels && mp.X < 70f && mp.Y < 320f) return false;
            float subX = rightLimit - 248f - 12f;
            float subY = h - 24f - 154f - 10f;
            if (mp.X >= subX && mp.X <= subX + 248f && mp.Y >= subY && mp.Y <= subY + 154f) return false;
            return true;
        }

        private void AttachClothToCharacterAtScreenPos(ClothEntity cloth, Vector2 mousePos)
        {
            if (_characterBvh == null) return;
            EnsureCharacterClothsSolved();

            GlClothViewportRenderer.ComputeWorldRay(
                mousePos.X, mousePos.Y,
                Math.Max(1, ClientSize.X), Math.Max(1, ClientSize.Y),
                _charCamYawDeg, _charCamPitchDeg, _charCamDistance, _charCamTarget,
                out Vec3 ro, out Vec3 rd);

            if (!_characterBvh.Raycast(ro, rd, out BvhRayHit hit))
            {
                _statusText = "Thả mảnh vải trực tiếp lên bề mặt Model để gắn";
                return;
            }

            RecordUndoState($"Gắn {cloth.Name} lên Model");

            // Snap vào Anchor sẵn có nếu gần (< 4cm), ngược lại tạo Anchor mới tại vị trí thả
            CharacterAnchorPoint? anchor = null;
            float bestD = 0.04f;
            for (int i = 0; i < _charAnchors.Count; i++)
            {
                float d = Vec3.Distance(_charAnchors[i].Position, hit.Point);
                if (d < bestD)
                {
                    bestD = d;
                    anchor = _charAnchors[i];
                }
            }

            if (anchor == null)
            {
                anchor = CreateAnchorAtHit(hit);
            }

            cloth.CharacterState.IsAttached = true;
            cloth.CharacterState.AnchorId = anchor.Id;
            cloth.CharacterState.WorldOffset = Vec3.Zero;
            cloth.CharacterState.WorldRotationEuler = Vec3.Zero;
            if (_charFormSnapEnabled)
                cloth.CharacterState.FormMode = _charFormSnapMode;

            SelectSingleCloth(cloth);
            _selectedAnchor = anchor;
            MarkCharacterSolverDirty();
            EnsureCharacterClothsSolved();
            _statusText = $"Đã gắn {cloth.Name} -> {anchor.Name}";
        }

        private CharacterAnchorPoint CreateAnchorAtHit(BvhRayHit hit)
        {
            _anchorCounter++;
            Vec3 n = hit.Normal.LengthSquared > 1e-6f ? hit.Normal.Normalized : Vec3.Forward;
            Vec3 upRef = Math.Abs(Vec3.Dot(n, Vec3.Up)) > 0.9f ? Vec3.Forward : Vec3.Up;
            Vec3 tangent = Vec3.Cross(upRef, n);
            tangent = tangent.LengthSquared > 1e-6f ? tangent.Normalized : Vec3.Right;

            var anchor = new CharacterAnchorPoint
            {
                Name = $"A{_anchorCounter}",
                Position = hit.Point,
                Normal = n,
                Tangent = tangent,
                TriangleIndex = hit.TriangleIndex,
                BarycentricUVW = hit.Barycentric
            };
            _charAnchors.Add(anchor);
            _glRenderer.MarkSubViewportDirty();
            return anchor;
        }

        private void HandleCharacterViewportLeftClick(Vector2 mp)
        {
            EnsureCharacterClothsSolved();
            GlClothViewportRenderer.ComputeWorldRay(
                mp.X, mp.Y,
                Math.Max(1, ClientSize.X), Math.Max(1, ClientSize.Y),
                _charCamYawDeg, _charCamPitchDeg, _charCamDistance, _charCamTarget,
                out Vec3 ro, out Vec3 rd);

            // 1. Check Transform Gizmo của mảnh vải đang gắn
            if (_showMoveGizmo && _selectedCloth != null && _selectedCloth.CharacterState.IsAttached && _selectedCloth.CharacterFittedMesh != null &&
                (_activeTool == EditorToolType.Select || _activeTool == EditorToolType.Move || _activeTool == EditorToolType.Rotate || _activeTool == EditorToolType.Scale))
            {
                Vec3 piv = GlClothViewportRenderer.ComputeClothCharacterPivot(_selectedCloth);
                if (GlClothViewportRenderer.TryHitMoveGizmo(ro, rd, piv, _charCamDistance, out GizmoAxis gAx, out _))
                {
                    _pendingDragUndoSnapshot = CaptureSnapshot("Di chuyển vải trên Model");
                    _activeGizmoAxis = gAx;
                    _isDraggingMoveElement = true;
                    return;
                }
            }

            // 2. Tool Ruler: Chấm chuỗi điểm đo cong trên Model
            if (_activeTool == EditorToolType.Ruler)
            {
                if (_characterBvh != null && _characterBvh.Raycast(ro, rd, out BvhRayHit hit))
                {
                    if (_activeDraftRuler == null)
                    {
                        _rulerCounter++;
                        _activeDraftRuler = new RulerMeasurement
                        {
                            Name = $"Ruler #{_rulerCounter}",
                            IsClosedLoop = _rulerClosedLoop
                        };
                    }
                    _activeDraftRuler.IsClosedLoop = _rulerClosedLoop;
                    _activeDraftRuler.ControlPoints.Add(hit.Point + hit.Normal * 0.002f);
                    _activeDraftRuler.ControlNormals.Add(hit.Normal);
                    RebuildRulerGeometry(_activeDraftRuler);
                    _selectedRuler = _activeDraftRuler;
                    _glRenderer.MarkSubViewportDirty();
                    _statusText = $"{_activeDraftRuler.Name}: {_activeDraftRuler.TotalLengthMeters * 100f:F1} cm";
                }
                return;
            }

            // 3. Tool Create (A): Chấm Điểm Neo (AnchorPoint) lên bề mặt Model
            if (_activeTool == EditorToolType.CreatePoint)
            {
                if (_characterBvh != null && _characterBvh.Raycast(ro, rd, out BvhRayHit hit))
                {
                    RecordUndoState("Tạo Điểm Neo");
                    var anchor = CreateAnchorAtHit(hit);
                    _selectedAnchor = anchor;
                    _selectedRuler = null;
                    _statusText = $"Đã tạo {anchor.Name}";
                }
                return;
            }

            // 4. Chọn thành phần trong CharacterViewport (Anchor -> Ruler -> Mảnh vải đã gắn)
            PickCharacterElementFromRay(ro, rd, out CharacterAnchorPoint? hitAnchor, out RulerMeasurement? hitRuler, out ClothEntity? hitCloth);

            if (hitAnchor != null)
            {
                SelectCharacterAnchor(hitAnchor);
                _statusText = $"Đã chọn {hitAnchor.Name}";
                return;
            }

            if (hitRuler != null)
            {
                SelectCharacterRuler(hitRuler);
                _statusText = $"{hitRuler.Name}: {hitRuler.TotalLengthMeters * 100f:F1} cm";
                return;
            }

            if (hitCloth != null)
            {
                _selectedAnchor = null;
                _selectedRuler = null;
                SelectSingleCloth(hitCloth);
                if (_activeTool == EditorToolType.Move)
                {
                    _pendingDragUndoSnapshot = CaptureSnapshot("Di chuyển vải trên Model");
                    _isDraggingMoveElement = true;
                }
                else if (_activeTool == EditorToolType.Rotate)
                {
                    _pendingDragUndoSnapshot = CaptureSnapshot("Xoay vải trên Model");
                    _isDraggingRotateCloth = true;
                }
                else if (_activeTool == EditorToolType.Scale)
                {
                    _pendingDragUndoSnapshot = CaptureSnapshot("Scale vải trên Model");
                    _isDraggingScaleElement = true;
                }
                _statusText = $"Đã chọn {hitCloth.Name}";
            }
        }

        private void SelectCharacterAnchor(CharacterAnchorPoint anchor)
        {
            ClearMultiSelections();
            _selectedCloth = null;
            _selectedPoint = null;
            _selectedEdge = null;
            _selectedBorderEdge = null;
            _selectedRuler = null;
            _selectedAnchor = anchor;
            SyncSelectionHashes();
        }

        private void SelectCharacterRuler(RulerMeasurement ruler)
        {
            ClearMultiSelections();
            _selectedCloth = null;
            _selectedPoint = null;
            _selectedEdge = null;
            _selectedBorderEdge = null;
            _selectedAnchor = null;
            _selectedRuler = ruler;
            SyncSelectionHashes();
        }

        private void PickCharacterElementAtScreenPos(
            Vector2 mp,
            out CharacterAnchorPoint? hitAnchor,
            out RulerMeasurement? hitRuler,
            out ClothEntity? hitCloth)
        {
            EnsureCharacterClothsSolved();
            GlClothViewportRenderer.ComputeWorldRay(
                mp.X, mp.Y,
                Math.Max(1, ClientSize.X), Math.Max(1, ClientSize.Y),
                _charCamYawDeg, _charCamPitchDeg, _charCamDistance, _charCamTarget,
                out Vec3 ro, out Vec3 rd);
            PickCharacterElementFromRay(ro, rd, out hitAnchor, out hitRuler, out hitCloth);
        }

        private void PickCharacterElementFromRay(
            Vec3 ro,
            Vec3 rd,
            out CharacterAnchorPoint? hitAnchor,
            out RulerMeasurement? hitRuler,
            out ClothEntity? hitCloth)
        {
            hitAnchor = null;
            float bestT = float.MaxValue;
            for (int i = 0; i < _charAnchors.Count; i++)
            {
                if (RaySphereSimple(ro, rd, _charAnchors[i].Position, 0.022f, out float t) && t < bestT)
                {
                    bestT = t;
                    hitAnchor = _charAnchors[i];
                }
            }

            hitRuler = null;
            float bestRulerT = float.MaxValue;
            for (int i = 0; i < _rulers.Count; i++)
            {
                var r = _rulers[i];
                if (!r.IsVisible) continue;
                for (int k = 0; k < r.ControlPoints.Count; k++)
                {
                    if (RaySphereSimple(ro, rd, r.ControlPoints[k], 0.018f, out float t) && t < bestRulerT)
                    {
                        bestRulerT = t;
                        hitRuler = r;
                    }
                }
            }

            hitCloth = null;
            float bestClothT = float.MaxValue;
            foreach (var c in _cloths)
            {
                if (!c.IsVisible || !c.CharacterState.IsAttached || c.CharacterFittedMesh == null) continue;
                var m = c.CharacterFittedMesh;
                for (int t = 0; t + 2 < m.Triangles.Count; t += 3)
                {
                    int triIdx = t / 3;
                    if (c.CharacterOccludedTriangles != null && triIdx < c.CharacterOccludedTriangles.Length && c.CharacterOccludedTriangles[triIdx])
                        continue;
                    if (RayTriSimple(ro, rd, m.Vertices[m.Triangles[t]], m.Vertices[m.Triangles[t + 1]], m.Vertices[m.Triangles[t + 2]], out float tHit) && tHit < bestClothT)
                    {
                        bestClothT = tHit;
                        hitCloth = c;
                    }
                }
            }
        }

        private void HandleCharacterDragMove(Vector2 delta)
        {
            if (_selectedCloth == null || !_selectedCloth.CharacterState.IsAttached) return;
            CommitPendingDragUndo();

            float moveScale = (float)(_charCamDistance * 0.0014);
            GetCameraBasis(out Vec3 camRight, out Vec3 camUp, out Vec3 camForward);
            Vec3 worldDelta;
            if (_activeGizmoAxis == GizmoAxis.X || _activeGizmoAxis == GizmoAxis.Y || _activeGizmoAxis == GizmoAxis.Z)
            {
                Vec3 axisDir = _activeGizmoAxis == GizmoAxis.X ? Vec3.Right :
                               (_activeGizmoAxis == GizmoAxis.Y ? Vec3.Up : Vec3.Forward);
                float projX = Vec3.Dot(axisDir, camRight);
                float projY = Vec3.Dot(axisDir, camUp);
                float denom = projX * projX + projY * projY;
                float sdx = delta.X * moveScale;
                float sdy = -delta.Y * moveScale;
                float amt = denom > 0.04f ? (sdx * projX + sdy * projY) / (float)Math.Sqrt(denom) : (sdx + sdy);
                worldDelta = axisDir * amt;
            }
            else
            {
                bool shift = KeyboardState.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.LeftShift) ||
                             KeyboardState.IsKeyDown(OpenTK.Windowing.GraphicsLibraryFramework.Keys.RightShift);
                worldDelta = shift
                    ? camForward * (-delta.Y * moveScale * 1.35f) + camRight * (delta.X * moveScale)
                    : camRight * (delta.X * moveScale) + camUp * (-delta.Y * moveScale);
            }

            _selectedCloth.CharacterState.WorldOffset = _selectedCloth.CharacterState.WorldOffset + worldDelta;
            if (_charFormSnapEnabled && _selectedCloth.CharacterState.FormMode == FormSnapMode.Off)
                _selectedCloth.CharacterState.FormMode = _charFormSnapMode;

            if (_selectedCloth.CharacterState.AnchorId.HasValue)
            {
                var anch = _charAnchors.FirstOrDefault(a => a.Id == _selectedCloth.CharacterState.AnchorId.Value);
                _selectedCloth.CharacterState.WorldOffset = CharacterEngine.ClampFrontContactOffset(_characterBvh, anch, _selectedCloth.CharacterState);
            }
            MarkCharacterSolverDirty();
        }

        private void HandleCharacterDragRotate(Vector2 delta)
        {
            if (_selectedCloth == null || !_selectedCloth.CharacterState.IsAttached) return;
            CommitPendingDragUndo();
            var r = _selectedCloth.CharacterState.WorldRotationEuler;
            _selectedCloth.CharacterState.WorldRotationEuler = new Vec3(
                ClampEuler180(r.X + delta.Y * 0.45f),
                ClampEuler180(r.Y + delta.X * 0.45f),
                r.Z);
            MarkCharacterSolverDirty();
        }

        private void HandleCharacterDragScale(Vector2 delta)
        {
            if (_selectedCloth == null || !_selectedCloth.CharacterState.IsAttached) return;
            CommitPendingDragUndo();
            float ds = 1f + (delta.X - delta.Y) * 0.004f;
            _selectedCloth.CharacterState.UniformScale = Math.Clamp(_selectedCloth.CharacterState.UniformScale * ds, 0.25f, 3.0f);
            MarkCharacterSolverDirty();
        }

        private void RebuildRulerGeometry(RulerMeasurement ruler)
        {
            ruler.SurfacePolyline = CharacterEngine.BuildGeodesicRulerPath(
                _characterBvh,
                ruler.ControlPoints,
                ruler.IsClosedLoop,
                out List<float> segLens,
                out float totalLen);
            ruler.SegmentLengths = segLens;
            ruler.TotalLengthMeters = totalLen;
        }

        private void FinishActiveDraftRuler()
        {
            if (_activeDraftRuler == null) return;
            if (_activeDraftRuler.ControlPoints.Count >= 2)
            {
                RecordUndoState($"Thêm {_activeDraftRuler.Name}");
                _rulers.Add(_activeDraftRuler);
                _selectedRuler = _activeDraftRuler;
                _statusText = $"{_activeDraftRuler.Name}: {_activeDraftRuler.TotalLengthMeters * 100f:F1} cm";
            }
            _activeDraftRuler = null;
            _glRenderer.MarkSubViewportDirty();
        }

        private void ToggleCharFormSnap()
        {
            _charFormSnapEnabled = !_charFormSnapEnabled;
            if (_selectedCloth != null && _selectedCloth.CharacterState.IsAttached)
            {
                RecordUndoState("Bật/Tắt Form Snap");
                _selectedCloth.CharacterState.FormMode = _charFormSnapEnabled ? _charFormSnapMode : FormSnapMode.Off;
                MarkCharacterSolverDirty();
                EnsureCharacterClothsSolved();
            }
            _statusText = _charFormSnapEnabled ? $"Form Snap: {_charFormSnapMode}" : "Form Snap: Off";
        }

        private void RenderCharacterLeftTools()
        {
            DrawIconToolButton("###CharSelectTool", ToolIconKind.SelectBox, EditorToolType.Select, "Select (Esc)");

            (ToolIconKind transformIcon, string transformName) = _transformToolMode switch
            {
                EditorToolType.Move => (ToolIconKind.Move, "Move (G)"),
                EditorToolType.Rotate => (ToolIconKind.Rotate, "Rotate (R)"),
                EditorToolType.Scale => (ToolIconKind.Scale, "Scale (S)"),
                _ => (ToolIconKind.Move, "Move (G)")
            };
            DrawIconToolButton(
                "###CharTransformTool",
                transformIcon,
                _transformToolMode,
                transformName,
                "##CharTransformFlyout",
                pos => _transformFlyoutPos = pos);

            ImGui.SetNextWindowPos(_transformFlyoutPos, ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 7f));
            if (ImGui.BeginPopup("##CharTransformFlyout", ImGuiWindowFlags.AlwaysAutoResize))
            {
                DrawTransformFlyoutItem("Move (G)", ToolIconKind.Move, EditorToolType.Move);
                DrawTransformFlyoutItem("Rotate (R)", ToolIconKind.Rotate, EditorToolType.Rotate);
                DrawTransformFlyoutItem("Scale (S)", ToolIconKind.Scale, EditorToolType.Scale);
                ImGui.EndPopup();
            }
            ImGui.PopStyleVar();

            DrawIconToolButton("###CharAnchorTool", ToolIconKind.CreatePoint, EditorToolType.CreatePoint, "Tạo Điểm Neo (A)");

            DrawIconToolButton(
                "###CharRulerTool",
                ToolIconKind.Ruler,
                EditorToolType.Ruler,
                "Ruler đo cong (M)",
                "##RulerFlyout",
                pos => _rulerFlyoutPos = pos);

            ImGui.SetNextWindowPos(_rulerFlyoutPos, ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 7f));
            if (ImGui.BeginPopup("##RulerFlyout", ImGuiWindowFlags.AlwaysAutoResize))
            {
                if (ImGui.Selectable("Đo chuỗi điểm", !_rulerClosedLoop, ImGuiSelectableFlags.None, new Vector2(126f, 0f)))
                {
                    _rulerClosedLoop = false;
                    _activeTool = EditorToolType.Ruler;
                    if (_activeDraftRuler != null) { _activeDraftRuler.IsClosedLoop = false; RebuildRulerGeometry(_activeDraftRuler); }
                    ImGui.CloseCurrentPopup();
                }
                if (ImGui.Selectable("Đóng vòng (Loop)", _rulerClosedLoop, ImGuiSelectableFlags.None, new Vector2(126f, 0f)))
                {
                    _rulerClosedLoop = true;
                    _activeTool = EditorToolType.Ruler;
                    if (_activeDraftRuler != null) { _activeDraftRuler.IsClosedLoop = true; RebuildRulerGeometry(_activeDraftRuler); }
                    ImGui.CloseCurrentPopup();
                }
                if (_activeDraftRuler != null)
                {
                    ImGui.Separator();
                    if (ImGui.Selectable("Hoàn tất thước", false)) { FinishActiveDraftRuler(); ImGui.CloseCurrentPopup(); }
                    if (ImGui.Selectable("Hủy thước", false)) { _activeDraftRuler = null; ImGui.CloseCurrentPopup(); }
                }
                ImGui.EndPopup();
            }
            ImGui.PopStyleVar();

            // Nút Toggle Form Snap (Hugging / Fitting kiểu Snap Blender)
            ImGui.Separator();
            bool snapOn = _charFormSnapEnabled;
            if (snapOn)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.30f, 0.30f, 0.33f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.76f, 0.76f, 0.78f, 1.0f));
            }
            if (ImGui.Button("###FormSnapBtn", new Vector2(42, 42)))
                ToggleCharFormSnap();

            Vector2 bMin = ImGui.GetItemRectMin();
            Vector2 bMax = ImGui.GetItemRectMax();
            var dl = ImGui.GetWindowDrawList();
            uint mainCol = ImGui.GetColorU32(snapOn ? new Vector4(0.96f, 0.96f, 0.97f, 1.0f) : new Vector4(0.76f, 0.76f, 0.78f, 0.96f));
            uint accentCol = ImGui.GetColorU32(snapOn ? new Vector4(0.35f, 0.92f, 0.65f, 1.0f) : new Vector4(0.60f, 0.60f, 0.63f, 0.95f));
            DrawToolVectorIcon(dl, (bMin + bMax) * 0.5f, 11.5f, _charFormSnapMode == FormSnapMode.Hugging ? ToolIconKind.FormHugging : ToolIconKind.FormFitting, mainCol, accentCol, drawSoftFill: true);
            dl.AddTriangleFilled(
                new Vector2(bMax.X - 3.5f, bMax.Y - 3.5f),
                new Vector2(bMax.X - 10f, bMax.Y - 3.5f),
                new Vector2(bMax.X - 3.5f, bMax.Y - 10f),
                ImGui.GetColorU32(new Vector4(0.76f, 0.76f, 0.78f, 0.92f)));
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _formSnapFlyoutPos = new Vector2(bMax.X + 6f, bMin.Y);
                ImGui.OpenPopup("##FormSnapFlyout");
            }
            if (snapOn) ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Form Snap: {(_charFormSnapEnabled ? _charFormSnapMode.ToString() : "Off")}");

            ImGui.SetNextWindowPos(_formSnapFlyoutPos, ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 7f));
            if (ImGui.BeginPopup("##FormSnapFlyout", ImGuiWindowFlags.AlwaysAutoResize))
            {
                DrawPopupIconItem("Hugging (Ôm sát)", ToolIconKind.FormHugging, _charFormSnapEnabled && _charFormSnapMode == FormSnapMode.Hugging, () =>
                {
                    _charFormSnapMode = FormSnapMode.Hugging;
                    _charFormSnapEnabled = true;
                    if (_selectedCloth != null && _selectedCloth.CharacterState.IsAttached)
                    {
                        RecordUndoState("Form Hugging");
                        _selectedCloth.CharacterState.FormMode = FormSnapMode.Hugging;
                        MarkCharacterSolverDirty();
                        EnsureCharacterClothsSolved();
                    }
                });
                DrawPopupIconItem("Fitting (Bắc cầu)", ToolIconKind.FormFitting, _charFormSnapEnabled && _charFormSnapMode == FormSnapMode.Fitting, () =>
                {
                    _charFormSnapMode = FormSnapMode.Fitting;
                    _charFormSnapEnabled = true;
                    if (_selectedCloth != null && _selectedCloth.CharacterState.IsAttached)
                    {
                        RecordUndoState("Form Fitting");
                        _selectedCloth.CharacterState.FormMode = FormSnapMode.Fitting;
                        MarkCharacterSolverDirty();
                        EnsureCharacterClothsSolved();
                    }
                });
                if (ImGui.Selectable("      Tắt Form Snap", !_charFormSnapEnabled))
                {
                    _charFormSnapEnabled = false;
                    if (_selectedCloth != null && _selectedCloth.CharacterState.IsAttached)
                    {
                        RecordUndoState("Tắt Form Snap");
                        _selectedCloth.CharacterState.FormMode = FormSnapMode.Off;
                        MarkCharacterSolverDirty();
                        EnsureCharacterClothsSolved();
                    }
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            ImGui.PopStyleVar();
        }

        private void RenderSubViewportPip(float windowW, float windowH, float rightPanelOccupiedW, float footerH)
        {
            const float subW = 248f;
            const float subH = 154f;
            float subX = windowW - rightPanelOccupiedW - subW - 12f;
            float subY = windowH - footerH - subH - 10f;
            if (subX < 70f || subY < 50f) return;

            int imgW = (int)(subW - 8f);
            int imgH = (int)(subH - 26f);

            ImGui.SetNextWindowPos(new Vector2(subX, subY), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(subW, subH), ImGuiCond.Always);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.114f, 0.114f, 0.118f, 0.96f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.34f, 0.36f, 1.0f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4f, 4f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 6f);

            ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                     ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                                     ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            if (ImGui.Begin("##SubViewportPiP", flags))
            {
                string title = _activeViewport == WorkspaceViewportKind.FabricViewport
                    ? "CharacterViewport"
                    : "FabricViewport";
                ImGui.TextColored(new Vector4(0.84f, 0.84f, 0.86f, 1f), title);
                ImGui.SameLine(subW - 62f);
                ImGui.TextDisabled("Click");

                IntPtr tex = _glRenderer.SubViewportTextureId;
                if (tex != IntPtr.Zero)
                {
                    if (ImGui.ImageButton("##SubVpBtn", tex, new Vector2(imgW, imgH), new Vector2(0, 1), new Vector2(1, 0)))
                        SwapActiveViewport();
                }
                else
                {
                    if (ImGui.Button("Chuyển Viewport", new Vector2(imgW, imgH)))
                        SwapActiveViewport();
                }

                if (_activeViewport == WorkspaceViewportKind.FabricViewport && ImGui.BeginDragDropTarget())
                {
                    if (ImGui.AcceptDragDropPayload("OUTLINER_CLOTH").IsDelivery() &&
                        _draggedClothFromOutliner != null && _characterBvh != null)
                    {
                        var dropped = _draggedClothFromOutliner;
                        _draggedClothFromOutliner = null;
                        float charMinY = _characterModel?.BoundsMin.Y ?? 0f;
                        float charH = (_characterModel != null && _characterModel.HeightMeters > 0.2f) ? _characterModel.HeightMeters : 1.70f;
                        if (_characterBvh.Raycast(new Vec3(0f, charMinY + charH * 0.74f, 1.5f), new Vec3(0f, 0f, -1f), out BvhRayHit hit))
                        {
                            RecordUndoState($"Gắn {dropped.Name} lên Model");
                            var a = CreateAnchorAtHit(hit);
                            dropped.CharacterState.IsAttached = true;
                            dropped.CharacterState.AnchorId = a.Id;
                            dropped.CharacterState.WorldOffset = Vec3.Zero;
                            dropped.CharacterState.WorldRotationEuler = Vec3.Zero;
                            if (_charFormSnapEnabled) dropped.CharacterState.FormMode = _charFormSnapMode;
                            SelectSingleCloth(dropped);
                            _selectedAnchor = a;
                            _activeViewport = WorkspaceViewportKind.CharacterViewport;
                            MarkCharacterSolverDirty();
                            EnsureCharacterClothsSolved();
                            _statusText = $"Đã gắn {dropped.Name} -> {a.Name}";
                        }
                    }
                    ImGui.EndDragDropTarget();
                }
            }
            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
        }

        private void RenderCharacterRulerOverlayLabels()
        {
            if (_activeViewport != WorkspaceViewportKind.CharacterViewport || !_showElements) return;
            var bgDl = ImGui.GetBackgroundDrawList();

            void DrawLabelForRuler(RulerMeasurement r, bool isDraft)
            {
                if (!r.IsVisible || r.SurfacePolyline.Count == 0) return;
                Vec3 mid = r.SurfacePolyline[r.SurfacePolyline.Count / 2];
                if (!GlClothViewportRenderer.ProjectWorldToScreen(
                    mid, ClientSize.X, ClientSize.Y,
                    _charCamYawDeg, _charCamPitchDeg, _charCamDistance, _charCamTarget,
                    out Vector2 sp))
                    return;

                string txt = $"{r.TotalLengthMeters * 100f:F1} cm";
                Vector2 ts = ImGui.CalcTextSize(txt);
                Vector2 boxMin = sp - ts * 0.5f - new Vector2(5f, 3f);
                Vector2 boxMax = sp + ts * 0.5f + new Vector2(5f, 3f);
                uint bgCol = isDraft ? 0xDD28241Au : 0xDD1A1A1Cu;
                uint bdCol = isDraft ? 0xFF45D6FBu : 0xFF8BE8A2u;
                bgDl.AddRectFilled(boxMin, boxMax, bgCol, 4f);
                bgDl.AddRect(boxMin, boxMax, bdCol, 4f);
                bgDl.AddText(sp - ts * 0.5f, 0xFFF2F2F4u, txt);
            }

            for (int i = 0; i < _rulers.Count; i++) DrawLabelForRuler(_rulers[i], false);
            if (_activeDraftRuler != null) DrawLabelForRuler(_activeDraftRuler, true);
        }

        private void RenderCharacterOutlinerItems(bool hasFilter, Func<string, bool> matchesFilter)
        {
            if (_activeViewport != WorkspaceViewportKind.CharacterViewport) return;

            // 1. Model Nhân Vật
            if (_characterModel != null && (!hasFilter || matchesFilter(_characterModel.Name) || matchesFilter("Character")))
            {
                bool modelOpen = ImGui.TreeNodeEx($"Character: {_characterModel.Name}##char_root", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowOverlap);
                float eyeBtnX = ImGui.GetWindowWidth() - 36f;
                ImGui.SameLine(Math.Max(140f, eyeBtnX));
                ImGui.PushStyleColor(ImGuiCol.Button, _characterModel.IsVisible ? new Vector4(0.27f, 0.27f, 0.29f, 0.90f) : new Vector4(0.16f, 0.16f, 0.17f, 0.70f));
                if (ImGui.SmallButton(_characterModel.IsVisible ? "H##char_vis" : "-##char_vis"))
                {
                    _characterModel.IsVisible = !_characterModel.IsVisible;
                    _glRenderer.MarkSubViewportDirty();
                }
                ImGui.PopStyleColor();

                if (modelOpen)
                {
                    // Anchors
                    if (ImGui.TreeNodeEx($"Anchors ({_charAnchors.Count})##char_anch", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
                    {
                        for (int i = 0; i < _charAnchors.Count; i++)
                        {
                            var a = _charAnchors[i];
                            if (hasFilter && !matchesFilter(a.Name)) continue;
                            bool sel = _selectedAnchor?.Id == a.Id;
                            if (ImGui.Selectable($"  {a.Name}##anch_{a.Id}", sel))
                            {
                                _selectedAnchor = a;
                                _selectedRuler = null;
                            }
                        }
                        ImGui.TreePop();
                    }

                    // Rulers
                    if (ImGui.TreeNodeEx($"Rulers ({_rulers.Count})##char_rulers", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
                    {
                        for (int i = 0; i < _rulers.Count; i++)
                        {
                            var r = _rulers[i];
                            if (hasFilter && !matchesFilter(r.Name)) continue;
                            bool sel = _selectedRuler?.Id == r.Id;
                            if (ImGui.Selectable($"  {r.Name} ({r.TotalLengthMeters * 100f:F1} cm)##rl_{r.Id}", sel))
                            {
                                _selectedRuler = r;
                                _selectedAnchor = null;
                            }
                            float rEyeX = ImGui.GetWindowWidth() - 36f;
                            ImGui.SameLine(Math.Max(140f, rEyeX));
                            ImGui.PushStyleColor(ImGuiCol.Button, r.IsVisible ? new Vector4(0.27f, 0.27f, 0.29f, 0.90f) : new Vector4(0.16f, 0.16f, 0.17f, 0.70f));
                            if (ImGui.SmallButton((r.IsVisible ? "H" : "-") + "##rv_" + i))
                            {
                                r.IsVisible = !r.IsVisible;
                                _glRenderer.MarkSubViewportDirty();
                            }
                            ImGui.PopStyleColor();
                        }
                        ImGui.TreePop();
                    }
                    ImGui.TreePop();
                }
            }
        }

        private void RenderCharacterClothAndToolProperties()
        {
            if (_activeViewport != WorkspaceViewportKind.CharacterViewport) return;

            if (_selectedRuler != null && ImGui.CollapsingHeader("Ruler", ImGuiTreeNodeFlags.DefaultOpen))
            {
                string rName = _selectedRuler.Name;
                if (ImGui.InputText("Tên", ref rName, 48))
                    _selectedRuler.Name = rName;
                ImGui.Text($"Chiều dài: {_selectedRuler.TotalLengthMeters * 100f:F2} cm");
                bool loop = _selectedRuler.IsClosedLoop;
                if (ImGui.Checkbox("Đóng vòng", ref loop))
                {
                    _selectedRuler.IsClosedLoop = loop;
                    RebuildRulerGeometry(_selectedRuler);
                    _glRenderer.MarkSubViewportDirty();
                }
                if (ImGui.Button("Xóa Ruler", new Vector2(-1, 0)))
                {
                    if (_selectedRuler == _activeDraftRuler) _activeDraftRuler = null;
                    else _rulers.Remove(_selectedRuler);
                    _selectedRuler = null;
                    _glRenderer.MarkSubViewportDirty();
                }
            }

            if (_selectedAnchor != null && ImGui.CollapsingHeader("Anchor", ImGuiTreeNodeFlags.DefaultOpen))
            {
                string aName = _selectedAnchor.Name;
                if (ImGui.InputText("Tên##anchName", ref aName, 32))
                    _selectedAnchor.Name = aName;
                if (ImGui.Button("Xóa Anchor", new Vector2(-1, 0)))
                {
                    Guid aid = _selectedAnchor.Id;
                    _charAnchors.Remove(_selectedAnchor);
                    foreach (var c in _cloths)
                        if (c.CharacterState.AnchorId == aid) c.CharacterState.AnchorId = null;
                    _selectedAnchor = null;
                    MarkCharacterSolverDirty();
                    EnsureCharacterClothsSolved();
                }
            }

            if (_selectedCloth != null && _selectedCloth.CharacterState.IsAttached &&
                ImGui.CollapsingHeader("Character Form & Layer", ImGuiTreeNodeFlags.DefaultOpen))
            {
                var st = _selectedCloth.CharacterState;

                if (DrawModePill("Off", st.FormMode == FormSnapMode.Off))
                {
                    RecordUndoState("Form: Off");
                    st.FormMode = FormSnapMode.Off;
                    MarkCharacterSolverDirty();
                    EnsureCharacterClothsSolved();
                }
                ImGui.SameLine(0, 4);
                if (DrawModePill("Hugging", st.FormMode == FormSnapMode.Hugging))
                {
                    RecordUndoState("Form: Hugging");
                    st.FormMode = FormSnapMode.Hugging;
                    _charFormSnapMode = FormSnapMode.Hugging;
                    _charFormSnapEnabled = true;
                    MarkCharacterSolverDirty();
                    EnsureCharacterClothsSolved();
                }
                ImGui.SameLine(0, 4);
                if (DrawModePill("Fitting", st.FormMode == FormSnapMode.Fitting))
                {
                    RecordUndoState("Form: Fitting");
                    st.FormMode = FormSnapMode.Fitting;
                    _charFormSnapMode = FormSnapMode.Fitting;
                    _charFormSnapEnabled = true;
                    MarkCharacterSolverDirty();
                    EnsureCharacterClothsSolved();
                }

                int layer = st.LayerIndex;
                if (ImGui.SliderInt("Layer", ref layer, 0, 5))
                {
                    st.LayerIndex = layer;
                    MarkCharacterSolverDirty();
                }
                if (ImGui.IsItemActivated()) RecordUndoState("Đổi Layer");

                float offMm = st.SurfaceOffsetMeters * 1000f;
                if (ImGui.SliderFloat("Offset", ref offMm, 1f, 35f, "%.1f mm"))
                {
                    st.SurfaceOffsetMeters = offMm * 0.001f;
                    MarkCharacterSolverDirty();
                }
                if (ImGui.IsItemActivated()) RecordUndoState("Đổi Surface Offset");

                if (st.FormMode == FormSnapMode.Fitting)
                {
                    float stiff = st.FittingBridgeStiffness;
                    if (ImGui.SliderFloat("Độ bắc cầu", ref stiff, 0.1f, 0.96f, "%.2f"))
                    {
                        st.FittingBridgeStiffness = stiff;
                        MarkCharacterSolverDirty();
                    }
                    if (ImGui.IsItemActivated()) RecordUndoState("Đổi độ bắc cầu");
                }

                float keepDeform = st.PreserveDeformBlend;
                if (ImGui.SliderFloat("Giữ nếp gốc", ref keepDeform, 0f, 1f, "%.2f"))
                {
                    st.PreserveDeformBlend = keepDeform;
                    MarkCharacterSolverDirty();
                }
                if (ImGui.IsItemActivated()) RecordUndoState("Đổi tỉ lệ giữ nếp");

                int totalTris = _selectedCloth.CharacterFittedMesh != null ? _selectedCloth.CharacterFittedMesh.Triangles.Count / 3 : 0;
                int visTris = _selectedCloth.VisibleCharacterTriangleCount / 3;
                if (totalTris > 0 && visTris < totalTris)
                    ImGui.TextDisabled($"Hiển thị: {visTris}/{totalTris} tris (ẩn {totalTris - visTris} khuất)");

                if (ImGui.Button("Gỡ khỏi Model", new Vector2(-1, 0)))
                {
                    RecordUndoState("Gỡ mảnh vải khỏi Model");
                    st.IsAttached = false;
                    _selectedCloth.CharacterFittedMesh = null;
                    MarkCharacterSolverDirty();
                    EnsureCharacterClothsSolved();
                }
            }
        }

        private static bool RaySphereSimple(Vec3 ro, Vec3 rd, Vec3 c, float r, out float t)
        {
            t = float.MaxValue;
            Vec3 oc = ro - c;
            float b = Vec3.Dot(oc, rd);
            float h = b * b - (Vec3.Dot(oc, oc) - r * r);
            if (h < 0f) return false;
            t = -b - (float)Math.Sqrt(h);
            return t > 0f;
        }

        private static bool RayTriSimple(Vec3 ro, Vec3 rd, Vec3 a, Vec3 b, Vec3 c, out float t)
        {
            t = 0f;
            Vec3 e1 = b - a;
            Vec3 e2 = c - a;
            Vec3 h = Vec3.Cross(rd, e2);
            float det = Vec3.Dot(e1, h);
            if (Math.Abs(det) < 1e-7f) return false;
            float inv = 1f / det;
            Vec3 s = ro - a;
            float u = Vec3.Dot(s, h) * inv;
            if (u < 0f || u > 1f) return false;
            Vec3 q = Vec3.Cross(s, e1);
            float v = Vec3.Dot(rd, q) * inv;
            if (v < 0f || u + v > 1f) return false;
            t = Vec3.Dot(e2, q) * inv;
            return t > 1e-4f;
        }
    }
}
