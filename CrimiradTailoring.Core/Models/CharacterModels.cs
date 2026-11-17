using System;
using System.Collections.Generic;

namespace CrimiradTailoring.Core.Models
{
    public enum WorkspaceViewportKind
    {
        FabricViewport,
        CharacterViewport
    }

    public enum FormSnapMode
    {
        Off,
        Hugging,
        Fitting
    }

    /// <summary>
    /// Model 3D nhân vật chuẩn hóa cho CharacterViewport (dùng làm form người mặc đồ và tính Ruler / Fitting / Hugging).
    /// </summary>
    [Serializable]
    public class CharacterModelEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Mannequin Mặc Định";
        public string SourcePath { get; set; } = "";
        public bool IsVisible { get; set; } = true;
        public MeshData Mesh { get; set; } = new MeshData();
        public Vec3 BoundsMin { get; set; }
        public Vec3 BoundsMax { get; set; }
        public float HeightMeters { get; set; } = 1.68f;
    }

    /// <summary>
    /// Điểm neo bề mặt trên Model Nhân vật để kéo-thả & snap Origin của mảnh vải vào đúng vùng cơ thể.
    /// </summary>
    [Serializable]
    public class CharacterAnchorPoint
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "A1";
        public Vec3 Position { get; set; }
        public Vec3 Normal { get; set; } = Vec3.Forward;
        public Vec3 Tangent { get; set; } = Vec3.Right;
        public int TriangleIndex { get; set; } = -1;
        public Vec3 BarycentricUVW { get; set; } = new Vec3(1f, 0f, 0f);
    }

    /// <summary>
    /// Đường thước dây đo may bám theo độ cong bề mặt Model Nhân vật.
    /// </summary>
    [Serializable]
    public class RulerMeasurement
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Ruler #1";
        public bool IsVisible { get; set; } = true;
        public bool IsClosedLoop { get; set; } = false;
        public List<Vec3> ControlPoints { get; set; } = new List<Vec3>();
        public List<Vec3> ControlNormals { get; set; } = new List<Vec3>();
        public List<Vec3> SurfacePolyline { get; set; } = new List<Vec3>();
        public List<float> SegmentLengths { get; set; } = new List<float>();
        public float TotalLengthMeters { get; set; }
    }

    /// <summary>
    /// Trạng thái gắn & ôm dáng của một mảnh vải trên CharacterViewport (liên kết 1-1 với lưới rập ở FabricViewport).
    /// </summary>
    [Serializable]
    public class ClothCharacterAttachment
    {
        public bool IsAttached { get; set; } = false;
        public Guid? AnchorId { get; set; }

        /// <summary>Tịnh tiến bổ sung trong không gian CharacterViewport sau khi snap vào Anchor</summary>
        public Vec3 WorldOffset { get; set; } = Vec3.Zero;

        /// <summary>Xoay bổ sung (độ Euler X,Y,Z) trên CharacterViewport</summary>
        public Vec3 WorldRotationEuler { get; set; } = Vec3.Zero;

        /// <summary>Tỉ lệ đồng đều khi ướm trên người</summary>
        public float UniformScale { get; set; } = 1.0f;

        /// <summary>Thứ tự lớp (Layer 0 trong cùng -> Layer cao hơn bọc ngoài, tự động chống clip & ẩn shader vùng bị che)</summary>
        public int LayerIndex { get; set; } = 0;

        /// <summary>Kiểu Form Snap đang kích hoạt trên mảnh vải này (Off = di chuyển tự do)</summary>
        public FormSnapMode FormMode { get; set; } = FormSnapMode.Off;

        /// <summary>Khoảng hở bề mặt da hoặc lớp dưới (mét), mặc định 8mm</summary>
        public float SurfaceOffsetMeters { get; set; } = 0.008f;

        /// <summary>Độ cứng bắc cầu qua vùng lõm ở chế độ Fitting [0..1]</summary>
        public float FittingBridgeStiffness { get; set; } = 0.72f;

        /// <summary>Tỉ lệ giữ lại nếp uốn DeformEdge đã điêu khắc ở FabricViewport [0..1]</summary>
        public float PreserveDeformBlend { get; set; } = 0.45f;
    }
}
