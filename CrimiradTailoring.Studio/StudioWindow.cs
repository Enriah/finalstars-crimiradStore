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
    public sealed partial class StudioWindow : GameWindow
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
        private RegionShapeMode _regionShape = RegionShapeMode.Box;
        private Vector2 _selectFlyoutPos;
        private EditorToolType _transformToolMode = EditorToolType.Move;
        private Vector2 _transformFlyoutPos;
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
        private bool _hasSweptRegionSelect;
        private Vector2 _regionSelectStartMousePos;
        private Vec3 _regionSelectStart3D;
        private bool _isDraggingMoveElement;
        private bool _isDraggingRotateCloth;
        private bool _isDraggingScaleElement;
        private GizmoAxis _activeGizmoAxis = GizmoAxis.None;
        private Vector2 _lastMousePos;
        private Vector2 _rightDownPos;

        // Cạnh biên vải (Border Edges - Scale/Rotate/Move/Arc tạo Hình Thang & Nón không chóp)
        private ClothBorderEdge? _selectedBorderEdge;
        private List<ClothBorderEdge> _selectedClothBorders = new();
        private List<Vec3>? _borderSessionBaseRestVerts;
        private List<Vec3>? _borderSessionBasePointPositions;
        private float _borderEditScale = 1.0f;
        private float _borderEditArc2D = 0.0f;
        private float _borderEditWrap3D = 0.0f;
        private float _borderEditRotateDeg = 0.0f;
        private Vec3 _borderEditMoveDelta = Vec3.Zero;

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

        // Context Menu (Right click cloth / Point / Edge / MeshEdge -> Copy / Paste / Delete & Origin & Shade)
        private ClothEntity? _contextMenuCloth;
        private ControlPoint? _contextMenuPoint;
        private Vec3? _contextMenuNearestVertexPos;
        private Vec3 _contextMenuWorldPos;
        private bool _contextMenuHasClothHit;
        private bool _openShadeContextNextFrame = false;

        // Unified Clipboard (Copy / Paste mọi thành phần có thể chọn)
        private enum ClipboardKind { None, ClothObjects, Points, DeformEdges, MeshEdge }
        private sealed record CopiedPointTemplate(Vec3 OffsetFromAnchor, float Radius, float Bending, Vec3 SculptDelta);
        private sealed record CopiedEdgeTemplate(Vec3 RelCenterOffset, Vec3 HalfVec, float Radius, float Bending, Vec3 DeltaA, Vec3 DeltaB);
        private readonly record struct CopiedBorderTemplate(float Scale, float Arc2D, float Wrap3D, float RotateDeg, float SourceChordLen, float MeasuredSagitta);

        private ClipboardKind _clipboardKind = ClipboardKind.None;
        private readonly List<string> _clipboardClothJsons = new();
        private readonly List<CopiedPointTemplate> _clipboardPoints = new();
        private readonly List<CopiedEdgeTemplate> _clipboardEdges = new();
        private CopiedBorderTemplate _clipboardBorder;
        private int _pasteStaggerCount = 0;

        // Blender-style Unified Right Sidebar (Outliner + Properties)
        private string _outlinerSearch = "";
        private float _outlinerSplitRatio = 0.40f;

        // Renderers
        private readonly GlClothViewportRenderer _glRenderer = new();
        private ImGuiController _imgui = null!;

        // Undo / Redo History
        private readonly Stack<EditorSnapshot> _undoStack = new();
        private readonly Stack<EditorSnapshot> _redoStack = new();
        private EditorSnapshot? _pendingDragUndoSnapshot;
        private const int MaxUndoSteps = 50;

        private string _statusText = "Sẵn sàng";
        private string? _alertMessage;
        private readonly bool _screenshotMode;
        private readonly bool _screenshotCharacterMode;
        private int _framesRendered = 0;
        private int _activeRenderBurstFrames = 4;
        private readonly System.Diagnostics.Stopwatch _framePacingSw = System.Diagnostics.Stopwatch.StartNew();

        private void WakeUpRenderLoop(int burstFrames = 3)
        {
            if (burstFrames > _activeRenderBurstFrames)
                _activeRenderBurstFrames = burstFrames;
            IsEventDriven = false;
        }

        private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
        {
            IncludeFields = true,
            IgnoreReadOnlyProperties = true
        };

        public StudioWindow(GameWindowSettings gwSettings, NativeWindowSettings nwSettings, bool screenshotMode, bool screenshotCharacterMode = false)
            : base(gwSettings, nwSettings)
        {
            _screenshotMode = screenshotMode;
            _screenshotCharacterMode = screenshotCharacterMode;
        }

        protected override void OnLoad()
        {
            base.OnLoad();
            VSync = VSyncMode.On;
            ExpectedSchedulerPeriod = 16;
            _glRenderer.EnsureInitialized();
            _imgui = new ImGuiController(ClientSize.X, ClientSize.Y);
            InitNavGizmo();
            InitializeDefaultCharacter();
            BuildCleanGraySquareScene();
            if (_screenshotCharacterMode)
                SetupCharacterScreenshotDemo();
            WakeUpRenderLoop(6);
        }

        protected override void OnFocusedChanged(FocusedChangedEventArgs e)
        {
            base.OnFocusedChanged(e);
            if (e.IsFocused) WakeUpRenderLoop(4);
            else if (!_screenshotMode) IsEventDriven = true;
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            base.OnResize(e);
            if (_imgui != null && e.Width > 0 && e.Height > 0)
            {
                _imgui.WindowResized(e.Width, e.Height);
            }
            WakeUpRenderLoop(3);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            WakeUpRenderLoop(3);
            ImGuiController.HandleMouseWheel(e.OffsetX, e.OffsetY);
            if (!ImGui.GetIO().WantCaptureMouse)
            {
                double zoomFactor = e.OffsetY > 0 ? 0.90 : 1.11;
                ActiveCamDistance = Math.Max(0.40, Math.Min(5.2, ActiveCamDistance * zoomFactor));
            }
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            WakeUpRenderLoop(3);
            ImGuiController.HandleTextInput(e.Unicode);
        }

        protected override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            base.OnKeyDown(e);
            WakeUpRenderLoop(3);
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
            if (ctrl && e.Key == Keys.C)
            {
                CopySelection();
                return;
            }
            if (ctrl && e.Key == Keys.V)
            {
                PasteClipboard(useContextMenuAnchor: false);
                return;
            }

            if (e.Modifiers != 0)
                return;

            switch (e.Key)
            {
                case Keys.G:
                    _transformToolMode = EditorToolType.Move;
                    _activeTool = EditorToolType.Move;
                    ResetConnectStep();
                    _statusText = "Move [G]";
                    break;
                case Keys.R:
                    _transformToolMode = EditorToolType.Rotate;
                    _activeTool = EditorToolType.Rotate;
                    ResetConnectStep();
                    _statusText = "Rotate [R]";
                    break;
                case Keys.S:
                    _transformToolMode = EditorToolType.Scale;
                    _activeTool = EditorToolType.Scale;
                    ResetConnectStep();
                    _statusText = "Scale [S]";
                    break;
                case Keys.A:
                    _activeTool = EditorToolType.CreatePoint;
                    ResetConnectStep();
                    _statusText = "Create [A]";
                    break;
                case Keys.M:
                    if (_activeViewport == WorkspaceViewportKind.CharacterViewport)
                    {
                        _activeTool = EditorToolType.Ruler;
                        ResetConnectStep();
                        _statusText = "Ruler [M]";
                    }
                    break;
                case Keys.Enter:
                    if (_activeViewport == WorkspaceViewportKind.CharacterViewport && _activeDraftRuler != null)
                        FinishActiveDraftRuler();
                    break;
                case Keys.F:
                    if (_selectedPoints.Count >= 2 || _selectedEdges.Count >= 2)
                    {
                        ExecuteConnectOnMultiSelection();
                    }
                    else
                    {
                        _statusText = "Chọn 2 Điểm hoặc 2 Cạnh rồi nhấn [F]";
                    }
                    break;
                case Keys.Escape:
                    _activeDraftRuler = null;
                    _activeTool = EditorToolType.Select;
                    ResetConnectStep();
                    _statusText = "Select [Esc]";
                    break;
                case Keys.X:
                case Keys.Delete:
                    DeleteCurrentSelection();
                    break;
            }
        }

        protected override void OnKeyUp(KeyboardKeyEventArgs e)
        {
            base.OnKeyUp(e);
            WakeUpRenderLoop(2);
            ImGuiController.HandleKeyEvent(e.Key, false);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            WakeUpRenderLoop(4);
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
            bool activeDrag = _isOrbitingCamera || _isPanningCamera || _isDraggingRegionSelect ||
                              _isDraggingMoveElement || _isDraggingRotateCloth || _isDraggingScaleElement || _isDraggingNavGizmo || IsAnyMouseButtonDown;
            if (activeDrag || _framePacingSw.ElapsedMilliseconds >= 22)
                WakeUpRenderLoop(activeDrag ? 3 : 2);
            Vector2 mp = new(e.X, e.Y);
            Vector2 delta = mp - _lastMousePos;
            _lastMousePos = mp;

            if (_isOrbitingCamera)
            {
                ActiveCamYaw -= delta.X * 0.42;
                ActiveCamPitch = Math.Max(-89.5, Math.Min(89.5, ActiveCamPitch + delta.Y * 0.42));
                return;
            }
            if (_isPanningCamera)
            {
                GetCameraBasis(out Vec3 camRight, out Vec3 camUp, out _);
                float panScale = (float)(ActiveCamDistance * 0.0014);
                ActiveCamTarget = new Vec3(
                    ActiveCamTarget.X - camRight.X * delta.X * panScale + camUp.X * delta.Y * panScale,
                    ActiveCamTarget.Y - camRight.Y * delta.X * panScale + camUp.Y * delta.Y * panScale,
                    ActiveCamTarget.Z - camRight.Z * delta.X * panScale + camUp.Z * delta.Y * panScale);
                return;
            }
            if (_activeViewport == WorkspaceViewportKind.CharacterViewport)
            {
                if (_isDraggingMoveElement) { HandleCharacterDragMove(delta); return; }
                if (_isDraggingRotateCloth) { HandleCharacterDragRotate(delta); return; }
                if (_isDraggingScaleElement) { HandleCharacterDragScale(delta); return; }
                return;
            }
            if (_isDraggingRegionSelect && _selectedCloth != null)
            {
                if (!_hasSweptRegionSelect && Vector2.Distance(mp, _regionSelectStartMousePos) < 4.0f)
                    return;
                _hasSweptRegionSelect = true;
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
                if (_selectedBorderEdge != null && _selectedCloth != null && (Math.Abs(delta.X) > 0.01f || Math.Abs(delta.Y) > 0.01f))
                {
                    CommitPendingDragUndo();
                    _borderEditRotateDeg = ClampEuler180(_borderEditRotateDeg + delta.X * 0.55f);
                    ApplyCurrentBorderSessionTransform();
                    _statusText = $"Đang xoay [{_selectedBorderEdge.Name}]: {_borderEditRotateDeg:F1}°";
                    return;
                }

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
                        Vec3 pivot = ClothSculptEngine.GetClothOrigin(c);
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
                    RefreshSelectedClothBorders();
                    if (_selectedCloth != null)
                        _statusText = $"Đang xoay [{_selectedCloth.Name}] quanh Origin - X:{_selectedCloth.RotXDeg:F0}°  Y:{_selectedCloth.RotYDeg:F0}°  Z:{_selectedCloth.RotZDeg:F0}°";
                }
            }
            if (_isDraggingScaleElement && _selectedCloth != null)
            {
                float dScale = 1f + (delta.X - delta.Y) * 0.005f;
                if (Math.Abs(dScale - 1f) > 1e-4f)
                {
                    CommitPendingDragUndo();
                    if (_selectedBorderEdge != null)
                    {
                        _borderEditScale = Math.Max(0.10f, Math.Min(3.00f, _borderEditScale * dScale));
                        ApplyCurrentBorderSessionTransform();
                        _statusText = $"Scale [{_selectedBorderEdge.Name}]: {_borderEditScale:F2}x (Hình thang)";
                    }
                    else
                    {
                        ClothSculptEngine.ScaleClothEntity(_selectedCloth, _selectedCloth.ScaleX * dScale, _selectedCloth.ScaleY * dScale);
                        _glRenderer.MarkClothDirty(_selectedCloth.Id);
                        ApplyAllConstraints();
                        RefreshSelectedClothBorders();
                        _statusText = $"Scale [{_selectedCloth.Name}] quanh Origin: X={_selectedCloth.ScaleX:F2}x, Y={_selectedCloth.ScaleY:F2}x";
                    }
                }
            }
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            WakeUpRenderLoop(3);

            if (_draggedClothFromOutliner != null && e.Button == MouseButton.Left)
            {
                var dropPos = new Vector2(MouseState.X, MouseState.Y);
                if (_activeViewport == WorkspaceViewportKind.CharacterViewport && IsPointOverCharacterCanvas(dropPos))
                {
                    var dropped = _draggedClothFromOutliner;
                    _draggedClothFromOutliner = null;
                    AttachClothToCharacterAtScreenPos(dropped, dropPos);
                }
            }

            if (_isOrbitingCamera || _isPanningCamera || _isDraggingRegionSelect || _isDraggingMoveElement || _isDraggingRotateCloth || _isDraggingScaleElement)
            {
                bool wasShortRight = _isOrbitingCamera &&
                    e.Button == MouseButton.Right &&
                    Vector2.Distance(new Vector2(MouseState.X, MouseState.Y), _rightDownPos) < 4.0f;
                bool wasRegionSelect = _isDraggingRegionSelect;
                bool didSweepRegion = _hasSweptRegionSelect;

                bool wasDraggingCharacterTransform = _activeViewport == WorkspaceViewportKind.CharacterViewport &&
                    (_isDraggingMoveElement || _isDraggingRotateCloth || _isDraggingScaleElement);

                _isOrbitingCamera = false;
                _isPanningCamera = false;
                _isDraggingRegionSelect = false;
                _hasSweptRegionSelect = false;
                _isDraggingMoveElement = false;
                _isDraggingRotateCloth = false;
                _isDraggingScaleElement = false;
                _activeGizmoAxis = GizmoAxis.None;
                _pendingDragUndoSnapshot = null;
                if (wasDraggingCharacterTransform)
                    MarkCharacterSolverDirty();

                if (wasShortRight && _activeViewport == WorkspaceViewportKind.CharacterViewport)
                {
                    if (_activeTool == EditorToolType.Ruler && _activeDraftRuler != null)
                    {
                        FinishActiveDraftRuler();
                        return;
                    }

                    PickCharacterElementAtScreenPos(new Vector2(MouseState.X, MouseState.Y), out var hitAnchor, out var hitRuler, out var hitCloth);
                    if (hitAnchor != null) SelectCharacterAnchor(hitAnchor);
                    else if (hitRuler != null) SelectCharacterRuler(hitRuler);
                    else if (hitCloth != null)
                    {
                        _selectedAnchor = null;
                        _selectedRuler = null;
                        SelectSingleCloth(hitCloth);
                    }

                    _contextMenuCloth = _selectedCloth;
                    _contextMenuPoint = null;
                    _contextMenuNearestVertexPos = null;
                    _openShadeContextNextFrame = true;
                    return;
                }

                if (wasRegionSelect && !didSweepRegion && e.Button == MouseButton.Left && _selectedCloth != null)
                {
                    bool ctrl = KeyboardState.IsKeyDown(Keys.LeftControl) || KeyboardState.IsKeyDown(Keys.RightControl);
                    if (ctrl)
                    {
                        ClothSculptEngine.SelectRegionOnCloth(
                            _selectedCloth, _regionShape, _regionSelectStart3D, _regionSelectStart3D, _coneHalfAngleDeg, additive: true);
                        _glRenderer.MarkClothSelectionDirty(_selectedCloth.Id);
                    }
                    else
                    {
                        ClearAllRegionSelections();
                        _statusText = $"Đã chọn {_selectedCloth.Name}";
                    }
                }

                if (wasShortRight)
                {
                    RaycastSceneFull(new Vector2(MouseState.X, MouseState.Y), out _, out ClothEntity? hitCloth, out ControlPoint? hitPt, out ControlEdge? hitEdge, out ClothBorderEdge? hitBorder, out Vec3 hitWorld, allowGizmo: false);
                    _contextMenuPoint = hitPt;
                    _contextMenuWorldPos = hitWorld;
                    _contextMenuHasClothHit = hitCloth != null;

                    if (hitPt != null && hitCloth != null)
                    {
                        if (!_selectedPointIds.Contains(hitPt.Id))
                            SelectSinglePoint(hitCloth, hitPt);
                        _contextMenuNearestVertexPos = hitPt.Position;
                    }
                    else if (hitEdge != null && hitCloth != null)
                    {
                        if (!_selectedEdgeIds.Contains(hitEdge.Id))
                            SelectSingleEdge(hitCloth, hitEdge);
                        _contextMenuNearestVertexPos = null;
                    }
                    else if (hitBorder != null && hitCloth != null)
                    {
                        if (_selectedBorderEdge == null || _selectedBorderEdge.Index != hitBorder.Index || _selectedCloth?.Id != hitCloth.Id)
                            SelectSingleBorderEdge(hitCloth, hitBorder);
                        _contextMenuNearestVertexPos = hitBorder.Midpoint;
                    }
                    else if (hitCloth != null)
                    {
                        int vIdx = ClothSculptEngine.FindClosestVertexIndex(hitCloth.Mesh, hitWorld);
                        _contextMenuNearestVertexPos = (vIdx >= 0 && vIdx < hitCloth.Mesh.Vertices.Count)
                            ? hitCloth.Mesh.Vertices[vIdx]
                            : (Vec3?)null;
                        if (_selectedPoints.Count < 2 && _selectedEdges.Count < 2 && _selectedBorderEdge == null && !_selectedClothIds.Contains(hitCloth.Id))
                            SelectSingleCloth(hitCloth);
                    }
                    else
                    {
                        _contextMenuNearestVertexPos = null;
                    }

                    bool hasRegionSel = _cloths.Any(c => c.SelectedFaces.Count > 0 || c.SelectedTriangles.Count > 0);
                    if (hitCloth != null || hitPt != null || hitEdge != null || hitBorder != null ||
                        _selectedPoints.Count >= 1 || _selectedEdges.Count >= 1 || _selectedBorderEdge != null ||
                        _selectedCloth != null || hasRegionSel || _clipboardKind != ClipboardKind.None)
                    {
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

            EnsureCharacterClothsSolved();

            // 1. Render 3D Scene chính & Sub-Viewport FBO
            if (_activeViewport == WorkspaceViewportKind.FabricViewport)
            {
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
                    _showElements, _showInfluence, IsTransformGizmoVisibleForTool(),
                    _selectedClothBorders, _selectedBorderEdge,
                    _selectedBorderEdge != null);
            }
            else
            {
                _glRenderer.RenderCharacterFrame(
                    ClientSize.X, ClientSize.Y,
                    _charCamYawDeg, _charCamPitchDeg, _charCamDistance, _charCamTarget,
                    _characterModel, _charAnchors, _rulers, _activeDraftRuler, _selectedAnchor, _selectedRuler,
                    _cloths, _selectedCloth, _selectedPoint, _selectedEdge,
                    _activeGizmoAxis, _shadingMode, _showWireOverlay, _showFloorGrid,
                    _showElements, IsTransformGizmoVisibleForTool());
            }

            _glRenderer.EnsureSubViewportRendered(
                240, 128, _activeViewport,
                _camYawDeg, _camPitchDeg, _camDistance, _camTarget,
                _charCamYawDeg, _charCamPitchDeg, _charCamDistance, _charCamTarget,
                _characterModel, _charAnchors, _rulers, _activeDraftRuler, _selectedAnchor, _selectedRuler,
                _cloths, _connections, _selectedCloth, _selectedPoint, _selectedEdge,
                _shadingMode, _showWireOverlay, _showFloorGrid, _showElements);

            // 2. Render Toàn Bộ Giao Diện ImGui trên cùng Ngữ Cảnh OpenGL (0ms delay!)
            float safeDt = Math.Clamp((float)args.Time, 0.001f, 0.05f);
            _imgui.Update(this, safeDt);
            RenderImGuiInterface(safeDt);
            if (_draggedClothFromOutliner != null && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
                _draggedClothFromOutliner = null;
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

            if (!_screenshotMode)
            {
                int ms = (int)_framePacingSw.ElapsedMilliseconds;
                if (ms < 16)
                    System.Threading.Thread.Sleep(16 - ms);
                _framePacingSw.Restart();

                bool keepActive = IsFocused &&
                    (_isOrbitingCamera || _isPanningCamera || _isDraggingRegionSelect ||
                     _isDraggingMoveElement || _isDraggingRotateCloth || _isDraggingScaleElement ||
                     _isDraggingNavGizmo || _draggedClothFromOutliner != null || IsAnyMouseButtonDown || IsAnyKeyDown ||
                     _openShadeContextNextFrame || !string.IsNullOrEmpty(_alertMessage) ||
                     ImGui.IsAnyItemActive() || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId));

                if (keepActive)
                {
                    _activeRenderBurstFrames = 3;
                    IsEventDriven = false;
                }
                else
                {
                    if (_activeRenderBurstFrames > 0) _activeRenderBurstFrames--;
                    if (_activeRenderBurstFrames <= 0)
                        IsEventDriven = true;
                }
            }
        }

        private void CaptureBackbufferScreenshot()
        {
            int w = ClientSize.X;
            int h = ClientSize.Y;
            byte[] pixels = new byte[w * h * 4];
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.ReadPixels(0, 0, w, h, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            string outPath = _screenshotCharacterMode
                ? @"C:\Users\khoar\.gemini\antigravity\brain\590c1375-180b-452d-8e75-aa5ae2ea8453\scratch\viewport_char_debug.png"
                : @"C:\Users\khoar\.gemini\antigravity\brain\590c1375-180b-452d-8e75-aa5ae2ea8453\scratch\viewport_debug.png";
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
            _statusText = "Sẵn sàng";
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

        private void ClearAllRegionSelections()
        {
            bool anyCleared = false;
            foreach (var c in _cloths)
            {
                if (c.SelectedFaces.Count > 0 || c.SelectedTriangles.Count > 0)
                {
                    c.SelectedFaces.Clear();
                    c.SelectedTriangles.Clear();
                    c.SelectedVertices.Clear();
                    c.ActiveRegion = null;
                    anyCleared = true;
                }
            }
            if (anyCleared)
                _glRenderer.MarkAllSelectionDirty();
        }

        private void ExecuteCut(CutActionMode mode)
        {
            var target = _cloths.FirstOrDefault(c => c.SelectedFaces.Count > 0 || c.SelectedTriangles.Count > 0) ?? _selectedCloth;
            if (target == null || (target.SelectedFaces.Count == 0 && target.SelectedTriangles.Count == 0))
            {
                _alertMessage = "Bạn chưa chọn vùng vải nào! Hãy dùng Select kéo chuột chọn vùng trên mặt vải, rồi Chuột phải -> Extract hoặc Delete.";
                return;
            }

            RecordUndoState(mode == CutActionMode.ExtractPiece ? "Extract Tách Mảnh" : "Delete Khoét Lỗ");
            _clothCounter++;
            if (ClothSculptEngine.CutSelectedRegion(target, mode, _clothCounter, out ClothEntity? extracted))
            {
                _glRenderer.MarkClothDirty(target.Id);
                if (extracted != null)
                {
                    _cloths.Add(extracted);
                    _glRenderer.MarkClothDirty(extracted.Id);
                    SelectSingleCloth(extracted);
                    _statusText = $"Extract thành công [{extracted.Name}] ({extracted.Mesh.Faces.Count} mặt tứ giác sạch)!";
                }
                else
                {
                    _statusText = $"Delete thành công vùng chọn trên [{target.Name}] (lưới O-Grid tứ giác sạch)!";
                }
            }
        }

        private void DeleteCurrentSelection()
        {
            if (_activeViewport == WorkspaceViewportKind.CharacterViewport)
            {
                if (_selectedRuler != null)
                {
                    var victim = _selectedRuler;
                    RecordUndoState($"Xóa {victim.Name}");
                    if (_activeDraftRuler == victim) _activeDraftRuler = null;
                    _rulers.Remove(victim);
                    _selectedRuler = null;
                    _glRenderer.MarkSubViewportDirty();
                    _statusText = $"Đã xóa {victim.Name}";
                    return;
                }
                if (_selectedAnchor != null)
                {
                    var anch = _selectedAnchor;
                    RecordUndoState($"Xóa {anch.Name}");
                    foreach (var c in _cloths)
                    {
                        if (c.CharacterState.AnchorId == anch.Id)
                            c.CharacterState.AnchorId = null;
                    }
                    _charAnchors.Remove(anch);
                    _selectedAnchor = null;
                    MarkCharacterSolverDirty();
                    _statusText = $"Đã xóa {anch.Name}";
                    return;
                }
            }
            var regionCloth = _cloths.FirstOrDefault(c => c.SelectedFaces.Count > 0 || c.SelectedTriangles.Count > 0);
            if (regionCloth != null)
            {
                ExecuteCut(CutActionMode.RemoveHole);
                return;
            }
            if (_selectedEdges.Count > 0 || _selectedEdge != null || _selectedPoints.Count > 0 || _selectedPoint != null)
            {
                DeleteSelectedElement();
                return;
            }
            if (_selectedBorderEdge != null && _selectedCloth != null)
            {
                DeleteSelectedBorderEdge();
                return;
            }
            if (_selectedCloths.Count > 0 || _selectedCloth != null)
            {
                DeleteSelectedCloth();
            }
        }

        private void DeleteSelectedCloth()
        {
            var targets = _selectedCloths.Count > 0
                ? new List<ClothEntity>(_selectedCloths)
                : (_selectedCloth != null ? new List<ClothEntity> { _selectedCloth } : new List<ClothEntity>());
            if (targets.Count == 0) return;

            RecordUndoState(targets.Count > 1 ? $"Xóa {targets.Count} mảnh vải" : "Xóa mảnh vải");
            foreach (var c in targets)
            {
                Guid id = c.Id;
                _cloths.Remove(c);
                _connections.RemoveAll(conn => conn.SourceClothId == id || conn.TargetClothId == id);
            }
            var next = _cloths.FirstOrDefault();
            if (next != null) SelectSingleCloth(next);
            else { ClearMultiSelections(); _selectedCloth = null; _selectedPoint = null; _selectedEdge = null; _selectedBorderEdge = null; SyncSelectionHashes(); }
            _glRenderer.MarkAllDirty();
            _statusText = targets.Count > 1 ? $"Đã xóa {targets.Count} mảnh vải." : "Đã xóa mảnh vải.";
        }

        private void DeleteSelectedElement()
        {
            var edgesToDelete = _selectedEdges.Count > 0
                ? new List<(ClothEntity cloth, ControlEdge edge)>(_selectedEdges)
                : (_selectedEdge != null && _selectedCloth != null ? new List<(ClothEntity, ControlEdge)> { (_selectedCloth, _selectedEdge) } : new List<(ClothEntity, ControlEdge)>());
            var ptsToDelete = _selectedPoints.Count > 0
                ? new List<(ClothEntity cloth, ControlPoint point)>(_selectedPoints)
                : (_selectedPoint != null && _selectedCloth != null ? new List<(ClothEntity, ControlPoint)> { (_selectedCloth, _selectedPoint) } : new List<(ClothEntity, ControlPoint)>());

            if (edgesToDelete.Count == 0 && ptsToDelete.Count == 0) return;

            RecordUndoState(edgesToDelete.Count > 0 ? "Xóa DeformEdge" : "Xóa Điểm");
            var touchedCloths = new HashSet<ClothEntity>();

            foreach (var (cloth, edge) in edgesToDelete)
            {
                cloth.Edges.Remove(edge);
                _connections.RemoveAll(c => c.SourceElementId == edge.Id || c.TargetElementId == edge.Id);
                touchedCloths.Add(cloth);
            }

            foreach (var (cloth, pt) in ptsToDelete)
            {
                var attachedEdges = cloth.Edges.Where(ed => ed.PointAId == pt.Id || ed.PointBId == pt.Id).ToList();
                foreach (var ae in attachedEdges)
                    _connections.RemoveAll(c => c.SourceElementId == ae.Id || c.TargetElementId == ae.Id);
                cloth.Edges.RemoveAll(ed => ed.PointAId == pt.Id || ed.PointBId == pt.Id);
                cloth.Points.Remove(pt);
                _connections.RemoveAll(c => c.SourceElementId == pt.Id || c.TargetElementId == pt.Id);
                touchedCloths.Add(cloth);
            }

            foreach (var cloth in touchedCloths)
            {
                ClothSculptEngine.RebuildClothDeformationFromControls(cloth);
                _glRenderer.MarkClothDirty(cloth.Id);
            }
            ApplyAllConstraints();

            var anchorCloth = _selectedCloth ?? touchedCloths.FirstOrDefault();
            if (anchorCloth != null) SelectSingleCloth(anchorCloth);
            else { ClearMultiSelections(); _selectedPoint = null; _selectedEdge = null; SyncSelectionHashes(); }

            _statusText = edgesToDelete.Count > 0
                ? $"Đã xóa {edgesToDelete.Count} DeformEdge (Ctrl+Z để hoàn tác)."
                : $"Đã xóa {ptsToDelete.Count} Điểm (Ctrl+Z để hoàn tác).";
        }

        private void DeleteSelectedBorderEdge()
        {
            if (_selectedCloth == null || _selectedBorderEdge == null) return;
            string bName = _selectedBorderEdge.Name;
            RecordUndoState($"Xóa {bName}");
            if (ClothSculptEngine.DeleteBorderEdgeStrip(_selectedCloth, _selectedBorderEdge))
            {
                ResetBorderEditSession(clearBorderSelection: true);
                RefreshSelectedClothBorders();
                _glRenderer.MarkClothDirty(_selectedCloth.Id);
                _statusText = $"Đã xóa dải mép [{bName}] trên [{_selectedCloth.Name}] (giữ lưới tứ giác sạch).";
            }
            else
            {
                _statusText = $"Không thể xóa thêm cạnh biên [{bName}] (lưới quá mỏng).";
            }
        }

        private bool DescribeActiveSelection(out ClipboardKind kind, out string label)
        {
            var regionCloth = _cloths.FirstOrDefault(c => c.SelectedFaces.Count > 0 || c.SelectedTriangles.Count > 0);
            if (regionCloth != null)
            {
                int cnt = regionCloth.SelectedFaces.Count > 0 ? regionCloth.SelectedFaces.Count : regionCloth.SelectedTriangles.Count;
                kind = ClipboardKind.ClothObjects;
                label = $"Vùng {cnt} ô";
                return true;
            }
            if (_selectedEdges.Count > 0 || _selectedEdge != null)
            {
                int cnt = Math.Max(1, _selectedEdges.Count);
                kind = ClipboardKind.DeformEdges;
                label = cnt > 1 ? $"{cnt} DeformEdge" : $"DeformEdge {(_selectedEdge?.Name ?? _selectedEdges[0].edge.Name)}";
                return true;
            }
            if (_selectedPoints.Count > 0 || _selectedPoint != null)
            {
                int cnt = Math.Max(1, _selectedPoints.Count);
                kind = ClipboardKind.Points;
                label = cnt > 1 ? $"{cnt} Điểm" : $"Điểm {(_selectedPoint?.Name ?? _selectedPoints[0].point.Name)}";
                return true;
            }
            if (_selectedBorderEdge != null)
            {
                kind = ClipboardKind.MeshEdge;
                label = $"MeshEdge {_selectedBorderEdge.Name}";
                return true;
            }
            if (_selectedCloths.Count > 0 || _selectedCloth != null)
            {
                int cnt = Math.Max(1, _selectedCloths.Count);
                kind = ClipboardKind.ClothObjects;
                label = cnt > 1 ? $"{cnt} Mảnh Vải" : (_selectedCloth?.Name ?? _selectedCloths[0].Name);
                return true;
            }
            if (_activeViewport == WorkspaceViewportKind.CharacterViewport)
            {
                if (_selectedRuler != null)
                {
                    kind = ClipboardKind.None;
                    label = _selectedRuler.Name;
                    return true;
                }
                if (_selectedAnchor != null)
                {
                    kind = ClipboardKind.None;
                    label = _selectedAnchor.Name;
                    return true;
                }
            }
            kind = ClipboardKind.None;
            label = "";
            return false;
        }

        private string GetClipboardLabel() => _clipboardKind switch
        {
            ClipboardKind.ClothObjects => _clipboardClothJsons.Count > 1 ? $"{_clipboardClothJsons.Count} Mảnh Vải" : "Mảnh Vải",
            ClipboardKind.Points => _clipboardPoints.Count > 1 ? $"{_clipboardPoints.Count} Điểm" : "Điểm",
            ClipboardKind.DeformEdges => _clipboardEdges.Count > 1 ? $"{_clipboardEdges.Count} DeformEdge" : "DeformEdge",
            ClipboardKind.MeshEdge => "Dáng MeshEdge",
            _ => ""
        };

        private static Vec3 GetPointSculptDelta(ClothEntity cloth, ControlPoint pt)
        {
            if (pt.AttachedVertexIndex >= 0 && pt.AttachedVertexIndex < cloth.Mesh.RestVertices.Count)
                return pt.Position - cloth.Mesh.RestVertices[pt.AttachedVertexIndex];
            return Vec3.Zero;
        }

        private void CopySelection()
        {
            if (!DescribeActiveSelection(out ClipboardKind kind, out string label)) return;

            _clipboardClothJsons.Clear();
            _clipboardPoints.Clear();
            _clipboardEdges.Clear();
            _pasteStaggerCount = 0;

            var regionCloth = _cloths.FirstOrDefault(c => c.SelectedFaces.Count > 0 || c.SelectedTriangles.Count > 0);
            if (regionCloth != null)
            {
                string raw = JsonSerializer.Serialize(regionCloth, SnapshotJsonOptions);
                var clone = JsonSerializer.Deserialize<ClothEntity>(raw, SnapshotJsonOptions);
                if (clone != null)
                {
                    clone.SelectedFaces = new HashSet<int>(regionCloth.SelectedFaces);
                    clone.SelectedTriangles = new HashSet<int>(regionCloth.SelectedTriangles);
                    clone.SelectedVertices = new HashSet<int>(regionCloth.SelectedVertices);
                    clone.ActiveRegion = regionCloth.ActiveRegion;
                    if (ClothSculptEngine.CutSelectedRegion(clone, CutActionMode.ExtractPiece, _clothCounter + 1, out ClothEntity? extracted) && extracted != null)
                    {
                        _clipboardClothJsons.Add(JsonSerializer.Serialize(extracted, SnapshotJsonOptions));
                        _clipboardKind = ClipboardKind.ClothObjects;
                        _statusText = $"Đã Copy [{label}] vào bộ nhớ tạm (Chuột phải -> Paste hoặc Ctrl+V).";
                        return;
                    }
                }
            }

            if (kind == ClipboardKind.DeformEdges)
            {
                var list = _selectedEdges.Count > 0
                    ? _selectedEdges
                    : new List<(ClothEntity cloth, ControlEdge edge)> { (_selectedCloth!, _selectedEdge!) };
                var valid = new List<(ClothEntity cloth, ControlPoint a, ControlPoint b, ControlEdge e)>();
                Vec3 centerSum = Vec3.Zero;
                foreach (var (cloth, edge) in list)
                {
                    var pA = cloth.FindPoint(edge.PointAId);
                    var pB = cloth.FindPoint(edge.PointBId);
                    if (pA != null && pB != null)
                    {
                        valid.Add((cloth, pA, pB, edge));
                        centerSum = centerSum + (pA.Position + pB.Position) * 0.5f;
                    }
                }
                if (valid.Count == 0) return;
                Vec3 anchor = centerSum / valid.Count;
                foreach (var (cloth, pA, pB, edge) in valid)
                {
                    Vec3 mid = (pA.Position + pB.Position) * 0.5f;
                    Vec3 half = (pB.Position - pA.Position) * 0.5f;
                    Vec3 dA = GetPointSculptDelta(cloth, pA);
                    Vec3 dB = GetPointSculptDelta(cloth, pB);
                    _clipboardEdges.Add(new CopiedEdgeTemplate(mid - anchor, half, edge.Radius, edge.Bending, dA, dB));
                }
                _clipboardKind = ClipboardKind.DeformEdges;
            }
            else if (kind == ClipboardKind.Points)
            {
                var list = _selectedPoints.Count > 0
                    ? _selectedPoints
                    : new List<(ClothEntity cloth, ControlPoint point)> { (_selectedCloth!, _selectedPoint!) };
                Vec3 anchor = list[0].point.Position;
                foreach (var (cloth, pt) in list)
                    _clipboardPoints.Add(new CopiedPointTemplate(pt.Position - anchor, pt.Radius, pt.Bending, GetPointSculptDelta(cloth, pt)));
                _clipboardKind = ClipboardKind.Points;
            }
            else if (kind == ClipboardKind.MeshEdge && _selectedCloth != null && _selectedBorderEdge != null)
            {
                ClothSculptEngine.MeasureBorderCurveProfile(_selectedCloth, _selectedBorderEdge, out float chordLen, out float sagitta);
                _clipboardBorder = new CopiedBorderTemplate(_borderEditScale, _borderEditArc2D, _borderEditWrap3D, _borderEditRotateDeg, chordLen, sagitta);
                _clipboardKind = ClipboardKind.MeshEdge;
            }
            else if (kind == ClipboardKind.ClothObjects)
            {
                var list = _selectedCloths.Count > 0
                    ? _selectedCloths
                    : (_selectedCloth != null ? new List<ClothEntity> { _selectedCloth } : new List<ClothEntity>());
                foreach (var c in list)
                    _clipboardClothJsons.Add(JsonSerializer.Serialize(c, SnapshotJsonOptions));
                if (_clipboardClothJsons.Count == 0) return;
                _clipboardKind = ClipboardKind.ClothObjects;
            }

            _statusText = $"Đã Copy [{label}] (Chuột phải -> Paste hoặc Ctrl+V).";
        }

        private void PasteClipboard(bool useContextMenuAnchor)
        {
            if (_clipboardKind == ClipboardKind.None) return;
            _pasteStaggerCount++;

            switch (_clipboardKind)
            {
                case ClipboardKind.ClothObjects:
                {
                    if (_clipboardClothJsons.Count == 0) return;
                    RecordUndoState("Paste Mảnh Vải");
                    var pastedCloths = new List<ClothEntity>();
                    Vec3 offset = new Vec3(0.08f, -0.03f, 0.05f) * _pasteStaggerCount;
                    foreach (string json in _clipboardClothJsons)
                    {
                        var clone = JsonSerializer.Deserialize<ClothEntity>(json, SnapshotJsonOptions);
                        if (clone == null) continue;

                        _clothCounter++;
                        clone.Id = Guid.NewGuid();
                        clone.Name = $"Mảnh Vải #{_clothCounter}";
                        clone.SelectedFaces = new HashSet<int>();
                        clone.SelectedTriangles = new HashSet<int>();
                        clone.SelectedVertices = new HashSet<int>();
                        clone.ActiveRegion = null;
                        clone.CachedBorders = null;
                        clone.RenderSubdividedMesh = null;

                        var ptMap = new Dictionary<Guid, Guid>(clone.Points.Count);
                        foreach (var pt in clone.Points)
                        {
                            Guid newPtId = Guid.NewGuid();
                            ptMap[pt.Id] = newPtId;
                            pt.Id = newPtId;
                            pt.OwnerClothId = clone.Id;
                        }
                        foreach (var edge in clone.Edges)
                        {
                            edge.Id = Guid.NewGuid();
                            edge.OwnerClothId = clone.Id;
                            if (ptMap.TryGetValue(edge.PointAId, out Guid na)) edge.PointAId = na;
                            if (ptMap.TryGetValue(edge.PointBId, out Guid nb)) edge.PointBId = nb;
                        }

                        if (clone.Mesh.Faces.Count > 0 && clone.Mesh.Triangles.Count == 0)
                            clone.Mesh.RebuildTrianglesFromFaces();
                        clone.Mesh.EnsureRestVerticesInitialized();
                        ClothSculptEngine.TranslateEntireCloth(clone, offset);
                        ClothSculptEngine.RebuildDisplayMesh(clone);
                        _cloths.Add(clone);
                        _glRenderer.MarkClothDirty(clone.Id);
                        pastedCloths.Add(clone);
                    }
                    if (pastedCloths.Count > 0)
                    {
                        ClearMultiSelections();
                        foreach (var pc in pastedCloths) _selectedCloths.Add(pc);
                        _selectedCloth = pastedCloths[^1];
                        _selectedPoint = null;
                        _selectedEdge = null;
                        ResetBorderEditSession(clearBorderSelection: true);
                        SyncSelectionHashes();
                        RefreshSelectedClothBorders();
                        _statusText = pastedCloths.Count > 1
                            ? $"Đã Paste {pastedCloths.Count} mảnh vải mới."
                            : $"Đã Paste [{pastedCloths[0].Name}].";
                    }
                    break;
                }

                case ClipboardKind.Points:
                {
                    var targetCloth = (useContextMenuAnchor ? _contextMenuCloth : null) ?? _selectedCloth;
                    if (targetCloth == null || _clipboardPoints.Count == 0) return;

                    RecordUndoState("Paste Điểm");
                    Vec3 anchor = (useContextMenuAnchor && _contextMenuHasClothHit && _contextMenuCloth == targetCloth)
                        ? _contextMenuWorldPos
                        : ClothSculptEngine.GetClothOrigin(targetCloth) + new Vec3(0.06f * _pasteStaggerCount, -0.03f * _pasteStaggerCount, 0f);

                    var pasted = new List<ControlPoint>();
                    bool anySculpt = false;
                    foreach (var tpl in _clipboardPoints)
                    {
                        var pt = ClothSculptEngine.CreatePointOnClothSurface(targetCloth, anchor + tpl.OffsetFromAnchor);
                        pt.Radius = tpl.Radius;
                        pt.Bending = tpl.Bending;
                        if (tpl.SculptDelta.LengthSquared > 1e-8f)
                        {
                            pt.Position = pt.Position + tpl.SculptDelta;
                            pt.IsEdited = true;
                            anySculpt = true;
                        }
                        pasted.Add(pt);
                    }
                    if (anySculpt)
                    {
                        ClothSculptEngine.RebuildClothDeformationFromControls(targetCloth);
                        ApplyAllConstraints();
                    }
                    _glRenderer.MarkClothDirty(targetCloth.Id);
                    ClearMultiSelections();
                    foreach (var pt in pasted) _selectedPoints.Add((targetCloth, pt));
                    _selectedCloth = targetCloth;
                    _selectedPoint = pasted[^1];
                    _selectedEdge = null;
                    ResetBorderEditSession(clearBorderSelection: true);
                    SyncSelectionHashes();
                    _statusText = $"Đã Paste {pasted.Count} Điểm lên [{targetCloth.Name}].";
                    break;
                }

                case ClipboardKind.DeformEdges:
                {
                    var targetCloth = (useContextMenuAnchor ? _contextMenuCloth : null) ?? _selectedCloth;
                    if (targetCloth == null || _clipboardEdges.Count == 0) return;

                    RecordUndoState("Paste DeformEdge");
                    Vec3 anchor = (useContextMenuAnchor && _contextMenuHasClothHit && _contextMenuCloth == targetCloth)
                        ? _contextMenuWorldPos
                        : ClothSculptEngine.GetClothOrigin(targetCloth) + new Vec3(0.06f * _pasteStaggerCount, -0.03f * _pasteStaggerCount, 0f);

                    var pasted = new List<ControlEdge>();
                    bool anySculpt = false;
                    foreach (var tpl in _clipboardEdges)
                    {
                        var pA = ClothSculptEngine.CreatePointOnClothSurface(targetCloth, anchor + tpl.RelCenterOffset - tpl.HalfVec);
                        var pB = ClothSculptEngine.CreatePointOnClothSurface(targetCloth, anchor + tpl.RelCenterOffset + tpl.HalfVec);
                        var edge = ClothSculptEngine.ConnectPointsToEdge(targetCloth, pA, pB);
                        if (edge != null)
                        {
                            edge.Radius = tpl.Radius;
                            edge.Bending = tpl.Bending;
                            pA.Radius = tpl.Radius; pA.Bending = tpl.Bending;
                            pB.Radius = tpl.Radius; pB.Bending = tpl.Bending;
                            if (tpl.DeltaA.LengthSquared > 1e-8f) { pA.Position = pA.Position + tpl.DeltaA; pA.IsEdited = true; anySculpt = true; }
                            if (tpl.DeltaB.LengthSquared > 1e-8f) { pB.Position = pB.Position + tpl.DeltaB; pB.IsEdited = true; anySculpt = true; }
                            if (pA.IsEdited || pB.IsEdited) edge.IsEdited = true;
                            pasted.Add(edge);
                        }
                    }
                    if (anySculpt)
                    {
                        ClothSculptEngine.RebuildClothDeformationFromControls(targetCloth);
                        ApplyAllConstraints();
                    }
                    _glRenderer.MarkClothDirty(targetCloth.Id);
                    if (pasted.Count > 0)
                    {
                        ClearMultiSelections();
                        foreach (var ed in pasted) _selectedEdges.Add((targetCloth, ed));
                        _selectedCloth = targetCloth;
                        _selectedEdge = pasted[^1];
                        _selectedPoint = null;
                        ResetBorderEditSession(clearBorderSelection: true);
                        SyncSelectionHashes();
                    }
                    _statusText = $"Đã Paste {pasted.Count} DeformEdge lên [{targetCloth.Name}].";
                    break;
                }

                case ClipboardKind.MeshEdge:
                {
                    if (_selectedCloth == null || _selectedBorderEdge == null)
                    {
                        _statusText = "Hãy bấm chọn một MeshEdge đích trước khi Paste dáng cạnh biên.";
                        return;
                    }
                    RecordUndoState("Paste dáng MeshEdge");
                    var b = _clipboardBorder;
                    bool hasActiveParams = Math.Abs(b.Scale - 1f) > 1e-4f || Math.Abs(b.Arc2D) > 1e-5f ||
                                           Math.Abs(b.Wrap3D) > 0.05f || Math.Abs(b.RotateDeg) > 0.05f;
                    if (hasActiveParams)
                    {
                        _borderEditScale = b.Scale;
                        _borderEditArc2D = b.Arc2D;
                        _borderEditWrap3D = b.Wrap3D;
                        _borderEditRotateDeg = b.RotateDeg;
                        _borderEditMoveDelta = Vec3.Zero;
                    }
                    else
                    {
                        float ratio = _selectedBorderEdge.ChordLength > 1e-4f
                            ? Math.Clamp(b.SourceChordLen / _selectedBorderEdge.ChordLength, 0.15f, 2.50f)
                            : 1f;
                        _borderEditScale = ratio;
                        _borderEditArc2D = b.MeasuredSagitta;
                        _borderEditWrap3D = 0f;
                        _borderEditRotateDeg = 0f;
                        _borderEditMoveDelta = Vec3.Zero;
                    }
                    ApplyCurrentBorderSessionTransform();
                    _statusText = $"Đã Paste dáng biên vào [{_selectedBorderEdge.Name}].";
                    break;
                }
            }
        }

        #endregion

        #region 3D Picking & Transforms

        private void HandleLeftMouseDownOn3D(Vector2 mousePos)
        {
            if (_activeViewport == WorkspaceViewportKind.CharacterViewport)
            {
                HandleCharacterViewportLeftClick(mousePos);
                return;
            }

            bool shift = KeyboardState.IsKeyDown(Keys.LeftShift) || KeyboardState.IsKeyDown(Keys.RightShift);
            bool allowGizmo = !shift &&
                              _activeTool != EditorToolType.CreatePoint &&
                              _activeTool != EditorToolType.Connect &&
                              _activeTool != EditorToolType.Rotate;

            RaycastSceneFull(mousePos, out GizmoAxis hitGizmo, out ClothEntity? hitCloth, out ControlPoint? hitPt, out ControlEdge? hitEdge, out ClothBorderEdge? hitBorder, out Vec3 hitWorld, allowGizmo);

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
                if (hitBorder != null && hitCloth != null)
                {
                    SelectSingleBorderEdge(hitCloth, hitBorder);
                }
                else if (hitCloth != null && _selectedBorderEdge == null)
                {
                    if (shift) ToggleMultiSelectCloth(hitCloth);
                    else if (_selectedCloths.Count <= 1) SelectSingleCloth(hitCloth);
                }
                if (_selectedBorderEdge != null || _selectedCloth != null || _selectedCloths.Count > 0)
                {
                    _pendingDragUndoSnapshot = CaptureSnapshot(_selectedBorderEdge != null ? "Xoay cạnh biên" : "Xoay vật thể");
                    _isDraggingRotateCloth = true;
                }
                return;
            }

            if (_activeTool == EditorToolType.Scale)
            {
                if (hitBorder != null && hitCloth != null)
                {
                    SelectSingleBorderEdge(hitCloth, hitBorder);
                }
                else if (hitCloth != null && _selectedBorderEdge == null)
                {
                    SelectSingleCloth(hitCloth);
                }
                if (_selectedBorderEdge != null || _selectedCloth != null)
                {
                    _pendingDragUndoSnapshot = CaptureSnapshot(_selectedBorderEdge != null ? "Scale cạnh biên" : "Scale vật thể");
                    _isDraggingScaleElement = true;
                }
                return;
            }

            if (_activeTool == EditorToolType.Move)
            {
                if (hitBorder != null && hitCloth != null)
                {
                    SelectSingleBorderEdge(hitCloth, hitBorder);
                }
                else if (hitPt != null && hitCloth != null)
                {
                    if (shift) ToggleMultiSelectPoint(hitCloth, hitPt);
                    else if (_selectedPoints.Count <= 1) SelectSinglePoint(hitCloth, hitPt);
                }
                else if (hitEdge != null && hitCloth != null)
                {
                    if (shift) ToggleMultiSelectEdge(hitCloth, hitEdge);
                    else if (_selectedEdges.Count <= 1) SelectSingleEdge(hitCloth, hitEdge);
                }
                else if (hitCloth != null && _selectedPoint == null && _selectedEdge == null && _selectedBorderEdge == null)
                {
                    if (shift) ToggleMultiSelectCloth(hitCloth);
                    else if (_selectedCloths.Count <= 1) SelectSingleCloth(hitCloth);
                }

                if (_selectedBorderEdge != null || _selectedPoint != null || _selectedEdge != null || _selectedCloth != null)
                {
                    _pendingDragUndoSnapshot = CaptureSnapshot("Di chuyển thành phần");
                    _isDraggingMoveElement = true;
                }
                return;
            }

            // Select / Cut: Bấm trực tiếp thành phần nào thì chọn thành phần đó; kéo trên mặt vải để quét vùng
            if (_activeTool == EditorToolType.Select)
            {
                if (hitPt != null && hitCloth != null)
                {
                    if (shift) ToggleMultiSelectPoint(hitCloth, hitPt);
                    else SelectSinglePoint(hitCloth, hitPt);
                    _statusText = _selectedPoints.Count >= 2
                        ? $"Đã chọn {_selectedPoints.Count} Điểm"
                        : $"Đã chọn {hitPt.Name}";
                    return;
                }

                if (hitEdge != null && hitCloth != null)
                {
                    if (shift) ToggleMultiSelectEdge(hitCloth, hitEdge);
                    else SelectSingleEdge(hitCloth, hitEdge);
                    _statusText = _selectedEdges.Count >= 2
                        ? $"Đã chọn {_selectedEdges.Count} DeformEdge"
                        : $"Đã chọn {hitEdge.Name}";
                    return;
                }

                if (hitBorder != null && hitCloth != null)
                {
                    SelectSingleBorderEdge(hitCloth, hitBorder);
                    _statusText = $"Đã chọn {hitBorder.Name}";
                    return;
                }

                if (shift && hitCloth != null)
                {
                    ToggleMultiSelectCloth(hitCloth);
                    _statusText = _selectedCloths.Count >= 2
                        ? $"Đã chọn {_selectedCloths.Count} mảnh vải"
                        : $"Đã chọn {hitCloth.Name}";
                    return;
                }
            }

            // Quét vùng (Select trên mặt vải hoặc Cut)
            if (hitCloth != null)
            {
                SelectSingleCloth(hitCloth);
                _regionSelectStart3D = hitWorld;
                _regionSelectStartMousePos = mousePos;
                _hasSweptRegionSelect = false;
                _isDraggingRegionSelect = true;
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
            else if (_selectedBorderEdge != null)
            {
                _borderEditMoveDelta = _borderEditMoveDelta + worldDelta;
                ApplyCurrentBorderSessionTransform();
                return;
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
            RefreshSelectedClothBorders();
        }

        private void ApplyAllConstraints()
        {
            foreach (var conn in _connections)
            {
                ClothSculptEngine.ApplyConnectionConstraint(_cloths, conn);
                _glRenderer.MarkClothDirty(conn.SourceClothId);
            }
            MarkCharacterSolverDirty();
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
            RaycastSceneFull(mousePos, out hitGizmo, out hitCloth, out hitPoint, out hitEdge, out _, out hitWorldPos, allowGizmo);
        }

        private void RaycastSceneFull(
            Vector2 mousePos,
            out GizmoAxis hitGizmo,
            out ClothEntity? hitCloth,
            out ControlPoint? hitPoint,
            out ControlEdge? hitEdge,
            out ClothBorderEdge? hitBorderEdge,
            out Vec3 hitWorldPos,
            bool allowGizmo = true)
        {
            bool allowBorders = _activeTool == EditorToolType.Select ||
                                _activeTool == EditorToolType.Move ||
                                _activeTool == EditorToolType.Rotate ||
                                _activeTool == EditorToolType.Scale;
            GlClothViewportRenderer.RaycastSceneAnalytic(
                mousePos.X, mousePos.Y,
                Math.Max(1, ClientSize.X), Math.Max(1, ClientSize.Y),
                _camYawDeg, _camPitchDeg, _camDistance, _camTarget,
                _cloths, _selectedCloth, _selectedPoint, _selectedEdge,
                allowGizmo && IsTransformGizmoVisibleForTool(), _showElements,
                out hitGizmo, out hitCloth, out hitPoint, out hitEdge, out hitWorldPos,
                _selectedClothBorders, _selectedBorderEdge, allowBorders, out hitBorderEdge);
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
            double yawRad = ActiveCamYaw * Math.PI / 180.0;
            double pitchRad = ActiveCamPitch * Math.PI / 180.0;
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

        private void ResetBorderEditSession(bool clearBorderSelection = false)
        {
            _borderSessionBaseRestVerts = null;
            _borderSessionBasePointPositions = null;
            _borderEditScale = 1.0f;
            _borderEditArc2D = 0.0f;
            _borderEditWrap3D = 0.0f;
            _borderEditRotateDeg = 0.0f;
            _borderEditMoveDelta = Vec3.Zero;
            if (clearBorderSelection)
                _selectedBorderEdge = null;
        }

        private void RefreshSelectedClothBorders()
        {
            if (_selectedCloth == null)
            {
                _selectedClothBorders.Clear();
                _selectedBorderEdge = null;
                return;
            }
            int? keepIdx = _selectedBorderEdge?.Index;
            _selectedClothBorders = ClothSculptEngine.DetectClothBorderEdges(_selectedCloth);
            _selectedCloth.CachedBorders = _selectedClothBorders;
            _selectedBorderEdge = keepIdx.HasValue
                ? _selectedClothBorders.FirstOrDefault(b => b.Index == keepIdx.Value)
                : null;
        }

        private List<ClothBorderEdge> GetOrComputeClothBorders(ClothEntity cloth)
        {
            if (cloth.Id == _selectedCloth?.Id && _selectedClothBorders.Count > 0)
                return _selectedClothBorders;
            cloth.CachedBorders ??= ClothSculptEngine.DetectClothBorderEdges(cloth);
            return cloth.CachedBorders;
        }

        private void EnsureBorderSessionBase()
        {
            if (_selectedCloth == null || _selectedBorderEdge == null) return;
            _selectedCloth.Mesh.EnsureRestVerticesInitialized();
            if (_borderSessionBaseRestVerts == null || _borderSessionBaseRestVerts.Count != _selectedCloth.Mesh.RestVertices.Count)
            {
                _borderSessionBaseRestVerts = new List<Vec3>(_selectedCloth.Mesh.RestVertices);
                _borderSessionBasePointPositions = _selectedCloth.Points.Select(p => p.Position).ToList();
                // Lưu lại đúng danh sách biên gốc cho session hiện tại
                _selectedClothBorders = ClothSculptEngine.DetectClothBorderEdges(_selectedCloth, _borderSessionBaseRestVerts);
                int idx = _selectedBorderEdge.Index;
                _selectedBorderEdge = _selectedClothBorders.FirstOrDefault(b => b.Index == idx) ?? _selectedBorderEdge;
            }
        }

        private Vec3 GetBorderRotateAxis()
        {
            if (_rotateAxisLock == 1) return Vec3.Right;
            if (_rotateAxisLock == 2) return Vec3.Up;
            if (_rotateAxisLock == 3) return Vec3.Forward;
            if (_selectedCloth != null && _selectedCloth.Mesh.Normals.Count > 0)
            {
                Vec3 sum = Vec3.Zero;
                foreach (var n in _selectedCloth.Mesh.Normals) sum = sum + n;
                if (sum.LengthSquared > 1e-6f) return sum.Normalized;
            }
            return Vec3.Forward;
        }

        private void ApplyCurrentBorderSessionTransform()
        {
            if (_selectedCloth == null || _selectedBorderEdge == null) return;
            EnsureBorderSessionBase();
            int selIdx = _selectedBorderEdge.Index;
            var baseBorders = _borderSessionBaseRestVerts != null
                ? ClothSculptEngine.DetectClothBorderEdges(_selectedCloth, _borderSessionBaseRestVerts)
                : _selectedClothBorders;
            var baseBorder = baseBorders.FirstOrDefault(b => b.Index == selIdx) ?? _selectedBorderEdge;

            ClothSculptEngine.ApplyBorderEdgeDeformation(
                _selectedCloth, baseBorders, baseBorder,
                _borderEditScale, _borderEditArc2D, _borderEditWrap3D,
                GetBorderRotateAxis(), _borderEditRotateDeg, _borderEditMoveDelta,
                _borderSessionBaseRestVerts, _borderSessionBasePointPositions);

            _glRenderer.MarkClothDirty(_selectedCloth.Id);
            ApplyAllConstraints();
            _selectedClothBorders = ClothSculptEngine.DetectClothBorderEdges(_selectedCloth);
            _selectedBorderEdge = _selectedClothBorders.FirstOrDefault(b => b.Index == selIdx);
        }

        private void SetClothOriginToWorldPos(ClothEntity cloth, Vec3? newOrigin, string undoLabel)
        {
            RecordUndoState(undoLabel);
            cloth.CustomOrigin = newOrigin;
            _glRenderer.MarkClothDirty(cloth.Id);
            _statusText = newOrigin.HasValue
                ? $"Đã đặt Origin của [{cloth.Name}] tại ({newOrigin.Value.X:F2}, {newOrigin.Value.Y:F2}, {newOrigin.Value.Z:F2})."
                : $"Đã đưa Origin của [{cloth.Name}] về Tâm hình học.";
        }

        private void ClearMultiSelections()
        {
            _selectedPoints.Clear();
            _selectedEdges.Clear();
            _selectedCloths.Clear();
            _selectedPointIds.Clear();
            _selectedEdgeIds.Clear();
            _selectedClothIds.Clear();
            ResetBorderEditSession(clearBorderSelection: true);
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

        private void SelectSingleBorderEdge(ClothEntity cloth, ClothBorderEdge border)
        {
            ClearMultiSelections();
            _selectedCloths.Add(cloth);
            _selectedCloth = cloth;
            _selectedPoint = null;
            _selectedEdge = null;
            SyncSelectionHashes();
            _selectedClothBorders = ClothSculptEngine.DetectClothBorderEdges(cloth);
            _selectedBorderEdge = _selectedClothBorders.FirstOrDefault(b => b.Index == border.Index) ?? border;
        }

        private void SelectSinglePoint(ClothEntity cloth, ControlPoint pt)
        {
            ClearMultiSelections();
            _selectedPoints.Add((cloth, pt));
            _selectedCloth = cloth;
            _selectedPoint = pt;
            _selectedEdge = null;
            SyncSelectionHashes();
            RefreshSelectedClothBorders();
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
            RefreshSelectedClothBorders();
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
            RefreshSelectedClothBorders();
        }

        private void ToggleMultiSelectCloth(ClothEntity cloth)
        {
            _selectedPoints.Clear();
            _selectedEdges.Clear();
            _selectedPoint = null;
            _selectedEdge = null;
            ResetBorderEditSession(clearBorderSelection: true);
            int idx = _selectedCloths.FindIndex(c => c.Id == cloth.Id);
            if (idx >= 0) { if (_selectedCloths.Count > 1) _selectedCloths.RemoveAt(idx); }
            else _selectedCloths.Add(cloth);
            _selectedCloth = _selectedCloths[^1];
            SyncSelectionHashes();
            RefreshSelectedClothBorders();
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
            var dto = new SavedSceneDto
            {
                Cloths = _cloths,
                Connections = _connections,
                Anchors = _charAnchors,
                Rulers = _rulers
            };
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
            _charAnchors.Clear();
            _rulers.Clear();
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
                    c.CachedBorders = null;
                    ClothSculptEngine.RebuildDisplayMesh(c);
                    _cloths.Add(c);
                }
            }
            if (dto.Connections != null)
                _connections.AddRange(dto.Connections);
            if (dto.Anchors != null)
                _charAnchors.AddRange(dto.Anchors);
            if (dto.Rulers != null)
            {
                foreach (var r in dto.Rulers)
                {
                    RebuildRulerGeometry(r);
                    _rulers.Add(r);
                }
            }
            _selectedAnchor = null;
            _selectedRuler = null;
            _activeDraftRuler = null;

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
            RefreshSelectedClothBorders();
            MarkCharacterSolverDirty();
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
            var dto = new SavedSceneDto
            {
                Cloths = _cloths,
                Connections = _connections,
                Anchors = _charAnchors,
                Rulers = _rulers
            };
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
                    _charAnchors.Clear();
                    _rulers.Clear();
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
                    if (dto.Anchors != null) _charAnchors.AddRange(dto.Anchors);
                    if (dto.Rulers != null)
                    {
                        foreach (var r in dto.Rulers)
                        {
                            RebuildRulerGeometry(r);
                            _rulers.Add(r);
                        }
                    }
                    _anchorCounter = _charAnchors.Count;
                    _rulerCounter = _rulers.Count;
                    MarkCharacterSolverDirty();
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
            bool exportWorn = _activeViewport == WorkspaceViewportKind.CharacterViewport;
            if (exportWorn) EnsureCharacterClothsSolved();
            string jsonPath = Path.ChangeExtension(objPath, ".sculpt.json");
            ClothSculptEngine.ExportSceneToObj(objPath, _cloths, preferCharacterPose: exportWorn);
            var opts = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, IgnoreReadOnlyProperties = true };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(_cloths, opts));
            _statusText = $"Đã xuất: {Path.GetFileName(objPath)} + {Path.GetFileName(jsonPath)}";
        }

        private sealed class SavedSceneDto
        {
            public List<ClothEntity> Cloths { get; set; } = new();
            public List<ClothConnection> Connections { get; set; } = new();
            public List<CharacterAnchorPoint> Anchors { get; set; } = new();
            public List<RulerMeasurement> Rulers { get; set; } = new();
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
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.88f, 0.88f, 0.90f, 1.0f));
                ImGui.TextUnformatted("CRIMIRAD");
                ImGui.PopStyleColor();
                ImGui.SameLine(0, 12);

                if (ImGui.BeginMenu("File"))
                {
                    if (ImGui.MenuItem("Màn mới")) BuildCleanGraySquareScene();
                    ImGui.Separator();
                    if (ImGui.MenuItem("Mở dự án...")) OpenProject();
                    if (ImGui.MenuItem("Lưu dự án...")) SaveProject();
                    ImGui.Separator();
                    if (ImGui.MenuItem("Nhập OBJ...")) ImportObj();
                    if (ImGui.MenuItem("Nhập Model Nhân Vật...")) ImportCharacterModelDialog();
                    if (ImGui.MenuItem("Xuất Unity...")) ExportToUnity();
                    ImGui.Separator();
                    if (ImGui.MenuItem("Thoát")) Close();
                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("+ Add"))
                {
                    if (ImGui.MenuItem("Thêm mảnh vải")) SpawnSquareCloth();
                    if (ImGui.MenuItem("Mẫu uốn P1-P2")) BuildDemoFoldedClothScene();
                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("View"))
                {
                    ImGui.MenuItem("Lưới sàn", "", ref _showFloorGrid);
                    ImGui.MenuItem("Điểm & Cạnh", "", ref _showElements);
                    ImGui.MenuItem("Vùng ảnh hưởng", "", ref _showInfluence);
                    ImGui.MenuItem("Gizmo 3D", "", ref _showMoveGizmo);
                    ImGui.MenuItem("Wireframe Overlay", "", ref _showWireOverlay);
                    ImGui.MenuItem("Normals", "", ref _showNormals);
                    ImGui.MenuItem("Bảng bên phải", "", ref _showUiPanels);
                    ImGui.Separator();
                    if (ImGui.MenuItem("Đặt lại góc nhìn"))
                    {
                        if (_activeViewport == WorkspaceViewportKind.CharacterViewport)
                        {
                            _charCamYawDeg = 18; _charCamPitchDeg = 8; _charCamDistance = 2.30; _charCamTarget = new Vec3(0f, 0.92f, 0f);
                        }
                        else
                        {
                            _camYawDeg = 28; _camPitchDeg = 18; _camTarget = Vec3.Zero;
                        }
                    }
                    ImGui.EndMenu();
                }

                // Right-aligned Viewport Switch + Undo/Redo & Shading pills
                float rightGroupW = 515f;
                ImGui.SetCursorPosX(Math.Max(260f, w - rightGroupW));
                if (DrawModePill("Fabric", _activeViewport == WorkspaceViewportKind.FabricViewport))
                {
                    if (_activeViewport != WorkspaceViewportKind.FabricViewport) SwapActiveViewport();
                }
                ImGui.SameLine(0, 2);
                if (DrawModePill("Character", _activeViewport == WorkspaceViewportKind.CharacterViewport))
                {
                    if (_activeViewport != WorkspaceViewportKind.CharacterViewport) SwapActiveViewport();
                }
                ImGui.SameLine(0, 10);
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
                if (_activeViewport == WorkspaceViewportKind.CharacterViewport)
                {
                    RenderCharacterLeftTools();
                }
                else
                {
                    (ToolIconKind selectIcon, string selectLabel) = _regionShape switch
                    {
                        RegionShapeMode.Box => (ToolIconKind.SelectBox, "Vuông"),
                        RegionShapeMode.Circle => (ToolIconKind.SelectCircle, "Tròn"),
                        RegionShapeMode.Cone => (ToolIconKind.SelectCone, "Nón"),
                        _ => (ToolIconKind.SelectBox, "Vuông")
                    };
                    DrawIconToolButton(
                        "###SelectTool",
                        selectIcon,
                        EditorToolType.Select,
                        $"Select - {selectLabel} (Esc)",
                        "##SelectFlyout",
                        pos => _selectFlyoutPos = pos);

                    ImGui.SetNextWindowPos(_selectFlyoutPos, ImGuiCond.Always);
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 7f));
                    if (ImGui.BeginPopup("##SelectFlyout", ImGuiWindowFlags.AlwaysAutoResize))
                    {
                        DrawRegionFlyoutItem("Vuông", ToolIconKind.SelectBox, RegionShapeMode.Box);
                        DrawRegionFlyoutItem("Tròn", ToolIconKind.SelectCircle, RegionShapeMode.Circle);
                        DrawRegionFlyoutItem("Nón", ToolIconKind.SelectCone, RegionShapeMode.Cone);
                        if (_regionShape == RegionShapeMode.Cone)
                        {
                            ImGui.Separator();
                            ImGui.SetNextItemWidth(120f);
                            ImGui.SliderFloat("Góc", ref _coneHalfAngleDeg, 8f, 75f, "%.0f°");
                        }
                        ImGui.EndPopup();
                    }
                    ImGui.PopStyleVar();

                    (ToolIconKind transformIcon, string transformName) = _transformToolMode switch
                    {
                        EditorToolType.Move => (ToolIconKind.Move, "Move (G)"),
                        EditorToolType.Rotate => (ToolIconKind.Rotate, "Rotate (R)"),
                        EditorToolType.Scale => (ToolIconKind.Scale, "Scale (S)"),
                        _ => (ToolIconKind.Move, "Move (G)")
                    };
                    DrawIconToolButton(
                        "###TransformTool",
                        transformIcon,
                        _transformToolMode,
                        transformName,
                        "##TransformFlyout",
                        pos => _transformFlyoutPos = pos);

                    ImGui.SetNextWindowPos(_transformFlyoutPos, ImGuiCond.Always);
                    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 7f));
                    if (ImGui.BeginPopup("##TransformFlyout", ImGuiWindowFlags.AlwaysAutoResize))
                    {
                        DrawTransformFlyoutItem("Move (G)", ToolIconKind.Move, EditorToolType.Move);
                        DrawTransformFlyoutItem("Rotate (R)", ToolIconKind.Rotate, EditorToolType.Rotate);
                        DrawTransformFlyoutItem("Scale (S)", ToolIconKind.Scale, EditorToolType.Scale);
                        ImGui.EndPopup();
                    }
                    ImGui.PopStyleVar();

                    DrawIconToolButton("###CreateTool", ToolIconKind.CreatePoint, EditorToolType.CreatePoint, "Create (A)");
                }
            }
            ImGui.End();

            // 3. Unified Blender-Style Right Docked Sidebar (Outliner + Properties)
            float rightSidebarW = 328f;
            float rightMargin = 8f;
            if (_showUiPanels)
            {
                float panelH = Math.Max(260f, h - toolWinY - 34f);
                ImGui.SetNextWindowPos(new Vector2(w - rightSidebarW - rightMargin, toolWinY), ImGuiCond.Always);
                ImGui.SetNextWindowSize(new Vector2(rightSidebarW, panelH), ImGuiCond.Always);
                ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.135f, 0.135f, 0.138f, 0.97f));
                ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.23f, 0.23f, 0.24f, 1.0f));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 6f));
                ImGuiWindowFlags sidebarFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                                                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
                if (ImGui.Begin("##RightBlenderSidebar", sidebarFlags))
                {
                    RenderUnifiedBlenderRightSidebar();
                }
                ImGui.End();
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);
            }

            // 5. Top-Right Interactive Blender 6-Axis Navigation Gizmo
            float navOffsetX = _showUiPanels ? (rightSidebarW + rightMargin + 12f) : rightMargin;
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
                var regionCloth = _cloths.FirstOrDefault(c => c.SelectedFaces.Count > 0 || c.SelectedTriangles.Count > 0);
                if (regionCloth != null)
                {
                    int rCount = regionCloth.SelectedFaces.Count > 0 ? regionCloth.SelectedFaces.Count : regionCloth.SelectedTriangles.Count;
                    ImGui.TextColored(new Vector4(0.96f, 0.72f, 0.28f, 1f), $"Vùng: {rCount} ô");
                    if (ImGui.MenuItem("Extract"))
                        ExecuteCut(CutActionMode.ExtractPiece);
                    if (ImGui.MenuItem("Bỏ chọn"))
                        ClearAllRegionSelections();
                    ImGui.Separator();
                }

                if (_selectedPoints.Count >= 2)
                {
                    var p1 = _selectedPoints[0];
                    var p2 = _selectedPoints[1];
                    if (p1.cloth.Id == p2.cloth.Id)
                    {
                        if (ImGui.MenuItem("Connect"))
                            ExecuteConnectOnMultiSelection(ConnectMode.CreateEdge);
                    }
                    if (ImGui.MenuItem("Snap"))
                        ExecuteConnectOnMultiSelection(ConnectMode.Snap);
                    if (ImGui.MenuItem("Hang"))
                        ExecuteConnectOnMultiSelection(ConnectMode.Hang);
                    DrawHangDistanceSlider();
                    ImGui.Separator();
                }
                else if (_selectedEdges.Count >= 2)
                {
                    if (ImGui.MenuItem("Snap"))
                        ExecuteConnectOnMultiSelection(ConnectMode.Snap);
                    if (ImGui.MenuItem("Hang"))
                        ExecuteConnectOnMultiSelection(ConnectMode.Hang);
                    DrawHangDistanceSlider();
                    ImGui.Separator();
                }

                bool hasSel = DescribeActiveSelection(out ClipboardKind selClipKind, out _);
                bool canCopySel = hasSel && selClipKind != ClipboardKind.None;
                if (hasSel || _clipboardKind != ClipboardKind.None)
                {
                    if (canCopySel && ImGui.MenuItem("Copy", "Ctrl+C"))
                        CopySelection();
                    if (_clipboardKind != ClipboardKind.None && ImGui.MenuItem("Paste", "Ctrl+V"))
                        PasteClipboard(useContextMenuAnchor: true);
                    if (hasSel && ImGui.MenuItem("Delete", "Del"))
                        DeleteCurrentSelection();
                    ImGui.Separator();
                }

                if (_contextMenuCloth != null)
                {
                    if (_contextMenuPoint != null)
                    {
                        if (ImGui.MenuItem($"Set Origin ({_contextMenuPoint.Name})"))
                            SetClothOriginToWorldPos(_contextMenuCloth, _contextMenuPoint.Position, $"Origin: {_contextMenuPoint.Name}");
                    }
                    else if (_contextMenuNearestVertexPos.HasValue)
                    {
                        var vp = _contextMenuNearestVertexPos.Value;
                        if (ImGui.MenuItem("Set Origin"))
                            SetClothOriginToWorldPos(_contextMenuCloth, vp, "Set Origin");
                    }
                    if (_contextMenuCloth.CustomOrigin.HasValue)
                    {
                        if (ImGui.MenuItem("Reset Origin"))
                            SetClothOriginToWorldPos(_contextMenuCloth, null, "Reset Origin");
                    }
                    ImGui.Separator();

                    if (ImGui.MenuItem("Shade Smooth")) ApplyClothSmoothMode(_contextMenuCloth, MeshShadingSmoothMode.Smooth, _contextMenuCloth.AutoSmoothAngleDeg);
                    if (ImGui.MenuItem("Auto Smooth 30°")) ApplyClothSmoothMode(_contextMenuCloth, MeshShadingSmoothMode.AutoSmooth, 30f);
                    if (ImGui.MenuItem("Auto Smooth 45°")) ApplyClothSmoothMode(_contextMenuCloth, MeshShadingSmoothMode.AutoSmooth, 45f);
                    if (ImGui.MenuItem("Shade Flat")) ApplyClothSmoothMode(_contextMenuCloth, MeshShadingSmoothMode.Flat, _contextMenuCloth.AutoSmoothAngleDeg);
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

            // 8. Bottom Status Bar + Character Ruler Labels + Sub-Viewport PiP
            float footerH = 26f;
            RenderCharacterRulerOverlayLabels();
            RenderSubViewportPip(w, h, _showUiPanels ? (rightSidebarW + rightMargin) : 0f, footerH);

            ImGui.SetNextWindowPos(new Vector2(0, h - footerH), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(w, footerH), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.95f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 4));
            if (ImGui.Begin("##StatusFooter", hudFlags))
            {
                ImGui.TextColored(new Vector4(0.76f, 0.76f, 0.78f, 1f), _statusText);
                int totalPts = _cloths.Sum(c => c.Points.Count);
                int totalEdges = _cloths.Sum(c => c.Edges.Count);
                int totalTris = _cloths.Sum(c => (c.RenderSubdividedMesh ?? c.Mesh).Triangles.Count / 3);
                float fps = deltaSec > 1e-4f ? (1f / deltaSec) : 60f;
                string stats = $"Vải: {_cloths.Count} ({totalTris:N0} tris) | Điểm: {totalPts} | Cạnh: {totalEdges} | Nối: {_connections.Count} | {fps:F0} FPS";
                float stW = ImGui.CalcTextSize(stats).X;
                ImGui.SameLine(Math.Max(420f, w - stW - 14f));
                ImGui.TextDisabled(stats);
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }

        private enum ToolIconKind
        {
            SelectBox,
            SelectCircle,
            SelectCone,
            Move,
            Rotate,
            Scale,
            CreatePoint,
            Cut,
            Connect,
            Ruler,
            FormHugging,
            FormFitting
        }

        private void DrawIconToolButton(
            string id,
            ToolIconKind iconKind,
            EditorToolType tool,
            string tooltip,
            string? flyoutPopupId = null,
            Action<Vector2>? onFlyoutOpen = null)
        {
            bool active = _activeTool == tool;
            if (active)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.30f, 0.30f, 0.33f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.68f, 0.68f, 0.71f, 1.0f));
            }
            if (ImGui.Button(id, new Vector2(42, 42)))
            {
                _activeTool = tool;
                ResetConnectStep();
            }

            Vector2 bMin = ImGui.GetItemRectMin();
            Vector2 bMax = ImGui.GetItemRectMax();
            var dl = ImGui.GetWindowDrawList();
            Vector2 center = (bMin + bMax) * 0.5f;
            uint mainCol = ImGui.GetColorU32(active
                ? new Vector4(0.96f, 0.96f, 0.97f, 1.0f)
                : new Vector4(0.78f, 0.78f, 0.80f, 0.96f));
            uint accentCol = ImGui.GetColorU32(active
                ? new Vector4(0.84f, 0.84f, 0.86f, 1.0f)
                : new Vector4(0.62f, 0.62f, 0.65f, 0.95f));
            DrawToolVectorIcon(dl, center, 11.5f, iconKind, mainCol, accentCol, drawSoftFill: true);

            if (!string.IsNullOrEmpty(flyoutPopupId))
            {
                uint triCol = ImGui.GetColorU32(new Vector4(0.76f, 0.76f, 0.78f, 0.92f));
                dl.AddTriangleFilled(
                    new Vector2(bMax.X - 3.5f, bMax.Y - 3.5f),
                    new Vector2(bMax.X - 10f, bMax.Y - 3.5f),
                    new Vector2(bMax.X - 3.5f, bMax.Y - 10f),
                    triCol);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    onFlyoutOpen?.Invoke(new Vector2(bMax.X + 6f, bMin.Y));
                    ImGui.OpenPopup(flyoutPopupId);
                }
            }
            if (active) ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        }

        private static void DrawToolVectorIcon(
            ImDrawListPtr dl,
            Vector2 c,
            float r,
            ToolIconKind kind,
            uint mainCol,
            uint accentCol,
            bool drawSoftFill)
        {
            float w = Math.Max(1.35f, r * 0.16f);
            switch (kind)
            {
                case ToolIconKind.SelectBox:
                {
                    float s = r * 0.84f;
                    if (drawSoftFill)
                        dl.AddRectFilled(new Vector2(c.X - s, c.Y - s), new Vector2(c.X + s, c.Y + s), ImGui.GetColorU32(new Vector4(0.53f, 0.75f, 0.82f, 0.12f)));
                    float[] t0 = { 0f, 0.37f, 0.74f };
                    float[] t1 = { 0.26f, 0.63f, 1.0f };
                    for (int i = 0; i < 3; i++)
                    {
                        float a = -s + 2f * s * t0[i];
                        float b = -s + 2f * s * t1[i];
                        dl.AddLine(new Vector2(c.X + a, c.Y - s), new Vector2(c.X + b, c.Y - s), mainCol, w);
                        dl.AddLine(new Vector2(c.X + a, c.Y + s), new Vector2(c.X + b, c.Y + s), mainCol, w);
                        dl.AddLine(new Vector2(c.X - s, c.Y + a), new Vector2(c.X - s, c.Y + b), mainCol, w);
                        dl.AddLine(new Vector2(c.X + s, c.Y + a), new Vector2(c.X + s, c.Y + b), mainCol, w);
                    }
                    break;
                }
                case ToolIconKind.SelectCircle:
                {
                    float cr = r * 0.86f;
                    if (drawSoftFill)
                        dl.AddCircleFilled(c, cr, ImGui.GetColorU32(new Vector4(0.53f, 0.75f, 0.82f, 0.12f)), 24);
                    const int dashes = 8;
                    for (int i = 0; i < dashes; i++)
                    {
                        float a0 = i * MathF.Tau / dashes;
                        float a1 = a0 + MathF.Tau / dashes * 0.56f;
                        Vector2 p0 = new(c.X + MathF.Cos(a0) * cr, c.Y + MathF.Sin(a0) * cr);
                        Vector2 p1 = new(c.X + MathF.Cos(a1) * cr, c.Y + MathF.Sin(a1) * cr);
                        dl.AddLine(p0, p1, mainCol, w);
                    }
                    break;
                }
                case ToolIconKind.SelectCone:
                {
                    Vector2 apex = new(c.X, c.Y + r * 0.82f);
                    float arcR = r * 1.48f;
                    float aStart = -MathF.PI * 0.72f;
                    float aEnd = -MathF.PI * 0.28f;
                    Vector2 pL = new(apex.X + MathF.Cos(aStart) * arcR, apex.Y + MathF.Sin(aStart) * arcR);
                    Vector2 pR = new(apex.X + MathF.Cos(aEnd) * arcR, apex.Y + MathF.Sin(aEnd) * arcR);
                    if (drawSoftFill)
                        dl.AddTriangleFilled(apex, pL, pR, ImGui.GetColorU32(new Vector4(0.53f, 0.75f, 0.82f, 0.16f)));
                    dl.AddLine(apex, pL, mainCol, w);
                    dl.AddLine(apex, pR, mainCol, w);
                    const int segs = 6;
                    for (int i = 0; i < segs; i++)
                    {
                        float u0 = aStart + (aEnd - aStart) * (i / (float)segs);
                        float u1 = aStart + (aEnd - aStart) * ((i + 0.65f) / segs);
                        dl.AddLine(
                            new Vector2(apex.X + MathF.Cos(u0) * arcR, apex.Y + MathF.Sin(u0) * arcR),
                            new Vector2(apex.X + MathF.Cos(u1) * arcR, apex.Y + MathF.Sin(u1) * arcR),
                            accentCol, w);
                    }
                    dl.AddCircleFilled(apex, Math.Max(1.6f, r * 0.17f), accentCol);
                    break;
                }
                case ToolIconKind.Move:
                {
                    float tip = r * 0.94f;
                    float stem = r * 0.60f;
                    float ah = r * 0.28f;
                    dl.AddLine(new Vector2(c.X - stem, c.Y), new Vector2(c.X + stem, c.Y), mainCol, w);
                    dl.AddLine(new Vector2(c.X, c.Y - stem), new Vector2(c.X, c.Y + stem), mainCol, w);
                    // N, S, W, E arrowheads
                    dl.AddTriangleFilled(new(c.X, c.Y - tip), new(c.X - ah, c.Y - stem + 1f), new(c.X + ah, c.Y - stem + 1f), accentCol);
                    dl.AddTriangleFilled(new(c.X, c.Y + tip), new(c.X + ah, c.Y + stem - 1f), new(c.X - ah, c.Y + stem - 1f), accentCol);
                    dl.AddTriangleFilled(new(c.X - tip, c.Y), new(c.X - stem + 1f, c.Y + ah), new(c.X - stem + 1f, c.Y - ah), accentCol);
                    dl.AddTriangleFilled(new(c.X + tip, c.Y), new(c.X + stem - 1f, c.Y - ah), new(c.X + stem - 1f, c.Y + ah), accentCol);
                    break;
                }
                case ToolIconKind.Rotate:
                {
                    float cr = r * 0.78f;
                    const int segs = 16;
                    float a0 = MathF.PI * 0.22f;
                    float a1 = MathF.PI * 1.72f;
                    for (int i = 0; i < segs; i++)
                    {
                        float tA = a0 + (a1 - a0) * (i / (float)segs);
                        float tB = a0 + (a1 - a0) * ((i + 1) / (float)segs);
                        dl.AddLine(
                            new Vector2(c.X + MathF.Cos(tA) * cr, c.Y + MathF.Sin(tA) * cr),
                            new Vector2(c.X + MathF.Cos(tB) * cr, c.Y + MathF.Sin(tB) * cr),
                            mainCol, w * 1.08f);
                    }
                    Vector2 headBase = new(c.X + MathF.Cos(a1) * cr, c.Y + MathF.Sin(a1) * cr);
                    Vector2 tangent = Vector2.Normalize(new(-MathF.Sin(a1), MathF.Cos(a1)));
                    Vector2 radial = Vector2.Normalize(new(MathF.Cos(a1), MathF.Sin(a1)));
                    float ah = r * 0.36f;
                    dl.AddTriangleFilled(
                        headBase + tangent * ah * 0.9f,
                        headBase - tangent * ah * 0.45f + radial * ah * 0.65f,
                        headBase - tangent * ah * 0.45f - radial * ah * 0.65f,
                        accentCol);
                    break;
                }
                case ToolIconKind.Scale:
                {
                    float s = r * 0.84f;
                    dl.AddRect(new Vector2(c.X - s, c.Y - s), new Vector2(c.X + s, c.Y + s), mainCol, 0f, ImDrawFlags.None, w * 0.85f);
                    Vector2 smMin = new(c.X - s, c.Y + s * 0.14f);
                    Vector2 smMax = new(c.X - s * 0.14f, c.Y + s);
                    dl.AddRectFilled(smMin, smMax, accentCol);
                    Vector2 arrStart = new(c.X + s * 0.02f, c.Y - s * 0.02f);
                    Vector2 arrTip = new(c.X + s * 0.72f, c.Y - s * 0.72f);
                    dl.AddLine(arrStart, arrTip, mainCol, w);
                    dl.AddTriangleFilled(
                        arrTip + new Vector2(1f, -1f),
                        new Vector2(arrTip.X - r * 0.42f, arrTip.Y),
                        new Vector2(arrTip.X, arrTip.Y + r * 0.42f),
                        accentCol);
                    break;
                }
                case ToolIconKind.CreatePoint:
                {
                    Vector2 ptCenter = new(c.X - r * 0.18f, c.Y + r * 0.22f);
                    float dotR = Math.Max(2.2f, r * 0.30f);
                    dl.AddCircleFilled(ptCenter, dotR, accentCol, 16);
                    dl.AddCircle(ptCenter, dotR, mainCol, 16, w * 0.8f);
                    Vector2 plusC = new(c.X + r * 0.46f, c.Y - r * 0.42f);
                    float pLen = r * 0.36f;
                    dl.AddLine(new(plusC.X - pLen, plusC.Y), new(plusC.X + pLen, plusC.Y), mainCol, w * 1.15f);
                    dl.AddLine(new(plusC.X, plusC.Y - pLen), new(plusC.X, plusC.Y + pLen), mainCol, w * 1.15f);
                    break;
                }
                case ToolIconKind.Cut:
                {
                    float loopR = Math.Max(1.8f, r * 0.25f);
                    Vector2 hL = new(c.X - r * 0.46f, c.Y + r * 0.50f);
                    Vector2 hR = new(c.X + r * 0.46f, c.Y + r * 0.50f);
                    Vector2 pivot = new(c.X, c.Y + r * 0.02f);
                    Vector2 bR = new(c.X + r * 0.58f, c.Y - r * 0.74f);
                    Vector2 bL = new(c.X - r * 0.58f, c.Y - r * 0.74f);
                    dl.AddLine(hL + new Vector2(loopR * 0.5f, -loopR * 0.6f), bR, mainCol, w);
                    dl.AddLine(hR + new Vector2(-loopR * 0.5f, -loopR * 0.6f), bL, mainCol, w);
                    dl.AddCircle(hL, loopR, accentCol, 14, w * 0.95f);
                    dl.AddCircle(hR, loopR, accentCol, 14, w * 0.95f);
                    dl.AddCircleFilled(pivot, Math.Max(1.4f, r * 0.14f), mainCol, 10);
                    break;
                }
                case ToolIconKind.Connect:
                {
                    Vector2 pA = new(c.X - r * 0.54f, c.Y + r * 0.48f);
                    Vector2 pB = new(c.X + r * 0.54f, c.Y - r * 0.48f);
                    float dotR = Math.Max(2.0f, r * 0.24f);
                    dl.AddLine(pA, pB, mainCol, w * 1.05f);
                    dl.AddCircleFilled(pA, dotR, accentCol, 14);
                    dl.AddCircleFilled(pB, dotR, accentCol, 14);
                    dl.AddCircle(pA, dotR, mainCol, 14, 1.0f);
                    dl.AddCircle(pB, dotR, mainCol, 14, 1.0f);
                    break;
                }
                case ToolIconKind.Ruler:
                {
                    Vector2 p0 = new(c.X - r * 0.78f, c.Y + r * 0.60f);
                    Vector2 p1 = new(c.X + r * 0.78f, c.Y - r * 0.60f);
                    dl.AddLine(p0, p1, mainCol, w * 1.25f);
                    Vector2 perp = Vector2.Normalize(new Vector2(-(p1.Y - p0.Y), p1.X - p0.X));
                    for (int k = 0; k <= 4; k++)
                    {
                        float t = k / 4f;
                        Vector2 pt = Vector2.Lerp(p0, p1, t);
                        float tickLen = (k % 2 == 0) ? r * 0.34f : r * 0.20f;
                        dl.AddLine(pt, pt + perp * tickLen, accentCol, w * 0.9f);
                    }
                    dl.AddCircleFilled(p0, Math.Max(1.6f, r * 0.16f), accentCol);
                    dl.AddCircleFilled(p1, Math.Max(1.6f, r * 0.16f), accentCol);
                    break;
                }
                case ToolIconKind.FormHugging:
                {
                    // Hai vấu lồi và đường vải ôm khít sâu xuống khe lõm ở giữa
                    Vector2 pL = new(c.X - r * 0.85f, c.Y + r * 0.25f);
                    Vector2 pPk1 = new(c.X - r * 0.40f, c.Y - r * 0.45f);
                    Vector2 pVal = new(c.X, c.Y + r * 0.35f);
                    Vector2 pPk2 = new(c.X + r * 0.40f, c.Y - r * 0.45f);
                    Vector2 pR = new(c.X + r * 0.85f, c.Y + r * 0.25f);
                    dl.AddLine(pL, pPk1, mainCol, w * 1.1f);
                    dl.AddLine(pPk1, pVal, accentCol, w * 1.2f);
                    dl.AddLine(pVal, pPk2, accentCol, w * 1.2f);
                    dl.AddLine(pPk2, pR, mainCol, w * 1.1f);
                    dl.AddCircleFilled(pVal, Math.Max(1.5f, r * 0.15f), accentCol);
                    break;
                }
                case ToolIconKind.FormFitting:
                {
                    // Hai vấu lồi với đường vải bắc cầu ngang qua đỉnh, bỏ qua vùng lõm bên dưới
                    Vector2 pL = new(c.X - r * 0.85f, c.Y + r * 0.25f);
                    Vector2 pPk1 = new(c.X - r * 0.42f, c.Y - r * 0.42f);
                    Vector2 pPk2 = new(c.X + r * 0.42f, c.Y - r * 0.42f);
                    Vector2 pR = new(c.X + r * 0.85f, c.Y + r * 0.25f);
                    dl.AddLine(pL, pPk1, mainCol, w * 1.0f);
                    dl.AddLine(pPk1, pPk2, accentCol, w * 1.3f);
                    dl.AddLine(pPk2, pR, mainCol, w * 1.0f);
                    // Đường lõm mờ bên dưới biểu thị phần lõm được bỏ qua
                    dl.AddLine(pPk1, new(c.X, c.Y + r * 0.35f), ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.48f, 0.7f)), 1.0f);
                    dl.AddLine(new(c.X, c.Y + r * 0.35f), pPk2, ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.48f, 0.7f)), 1.0f);
                    break;
                }
            }
        }

        private void DrawRegionFlyoutItem(string label, ToolIconKind icon, RegionShapeMode shape)
        {
            DrawPopupIconItem(label, icon, _regionShape == shape, () =>
            {
                _regionShape = shape;
                _activeTool = EditorToolType.Select;
            });
        }

        private void DrawTransformFlyoutItem(string label, ToolIconKind icon, EditorToolType mode)
        {
            DrawPopupIconItem(label, icon, _transformToolMode == mode, () =>
            {
                _transformToolMode = mode;
                _activeTool = mode;
            });
        }

        private void DrawHangDistanceSlider()
        {
            ImGui.SetNextItemWidth(145f);
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
        }

        private void DrawPopupIconItem(string label, ToolIconKind icon, bool selected, Action apply)
        {
            Vector2 p = ImGui.GetCursorScreenPos();
            float rowH = ImGui.GetTextLineHeightWithSpacing();
            if (ImGui.Selectable($"      {label}", selected, ImGuiSelectableFlags.None, new Vector2(132f, 0f)))
            {
                apply();
                ResetConnectStep();
                ImGui.CloseCurrentPopup();
            }
            var dl = ImGui.GetWindowDrawList();
            uint mainCol = ImGui.GetColorU32(new Vector4(0.88f, 0.92f, 0.96f, 0.96f));
            uint accentCol = ImGui.GetColorU32(new Vector4(0.53f, 0.78f, 0.88f, 1.0f));
            DrawToolVectorIcon(dl, new Vector2(p.X + 10f, p.Y + rowH * 0.45f), 6.5f, icon, mainCol, accentCol, drawSoftFill: false);
        }

        private static void DrawTogglePill(string label, ref bool value)
        {
            bool v = value;
            if (v) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.36f, 0.36f, 0.39f, 1.0f));
            if (ImGui.Button(label)) value = !value;
            if (v) ImGui.PopStyleColor();
        }

        private static bool DrawModePill(string label, bool active)
        {
            if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.36f, 0.36f, 0.39f, 1.0f));
            bool clicked = ImGui.Button(label);
            if (active) ImGui.PopStyleColor();
            return clicked;
        }

        private void RenderUnifiedBlenderRightSidebar()
        {
            Vector2 avail = ImGui.GetContentRegionAvail();
            float splitterH = 6f;
            float outlinerH = Math.Max(110f, Math.Min(avail.Y - 150f, avail.Y * _outlinerSplitRatio));
            float propsH = Math.Max(120f, avail.Y - outlinerH - splitterH - 4f);

            // ================= TOP: OUTLINER =================
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.114f, 0.114f, 0.118f, 1.0f));
            if (ImGui.BeginChild("##BlenderOutliner", new Vector2(0f, outlinerH), ImGuiChildFlags.Borders))
            {
                RenderOutlinerSection();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();

            // ================= SPLITTER HANDLE =================
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.19f, 0.19f, 0.20f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.34f, 0.34f, 0.36f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.46f, 0.46f, 0.48f, 1.0f));
            ImGui.Button("##OutlinerSplitter", new Vector2(-1f, splitterH));
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                _outlinerSplitRatio = Math.Clamp(_outlinerSplitRatio + ImGui.GetIO().MouseDelta.Y / Math.Max(200f, avail.Y), 0.20f, 0.72f);
            }
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
            ImGui.PopStyleColor(3);

            // ================= BOTTOM: PROPERTIES =================
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.155f, 0.155f, 0.158f, 1.0f));
            if (ImGui.BeginChild("##BlenderProperties", new Vector2(0f, propsH), ImGuiChildFlags.Borders))
            {
                RenderPropertiesSection();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
        }

        private void RenderOutlinerSection()
        {
            ImGui.TextColored(new Vector4(0.84f, 0.84f, 0.86f, 1f), "Outliner");
            if (_activeViewport == WorkspaceViewportKind.CharacterViewport)
            {
                ImGui.SameLine(ImGui.GetWindowWidth() - 246f);
                if (ImGui.SmallButton("+ Model")) ImportCharacterModelDialog();
                ImGui.SameLine(0, 4);
            }
            else
            {
                ImGui.SameLine(ImGui.GetWindowWidth() - 186f);
            }
            if (ImGui.SmallButton("+ Vải")) SpawnSquareCloth();
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton("Xóa Vải")) DeleteSelectedCloth();
            ImGui.SameLine(0, 4);
            if (ImGui.SmallButton("Xóa Đ/C")) DeleteSelectedElement();

            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##OutlinerSearch", "Search...", ref _outlinerSearch, 64);

            string filter = _outlinerSearch.Trim();
            bool hasFilter = filter.Length > 0;
            bool MatchesFilter(string s) => !hasFilter || s.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            if (ImGui.BeginChild("##OutlinerTree", new Vector2(0f, 0f), ImGuiChildFlags.None))
            {
                bool shift = ImGui.GetIO().KeyShift;
                RenderCharacterOutlinerItems(hasFilter, MatchesFilter);
                bool sceneOpen = ImGui.TreeNodeEx("Fabric Collector", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
                if (sceneOpen)
                {
                    for (int ci = 0; ci < _cloths.Count; ci++)
                    {
                        var cloth = _cloths[ci];
                        var borders = GetOrComputeClothBorders(cloth);

                        bool clothMatch = MatchesFilter(cloth.Name) ||
                                          borders.Any(b => MatchesFilter(b.Name) || MatchesFilter("MeshEdge")) ||
                                          cloth.Edges.Any(e => MatchesFilter(e.Name) || MatchesFilter("DeformEdge")) ||
                                          cloth.Points.Any(p => MatchesFilter(p.Name) || MatchesFilter("DeformEdge"));
                        if (!clothMatch) continue;

                        bool isClothSel = _selectedCloth?.Id == cloth.Id && _selectedPoint == null && _selectedEdge == null && _selectedBorderEdge == null;
                        bool isPrimaryCloth = _selectedCloth?.Id == cloth.Id || _selectedClothIds.Contains(cloth.Id);

                        ImGuiTreeNodeFlags cFlags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.AllowOverlap;
                        if (isPrimaryCloth) cFlags |= ImGuiTreeNodeFlags.DefaultOpen;
                        if (isClothSel) cFlags |= ImGuiTreeNodeFlags.Selected;

                        bool clothOpen = ImGui.TreeNodeEx($"{cloth.Name}##c_{cloth.Id}", cFlags);
                        if (ImGui.BeginDragDropSource())
                        {
                            _draggedClothFromOutliner = cloth;
                            ImGui.SetDragDropPayload("OUTLINER_CLOTH", IntPtr.Zero, 0);
                            ImGui.Text($"Gắn {cloth.Name} -> Model");
                            ImGui.EndDragDropSource();
                        }
                        if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
                        {
                            if (shift) ToggleMultiSelectCloth(cloth);
                            else SelectSingleCloth(cloth);
                        }

                        // Nút con mắt Hiện/Ẩn ở mép phải giống Blender
                        float eyeBtnX = ImGui.GetWindowWidth() - 36f;
                        ImGui.SameLine(Math.Max(140f, eyeBtnX));
                        ImGui.PushStyleColor(ImGuiCol.Button, cloth.IsVisible ? new Vector4(0.27f, 0.27f, 0.29f, 0.90f) : new Vector4(0.16f, 0.16f, 0.17f, 0.70f));
                        if (ImGui.SmallButton(cloth.IsVisible ? "H##vis_" + ci : "-##vis_" + ci))
                        {
                            cloth.IsVisible = !cloth.IsVisible;
                            _statusText = cloth.IsVisible ? $"Đã hiện [{cloth.Name}]." : $"Đã ẩn [{cloth.Name}].";
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(cloth.IsVisible ? "Đang hiện - Click để Ẩn mảnh vải" : "Đang ẩn - Click để Hiện lại mảnh vải");
                        ImGui.PopStyleColor();

                        if (clothOpen)
                        {
                            // 1. Nhóm MeshEdge
                            bool meOpen = ImGui.TreeNodeEx($"MeshEdge ({borders.Count})##me_{cloth.Id}", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
                            if (meOpen)
                            {
                                foreach (var border in borders)
                                {
                                    if (!MatchesFilter(border.Name) && !MatchesFilter("MeshEdge") && !MatchesFilter(cloth.Name))
                                        continue;
                                    bool sel = _selectedCloth?.Id == cloth.Id && _selectedBorderEdge != null && _selectedBorderEdge.Index == border.Index;
                                    ImGui.PushStyleColor(ImGuiCol.Text, sel ? new Vector4(1f, 0.84f, 0.28f, 1f) : new Vector4(0.84f, 0.84f, 0.86f, 1f));
                                    if (ImGui.Selectable($"  {border.Name}##be_{cloth.Id}_{border.Index}", sel))
                                    {
                                        SelectSingleBorderEdge(cloth, border);
                                    }
                                    ImGui.PopStyleColor();
                                }
                                ImGui.TreePop();
                            }

                            // 2. Nhóm DeformEdge
                            bool deOpen = ImGui.TreeNodeEx($"DeformEdge ({cloth.Edges.Count})##de_{cloth.Id}", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth);
                            if (deOpen)
                            {
                                foreach (var edge in cloth.Edges)
                                {
                                    if (!MatchesFilter(edge.Name) && !MatchesFilter("DeformEdge") && !MatchesFilter(cloth.Name))
                                        continue;
                                    bool sel = _selectedEdgeIds.Contains(edge.Id) || _selectedEdge?.Id == edge.Id;
                                    ImGui.PushStyleColor(ImGuiCol.Text, sel ? new Vector4(1f, 0.80f, 0.25f, 1f) : new Vector4(0.86f, 0.86f, 0.88f, 1f));
                                    if (ImGui.Selectable($"  {edge.Name}##ce_{edge.Id}", sel))
                                    {
                                        if (shift) ToggleMultiSelectEdge(cloth, edge);
                                        else SelectSingleEdge(cloth, edge);
                                    }
                                    ImGui.PopStyleColor();
                                }
                                foreach (var pt in cloth.Points)
                                {
                                    if (!MatchesFilter(pt.Name) && !MatchesFilter("DeformEdge") && !MatchesFilter(cloth.Name))
                                        continue;
                                    bool sel = _selectedPointIds.Contains(pt.Id) || _selectedPoint?.Id == pt.Id;
                                    ImGui.PushStyleColor(ImGuiCol.Text, sel ? new Vector4(1f, 0.86f, 0.30f, 1f) : new Vector4(0.76f, 0.76f, 0.78f, 1f));
                                    if (ImGui.Selectable($"  {pt.Name}##cp_{pt.Id}", sel))
                                    {
                                        if (shift) ToggleMultiSelectPoint(cloth, pt);
                                        else SelectSinglePoint(cloth, pt);
                                    }
                                    ImGui.PopStyleColor();
                                }
                                ImGui.TreePop();
                            }

                            ImGui.TreePop();
                        }
                    }
                    ImGui.TreePop();
                }
            }
            ImGui.EndChild();
        }

        private void RenderPropertiesSection()
        {
            string ctxName = _selectedPoint != null
                ? (_selectedPoints.Count >= 2 ? string.Join(", ", _selectedPoints.Select(x => x.point.Name)) : _selectedPoint.Name)
                : _selectedEdge != null ? _selectedEdge.Name
                : _selectedBorderEdge != null ? _selectedBorderEdge.Name
                : _selectedCloth != null ? _selectedCloth.Name : "-";

            ImGui.TextColored(new Vector4(0.84f, 0.84f, 0.86f, 1f), "Properties");
            ImGui.SameLine(0, 8);
            ImGui.TextDisabled($"| {ctxName}");
            ImGui.Separator();

            if (ImGui.BeginChild("##PropsContent", new Vector2(0f, 0f), ImGuiChildFlags.None))
            {
                RenderCharacterClothAndToolProperties();
                if (_selectedCloth == null)
                {
                    ImGui.EndChild();
                    return;
                }

                ImGui.PushItemWidth(175f);

                // 1. DeformEdge / Point Properties
                if (_selectedPoint != null || _selectedEdge != null)
                {
                    if (ImGui.CollapsingHeader("DeformEdge", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        float bend = _selectedPoint != null ? _selectedPoint.Bending : _selectedEdge!.Bending;
                        float rad = _selectedPoint != null ? _selectedPoint.Radius : _selectedEdge!.Radius;
                        if (ImGui.SliderFloat("Độ uốn", ref bend, 0f, 1f, "%.2f"))
                        {
                            if (_selectedPoint != null)
                            {
                                _selectedPoint.Bending = bend;
                                _selectedPoint.IsEdited = true;
                                foreach (var e in _selectedCloth.Edges)
                                    if (e.PointAId == _selectedPoint.Id || e.PointBId == _selectedPoint.Id)
                                    {
                                        e.Bending = bend;
                                        e.IsEdited = true;
                                    }
                            }
                            else if (_selectedEdge != null)
                            {
                                _selectedEdge.Bending = bend;
                                _selectedEdge.IsEdited = true;
                                var pA = _selectedCloth.FindPoint(_selectedEdge.PointAId);
                                var pB = _selectedCloth.FindPoint(_selectedEdge.PointBId);
                                if (pA != null) pA.Bending = bend;
                                if (pB != null) pB.Bending = bend;
                            }

                            ClothSculptEngine.RebuildClothDeformationFromControls(_selectedCloth);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                            ApplyAllConstraints();
                        }
                        if (ImGui.IsItemActivated()) RecordUndoState("Đổi Độ uốn");

                        if (ImGui.SliderFloat("Bán kính", ref rad, 0.04f, 0.65f, "%.2f m"))
                        {
                            if (_selectedPoint != null)
                            {
                                _selectedPoint.Radius = rad;
                                _selectedPoint.IsEdited = true;
                                foreach (var e in _selectedCloth.Edges)
                                    if (e.PointAId == _selectedPoint.Id || e.PointBId == _selectedPoint.Id)
                                    {
                                        e.Radius = rad;
                                        e.IsEdited = true;
                                    }
                            }
                            else if (_selectedEdge != null)
                            {
                                _selectedEdge.Radius = rad;
                                _selectedEdge.IsEdited = true;
                                var pA = _selectedCloth.FindPoint(_selectedEdge.PointAId);
                                var pB = _selectedCloth.FindPoint(_selectedEdge.PointBId);
                                if (pA != null) pA.Radius = rad;
                                if (pB != null) pB.Radius = rad;
                            }

                            ClothSculptEngine.RebuildClothDeformationFromControls(_selectedCloth);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                            ApplyAllConstraints();
                        }
                        if (ImGui.IsItemActivated()) RecordUndoState("Đổi Bán kính");

                        if (_selectedPoint != null)
                        {
                            Vector3 loc = new(_selectedPoint.Position.X, _selectedPoint.Position.Y, _selectedPoint.Position.Z);
                            if (ImGui.DragFloat3("Vị trí", ref loc, 0.005f, -10f, 10f, "%.3f"))
                            {
                                ClothSculptEngine.MoveControlPoint(_selectedCloth, _selectedPoint, new Vec3(loc.X, loc.Y, loc.Z) - _selectedPoint.Position);
                                _glRenderer.MarkClothDirty(_selectedCloth.Id);
                                ApplyAllConstraints();
                            }
                            if (ImGui.IsItemActivated()) RecordUndoState("Đổi tọa độ Điểm");
                            if (ImGui.Button("Set Origin", new Vector2(-1, 0)))
                                SetClothOriginToWorldPos(_selectedCloth, _selectedPoint.Position, $"Set Origin tại {_selectedPoint.Name}");
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
                    }
                }

                // 2. MeshEdge Properties
                if (_selectedBorderEdge != null)
                {
                    if (ImGui.CollapsingHeader("MeshEdge", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGui.TextDisabled($"Dài: {_selectedBorderEdge.ArcLength:F3}m");

                        float bScale = _borderEditScale;
                        if (ImGui.SliderFloat("Scale", ref bScale, 0.10f, 2.50f, "%.2fx"))
                        {
                            _borderEditScale = bScale;
                            ApplyCurrentBorderSessionTransform();
                        }
                        if (ImGui.IsItemActivated()) RecordUndoState("Scale MeshEdge");

                        float bArc = _borderEditArc2D;
                        if (ImGui.SliderFloat("Cong Phẳng", ref bArc, -0.45f, 0.45f, "%.3f m"))
                        {
                            _borderEditArc2D = bArc;
                            ApplyCurrentBorderSessionTransform();
                        }
                        if (ImGui.IsItemActivated()) RecordUndoState("Cong Phẳng MeshEdge");

                        float bWrap = _borderEditWrap3D;
                        if (ImGui.SliderFloat("Cong 3D", ref bWrap, -180f, 180f, "%.0f deg"))
                        {
                            _borderEditWrap3D = bWrap;
                            ApplyCurrentBorderSessionTransform();
                        }
                        if (ImGui.IsItemActivated()) RecordUndoState("Cong 3D MeshEdge");

                        float bRot = _borderEditRotateDeg;
                        if (ImGui.SliderFloat("Xoay Cạnh", ref bRot, -90f, 90f, "%.1f deg"))
                        {
                            _borderEditRotateDeg = bRot;
                            ApplyCurrentBorderSessionTransform();
                        }
                        if (ImGui.IsItemActivated()) RecordUndoState("Xoay MeshEdge");

                        Vector3 bMov = new(_borderEditMoveDelta.X, _borderEditMoveDelta.Y, _borderEditMoveDelta.Z);
                        if (ImGui.DragFloat3("Dịch Biên", ref bMov, 0.005f, -3f, 3f, "%.3f"))
                        {
                            _borderEditMoveDelta = new Vec3(bMov.X, bMov.Y, bMov.Z);
                            ApplyCurrentBorderSessionTransform();
                        }
                        if (ImGui.IsItemActivated()) RecordUndoState("Dịch MeshEdge");

                        if (ImGui.Button("Áp dụng", new Vector2(82, 0)))
                        {
                            ResetBorderEditSession(clearBorderSelection: false);
                            RefreshSelectedClothBorders();
                            _statusText = "Đã áp dụng MeshEdge";
                        }
                        ImGui.SameLine(0, 6);
                        if (ImGui.Button("Reset", new Vector2(82, 0)))
                        {
                            RecordUndoState("Reset MeshEdge");
                            _borderEditScale = 1f; _borderEditArc2D = 0f; _borderEditWrap3D = 0f;
                            _borderEditRotateDeg = 0f; _borderEditMoveDelta = Vec3.Zero;
                            ApplyCurrentBorderSessionTransform();
                        }
                    }
                }
                // 3. Transform (Location / Rotation / Scale / Origin)
                if (ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        Vec3 c0 = ClothSculptEngine.GetClothCentroid(_selectedCloth);
                        float lx = c0.X, ly = c0.Y, lz = c0.Z;
                        bool locChg = false;
                        if (ImGui.DragFloat("Location X", ref lx, 0.005f, -10f, 10f, "%.3f m")) locChg = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Location X");
                        if (ImGui.DragFloat("Y##locY", ref ly, 0.005f, -10f, 10f, "%.3f m")) locChg = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Location Y");
                        if (ImGui.DragFloat("Z##locZ", ref lz, 0.005f, -10f, 10f, "%.3f m")) locChg = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Location Z");
                        if (locChg)
                        {
                            ClothSculptEngine.TranslateEntireCloth(_selectedCloth, new Vec3(lx, ly, lz) - c0);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                            ApplyAllConstraints();
                            RefreshSelectedClothBorders();
                        }

                        float rx = ClampEuler180(_selectedCloth.RotXDeg);
                        float ry = ClampEuler180(_selectedCloth.RotYDeg);
                        float rz = ClampEuler180(_selectedCloth.RotZDeg);
                        bool rotChg = false;
                        if (ImGui.DragFloat("Rotation X", ref rx, 0.5f, -180f, 180f, "%.1f°")) rotChg = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Rotation X");
                        if (ImGui.DragFloat("Y##rotY", ref ry, 0.5f, -180f, 180f, "%.1f°")) rotChg = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Rotation Y");
                        if (ImGui.DragFloat("Z##rotZ", ref rz, 0.5f, -180f, 180f, "%.1f°")) rotChg = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Rotation Z");
                        if (rotChg)
                        {
                            ClothSculptEngine.RotateClothToEuler(_selectedCloth, rx, ry, rz);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                            ApplyAllConstraints();
                            RefreshSelectedClothBorders();
                        }

                        float sx = _selectedCloth.ScaleX;
                        float sy = _selectedCloth.ScaleY;
                        bool scChg = false;
                        if (ImGui.DragFloat("Scale X", ref sx, 0.01f, 0.25f, 2.50f, "%.3f")) scChg = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Scale X");
                        if (ImGui.DragFloat("Y##scY", ref sy, 0.01f, 0.25f, 2.50f, "%.3f")) scChg = true;
                        if (ImGui.IsItemActivated()) RecordUndoState("Scale Y");
                        if (scChg)
                        {
                            ClothSculptEngine.ScaleClothEntity(_selectedCloth, sx, sy);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                            ApplyAllConstraints();
                            RefreshSelectedClothBorders();
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

                        Vec3 orig = ClothSculptEngine.GetClothOrigin(_selectedCloth);
                        ImGui.TextDisabled($"Origin: ({orig.X:F2}, {orig.Y:F2}, {orig.Z:F2})");
                        if (_selectedCloth.CustomOrigin.HasValue && ImGui.Button("Reset Origin", new Vector2(-1, 0)))
                            SetClothOriginToWorldPos(_selectedCloth, null, "Reset Origin");
                    }

                // 4. Subsurf & Shading
                if (ImGui.CollapsingHeader("Subsurf", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        if (DrawModePill("Gốc", _selectedCloth.SubdivisionLevel == 0))
                        {
                            RecordUndoState("Mịn lưới: Gốc");
                            _selectedCloth.SubdivisionLevel = 0;
                            ClothSculptEngine.RebuildDisplayMesh(_selectedCloth);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                        }
                        ImGui.SameLine(0, 4);
                        if (DrawModePill("4x", _selectedCloth.SubdivisionLevel == 1))
                        {
                            RecordUndoState("Mịn lưới: 4x");
                            _selectedCloth.SubdivisionLevel = 1;
                            ClothSculptEngine.RebuildDisplayMesh(_selectedCloth);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                        }
                        ImGui.SameLine(0, 4);
                        if (DrawModePill("16x", _selectedCloth.SubdivisionLevel == 2))
                        {
                            RecordUndoState("Mịn lưới: 16x");
                            _selectedCloth.SubdivisionLevel = 2;
                            ClothSculptEngine.RebuildDisplayMesh(_selectedCloth);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                        }

                        bool eq = _selectedCloth.EqualizeGridOnBend;
                        if (ImGui.Checkbox("Đều lưới", ref eq))
                        {
                            RecordUndoState("Bật/Tắt Đều lưới");
                            _selectedCloth.EqualizeGridOnBend = eq;
                            ClothSculptEngine.RebuildDisplayMesh(_selectedCloth);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                        }
                        ImGui.SameLine(0, 6);
                        if (ImGui.Button("Lưới x4"))
                        {
                            RecordUndoState("Chia mịn lưới gốc x4");
                            ResetBorderEditSession(clearBorderSelection: true);
                            ClothSculptEngine.SubdivideBaseClothMesh(_selectedCloth);
                            _glRenderer.MarkClothDirty(_selectedCloth.Id);
                            RefreshSelectedClothBorders();
                            _statusText = $"Đã chia mịn lưới gốc [{_selectedCloth.Name}] thành {_selectedCloth.Mesh.Faces.Count} ô tứ giác!";
                        }
                    }

                    if (ImGui.CollapsingHeader("Shading"))
                    {
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
            ImGui.EndChild();
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
                            ActiveCamYaw -= d.X * 0.72;
                            ActiveCamPitch = Math.Max(-89.5, Math.Min(89.5, ActiveCamPitch + d.Y * 0.72));
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
                                if (Math.Abs(NormAngle(ActiveCamYaw - targetAx.Yaw)) < 2.0 &&
                                    Math.Abs(ActiveCamPitch - targetAx.Pitch) < 2.0)
                                {
                                    targetAx = _navAxes[(clickIdx + 3) % 6];
                                }
                                ActiveCamYaw = targetAx.Yaw;
                                ActiveCamPitch = targetAx.Pitch;
                                ActiveCamTarget = DefaultActiveCamTarget();
                                _statusText = $"Góc nhìn: {targetAx.Desc}";
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
