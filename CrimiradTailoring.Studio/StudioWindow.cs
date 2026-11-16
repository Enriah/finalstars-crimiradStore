using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using CrimiradTailoring.Core.Engine;
using CrimiradTailoring.Core.Models;
using CrimiradTailoring.Studio.Platform;
using CrimiradTailoring.Studio.Rendering;

namespace CrimiradTailoring.Studio
{
    public sealed class StudioWindow : GameWindow
    {
        // Scene Data
        private readonly List<ClothEntity> _cloths = new();
        private readonly List<ClothConnection> _connections = new();
        private int _clothCounter = 0;

        // Selection & Multi-Selection (Shift + Click)
        private ClothEntity? _selectedCloth;
        private ControlPoint? _selectedPoint;
        private ControlEdge? _selectedEdge;
        private readonly List<(ClothEntity cloth, ControlPoint point)> _selectedPoints = new();
        private readonly List<(ClothEntity cloth, ControlEdge edge)> _selectedEdges = new();
        private readonly List<ClothEntity> _selectedCloths = new();
        private readonly HashSet<Guid> _selectedClothIds = new();
        private readonly HashSet<Guid> _selectedPointIds = new();
        private readonly HashSet<Guid> _selectedEdgeIds = new();

        // Active Tool & Sub-Options
        private EditorToolType _activeTool = EditorToolType.Select;
        private SelectTargetFilter _selectFilter = SelectTargetFilter.Region;
        private RegionShapeMode _regionShape = RegionShapeMode.Box;
        private float _coneHalfAngleDeg = 28f;
        private bool _moveDepthZ = false;
        private int _rotateAxisLock = 0; // 0=Free, 1=X, 2=Y, 3=Z
        private ConnectMode _connectMode = ConnectMode.CreateEdge;
        private float _hangDistance = 0.14f;

        // Connect Step State
        private ClothEntity? _connectFirstCloth;
        private ControlPoint? _connectFirstPoint;
        private ControlEdge? _connectFirstEdge;

        // View Options
        private ViewportShadingMode _shadingMode = ViewportShadingMode.TexturedGrid;
        private bool _showWireOverlay = false;
        private bool _showFloorGrid = true;
        private bool _showElements = true;
        private bool _showInfluence = true;
        private bool _showMoveGizmo = true;
        private bool _showNormals = false;
        private bool _showUiPanels = true;

        // Camera State
        private double _camYawDeg = 22.0;
        private double _camPitchDeg = 14.0;
        private double _camDistance = 1.52;
        private Vec3 _camTarget = Vec3.Zero;

        // Viewport Mouse State
        private bool _isOrbitingCamera;
        private bool _isPanningCamera;
        private bool _isDraggingRegionSelect;
        private Vec3 _regionSelectStart3D;
        private bool _isDraggingMoveElement;
        private bool _isDraggingRotateCloth;
        private GizmoAxis _activeGizmoAxis = GizmoAxis.None;
        private Vector2 _lastMousePos;
        private Vector2 _rightDownPos;

        // Navigation Gizmo Top-Right (Blender 6-Axis)
        private sealed class NavAxisDef
        {
            public Vec3 Vec;
            public string Label = "";
            public string Desc = "";
            public double Yaw;
            public double Pitch;
            public bool IsPos;
            public uint FillCol;
            public uint StrokeCol;
            public float Radius;
            public Vector2 ScreenPt;
            public float Depth;
        }
        private NavAxisDef[] _navAxes = Array.Empty<NavAxisDef>();
        private int _hoveredNavIdx = -1;
        private bool _isDraggingNavGizmo = false;
        private Vector2 _navDownPos;
        private Vector2 _navLastPos;

        // Context Menu (Right click cloth)
        private ClothEntity? _contextMenuCloth;
        private bool _openShadeContextNextFrame = false;

        // Renderers
        private readonly GlClothViewportRenderer _glRenderer = new();
        private ImGuiController _imgui = null!;

        // Undo / Redo History
        private readonly Stack<EditorSnapshot> _undoStack = new();
        private readonly Stack<EditorSnapshot> _redoStack = new();
        private EditorSnapshot? _pendingDragUndoSnapshot;
        private const int MaxUndoSteps = 50;

        private string _statusText = "Sẵn sàng - Tấm vải phẳng màu xám (24x24 tứ giác). Dùng Select Vùng + Cut, hoặc nhấn [A] để tạo Điểm.";
        private string? _alertMessage;
        private readonly bool _screenshotMode;
        private int _framesRendered = 0;

        private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
        {
            IncludeFields = true,
            IgnoreReadOnlyProperties = true
        };

        public StudioWindow(GameWindowSettings gwSettings, NativeWindowSettings nwSettings, bool screenshotMode)
            : base(gwSettings, nwSettings)
        {
            _screenshotMode = screenshotMode;
        }

        protected override void OnLoad()
        {
            base.OnLoad();
            VSync = VSyncMode.On;
            _glRenderer.EnsureInitialized();
            _imgui = new ImGuiController(ClientSize.X, ClientSize.Y);
            InitNavGizmo();
            BuildCleanGraySquareScene();
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            if (_imgui != null && e.Width > 0 && e.Height > 0)
            {
                _imgui.WindowResized(e.Width, e.Height);
            }
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            ImGuiController.HandleMouseWheel(e.OffsetX, e.OffsetY);
            if (!ImGui.GetIO().WantCaptureMouse)
            {
                double zoomFactor = e.OffsetY > 0 ? 0.90 : 1.11;
                _camDistance = Math.Max(0.40, Math.Min(4.8, _camDistance * zoomFactor));
            }
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            ImGuiController.HandleTextInput(e.Unicode);
        }

        protected override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            base.OnKeyDown(e);
            ImGuiController.HandleKeyEvent(e.Key, true);

            if (ImGui.GetIO().WantTextInput)
                return;

            bool ctrl = e.Control;
            if (ctrl && !e.Shift && e.Key == Keys.Z)
            {
                PerformUndo();
                return;
            }
            if ((ctrl && e.Key == Keys.Y) || (ctrl && e.Shift && e.Key == Keys.Z))
            {
                PerformRedo();
                return;
            }

            if (e.Modifiers != 0)
                return;

            switch (e.Key)
            {
                case Keys.G:
                    _activeTool = EditorToolType.Move;
                    ResetConnectStep();
                    _statusText = "Phím tắt [G]: Công cụ Move (Di chuyển / Giữ Shift kéo chiều sâu Z).";
                    break;
                case Keys.R:
                    _activeTool = EditorToolType.Rotate;
                    ResetConnectStep();
                    _statusText = "Phím tắt [R]: Công cụ Rotate (Xoay vật thể).";
                    break;
                case Keys.S:
                    _activeTool = EditorToolType.Scale;
                    ResetConnectStep();
                    _statusText = "Phím tắt [S]: Công cụ Scale (Phóng to / Thu nhỏ).";
                    break;
                case Keys.A:
                    _activeTool = EditorToolType.CreatePoint;
                    ResetConnectStep();
                    _statusText = "Phím tắt [A]: Công cụ Create (Click mặt vải tạo Điểm).";
                    break;
                case Keys.F:
                    if (_selectedPoints.Count >= 2 || _selectedEdges.Count >= 2)
                    {
                        ExecuteConnectOnMultiSelection();
                    }
                    else
                    {
                        _activeTool = EditorToolType.Connect;
                        ResetConnectStep();
                        _statusText = "Phím tắt [F]: Công cụ Nối (Cạnh / Snap / Hang).";
                    }
                    break;
                case Keys.Escape:
                    _activeTool = EditorToolType.Select;
                    ResetConnectStep();
                    _statusText = "Phím tắt [Esc]: Công cụ Select.";
                    break;
                case Keys.X:
                case Keys.Delete:
                    if (_selectedEdge != null || _selectedPoint != null)
                        DeleteSelectedElement();
                    else if (_selectedCloth != null)
                        DeleteSelectedCloth();
                    break;
            }
        }

        protected override void OnKeyUp(KeyboardKeyEventArgs e)
        {
            base.OnKeyUp(e);
            ImGuiController.HandleKeyEvent(e.Key, false);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (_isDraggingNavGizmo) return;
            if (ImGui.GetIO().WantCaptureMouse) return;

            Vector2 mp = new(MouseState.X, MouseState.Y);
            _lastMousePos = mp;

            if (e.Button == MouseButton.Right)
            {
                _rightDownPos = mp;
                _isOrbitingCamera = true;
                return;
            }
            if (e.Button == MouseButton.Middle)
            {
                _isPanningCamera = true;
                return;
            }
            if (e.Button == MouseButton.Left)
            {
                HandleLeftMouseDownOn3D(mp);
            }
        }

