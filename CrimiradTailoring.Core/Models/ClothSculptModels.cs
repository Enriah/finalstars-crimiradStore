using System;
using System.Collections.Generic;

namespace CrimiradTailoring.Core.Models
{
    public enum EditorToolType
    {
        Select,
        CreatePoint,
        Move,
        Rotate,
        Cut,
        Scale,
        Connect
    }

    public enum SelectTargetFilter
    {
        Region,
        ClothObject,
        Point,
        Edge
    }

    public enum RegionShapeMode
    {
        Box,
        Circle,
        Cone
    }

    public enum CutActionMode
    {
        ExtractPiece,
        RemoveHole
    }

    public enum ConnectMode
    {
        CreateEdge,
        Snap,
        Hang
    }

    public enum GizmoAxis
    {
        None,
        X,
        Y,
        Z,
        FreeCenter
    }

    public enum MeshShadingSmoothMode
    {
        Smooth,
        AutoSmooth,
        Flat
    }

    [Serializable]
    public class RegionSelectionSpec
    {
        public RegionShapeMode Shape { get; set; }
        public Vec3 StartPoint { get; set; }
        public Vec3 DragPoint { get; set; }
        public Vec3 PlaneNormal { get; set; } = Vec3.Forward;
        public float ConeHalfAngleDeg { get; set; } = 28f;
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Điểm điều khiển trên bề mặt vải (uốn vùng cầu xung quanh điểm).
    /// </summary>
    [Serializable]
    public class ControlPoint
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "P1";
        public Guid OwnerClothId { get; set; }
        public int AttachedVertexIndex { get; set; } = -1;
        public Vec2 SurfaceUV { get; set; }
        public Vec3 Position { get; set; }

        /// <summary>Bán kính vùng cầu ảnh hưởng quanh điểm (mét)</summary>
        public float Radius { get; set; } = 0.24f;

        /// <summary>Độ uốn [0..1]: 0 = Gập nhọn chữ V, 1 = Uốn cong mượt chữ U/S</summary>
        public float Bending { get; set; } = 0.65f;
    }

    /// <summary>
    /// Cạnh điều khiển được tạo từ 2 Điểm trên vải (uốn vùng hình trụ có trục là Điểm 1 - Điểm 2).
    /// </summary>
    [Serializable]
    public class ControlEdge
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "E1";
        public Guid OwnerClothId { get; set; }
        public Guid PointAId { get; set; }
        public Guid PointBId { get; set; }

        /// <summary>Bán kính vùng hình trụ xung quanh cạnh (mét)</summary>
        public float Radius { get; set; } = 0.26f;

        /// <summary>Độ uốn [0..1]: 0 = Nếp gập sắc chữ V, 1 = Uốn cong tròn mượt</summary>
        public float Bending { get; set; } = 0.70f;
    }

    /// <summary>
    /// Liên kết Snap hoặc Hang giữa Thành phần 1 và Thành phần 2 (Điểm - Điểm hoặc Cạnh - Cạnh).
    /// </summary>
    [Serializable]
    public class ClothConnection
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ConnectMode Mode { get; set; } = ConnectMode.Snap;
        public bool IsEdgeConnection { get; set; }
        public Guid SourceClothId { get; set; }
        public Guid SourceElementId { get; set; }
        public Guid TargetClothId { get; set; }
        public Guid TargetElementId { get; set; }

        /// <summary>Khoảng cách dây treo khi ở chế độ Hang (Snap thì bằng 0)</summary>
        public float HangDistance { get; set; } = 0.14f;
    }

    /// <summary>
    /// Một mảnh vải trong không gian 3D (có thể cắt, tách, thêm điểm/cạnh, uốn và nối).
    /// </summary>
    [Serializable]
    public class ClothEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Tấm Vải #1";
        public ColorRgba ClothColor { get; set; } = new ColorRgba(72, 126, 188);
        public ColorRgba WireColor { get; set; } = new ColorRgba(175, 205, 240);
        public float ScaleX { get; set; } = 1.0f;
        public float ScaleY { get; set; } = 1.0f;
        public float RotXDeg { get; set; } = 0.0f;
        public float RotYDeg { get; set; } = 0.0f;
        public float RotZDeg { get; set; } = 0.0f;
        public MeshShadingSmoothMode SmoothMode { get; set; } = MeshShadingSmoothMode.AutoSmooth;
        public float AutoSmoothAngleDeg { get; set; } = 30f;

        public MeshData Mesh { get; set; } = new MeshData();
        public List<ControlPoint> Points { get; set; } = new List<ControlPoint>();
        public List<ControlEdge> Edges { get; set; } = new List<ControlEdge>();

        /// <summary>Các mặt tứ giác/đa giác đang nằm trong Vùng Chọn</summary>
        [NonSerialized]
        public HashSet<int> SelectedFaces = new HashSet<int>();

        /// <summary>Các tam giác đang nằm trong Vùng Chọn (Vuông / Tròn / Nón)</summary>
        [NonSerialized]
        public HashSet<int> SelectedTriangles = new HashSet<int>();

        /// <summary>Các đỉnh đang nằm trong Vùng Chọn</summary>
        [NonSerialized]
        public HashSet<int> SelectedVertices = new HashSet<int>();

        /// <summary>Hình học vùng chọn chính xác (dùng để cắt bo tròn bù cạnh và điểm khi bấm Cut)</summary>
        [NonSerialized]
        public RegionSelectionSpec? ActiveRegion = null;

        public ControlPoint? FindPoint(Guid pointId)
        {
            for (int i = 0; i < Points.Count; i++)
                if (Points[i].Id == pointId) return Points[i];
            return null;
        }

        public ControlEdge? FindEdge(Guid edgeId)
        {
            for (int i = 0; i < Edges.Count; i++)
                if (Edges[i].Id == edgeId) return Edges[i];
            return null;
        }
    }
}