        protected override void OnMouseMove(MouseMoveEventArgs e)
        {
            base.OnMouseMove(e);
            Vector2 mp = new(e.X, e.Y);
            Vector2 delta = mp - _lastMousePos;
            _lastMousePos = mp;

            if (_isOrbitingCamera)
            {
                _camYawDeg -= delta.X * 0.42;
                _camPitchDeg = Math.Max(-89.5, Math.Min(89.5, _camPitchDeg + delta.Y * 0.42));
                return;
            }
            if (_isPanningCamera)
            {
                GetCameraBasis(out Vec3 camRight, out Vec3 camUp, out _);
                float panScale = (float)(_camDistance * 0.0014);
                _camTarget = new Vec3(
                    _camTarget.X - camRight.X * delta.X * panScale + camUp.X * delta.Y * panScale,
                    _camTarget.Y - camRight.Y * delta.X * panScale + camUp.Y * delta.Y * panScale,
                    _camTarget.Z - camRight.Z * delta.X * panScale + camUp.Z * delta.Y * panScale);
                return;
            }
            if (_isDraggingRegionSelect && _selectedCloth != null)
            {
                if (TryProjectMouseOntoClothPlane(mp, _regionSelectStart3D, out Vec3 currWorld))
                {
                    bool additive = KeyboardState.IsKeyDown(Keys.LeftControl) || KeyboardState.IsKeyDown(Keys.RightControl);
                    ClothSculptEngine.SelectRegionOnCloth(
                        _selectedCloth, _regionShape, _regionSelectStart3D, currWorld, _coneHalfAngleDeg, additive);
                    _glRenderer.MarkClothSelectionDirty(_selectedCloth.Id);
                }
                return;
            }
            if (_isDraggingMoveElement && _selectedCloth != null)
            {
                float moveScale = (float)(_camDistance * 0.0014);
                GetCameraBasis(out Vec3 camRight, out Vec3 camUp, out Vec3 camForward);
                Vec3 worldDelta;
                if (_activeGizmoAxis == GizmoAxis.X || _activeGizmoAxis == GizmoAxis.Y || _activeGizmoAxis == GizmoAxis.Z)
                {
                    Vec3 axisDir = _activeGizmoAxis == GizmoAxis.X ? Vec3.Right :
                                   _activeGizmoAxis == GizmoAxis.Y ? Vec3.Up : Vec3.Forward;
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
                    bool shift = KeyboardState.IsKeyDown(Keys.LeftShift) || KeyboardState.IsKeyDown(Keys.RightShift);
                    bool moveZ = _moveDepthZ || shift;
                    worldDelta = moveZ
                        ? camForward * (-delta.Y * moveScale * 1.35f) + camRight * (delta.X * moveScale)
                        : camRight * (delta.X * moveScale) + camUp * (-delta.Y * moveScale);
                }
                ApplyDeltaMoveToSelection(worldDelta);
                return;
            }
            if (_isDraggingRotateCloth)
            {
                var targets = _selectedCloths.Count > 0
                    ? _selectedCloths
                    : (_selectedCloth != null ? new List<ClothEntity> { _selectedCloth } : new List<ClothEntity>());

                if (targets.Count > 0 && (Math.Abs(delta.X) > 0.01f || Math.Abs(delta.Y) > 0.01f))
                {
                    CommitPendingDragUndo();
                    GetCameraBasis(out Vec3 camRight, out Vec3 camUp, out _);
                    float dH = delta.X * 0.55f;
                    float dV = delta.Y * 0.55f;

                    foreach (var c in targets)
                    {
                        Vec3 pivot = ClothSculptEngine.GetClothCentroid(c);
                        if (_rotateAxisLock == 1)
                        {
                            ClothSculptEngine.RotateClothAroundAxis(c, Vec3.Right, dH + dV, pivot);
                            c.RotXDeg = ClampEuler180(c.RotXDeg + dH + dV);
                        }
                        else if (_rotateAxisLock == 2)
                        {
                            ClothSculptEngine.RotateClothAroundAxis(c, Vec3.Up, dH, pivot);
                            c.RotYDeg = ClampEuler180(c.RotYDeg + dH);
                        }
                        else if (_rotateAxisLock == 3)
                        {
                            ClothSculptEngine.RotateClothAroundAxis(c, Vec3.Forward, -dH, pivot);
                            c.RotZDeg = ClampEuler180(c.RotZDeg - dH);
                        }
                        else
                        {
                            if (Math.Abs(dH) > 1e-3f)
                            {
                                ClothSculptEngine.RotateClothAroundAxis(c, camUp, dH, pivot);
                                c.RotYDeg = ClampEuler180(c.RotYDeg + dH);
                            }
                            if (Math.Abs(dV) > 1e-3f)
                            {
                                ClothSculptEngine.RotateClothAroundAxis(c, camRight, dV, pivot);
                                c.RotXDeg = ClampEuler180(c.RotXDeg + dV);
                            }
                        }
                        _glRenderer.MarkClothDirty(c.Id);
                    }
                    ApplyAllConstraints();
                    if (_selectedCloth != null)
                        _statusText = $"Đang xoay [{_selectedCloth.Name}] - X:{_selectedCloth.RotXDeg:F0}°  Y:{_selectedCloth.RotYDeg:F0}°  Z:{_selectedCloth.RotZDeg:F0}°";
                }
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (_isOrbitingCamera || _isPanningCamera || _isDraggingRegionSelect || _isDraggingMoveElement || _isDraggingRotateCloth)
            {
                bool wasShortRight = _isOrbitingCamera &&
                    e.Button == MouseButton.Right &&
                    Vector2.Distance(new Vector2(MouseState.X, MouseState.Y), _rightDownPos) < 4.0f;

                _isOrbitingCamera = false;
                _isPanningCamera = false;
                _isDraggingRegionSelect = false;
                _isDraggingMoveElement = false;
                _isDraggingRotateCloth = false;
                _activeGizmoAxis = GizmoAxis.None;
                _pendingDragUndoSnapshot = null;

                if (wasShortRight)
                {
                    RaycastScene(new Vector2(MouseState.X, MouseState.Y), out _, out ClothEntity? hitCloth, out _, out _, out _, allowGizmo: false);
                    if (hitCloth != null || _selectedPoints.Count >= 2 || _selectedEdges.Count >= 2)
                    {
                        if (hitCloth != null && _selectedPoints.Count < 2 && _selectedEdges.Count < 2)
                            _selectedCloth = hitCloth;
                        _contextMenuCloth = hitCloth ?? _selectedCloth;
                        _openShadeContextNextFrame = true;
                    }
                }
            }
        }

        private bool IsTransformGizmoVisibleForTool() =>
            _showMoveGizmo &&
            (_activeTool == EditorToolType.Move ||
             _activeTool == EditorToolType.Rotate ||
             _activeTool == EditorToolType.Scale ||
             _activeTool == EditorToolType.Select);

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            base.OnRenderFrame(args);
            if (ClientSize.X <= 0 || ClientSize.Y <= 0) return;

            // 1. Render 3D Scene trực tiếp trên OpenGL 3.3 GPU
            _glRenderer.RenderFrame(
                ClientSize.X, ClientSize.Y,
                _camYawDeg, _camPitchDeg, _camDistance, _camTarget,
                _cloths, _connections,
                _selectedCloth, _selectedPoint, _selectedEdge,
                _connectFirstPoint, _connectFirstEdge,
                _selectedClothIds, _selectedPointIds, _selectedEdgeIds,
                _activeGizmoAxis,
                _shadingMode,
                _showWireOverlay, _showNormals, _showFloorGrid,
                _showElements, _showInfluence, IsTransformGizmoVisibleForTool());

            // 2. Render Toàn Bộ Giao Diện ImGui trên cùng Ngữ Cảnh OpenGL (0ms delay!)
            _imgui.Update(this, (float)args.Time);
            RenderImGuiInterface((float)args.Time);
            _imgui.Render();

            if (_screenshotMode)
            {
                _framesRendered++;
                if (_framesRendered >= 2)
                {
                    CaptureBackbufferScreenshot();
                    Close();
                    return;
                }
            }

            SwapBuffers();
        }

        private void CaptureBackbufferScreenshot()
        {
            int w = ClientSize.X;
            int h = ClientSize.Y;
            byte[] pixels = new byte[w * h * 4];
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            string outPath = @"C:\Users\khoar\.gemini\antigravity\brain\590c1375-180b-452d-8e75-aa5ae2ea8453\scratch\viewport_debug.png";
            NativeFileDialog.WriteGlRgbaPng(outPath, w, h, pixels);
        }

        protected override void OnUnload()
        {
            _glRenderer.Dispose();
            _imgui?.Dispose();
            base.OnUnload();
        }

        #region Scene & Actions

        private void BuildCleanGraySquareScene()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _cloths.Clear();
            _connections.Clear();
            _glRenderer.MarkAllDirty();
            _clothCounter = 1;

            var cloth = ClothSculptEngine.CreateSquareClothSheet(_clothCounter, sizeMeters: 0.84f, resolution: 24);
            cloth.ClothColor = new ColorRgba(156, 162, 172);
            _cloths.Add(cloth);
            SelectSingleCloth(cloth);
            _statusText = "Sẵn sàng - Tấm vải phẳng màu xám (24x24 tứ giác). Dùng Select Vùng + Cut, hoặc nhấn [A] để tạo Điểm.";
        }

        private void BuildDemoFoldedClothScene()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            _cloths.Clear();
            _connections.Clear();
            _glRenderer.MarkAllDirty();
            _clothCounter = 1;

            var cloth = ClothSculptEngine.CreateSquareClothSheet(_clothCounter, sizeMeters: 0.84f, resolution: 24);
            _cloths.Add(cloth);
            SelectSingleCloth(cloth);

            var p1 = ClothSculptEngine.CreatePointOnClothSurface(cloth, new Vec3(0.14f, 0.22f, 0f));
            var p2 = ClothSculptEngine.CreatePointOnClothSurface(cloth, new Vec3(-0.18f, -0.28f, 0f));
            var edge = ClothSculptEngine.ConnectPointsToEdge(cloth, p1, p2);
            if (edge != null)
            {
                edge.Radius = 0.28f;
                edge.Bending = 0.78f;
                ClothSculptEngine.MoveControlEdge(cloth, edge, new Vec3(0f, 0f, 0.14f), new Vec3(0.02f, 0.03f, 0.18f));
                SelectSingleEdge(cloth, edge);
            }
            _glRenderer.MarkAllDirty();
            _statusText = "Đã tải mẫu uốn cong P1-P2.";
        }

        private void SpawnSquareCloth()
        {
            RecordUndoState("Spawn Vải Vuông");
            _clothCounter++;
            float offsetX = ((_cloths.Count % 3) - 1) * 0.18f;
            float offsetZ = _cloths.Count * 0.06f;
            var newCloth = ClothSculptEngine.CreateSquareClothSheet(_clothCounter, 0.84f, 24, new Vec3(offsetX, 0f, offsetZ));
            newCloth.ClothColor = new ColorRgba(156, 162, 172);
            _cloths.Add(newCloth);
            SelectSingleCloth(newCloth);
            _glRenderer.MarkAllDirty();
            _statusText = $"Đã Spawn [{newCloth.Name}] (84x84cm).";
        }

        private void ExecuteCut(CutActionMode mode)
        {
            var target = _selectedCloth ?? _cloths.FirstOrDefault(c => c.SelectedFaces.Count > 0 || c.SelectedTriangles.Count > 0);
            if (target == null || (target.SelectedFaces.Count == 0 && target.SelectedTriangles.Count == 0))
            {
                _alertMessage = "Bạn chưa chọn vùng vải nào để cắt! Hãy dùng Select -> Vùng và kéo chuột trên mặt vải trước.";
                return;
            }

            RecordUndoState(mode == CutActionMode.ExtractPiece ? "Cut Tách Mảnh" : "Cut Khoét Lỗ");
            _clothCounter++;
            if (ClothSculptEngine.CutSelectedRegion(target, mode, _clothCounter, out ClothEntity? extracted))
            {
                _glRenderer.MarkClothDirty(target.Id);
                if (extracted != null)
                {
                    _cloths.Add(extracted);
                    _glRenderer.MarkClothDirty(extracted.Id);
                    SelectSingleCloth(extracted);
                    _statusText = $"Đã tách [{extracted.Name}] ({extracted.Mesh.Faces.Count} mặt tứ giác sạch)!";
                }
                else
                {
                    _statusText = $"Đã khoét lỗ trên [{target.Name}] với lưới O-Grid tứ giác sạch!";
                }
            }
        }

        private void DeleteSelectedCloth()
        {
            if (_selectedCloth == null) return;
            RecordUndoState("Xóa mảnh vải");
            Guid id = _selectedCloth.Id;
            _cloths.Remove(_selectedCloth);
            _connections.RemoveAll(c => c.SourceClothId == id || c.TargetClothId == id);
            var next = _cloths.FirstOrDefault();
            if (next != null) SelectSingleCloth(next);
            else { ClearMultiSelections(); _selectedCloth = null; _selectedPoint = null; _selectedEdge = null; }
            _statusText = "Đã xóa mảnh vải.";
        }

        private void DeleteSelectedElement()
        {
            if (_selectedCloth == null || (_selectedEdge == null && _selectedPoint == null)) return;
            RecordUndoState(_selectedEdge != null ? "Xóa Cạnh" : "Xóa Điểm");
            if (_selectedEdge != null)
            {
                Guid eId = _selectedEdge.Id;
                _selectedCloth.Edges.Remove(_selectedEdge);
                _connections.RemoveAll(c => c.SourceElementId == eId || c.TargetElementId == eId);
                SelectSingleCloth(_selectedCloth);
            }
            else if (_selectedPoint != null)
            {
                Guid pId = _selectedPoint.Id;
                _selectedCloth.Edges.RemoveAll(ed => ed.PointAId == pId || ed.PointBId == pId);
                _selectedCloth.Points.Remove(_selectedPoint);
                _connections.RemoveAll(c => c.SourceElementId == pId || c.TargetElementId == pId);
                SelectSingleCloth(_selectedCloth);
            }
            _statusText = "Đã xóa Điểm/Cạnh (Ctrl+Z để hoàn tác).";
        }

        #endregion

        #region 3D Picking & Transforms

        private void HandleLeftMouseDownOn3D(Vector2 mousePos)
        {
            bool shift = KeyboardState.IsKeyDown(Keys.LeftShift) || KeyboardState.IsKeyDown(Keys.RightShift);
            bool allowGizmo = !shift &&
                              _activeTool != EditorToolType.CreatePoint &&
                              _activeTool != EditorToolType.Connect &&
                              _activeTool != EditorToolType.Rotate;

            RaycastScene(mousePos, out GizmoAxis hitGizmo, out ClothEntity? hitCloth, out ControlPoint? hitPt, out ControlEdge? hitEdge, out Vec3 hitWorld, allowGizmo);

            if (allowGizmo && hitGizmo != GizmoAxis.None && _selectedCloth != null)
            {
                _pendingDragUndoSnapshot = CaptureSnapshot("Di chuyển bằng Gizmo");
                _activeGizmoAxis = hitGizmo;
                _isDraggingMoveElement = true;
                return;
            }

            if (_activeTool == EditorToolType.CreatePoint)
            {
                if (hitEdge != null && hitCloth != null)
                {
                    RecordUndoState("Tạo Điểm trên Cạnh");
                    var midPt = ClothSculptEngine.CreatePointOnExistingEdge(hitCloth, hitEdge, hitWorld, out _);
                    SelectSinglePoint(hitCloth, midPt);
                    _statusText = $"Đã tạo Điểm [{midPt.Name}] trên Cạnh [{hitEdge.Name}].";
                }
                else if (hitCloth != null)
                {
                    RecordUndoState("Tạo Điểm trên Vải");
                    var newPt = ClothSculptEngine.CreatePointOnClothSurface(hitCloth, hitWorld);
                    SelectSinglePoint(hitCloth, newPt);
                    _statusText = $"Đã tạo Điểm [{newPt.Name}] tại {newPt.Position}.";
                }
                return;
            }

            if (_activeTool == EditorToolType.Connect)
            {
                HandleConnectToolClick(hitCloth, hitPt, hitEdge);
                return;
            }

            if (_activeTool == EditorToolType.Rotate)
            {
                if (hitCloth != null)
                {
                    if (shift) ToggleMultiSelectCloth(hitCloth);
                    else if (_selectedCloths.Count <= 1) SelectSingleCloth(hitCloth);
                }
                if (_selectedCloth != null || _selectedCloths.Count > 0)
                {
                    _pendingDragUndoSnapshot = CaptureSnapshot("Xoay vật thể");
                    _isDraggingRotateCloth = true;
                }
                return;
            }

            if (_activeTool == EditorToolType.Move)
            {
                if (hitPt != null && hitCloth != null)
                {
                    if (shift) ToggleMultiSelectPoint(hitCloth, hitPt);
                    else if (_selectedPoints.Count <= 1) SelectSinglePoint(hitCloth, hitPt);
                }
                else if (hitEdge != null && hitCloth != null)
                {
                    if (shift) ToggleMultiSelectEdge(hitCloth, hitEdge);
                    else if (_selectedEdges.Count <= 1) SelectSingleEdge(hitCloth, hitEdge);
                }
                else if (hitCloth != null && _selectedPoint == null && _selectedEdge == null)
                {
                    if (shift) ToggleMultiSelectCloth(hitCloth);
                    else if (_selectedCloths.Count <= 1) SelectSingleCloth(hitCloth);
                }

                if (_selectedPoint != null || _selectedEdge != null || _selectedCloth != null)
                {
                    _pendingDragUndoSnapshot = CaptureSnapshot("Di chuyển thành phần");
                    _isDraggingMoveElement = true;
                }
                return;
            }

            // Select / Cut
            if (_activeTool == EditorToolType.Select && (_selectFilter == SelectTargetFilter.Point || hitPt != null))
            {
                if (hitPt != null && hitCloth != null)
                {
                    if (shift) ToggleMultiSelectPoint(hitCloth, hitPt);
                    else SelectSinglePoint(hitCloth, hitPt);
                    _statusText = _selectedPoints.Count >= 2
                        ? $"Đã chọn {_selectedPoints.Count} Điểm - Chuột Phải -> Connect (hoặc nhấn [F])!"
                        : $"Đã chọn Điểm [{hitPt.Name}] (Shift+Click chọn thêm rồi Chuột Phải để Connect).";
                    return;
                }
            }

            if (_activeTool == EditorToolType.Select && (_selectFilter == SelectTargetFilter.Edge || hitEdge != null))
            {
                if (hitEdge != null && hitCloth != null)
                {
                    if (shift) ToggleMultiSelectEdge(hitCloth, hitEdge);
                    else SelectSingleEdge(hitCloth, hitEdge);
                    _statusText = _selectedEdges.Count >= 2
                        ? $"Đã chọn {_selectedEdges.Count} Cạnh - Nhấn [F] để Snap / Hang!"
                        : $"Đã chọn Cạnh [{hitEdge.Name}] (Shift+Click cạnh khác để Snap/Hang).";
                    return;
                }
            }

            if (_activeTool == EditorToolType.Select && (_selectFilter == SelectTargetFilter.ClothObject || shift))
            {
                if (hitCloth != null)
                {
                    if (shift) ToggleMultiSelectCloth(hitCloth);
                    else SelectSingleCloth(hitCloth);
                    _statusText = _selectedCloths.Count >= 2
                        ? $"Đã chọn {_selectedCloths.Count} vật thể."
                        : $"Đã chọn vật thể [{hitCloth.Name}] (Shift+Click chọn thêm).";
                }
                return;
            }

            // Quét vùng
            if (hitCloth != null)
            {
                SelectSingleCloth(hitCloth);
                _regionSelectStart3D = hitWorld;
                _isDraggingRegionSelect = true;
                bool additive = KeyboardState.IsKeyDown(Keys.LeftControl) || KeyboardState.IsKeyDown(Keys.RightControl);
                ClothSculptEngine.SelectRegionOnCloth(hitCloth, _regionShape, _regionSelectStart3D, hitWorld, _coneHalfAngleDeg, additive);
                _glRenderer.MarkClothSelectionDirty(hitCloth.Id);
            }
        }

        private void HandleConnectToolClick(ClothEntity? hitCloth, ControlPoint? hitPoint, ControlEdge? hitEdge)
        {
            if (hitCloth == null || (hitPoint == null && hitEdge == null))
            {
                _statusText = "Hãy click trực tiếp vào một Điểm hoặc một Cạnh để Nối / Snap!";
                return;
            }

            if (_connectFirstPoint == null && _connectFirstEdge == null)
            {
                if (_connectMode == ConnectMode.CreateEdge && hitPoint == null)
                {
                    _statusText = "Mode Tạo Cạnh cần chọn 2 Điểm trên cùng mảnh vải!";
                    return;
                }
                _connectFirstCloth = hitCloth;
                _connectFirstPoint = hitPoint;
                _connectFirstEdge = hitEdge;
                _selectedCloth = hitCloth;
                _selectedPoint = hitPoint;
                _selectedEdge = hitEdge;
                _statusText = "Đã chọn thành phần 1 -> Click thành phần 2 để Nối!";
                return;
            }

            if (_connectMode == ConnectMode.CreateEdge)
            {
                if (_connectFirstPoint != null && hitPoint != null && _connectFirstCloth != null && _connectFirstCloth.Id == hitCloth.Id)
                {
                    RecordUndoState("Nối 2 Điểm tạo Cạnh");
                    var edge = ClothSculptEngine.ConnectPointsToEdge(hitCloth, _connectFirstPoint, hitPoint);
                    if (edge != null)
                    {
                        SelectSingleEdge(hitCloth, edge);
                        _statusText = $"Đã nối [{_connectFirstPoint.Name}] - [{hitPoint.Name}] thành Cạnh [{edge.Name}]!";
                    }
                }
                else
                {
                    _statusText = "Tạo Cạnh yêu cầu 2 Điểm cùng thuộc một mảnh vải.";
                }
            }
            else if (_connectFirstPoint != null && hitPoint != null && _connectFirstCloth != null && _connectFirstPoint.Id != hitPoint.Id)
            {
                RecordUndoState(_connectMode == ConnectMode.Snap ? "Snap Điểm" : "Hang Điểm");
                var conn = new ClothConnection
                {
                    Mode = _connectMode,
                    IsEdgeConnection = false,
                    SourceClothId = _connectFirstCloth.Id,
                    SourceElementId = _connectFirstPoint.Id,
                    TargetClothId = hitCloth.Id,
                    TargetElementId = hitPoint.Id,
                    HangDistance = _hangDistance
                };
                _connections.Add(conn);
                ClothSculptEngine.ApplyConnectionConstraint(_cloths, conn);
                _glRenderer.MarkAllDirty();
                _statusText = _connectMode == ConnectMode.Snap
                    ? $"Đã SNAP [{_connectFirstPoint.Name}] vào [{hitPoint.Name}]!"
                    : $"Đã HANG [{_connectFirstPoint.Name}] dưới [{hitPoint.Name}] ({_hangDistance:F2}m)!";
            }
            else if (_connectFirstEdge != null && hitEdge != null && _connectFirstCloth != null && _connectFirstEdge.Id != hitEdge.Id)
            {
                RecordUndoState(_connectMode == ConnectMode.Snap ? "Snap Cạnh" : "Hang Cạnh");
                var conn = new ClothConnection
                {
                    Mode = _connectMode,
                    IsEdgeConnection = true,
                    SourceClothId = _connectFirstCloth.Id,
                    SourceElementId = _connectFirstEdge.Id,
                    TargetClothId = hitCloth.Id,
                    TargetElementId = hitEdge.Id,
                    HangDistance = _hangDistance
                };
                _connections.Add(conn);
                ClothSculptEngine.ApplyConnectionConstraint(_cloths, conn);
                _glRenderer.MarkAllDirty();
                _statusText = _connectMode == ConnectMode.Snap
                    ? $"Đã SNAP Cạnh [{_connectFirstEdge.Name}] vào [{hitEdge.Name}]!"
                    : $"Đã HANG Cạnh [{_connectFirstEdge.Name}] dưới [{hitEdge.Name}]!";
            }

            ResetConnectStep();
        }

        private bool ExecuteConnectOnMultiSelection(ConnectMode? overrideMode = null)
        {
            ConnectMode chosenMode = overrideMode ?? _connectMode;

            if (_selectedPoints.Count >= 2)
            {
                var p1 = _selectedPoints[0];
                var p2 = _selectedPoints[1];
                if (chosenMode == ConnectMode.CreateEdge && p1.cloth.Id == p2.cloth.Id)
                {
                    RecordUndoState("Nối Điểm tạo Cạnh");
                    ControlEdge? lastCreated = null;
                    int createdCount = 0;
                    for (int i = 0; i < _selectedPoints.Count - 1; i++)
                    {
                        var a = _selectedPoints[i];
                        var b = _selectedPoints[i + 1];
                        if (a.cloth.Id == b.cloth.Id)
                        {
                            var ed = ClothSculptEngine.ConnectPointsToEdge(a.cloth, a.point, b.point);
                            if (ed != null) { lastCreated = ed; createdCount++; }
                        }
                    }
                    if (lastCreated != null)
                    {
                        SelectSingleEdge(p1.cloth, lastCreated);
                        _statusText = createdCount == 1
                            ? $"Đã Connect [{p1.point.Name}] - [{p2.point.Name}] thành Cạnh [{lastCreated.Name}]!"
                            : $"Đã Connect tạo {createdCount} Cạnh nối tiếp!";
                        return true;
                    }
                    return false;
                }

                ConnectMode mode = (chosenMode == ConnectMode.CreateEdge && p1.cloth.Id != p2.cloth.Id) ? ConnectMode.Snap : chosenMode;
                RecordUndoState(mode == ConnectMode.Snap ? "Snap Điểm" : "Hang Điểm");
                var conn = new ClothConnection
                {
                    Mode = mode,
                    IsEdgeConnection = false,
                    SourceClothId = p1.cloth.Id,
                    SourceElementId = p1.point.Id,
                    TargetClothId = p2.cloth.Id,
                    TargetElementId = p2.point.Id,
                    HangDistance = _hangDistance
                };
                _connections.Add(conn);
                ClothSculptEngine.ApplyConnectionConstraint(_cloths, conn);
                _glRenderer.MarkAllDirty();
                _statusText = mode == ConnectMode.Snap
                    ? $"Đã SNAP [{p1.point.Name}] vào [{p2.point.Name}]!"
                    : $"Đã HANG [{p1.point.Name}] dưới [{p2.point.Name}]!";
                return true;
            }

            if (_selectedEdges.Count >= 2)
            {
                var e1 = _selectedEdges[0];
                var e2 = _selectedEdges[1];
                ConnectMode mode = chosenMode == ConnectMode.Hang ? ConnectMode.Hang : ConnectMode.Snap;
                RecordUndoState(mode == ConnectMode.Snap ? "Snap Cạnh" : "Hang Cạnh");
                var conn = new ClothConnection
                {
                    Mode = mode,
                    IsEdgeConnection = true,
                    SourceClothId = e1.cloth.Id,
                    SourceElementId = e1.edge.Id,
                    TargetClothId = e2.cloth.Id,
                    TargetElementId = e2.edge.Id,
                    HangDistance = _hangDistance
                };
                _connections.Add(conn);
                ClothSculptEngine.ApplyConnectionConstraint(_cloths, conn);
                _glRenderer.MarkAllDirty();
                _statusText = $"Đã {mode} Cạnh [{e1.edge.Name}] -> [{e2.edge.Name}]!";
                return true;
            }

            return false;
        }

        private void ApplyDeltaMoveToSelection(Vec3 worldDelta)
        {
            if (_selectedCloth == null || worldDelta.LengthSquared < 1e-10f) return;
            CommitPendingDragUndo();

            if (_selectedPoints.Count > 1)
            {
                foreach (var p in _selectedPoints)
                {
                    ClothSculptEngine.MoveControlPoint(p.cloth, p.point, worldDelta);
                    _glRenderer.MarkClothDirty(p.cloth.Id);
                }
            }
            else if (_selectedPoint != null)
            {
                ClothSculptEngine.MoveControlPoint(_selectedCloth, _selectedPoint, worldDelta);
            }
            else if (_selectedEdges.Count > 1)
            {
                foreach (var ed in _selectedEdges)
                {
                    ClothSculptEngine.MoveControlEdge(ed.cloth, ed.edge, worldDelta, worldDelta);
                    _glRenderer.MarkClothDirty(ed.cloth.Id);
                }
            }
            else if (_selectedEdge != null)
            {
                ClothSculptEngine.MoveControlEdge(_selectedCloth, _selectedEdge, worldDelta, worldDelta);
            }
            else if (_selectedCloths.Count > 1)
            {
                foreach (var c in _selectedCloths)
                {
                    ClothSculptEngine.TranslateEntireCloth(c, worldDelta);
                    _glRenderer.MarkClothDirty(c.Id);
                }
            }
            else
            {
                ClothSculptEngine.TranslateEntireCloth(_selectedCloth, worldDelta);
            }

            _glRenderer.MarkClothDirty(_selectedCloth.Id);
            ApplyAllConstraints();
        }

        private void ApplyAllConstraints()
        {
            foreach (var conn in _connections)
            {
                ClothSculptEngine.ApplyConnectionConstraint(_cloths, conn);
                _glRenderer.MarkClothDirty(conn.SourceClothId);
            }
        }

        private void RaycastScene(
            Vector2 mousePos,
            out GizmoAxis hitGizmo,
            out ClothEntity? hitCloth,
            out ControlPoint? hitPoint,
            out ControlEdge? hitEdge,
            out Vec3 hitWorldPos,
            bool allowGizmo = true)
        {
            GlClothViewportRenderer.RaycastSceneAnalytic(
                mousePos.X, mousePos.Y,
                Math.Max(1, ClientSize.X), Math.Max(1, ClientSize.Y),
                _camYawDeg, _camPitchDeg, _camDistance, _camTarget,
                _cloths, _selectedCloth, _selectedPoint, _selectedEdge,
                allowGizmo && IsTransformGizmoVisibleForTool(), _showElements,
                out hitGizmo, out hitCloth, out hitPoint, out hitEdge, out hitWorldPos);
        }

        private bool TryProjectMouseOntoClothPlane(Vector2 mousePos, Vec3 planeRefPoint, out Vec3 worldPoint)
        {
            RaycastScene(mousePos, out _, out ClothEntity? hitCloth, out _, out _, out Vec3 hitPos);
            if (hitCloth != null)
            {
                worldPoint = hitPos;
                return true;
            }

            GetCameraBasis(out _, out _, out Vec3 camForward);
            GlClothViewportRenderer.ComputeWorldRay(
                mousePos.X, mousePos.Y,
                Math.Max(1, ClientSize.X), Math.Max(1, ClientSize.Y),
                _camYawDeg, _camPitchDeg, _camDistance, _camTarget,
                out Vec3 ro, out Vec3 rd);

            float denom = Vec3.Dot(rd, camForward);
            if (Math.Abs(denom) > 1e-4f)
            {
                float t = Vec3.Dot(planeRefPoint - ro, camForward) / denom;
                if (t > 0.05f)
                {
                    worldPoint = ro + rd * t;
                    return true;
                }
            }
            worldPoint = planeRefPoint;
            return true;
        }

        private void GetCameraBasis(out Vec3 camRight, out Vec3 camUp, out Vec3 camForward)
        {
            double yawRad = _camYawDeg * Math.PI / 180.0;
            double pitchRad = _camPitchDeg * Math.PI / 180.0;
            double cosPitch = Math.Cos(pitchRad);
            camForward = new Vec3(
                (float)(-cosPitch * Math.Sin(yawRad)),
                (float)(-Math.Sin(pitchRad)),
                (float)(-cosPitch * Math.Cos(yawRad))).Normalized;
            camRight = Vec3.Cross(camForward, Vec3.Up);
            if (camRight.LengthSquared < 1e-5f) camRight = Vec3.Right;
            camRight = camRight.Normalized;
            camUp = Vec3.Cross(camRight, camForward).Normalized;
        }

        private static float ClampEuler180(float deg) => ((deg + 180f) % 360f + 360f) % 360f - 180f;

        #endregion

        #region Selection Helpers

        private void ClearMultiSelections()
        {
            _selectedPoints.Clear();
            _selectedEdges.Clear();
            _selectedCloths.Clear();
            _selectedPointIds.Clear();
            _selectedEdgeIds.Clear();
            _selectedClothIds.Clear();
        }

        private void SyncSelectionHashes()
        {
            _selectedPointIds.Clear();
            foreach (var p in _selectedPoints) _selectedPointIds.Add(p.point.Id);
            _selectedEdgeIds.Clear();
            foreach (var e in _selectedEdges) _selectedEdgeIds.Add(e.edge.Id);
            _selectedClothIds.Clear();
            foreach (var c in _selectedCloths) _selectedClothIds.Add(c.Id);
        }

        private void SelectSinglePoint(ClothEntity cloth, ControlPoint pt)
        {
            ClearMultiSelections();
            _selectedPoints.Add((cloth, pt));
            _selectedCloth = cloth;
            _selectedPoint = pt;
            _selectedEdge = null;
            SyncSelectionHashes();
        }

        private void ToggleMultiSelectPoint(ClothEntity cloth, ControlPoint pt)
        {
            _selectedEdges.Clear();
            _selectedCloths.Clear();
            _selectedEdge = null;
            int idx = _selectedPoints.FindIndex(x => x.point.Id == pt.Id);
            if (idx >= 0) { if (_selectedPoints.Count > 1) _selectedPoints.RemoveAt(idx); }
            else _selectedPoints.Add((cloth, pt));
            var last = _selectedPoints[^1];
            _selectedCloth = last.cloth;
            _selectedPoint = last.point;
            SyncSelectionHashes();
        }

        private void SelectSingleEdge(ClothEntity cloth, ControlEdge edge)
        {
            ClearMultiSelections();
            _selectedEdges.Add((cloth, edge));
            _selectedCloth = cloth;
            _selectedEdge = edge;
            _selectedPoint = null;
            SyncSelectionHashes();
        }

        private void ToggleMultiSelectEdge(ClothEntity cloth, ControlEdge edge)
        {
            _selectedPoints.Clear();
            _selectedCloths.Clear();
            _selectedPoint = null;
            int idx = _selectedEdges.FindIndex(x => x.edge.Id == edge.Id);
            if (idx >= 0) { if (_selectedEdges.Count > 1) _selectedEdges.RemoveAt(idx); }
            else _selectedEdges.Add((cloth, edge));
            var last = _selectedEdges[^1];
            _selectedCloth = last.cloth;
            _selectedEdge = last.edge;
            SyncSelectionHashes();
        }

        private void SelectSingleCloth(ClothEntity cloth)
        {
            ClearMultiSelections();
            _selectedCloths.Add(cloth);
            _selectedCloth = cloth;
            _selectedPoint = null;
            _selectedEdge = null;
            SyncSelectionHashes();
        }

        private void ToggleMultiSelectCloth(ClothEntity cloth)
        {
            _selectedPoints.Clear();
            _selectedEdges.Clear();
            _selectedPoint = null;
            _selectedEdge = null;
            int idx = _selectedCloths.FindIndex(c => c.Id == cloth.Id);
            if (idx >= 0) { if (_selectedCloths.Count > 1) _selectedCloths.RemoveAt(idx); }
            else _selectedCloths.Add(cloth);
            _selectedCloth = _selectedCloths[^1];
            SyncSelectionHashes();
        }

        private void ResetConnectStep()
        {
            _connectFirstCloth = null;
            _connectFirstPoint = null;
            _connectFirstEdge = null;
        }

        #endregion

        #region Undo / Redo & File I/O

        private void CommitPendingDragUndo()
        {
            if (_pendingDragUndoSnapshot != null)
            {
                PushUndoSnapshot(_pendingDragUndoSnapshot);
                _pendingDragUndoSnapshot = null;
            }
        }

        private void RecordUndoState(string label) => PushUndoSnapshot(CaptureSnapshot(label));

        private void PushUndoSnapshot(EditorSnapshot snap)
        {
            _undoStack.Push(snap);
            if (_undoStack.Count > MaxUndoSteps)
            {
                var keep = _undoStack.Take(MaxUndoSteps).Reverse().ToList();
                _undoStack.Clear();
                foreach (var s in keep) _undoStack.Push(s);
            }
            _redoStack.Clear();
        }

        private EditorSnapshot CaptureSnapshot(string label)
        {
            var dto = new SavedSceneDto { Cloths = _cloths, Connections = _connections };
            return new EditorSnapshot
            {
                Label = label,
                SceneJson = JsonSerializer.Serialize(dto, SnapshotJsonOptions),
                ClothCounter = _clothCounter,
                SelectedClothId = _selectedCloth?.Id,
                SelectedPointId = _selectedPoint?.Id,
                SelectedEdgeId = _selectedEdge?.Id
            };
        }

        private void RestoreSnapshot(EditorSnapshot snap)
        {
            var dto = JsonSerializer.Deserialize<SavedSceneDto>(snap.SceneJson, SnapshotJsonOptions);
            if (dto == null) return;

            _cloths.Clear();
            _connections.Clear();
            _glRenderer.MarkAllDirty();
            if (dto.Cloths != null)
            {
                foreach (var c in dto.Cloths)
                {
                    c.SelectedFaces ??= new HashSet<int>();
                    c.SelectedTriangles ??= new HashSet<int>();
                    c.SelectedVertices ??= new HashSet<int>();
                    if (c.Mesh.Faces.Count > 0 && c.Mesh.Triangles.Count == 0)
                        c.Mesh.RebuildTrianglesFromFaces();
                    _cloths.Add(c);
                }
            }
            if (dto.Connections != null)
                _connections.AddRange(dto.Connections);

            _clothCounter = snap.ClothCounter;
            _selectedCloth = snap.SelectedClothId.HasValue
                ? _cloths.FirstOrDefault(c => c.Id == snap.SelectedClothId.Value)
                : _cloths.FirstOrDefault();
            _selectedPoint = (snap.SelectedPointId.HasValue && _selectedCloth != null)
                ? _selectedCloth.FindPoint(snap.SelectedPointId.Value)
                : null;
            _selectedEdge = (snap.SelectedEdgeId.HasValue && _selectedCloth != null)
                ? _selectedCloth.FindEdge(snap.SelectedEdgeId.Value)
                : null;

            ClearMultiSelections();
            if (_selectedPoint != null && _selectedCloth != null) _selectedPoints.Add((_selectedCloth, _selectedPoint));
            else if (_selectedEdge != null && _selectedCloth != null) _selectedEdges.Add((_selectedCloth, _selectedEdge));
            else if (_selectedCloth != null) _selectedCloths.Add(_selectedCloth);
            SyncSelectionHashes();
            ResetConnectStep();
        }

        private void PerformUndo()
        {
            if (_undoStack.Count == 0) return;
            var prev = _undoStack.Pop();
            _redoStack.Push(CaptureSnapshot(prev.Label));
            RestoreSnapshot(prev);
            _statusText = $"Đã hoàn tác (Ctrl+Z): {prev.Label}";
        }

        private void PerformRedo()
        {
            if (_redoStack.Count == 0) return;
            var next = _redoStack.Pop();
            _undoStack.Push(CaptureSnapshot(next.Label));
            RestoreSnapshot(next);
            _statusText = $"Đã làm lại (Ctrl+Y): {next.Label}";
        }

        private void SaveProject()
        {
            string? path = NativeFileDialog.ShowSaveDialog(
                "Lưu File Dự Án Cloth Sculpt (.sculpt.json)",
                "Cloth Sculpt Project (*.sculpt.json)\0*.sculpt.json\0JSON (*.json)\0*.json\0",
                "MyClothProject.sculpt.json", "json");
            if (string.IsNullOrEmpty(path)) return;
            var dto = new SavedSceneDto { Cloths = _cloths, Connections = _connections };
            var opts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, IgnoreReadOnlyProperties = true };
            File.WriteAllText(path, JsonSerializer.Serialize(dto, opts));
            _statusText = $"Đã lưu dự án: {Path.GetFileName(path)}";
        }

        private void OpenProject()
        {
            string? path = NativeFileDialog.ShowOpenDialog(
                "Mở File Dự Án Cloth Sculpt (.sculpt.json)",
                "Cloth Sculpt Project (*.sculpt.json;*.json)\0*.sculpt.json;*.json\0All Files (*.*)\0*.*\0");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var opts = new JsonSerializerOptions { IncludeFields = true, IgnoreReadOnlyProperties = true };
                var dto = JsonSerializer.Deserialize<SavedSceneDto>(File.ReadAllText(path), opts);
                if (dto?.Cloths != null)
                {
                    _cloths.Clear();
                    _connections.Clear();
                    _glRenderer.MarkAllDirty();
                    foreach (var c in dto.Cloths)
                    {
                        c.SelectedFaces = new HashSet<int>();
                        c.SelectedTriangles = new HashSet<int>();
                        c.SelectedVertices = new HashSet<int>();
                        if (c.Mesh.Faces.Count > 0 && c.Mesh.Triangles.Count == 0)
                            c.Mesh.RebuildTrianglesFromFaces();
                        _cloths.Add(c);
                    }
                    if (dto.Connections != null) _connections.AddRange(dto.Connections);
                    _clothCounter = _cloths.Count;
                    var first = _cloths.FirstOrDefault();
                    if (first != null) SelectSingleCloth(first);
                    _statusText = $"Đã mở dự án: {Path.GetFileName(path)} ({_cloths.Count} mảnh vải)";
                }
            }
            catch (Exception ex) { _alertMessage = $"Lỗi mở file: {ex.Message}"; }
        }

        private void ImportObj()
        {
            string? path = NativeFileDialog.ShowOpenDialog(
                "Nhập File Mesh 3D (.OBJ)",
                "Wavefront OBJ (*.obj)\0*.obj\0All Files (*.*)\0*.*\0");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                _clothCounter++;
                RecordUndoState("Nhập Mesh .OBJ");
                var imported = ClothSculptEngine.ImportClothFromObj(path, _clothCounter);
                _cloths.Add(imported);
                SelectSingleCloth(imported);
                _glRenderer.MarkAllDirty();
                _statusText = $"Đã nhập Mesh [{imported.Name}] ({imported.Mesh.Triangles.Count / 3} tris).";
            }
            catch (Exception ex) { _alertMessage = $"Lỗi nhập .OBJ: {ex.Message}"; }
        }

        private void ExportToUnity()
        {
            string? objPath = NativeFileDialog.ShowSaveDialog(
                "Xuất File .OBJ cho Unity (tự kèm .sculpt.json)",
                "Wavefront OBJ (*.obj)\0*.obj\0",
                "CrimiradClothExport.obj", "obj");
            if (string.IsNullOrEmpty(objPath)) return;
            string jsonPath = Path.ChangeExtension(objPath, ".sculpt.json");
            ClothSculptEngine.ExportSceneToObj(objPath, _cloths);
            var opts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, IgnoreReadOnlyProperties = true };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(_cloths, opts));
            _statusText = $"Đã xuất: {Path.GetFileName(objPath)} + {Path.GetFileName(jsonPath)}";
        }

        private sealed class SavedSceneDto
        {
            public List<ClothEntity> Cloths { get; set; } = new();
            public List<ClothConnection> Connections { get; set; } = new();
        }

        private sealed class EditorSnapshot
        {
            public string Label { get; init; } = "";
            public string SceneJson { get; init; } = "";
            public int ClothCounter { get; init; }
            public Guid? SelectedClothId { get; init; }
            public Guid? SelectedPointId { get; init; }
            public Guid? SelectedEdgeId { get; init; }
        }

        #endregion

        #region Blender-Style ImGui HUD + Top-Right Navigation Gizmo

        private void InitNavGizmo()
        {
            static uint Col(byte r, byte g, byte b, byte a = 255) => (uint)((a << 24) | (b << 16) | (g << 8) | r);
            uint negFill = Col(26, 31, 44, 240);
            uint red = Col(235, 85, 95);
            uint green = Col(125, 215, 100);
            uint blue = Col(85, 175, 248);

            _navAxes = new[]
            {
                new NavAxisDef { Vec = Vec3.Right,    Label = "X",  Desc = "Mặt Phải (+X)",  Yaw = 90,   Pitch = 0,     IsPos = true,  FillCol = red,     StrokeCol = red,   Radius = 9.5f },
                new NavAxisDef { Vec = Vec3.Up,       Label = "Y",  Desc = "Từ Đỉnh (+Y)",   Yaw = 0,    Pitch = 89.5,  IsPos = true,  FillCol = green,   StrokeCol = green, Radius = 9.5f },
                new NavAxisDef { Vec = Vec3.Forward,  Label = "Z",  Desc = "Mặt Trước (+Z)", Yaw = 0,    Pitch = 0,     IsPos = true,  FillCol = blue,    StrokeCol = blue,  Radius = 9.5f },
                new NavAxisDef { Vec = -Vec3.Right,   Label = "-X", Desc = "Mặt Trái (-X)",  Yaw = -90,  Pitch = 0,     IsPos = false, FillCol = negFill, StrokeCol = red,   Radius = 7.8f },
                new NavAxisDef { Vec = -Vec3.Up,      Label = "-Y", Desc = "Từ Đáy (-Y)",    Yaw = 0,    Pitch = -89.5, IsPos = false, FillCol = negFill, StrokeCol = green, Radius = 7.8f },
                new NavAxisDef { Vec = -Vec3.Forward, Label = "-Z", Desc = "Mặt Sau (-Z)",   Yaw = 180,  Pitch = 0,     IsPos = false, FillCol = negFill, StrokeCol = blue,  Radius = 7.8f },
            };
        }

        private void RenderImGuiInterface(float deltaSec)
        {
            float w = ClientSize.X;
            float h = ClientSize.Y;

            // 1. Top Menu & Shading Header Bar
            float menuBarH = 32f;
            if (ImGui.BeginMainMenuBar())
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.53f, 0.75f, 0.82f, 1.0f));
                ImGui.TextUnformatted("CRIMIRAD");
                ImGui.PopStyleColor();
                ImGui.SameLine(0, 12);

                if (ImGui.BeginMenu("File"))
                {
                    if (ImGui.MenuItem("Màn mới (Vải phẳng xám)")) BuildCleanGraySquareScene();
                    ImGui.Separator();
                    if (ImGui.MenuItem("Mở dự án (.sculpt.json)...")) OpenProject();
                    if (ImGui.MenuItem("Lưu dự án (.sculpt.json)...")) SaveProject();
                    ImGui.Separator();
                    if (ImGui.MenuItem("Nhập Mesh (.OBJ)...")) ImportObj();
                    if (ImGui.MenuItem("Xuất ra Unity (.OBJ + JSON)...")) ExportToUnity();
                    ImGui.Separator();
                    if (ImGui.MenuItem("Thoát")) Close();
                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("+ Add"))
                {
                    if (ImGui.MenuItem("Thêm mảnh vải vuông (24x24)")) SpawnSquareCloth();
                    if (ImGui.MenuItem("Mở mẫu uốn cong P1-P2")) BuildDemoFoldedClothScene();
                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("View"))
                {
                    ImGui.MenuItem("Hiện lưới sàn & trục tọa độ", "", ref _showFloorGrid);
                    ImGui.MenuItem("Hiện Điểm & Cạnh điều khiển", "", ref _showElements);
                    ImGui.MenuItem("Hiện vùng ảnh hưởng (Radius)", "", ref _showInfluence);
                    ImGui.MenuItem("Hiện Move Gizmo 3D", "", ref _showMoveGizmo);
                    ImGui.MenuItem("Wireframe Overlay (Khung dây)", "", ref _showWireOverlay);
                    ImGui.MenuItem("Tia pháp tuyến (Normals)", "", ref _showNormals);
                    ImGui.MenuItem("Hiện bảng UI phải (Panels)", "", ref _showUiPanels);
                    ImGui.Separator();
                    if (ImGui.MenuItem("Đặt lại Góc Phối Cảnh 3D"))
                    {
                        _camYawDeg = 28; _camPitchDeg = 18; _camTarget = Vec3.Zero;
                    }
                    ImGui.EndMenu();
                }

                // Right-aligned Undo/Redo & Shading pills
                float rightGroupW = 365f;
                ImGui.SetCursorPosX(Math.Max(260f, w - rightGroupW));
                ImGui.BeginDisabled(_undoStack.Count == 0);
                if (ImGui.Button("Undo")) PerformUndo();
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && _undoStack.Count > 0)
                    ImGui.SetTooltip($"Hoàn tác (Ctrl+Z): {_undoStack.Peek().Label}");

                ImGui.SameLine(0, 4);
                ImGui.BeginDisabled(_redoStack.Count == 0);
                if (ImGui.Button("Redo")) PerformRedo();
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && _redoStack.Count > 0)
                    ImGui.SetTooltip($"Làm lại (Ctrl+Y): {_redoStack.Peek().Label}");

                ImGui.SameLine(0, 12);
                DrawTogglePill("Wireframe", ref _showWireOverlay);
                ImGui.SameLine(0, 8);
                if (DrawModePill("Wire", _shadingMode == ViewportShadingMode.WireframeOnly)) _shadingMode = ViewportShadingMode.WireframeOnly;
                ImGui.SameLine(0, 2);
                if (DrawModePill("Solid", _shadingMode == ViewportShadingMode.SolidClay)) _shadingMode = ViewportShadingMode.SolidClay;
                ImGui.SameLine(0, 2);
                if (DrawModePill("Grid", _shadingMode == ViewportShadingMode.TexturedGrid)) _shadingMode = ViewportShadingMode.TexturedGrid;

                menuBarH = ImGui.GetWindowHeight();
                ImGui.EndMainMenuBar();
            }

            // 2. Left Floating Vertical Tool Column
            float toolWinX = 12f;
            float toolWinY = menuBarH + 10f;
            ImGui.SetNextWindowPos(new Vector2(toolWinX, toolWinY), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.84f);
            ImGuiWindowFlags hudFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                        ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize;

            if (ImGui.Begin("##LeftTools", hudFlags))
            {
                DrawToolButton("Select", EditorToolType.Select, "Select (Esc): Quét vùng / Click / Shift+Click chọn 2 Điểm rồi Chuột Phải -> Connect (hoặc nhấn F)");
                DrawToolButton("Create (A)", EditorToolType.CreatePoint, "Create (A): Click mặt vải tạo Điểm, click Cạnh để chia Cạnh");
                DrawToolButton("Move (G)", EditorToolType.Move, "Move (G): Kéo di chuyển & uốn vải (giữ Shift để đẩy theo trục Z)");
                DrawToolButton("Rotate (R)", EditorToolType.Rotate, "Rotate (R): Kéo trên Viewport hoặc kéo thanh X/Y/Z để xoay vật thể");
                DrawToolButton("Cut", EditorToolType.Cut, "Cut: Tách mảnh mới hoặc khoét lỗ với lưới O-Grid tứ giác sạch");
                DrawToolButton("Scale (S)", EditorToolType.Scale, "Scale (S): Phóng to / thu nhỏ mảnh vải");
                DrawToolButton("Nối (F)", EditorToolType.Connect, "Nối (F): Tạo Cạnh, Snap chập khít hoặc Hang treo cách khoảng");
            }
            float toolWinW = ImGui.GetWindowWidth();
            ImGui.End();

            // 3. Left Contextual Sub-Tool Options Card
            ImGui.SetNextWindowPos(new Vector2(toolWinX + toolWinW + 8f, toolWinY), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(238f, 0f), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.88f);
            if (ImGui.Begin("##SubToolOptions", hudFlags))
            {
                RenderSubToolPanel();
            }
            ImGui.End();

            // 4. Right Side Scene Hierarchy & Bottom Properties Cards
            float rightCardW = 295f;
            float rightMargin = 12f;
            if (_showUiPanels)
            {
                ImGui.SetNextWindowPos(new Vector2(w - rightCardW - rightMargin, toolWinY), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(rightCardW, 215f), ImGuiCond.Always);
                ImGui.SetNextWindowBgAlpha(0.88f);
                if (ImGui.Begin("##SceneHierarchy", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings))
                {
                    RenderSceneHierarchyCard();
                }
                ImGui.End();

                ImGui.SetNextWindowPos(new Vector2(w - rightCardW - rightMargin, h - 34f), ImGuiCond.Always, new Vector2(0f, 1f));
                ImGui.SetNextWindowSize(new Vector2(rightCardW, 0f), ImGuiCond.Always);
                ImGui.SetNextWindowBgAlpha(0.90f);
                if (ImGui.Begin("##InspectorProps", hudFlags))
                {
                    RenderInspectorCard();
                }
                ImGui.End();
            }

            // 5. Top-Right Interactive Blender 6-Axis Navigation Gizmo
            float navOffsetX = _showUiPanels ? (rightCardW + rightMargin + 12f) : rightMargin;
            Vector2 navPos = new(w - navOffsetX - 98f, toolWinY);
            RenderTopRightNavGizmo(navPos);

            // 6. Right-Click Context Popup (Connect / Snap / Hang + Shade Smooth)
            if (_openShadeContextNextFrame)
            {
                ImGui.OpenPopup("##ShadeContext");
                _openShadeContextNextFrame = false;
            }
            if (ImGui.BeginPopup("##ShadeContext"))
            {
                if (_selectedPoints.Count >= 2)
                {
                    var p1 = _selectedPoints[0];
                    var p2 = _selectedPoints[1];
                    ImGui.TextColored(new Vector4(0.96f, 0.80f, 0.30f, 1f), $"Điểm đang chọn: {string.Join(", ", _selectedPoints.Select(x => x.point.Name))}");
                    if (p1.cloth.Id == p2.cloth.Id)
                    {
                        if (ImGui.MenuItem("Connect (Nối 2 Điểm tạo Cạnh)"))
                            ExecuteConnectOnMultiSelection(ConnectMode.CreateEdge);
                    }
                    if (ImGui.MenuItem($"Snap ({p1.point.Name} -> {p2.point.Name})"))
                        ExecuteConnectOnMultiSelection(ConnectMode.Snap);
                    if (ImGui.MenuItem($"Hang (Treo {p1.point.Name} dưới {p2.point.Name})"))
                        ExecuteConnectOnMultiSelection(ConnectMode.Hang);
                    ImGui.Separator();
                }
                else if (_selectedEdges.Count >= 2)
                {
                    var e1 = _selectedEdges[0];
                    var e2 = _selectedEdges[1];
                    ImGui.TextColored(new Vector4(0.96f, 0.80f, 0.30f, 1f), $"Cạnh đang chọn: {e1.edge.Name}, {e2.edge.Name}");
                    if (ImGui.MenuItem($"Snap Cạnh ({e1.edge.Name} -> {e2.edge.Name})"))
                        ExecuteConnectOnMultiSelection(ConnectMode.Snap);
                    if (ImGui.MenuItem($"Hang Cạnh (Treo dưới {e2.edge.Name})"))
                        ExecuteConnectOnMultiSelection(ConnectMode.Hang);
                    ImGui.Separator();
                }

                if (_contextMenuCloth != null)
                {
                    ImGui.TextColored(new Vector4(0.53f, 0.75f, 0.82f, 1f), $"Lưới: {_contextMenuCloth.Name}");
                    if (ImGui.MenuItem("Shade Smooth (Mượt toàn phần)")) ApplyClothSmoothMode(_contextMenuCloth, MeshShadingSmoothMode.Smooth, _contextMenuCloth.AutoSmoothAngleDeg);
                    if (ImGui.MenuItem("Shade Auto Smooth (30°)")) ApplyClothSmoothMode(_contextMenuCloth, MeshShadingSmoothMode.AutoSmooth, 30f);
                    if (ImGui.MenuItem("Shade Auto Smooth (45°)")) ApplyClothSmoothMode(_contextMenuCloth, MeshShadingSmoothMode.AutoSmooth, 45f);
                    if (ImGui.MenuItem("Shade Flat (Mặt phẳng sắc)")) ApplyClothSmoothMode(_contextMenuCloth, MeshShadingSmoothMode.Flat, _contextMenuCloth.AutoSmoothAngleDeg);
                }
                ImGui.EndPopup();
            }

            // 7. Alert Popup (nếu có)
            if (!string.IsNullOrEmpty(_alertMessage))
            {
                ImGui.OpenPopup("Thông báo##Alert");
            }
            bool alertOpen = true;
            if (ImGui.BeginPopupModal("Thông báo##Alert", ref alertOpen, ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextWrapped(_alertMessage ?? "");
                if (ImGui.Button("OK", new Vector2(90, 0)))
                {
                    _alertMessage = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
            if (!alertOpen) _alertMessage = null;

            // 8. Bottom Status Bar
            float footerH = 26f;
            ImGui.SetNextWindowPos(new Vector2(0, h - footerH), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(w, footerH), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.95f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 4));
            if (ImGui.Begin("##StatusFooter", hudFlags))
            {
                ImGui.TextColored(new Vector4(0.53f, 0.75f, 0.82f, 1f), _statusText);
                int totalPts = _cloths.Sum(c => c.Points.Count);
                int totalEdges = _cloths.Sum(c => c.Edges.Count);
                int totalTris = _cloths.Sum(c => c.Mesh.Triangles.Count / 3);
                float fps = deltaSec > 1e-4f ? (1f / deltaSec) : 60f;
                string stats = $"Vải: {_cloths.Count} ({totalTris:N0} tris) | Điểm: {totalPts} | Cạnh: {totalEdges} | Nối: {_connections.Count} | {fps:F0} FPS";
                float stW = ImGui.CalcTextSize(stats).X;
                ImGui.SameLine(Math.Max(420f, w - stW - 14f));
                ImGui.TextDisabled(stats);
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }

        private void DrawToolButton(string label, EditorToolType tool, string tooltip)
        {
            bool active = _activeTool == tool;
            if (active)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.24f, 0.42f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.53f, 0.75f, 0.82f, 1.0f));
            }
            if (ImGui.Button(label, new Vector2(78, 36)))
            {
                _activeTool = tool;
                ResetConnectStep();
            }
            if (active) ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        }

        private static void DrawTogglePill(string label, ref bool value)
        {
            bool v = value;
            if (v) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.42f, 0.90f, 1.0f));
            if (ImGui.Button(label)) value = !value;
            if (v) ImGui.PopStyleColor();
        }

        private static bool DrawModePill(string label, bool active)
        {
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.42f, 0.90f, 1.0f));
            bool clicked = ImGui.Button(label);
            if (active) ImGui.PopStyleColor();
            return clicked;
        }

        private void RenderSubToolPanel()
        {
            ImGui.PushItemWidth(135f);
            ImGui.TextColored(new Vector4(0.53f, 0.75f, 0.82f, 1f), _activeTool switch
            {
                EditorToolType.Select => "SELECT (CHỌN / SHIFT+CLICK)",
                EditorToolType.CreatePoint => "CREATE (TẠO ĐIỂM)",
                EditorToolType.Move => "MOVE (DI CHUYỂN)",
                EditorToolType.Rotate => "ROTATE (XOAY VẬT THỂ)",
                EditorToolType.Cut => "CUT (CẮT SẠCH TỨ GIÁC)",
                EditorToolType.Scale => "SCALE (PHÓNG / THU)",
                EditorToolType.Connect => "NỐI (CẠNH / SNAP / HANG)",
                _ => "TOOL"
            });
            ImGui.Separator();

            switch (_activeTool)
            {
                case EditorToolType.Select:
                    int sf = (int)_selectFilter;
                    if (ImGui.RadioButton("Vùng", ref sf, (int)SelectTargetFilter.Region)) _selectFilter = SelectTargetFilter.Region;
                    ImGui.SameLine();
                    if (ImGui.RadioButton("Điểm", ref sf, (int)SelectTargetFilter.Point)) _selectFilter = SelectTargetFilter.Point;
                    if (ImGui.RadioButton("Cạnh", ref sf, (int)SelectTargetFilter.Edge)) _selectFilter = SelectTargetFilter.Edge;
                    ImGui.SameLine();
                    if (ImGui.RadioButton("Vật thể", ref sf, (int)SelectTargetFilter.ClothObject)) _selectFilter = SelectTargetFilter.ClothObject;

                    if (_selectFilter == SelectTargetFilter.Region)
                    {
                        ImGui.Separator();
                        int rs = (int)_regionShape;
                        if (ImGui.RadioButton("Vuông", ref rs, (int)RegionShapeMode.Box)) _regionShape = RegionShapeMode.Box;
                        ImGui.SameLine();
                        if (ImGui.RadioButton("Tròn", ref rs, (int)RegionShapeMode.Circle)) _regionShape = RegionShapeMode.Circle;
                        ImGui.SameLine();
                        if (ImGui.RadioButton("Nón", ref rs, (int)RegionShapeMode.Cone)) _regionShape = RegionShapeMode.Cone;

                        if (_regionShape == RegionShapeMode.Cone)
                            ImGui.SliderFloat("Góc Nón", ref _coneHalfAngleDeg, 8f, 75f, "%.0f deg");

                        int selCnt = _selectedCloth?.SelectedFaces.Count ?? 0;
                        ImGui.TextColored(new Vector4(0.64f, 0.75f, 0.55f, 1f), $"Đã chọn: {selCnt} ô");
                        ImGui.SameLine();
                        if (ImGui.Button("Bỏ chọn"))
                        {
                            foreach (var c in _cloths)
                            {
                                c.SelectedFaces.Clear(); c.SelectedTriangles.Clear();
                                c.SelectedVertices.Clear(); c.ActiveRegion = null;
                            }
                            _glRenderer.MarkAllSelectionDirty();
                        }
                    }
                    break;

                case EditorToolType.CreatePoint:
                    ImGui.TextWrapped("Click mặt vải để tạo Điểm.\nClick vào Cạnh để chèn Điểm chia Cạnh.");
                    break;

                case EditorToolType.Move:
                    int mz = _moveDepthZ ? 1 : 0;
                    if (ImGui.RadioButton("Mặt phẳng X/Y", ref mz, 0)) _moveDepthZ = false;
                    if (ImGui.RadioButton("Chiều sâu Z (hoặc Shift)", ref mz, 1)) _moveDepthZ = true;
                    break;

                case EditorToolType.Rotate:
                    if (_selectedCloth != null)
                    {
                        ImGui.RadioButton("Tự do", ref _rotateAxisLock, 0); ImGui.SameLine();
                        ImGui.RadioButton("X", ref _rotateAxisLock, 1); ImGui.SameLine();
                        ImGui.RadioButton("Y", ref _rotateAxisLock, 2); ImGui.SameLine();
                        ImGui.RadioButton("Z", ref _rotateAxisLock, 3);

                        float rx = ClampEuler180(_selectedCloth.RotXDeg);
                        float ry = ClampEuler180(_selectedCloth.RotYDeg);
                        float rz = ClampEuler180(_selectedCloth.RotZDeg);
                        bool rotChanged = false;

                        if (ImGui.SliderFloat("Xoay X", ref rx, -180f, 180f, "%.0f deg")) rotChanged = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Xoay trục X");
                        if (ImGui.SliderFloat("Xoay Y", ref ry, -180f, 180f, "%.0f deg")) rotChanged = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Xoay trục Y");
                        if (ImGui.SliderFloat("Xoay Z", ref rz, -180f, 180f, "%.0f deg")) rotChanged = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Xoay trục Z");

                        if (rotChanged)
                        {
                            var targets = _selectedCloths.Count > 0 ? _selectedCloths : new List<ClothEntity> { _selectedCloth };
                            foreach (var c in targets)
                            {
                                ClothSculptEngine.RotateClothToEuler(c, rx, ry, rz);
                                _glRenderer.MarkClothDirty(c.Id);
                            }
                            ApplyAllConstraints();
                        }

                        if (ImGui.Button("Reset góc 0°", new Vector2(-1, 0)))
                        {
                            RecordUndoState("Reset góc xoay");
                            ClothSculptEngine.RotateClothToEuler(_selectedCloth, 0, 0, 0);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                        }
                    }
                    break;

                case EditorToolType.Cut:
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.42f, 0.90f, 1f));
                    if (ImGui.Button("Tách Lấy Mảnh Mới", new Vector2(-1, 28))) ExecuteCut(CutActionMode.ExtractPiece);
                    ImGui.PopStyleColor();
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.37f, 0.17f, 0.21f, 1f));
                    if (ImGui.Button("Khoét Bỏ Tạo Lỗ", new Vector2(-1, 28))) ExecuteCut(CutActionMode.RemoveHole);
                    ImGui.PopStyleColor();
                    break;

                case EditorToolType.Scale:
                    if (_selectedCloth != null)
                    {
                        float sx = _selectedCloth.ScaleX;
                        float sy = _selectedCloth.ScaleY;
                        bool scChanged = false;
                        if (ImGui.SliderFloat("Scale X", ref sx, 0.25f, 2.50f, "%.2fx")) scChanged = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Scale X");
                        if (ImGui.SliderFloat("Scale Y", ref sy, 0.25f, 2.50f, "%.2fx")) scChanged = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Scale Y");
                        if (scChanged)
                        {
                            ClothSculptEngine.ScaleClothEntity(_selectedCloth, sx, sy);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                        }
                    }
                    break;

                case EditorToolType.Connect:
                    int cm = (int)_connectMode;
                    if (ImGui.RadioButton("1. Tạo Cạnh", ref cm, (int)ConnectMode.CreateEdge)) { _connectMode = ConnectMode.CreateEdge; ResetConnectStep(); }
                    if (ImGui.RadioButton("2. Snap (Chập khít)", ref cm, (int)ConnectMode.Snap)) { _connectMode = ConnectMode.Snap; ResetConnectStep(); }
                    if (ImGui.RadioButton("3. Hang (Treo)", ref cm, (int)ConnectMode.Hang)) { _connectMode = ConnectMode.Hang; ResetConnectStep(); }

                    if (ImGui.SliderFloat("Khoảng treo", ref _hangDistance, 0.02f, 0.60f, "%.2f m"))
                    {
                        var lastHang = _connections.LastOrDefault(c => c.Mode == ConnectMode.Hang);
                        if (lastHang != null)
                        {
                            lastHang.HangDistance = _hangDistance;
                            ClothSculptEngine.ApplyConnectionConstraint(_cloths, lastHang);
                            _glRenderer.MarkAllDirty();
                        }
                    }
                    if (ImGui.IsItemActivated()) RecordUndoState("Đổi khoảng treo");

                    ImGui.TextColored(new Vector4(0.92f, 0.80f, 0.55f, 1f),
                        _connectFirstPoint != null ? $"Đã chọn B1: {_connectFirstPoint.Name} -> Chọn 2" :
                        _connectFirstEdge != null ? $"Đã chọn B1: {_connectFirstEdge.Name} -> Chọn 2" :
                        "Shift+Click 2 mục hoặc click B1 -> B2");

                    ImGui.BeginDisabled(_selectedPoints.Count < 2 && _selectedEdges.Count < 2);
                    if (ImGui.Button("Thực hiện trên mục đã chọn (F)", new Vector2(-1, 0)))
                        ExecuteConnectOnMultiSelection();
                    ImGui.EndDisabled();
                    break;
            }
            ImGui.PopItemWidth();
        }

        private void RenderSceneHierarchyCard()
        {
            ImGui.TextColored(new Vector4(0.53f, 0.75f, 0.82f, 1f), "MẢNH VẢI & ĐIỂM");
            ImGui.SameLine(ImGui.GetWindowWidth() - 142f);
            if (ImGui.Button("Xóa Vải")) DeleteSelectedCloth();
            ImGui.SameLine(0, 4);
            if (ImGui.Button("Xóa Đ/C")) DeleteSelectedElement();

            if (ImGui.BeginChild("##ClothsList", new Vector2(0, 75), ImGuiChildFlags.Borders))
            {
                bool shift = ImGui.GetIO().KeyShift;
                for (int i = 0; i < _cloths.Count; i++)
                {
                    var c = _cloths[i];
                    bool sel = _selectedClothIds.Contains(c.Id) || _selectedCloth?.Id == c.Id;
                    if (ImGui.Selectable($"{c.Name} ({c.Mesh.Triangles.Count / 3} tris, {c.Points.Count}đ)", sel))
                    {
                        if (shift) ToggleMultiSelectCloth(c);
                        else SelectSingleCloth(c);
                    }
                }
            }
            ImGui.EndChild();

            if (ImGui.BeginChild("##ElemsList", new Vector2(0, 0), ImGuiChildFlags.Borders))
            {
                if (_selectedCloth != null)
                {
                    bool shift = ImGui.GetIO().KeyShift;
                    foreach (var edge in _selectedCloth.Edges)
                    {
                        bool sel = _selectedEdgeIds.Contains(edge.Id) || _selectedEdge?.Id == edge.Id;
                        if (ImGui.Selectable($"[Cạnh] {edge.Name} (R:{edge.Radius:F2}m B:{edge.Bending:F2})", sel))
                        {
                            if (shift) ToggleMultiSelectEdge(_selectedCloth, edge);
                            else SelectSingleEdge(_selectedCloth, edge);
                        }
                    }
                    foreach (var pt in _selectedCloth.Points)
                    {
                        bool sel = _selectedPointIds.Contains(pt.Id) || _selectedPoint?.Id == pt.Id;
                        if (ImGui.Selectable($"[Điểm] {pt.Name} ({pt.Position.X:F2},{pt.Position.Y:F2},{pt.Position.Z:F2})", sel))
                        {
                            if (shift) ToggleMultiSelectPoint(_selectedCloth, pt);
                            else SelectSinglePoint(_selectedCloth, pt);
                        }
                    }
                }
            }
            ImGui.EndChild();
        }

        private void RenderInspectorCard()
        {
            ImGui.PushItemWidth(182f);
            ImGui.TextColored(new Vector4(0.92f, 0.80f, 0.55f, 1f), "THUỘC TÍNH (PROPERTIES)");
            string title = _selectedPoint != null
                ? (_selectedPoints.Count >= 2 ? $"{_selectedPoints.Count} Điểm: {string.Join(", ", _selectedPoints.Select(x => x.point.Name))}" : $"Điểm: {_selectedPoint.Name}")
                : _selectedEdge != null ? $"Cạnh: {_selectedEdge.Name}"
                : _selectedCloth != null ? $"Vật thể: {_selectedCloth.Name}" : "Chưa chọn";
            ImGui.TextUnformatted(title);
            ImGui.Separator();

            if (_selectedPoint != null || _selectedEdge != null)
            {
                float bend = _selectedPoint != null ? _selectedPoint.Bending : _selectedEdge!.Bending;
                float rad = _selectedPoint != null ? _selectedPoint.Radius : _selectedEdge!.Radius;
                if (ImGui.SliderFloat("Độ uốn", ref bend, 0f, 1f, "%.2f"))
                {
                    if (_selectedPoint != null)
                    {
                        _selectedPoint.Bending = bend;
                        if (_selectedCloth != null)
                            foreach (var e in _selectedCloth.Edges)
                                if (e.PointAId == _selectedPoint.Id || e.PointBId == _selectedPoint.Id) e.Bending = bend;
                    }
                    else if (_selectedEdge != null && _selectedCloth != null)
                    {
                        _selectedEdge.Bending = bend;
                        var pA = _selectedCloth.FindPoint(_selectedEdge.PointAId);
                        var pB = _selectedCloth.FindPoint(_selectedEdge.PointBId);
                        if (pA != null) pA.Bending = bend;
                        if (pB != null) pB.Bending = bend;
                    }
                }
                if (ImGui.IsItemActivated()) RecordUndoState("Đổi Độ uốn");

                if (ImGui.SliderFloat("Bán kính", ref rad, 0.04f, 0.65f, "%.2f m"))
                {
                    if (_selectedPoint != null)
                    {
                        _selectedPoint.Radius = rad;
                        if (_selectedCloth != null)
                            foreach (var e in _selectedCloth.Edges)
                                if (e.PointAId == _selectedPoint.Id || e.PointBId == _selectedPoint.Id) e.Radius = rad;
                    }
                    else if (_selectedEdge != null && _selectedCloth != null)
                    {
                        _selectedEdge.Radius = rad;
                        var pA = _selectedCloth.FindPoint(_selectedEdge.PointAId);
                        var pB = _selectedCloth.FindPoint(_selectedEdge.PointBId);
                        if (pA != null) pA.Radius = rad;
                        if (pB != null) pB.Radius = rad;
                    }
                }
                if (ImGui.IsItemActivated()) RecordUndoState("Đổi Bán kính");
            }

            if (_selectedCloth != null)
            {
                // Location 1 / 2
                if (_selectedPoint != null)
                {
                    Vector3 loc = new(_selectedPoint.Position.X, _selectedPoint.Position.Y, _selectedPoint.Position.Z);
                    if (ImGui.DragFloat3("Vị trí Điểm", ref loc, 0.005f, -10f, 10f, "%.3f"))
                    {
                        ClothSculptEngine.MoveControlPoint(_selectedCloth, _selectedPoint, new Vec3(loc.X, loc.Y, loc.Z) - _selectedPoint.Position);
                        _glRenderer.MarkClothDirty(_selectedCloth.Id);
                        ApplyAllConstraints();
                    }
                    if (ImGui.IsItemActivated()) RecordUndoState("Đổi tọa độ Điểm");
                }
                else if (_selectedEdge != null)
                {
                    var pA = _selectedCloth.FindPoint(_selectedEdge.PointAId);
                    var pB = _selectedCloth.FindPoint(_selectedEdge.PointBId);
                    if (pA != null && pB != null)
                    {
                        Vector3 l1 = new(pA.Position.X, pA.Position.Y, pA.Position.Z);
                        Vector3 l2 = new(pB.Position.X, pB.Position.Y, pB.Position.Z);
                        if (ImGui.DragFloat3("Đầu A", ref l1, 0.005f, -10f, 10f, "%.3f"))
                        {
                            ClothSculptEngine.MoveControlEdge(_selectedCloth, _selectedEdge, new Vec3(l1.X, l1.Y, l1.Z) - pA.Position, Vec3.Zero);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                            ApplyAllConstraints();
                        }
                        if (ImGui.IsItemActivated()) RecordUndoState("Đổi tọa độ Đầu A");

                        if (ImGui.DragFloat3("Đầu B", ref l2, 0.005f, -10f, 10f, "%.3f"))
                        {
                            ClothSculptEngine.MoveControlEdge(_selectedCloth, _selectedEdge, Vec3.Zero, new Vec3(l2.X, l2.Y, l2.Z) - pB.Position);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                            ApplyAllConstraints();
                        }
                        if (ImGui.IsItemActivated()) RecordUndoState("Đổi tọa độ Đầu B");
                    }
                }
                else
                {
                    Vec3 c0 = ClothSculptEngine.GetClothCentroid(_selectedCloth);
                    Vector3 lc = new(c0.X, c0.Y, c0.Z);
                    if (ImGui.DragFloat3("Tâm Vải", ref lc, 0.005f, -10f, 10f, "%.3f"))
                    {
                        ClothSculptEngine.TranslateEntireCloth(_selectedCloth, new Vec3(lc.X, lc.Y, lc.Z) - c0);
                        _glRenderer.MarkClothDirty(_selectedCloth.Id);
                        ApplyAllConstraints();
                    }
                    if (ImGui.IsItemActivated()) RecordUndoState("Dịch chuyển Vải");
                }

                // Quick Nudge
                void NudgeBtn(string lbl, Vec3 d)
                {
                    if (ImGui.Button(lbl, new Vector2(36, 0)))
                    {
                        RecordUndoState("Dịch chuyển nhanh");
                        ApplyDeltaMoveToSelection(d);
                    }
                }
                NudgeBtn("+Y", new Vec3(0, 0.04f, 0)); ImGui.SameLine(0, 3);
                NudgeBtn("-Y", new Vec3(0, -0.04f, 0)); ImGui.SameLine(0, 3);
                NudgeBtn("-X", new Vec3(-0.04f, 0, 0)); ImGui.SameLine(0, 3);
                NudgeBtn("+X", new Vec3(0.04f, 0, 0)); ImGui.SameLine(0, 3);
                NudgeBtn("+Z", new Vec3(0, 0, 0.05f)); ImGui.SameLine(0, 3);
                NudgeBtn("-Z", new Vec3(0, 0, -0.05f));

                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.53f, 0.75f, 0.82f, 1f), "Làm mượt (Shading):");
                if (DrawModePill("Smooth", _selectedCloth.SmoothMode == MeshShadingSmoothMode.Smooth))
                    ApplyClothSmoothMode(_selectedCloth, MeshShadingSmoothMode.Smooth, _selectedCloth.AutoSmoothAngleDeg);
                ImGui.SameLine(0, 4);
                if (DrawModePill("Auto Smooth", _selectedCloth.SmoothMode == MeshShadingSmoothMode.AutoSmooth))
                    ApplyClothSmoothMode(_selectedCloth, MeshShadingSmoothMode.AutoSmooth, _selectedCloth.AutoSmoothAngleDeg);
                ImGui.SameLine(0, 4);
                if (DrawModePill("Flat", _selectedCloth.SmoothMode == MeshShadingSmoothMode.Flat))
                    ApplyClothSmoothMode(_selectedCloth, MeshShadingSmoothMode.Flat, _selectedCloth.AutoSmoothAngleDeg);

                if (_selectedCloth.SmoothMode == MeshShadingSmoothMode.AutoSmooth)
                {
                    float ang = _selectedCloth.AutoSmoothAngleDeg;
                    if (ImGui.SliderFloat("Góc mượt", ref ang, 0f, 180f, "%.0f deg"))
                    {
                        _selectedCloth.AutoSmoothAngleDeg = ang;
                        _glRenderer.MarkClothDirty(_selectedCloth.Id);
                    }
                    if (ImGui.IsItemActivated()) RecordUndoState("Đổi góc Auto Smooth");
                }
            }
            ImGui.PopItemWidth();
        }

        private void ApplyClothSmoothMode(ClothEntity cloth, MeshShadingSmoothMode mode, float angleDeg)
        {
            RecordUndoState($"Shading: {mode}");
            cloth.SmoothMode = mode;
            cloth.AutoSmoothAngleDeg = angleDeg;
            _glRenderer.MarkClothDirty(cloth.Id);
            _statusText = $"[{cloth.Name}]: Đã chuyển chế độ tô bóng {mode}.";
        }

        private void RenderTopRightNavGizmo(Vector2 topLeft)
        {
            const float size = 98f;
            const float radiusBg = 48f;
            const float armLen = 31f;
            Vector2 center = topLeft + new Vector2(size * 0.5f, size * 0.5f);

            ImGui.SetNextWindowPos(topLeft, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(size, size), ImGuiCond.Always);
            ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                     ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoSavedSettings;
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

            if (ImGui.Begin("##NavGizmo", flags))
            {
                var dl = ImGui.GetWindowDrawList();
                dl.AddCircleFilled(center, radiusBg, 0xB0141822, 40);
                dl.AddCircle(center, radiusBg, 0xCC4F362E, 40, 1.2f);

                GetCameraBasis(out Vec3 camRight, out Vec3 camUp, out Vec3 camForward);
                Span<int> order = stackalloc int[6] { 0, 1, 2, 3, 4, 5 };
                for (int i = 0; i < 6; i++)
                    _navAxes[i].Depth = Vec3.Dot(_navAxes[i].Vec, camForward);

                for (int i = 0; i < 5; i++)
                    for (int j = i + 1; j < 6; j++)
                        if (_navAxes[order[j]].Depth > _navAxes[order[i]].Depth)
                            (order[i], order[j]) = (order[j], order[i]);

                ImGui.SetCursorScreenPos(topLeft);
                ImGui.InvisibleButton("##NavGizmoHit", new Vector2(size, size));
                Vector2 mPos = ImGui.GetMousePos();

                int hit = -1;
                if (ImGui.IsItemHovered() && !_isDraggingNavGizmo)
                {
                    float bestD = float.MaxValue;
                    for (int i = 0; i < 6; i++)
                    {
                        var a = _navAxes[i];
                        Vector2 ap = center + new Vector2(Vec3.Dot(a.Vec, camRight) * armLen, -Vec3.Dot(a.Vec, camUp) * armLen);
                        float hr = a.Radius + 3.5f;
                        if (Vector2.DistanceSquared(mPos, ap) <= hr * hr && a.Depth < bestD)
                        {
                            bestD = a.Depth;
                            hit = i;
                        }
                    }
                }
                _hoveredNavIdx = hit;

                if (ImGui.IsItemActivated())
                {
                    _navDownPos = mPos;
                    _navLastPos = mPos;
                    _isDraggingNavGizmo = true;
                }
                if (_isDraggingNavGizmo)
                {
                    if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    {
                        Vector2 d = mPos - _navLastPos;
                        _navLastPos = mPos;
                        if (Vector2.Distance(mPos, _navDownPos) >= 3f)
                        {
                            _camYawDeg -= d.X * 0.72;
                            _camPitchDeg = Math.Max(-89.5, Math.Min(89.5, _camPitchDeg + d.Y * 0.72));
                        }
                    }
                    else
                    {
                        _isDraggingNavGizmo = false;
                        if (Vector2.Distance(mPos, _navDownPos) < 4.5f)
                        {
                            float bestD = float.MaxValue;
                            int clickIdx = -1;
                            for (int i = 0; i < 6; i++)
                            {
                                var a = _navAxes[i];
                                Vector2 ap = center + new Vector2(Vec3.Dot(a.Vec, camRight) * armLen, -Vec3.Dot(a.Vec, camUp) * armLen);
                                float hr = a.Radius + 4.0f;
                                if (Vector2.DistanceSquared(mPos, ap) <= hr * hr && a.Depth < bestD)
                                {
                                    bestD = a.Depth;
                                    clickIdx = i;
                                }
                            }
                            if (clickIdx >= 0)
                            {
                                var targetAx = _navAxes[clickIdx];
                                static double NormAngle(double a) => ((a + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
                                if (Math.Abs(NormAngle(_camYawDeg - targetAx.Yaw)) < 2.0 &&
                                    Math.Abs(_camPitchDeg - targetAx.Pitch) < 2.0)
                                {
                                    targetAx = _navAxes[(clickIdx + 3) % 6];
                                }
                                _camYawDeg = targetAx.Yaw;
                                _camPitchDeg = targetAx.Pitch;
                                _camTarget = Vec3.Zero;
                                _statusText = $"Góc nhìn vuông góc: {targetAx.Desc}";
                            }
                        }
                    }
                }

                for (int rank = 0; rank < 6; rank++)
                {
                    int idx = order[rank];
                    var ax = _navAxes[idx];
                    Vector2 endPt = center + new Vector2(Vec3.Dot(ax.Vec, camRight) * armLen, -Vec3.Dot(ax.Vec, camUp) * armLen);
                    ax.ScreenPt = endPt;

                    bool hovered = idx == _hoveredNavIdx;
                    bool front = ax.Depth <= 0.15f;
                    uint alphaScale = (uint)(hovered ? 255 : (front ? 245 : 135));
                    uint WithAlpha(uint col, uint a) => (col & 0x00FFFFFFu) | (a << 24);

                    if (ax.IsPos)
                        dl.AddLine(center, endPt, WithAlpha(ax.StrokeCol, alphaScale), hovered ? 2.8f : 2.3f);

                    dl.AddCircleFilled(endPt, ax.Radius, WithAlpha(ax.FillCol, alphaScale), 20);
                    dl.AddCircle(endPt, ax.Radius, hovered ? 0xFFFFFFFFu : WithAlpha(ax.StrokeCol, alphaScale), 20, hovered ? 2.0f : 1.3f);

                    Vector2 ts = ImGui.CalcTextSize(ax.Label);
                    uint textCol = ax.IsPos ? 0xFF0A0C10u : WithAlpha(ax.StrokeCol, hovered ? 255u : (front ? 235u : 110u));
                    dl.AddText(endPt - ts * 0.5f, textCol, ax.Label);
                }

                if (_hoveredNavIdx >= 0)
                    ImGui.SetTooltip($"Click: {_navAxes[_hoveredNavIdx].Desc} | Kéo vòng tròn để xoay");
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }

        #endregion
    }
}
