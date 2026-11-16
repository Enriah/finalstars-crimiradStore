using System;
using System.Collections.Generic;

namespace CrimiradTailoring.Core.Models
{
    [Serializable]
    public struct Vec2
    {
        public float X;
        public float Y;

        public static readonly Vec2 Zero = new Vec2(0f, 0f);

        public Vec2(float x, float y)
        {
            X = x;
            Y = y;
        }

        public float Length => (float)Math.Sqrt(X * X + Y * Y);

        public Vec2 Normalized
        {
            get
            {
                float len = Length;
                return len > 1e-6f ? new Vec2(X / len, Y / len) : new Vec2(0f, 0f);
            }
        }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator *(float s, Vec2 a) => new Vec2(a.X * s, a.Y * s);

        public static float Distance(Vec2 a, Vec2 b) => (a - b).Length;
        public static float Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

        public static Vec2 Lerp(Vec2 a, Vec2 b, float t) =>
            new Vec2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        public Vec2 RotateDegrees(float degrees)
        {
            float rad = degrees * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);
            return new Vec2(X * cos - Y * sin, X * sin + Y * cos);
        }

        public override string ToString() => $"({X:F2}, {Y:F2})";
    }

    [Serializable]
    public struct Vec3
    {
        public float X;
        public float Y;
        public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0f, 0f, 0f);
        public static readonly Vec3 Up = new Vec3(0f, 1f, 0f);
        public static readonly Vec3 Forward = new Vec3(0f, 0f, 1f);
        public static readonly Vec3 Right = new Vec3(1f, 0f, 0f);

        public float Length => (float)Math.Sqrt(X * X + Y * Y + Z * Z);
        public float LengthSquared => X * X + Y * Y + Z * Z;

        public Vec3 Normalized
        {
            get
            {
                float len = Length;
                return len > 1e-6f ? new Vec3(X / len, Y / len, Z / len) : new Vec3(0f, 1f, 0f);
            }
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(float s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);

        public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;

        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) =>
            new Vec3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        public static Vec3 ProjectOnPlane(Vec3 vector, Vec3 planeNormal)
        {
            Vec3 n = planeNormal.Normalized;
            return vector - n * Dot(vector, n);
        }

        public static Vec3 RotateAroundAxis(Vec3 v, Vec3 axis, float degrees)
        {
            Vec3 k = axis.Normalized;
            float rad = degrees * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);
            // Rodrigues' rotation formula
            return v * cos + Cross(k, v) * sin + k * (Dot(k, v) * (1f - cos));
        }

        public Vec3 MirrorX() => new Vec3(-X, Y, Z);

        public override string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
    }

    [Serializable]
    public struct ColorRgba
    {
        public byte R;
        public byte G;
        public byte B;
        public byte A;

        public ColorRgba(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public static ColorRgba FromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return new ColorRgba(200, 200, 200);
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                return new ColorRgba(
                    Convert.ToByte(hex.Substring(0, 2), 16),
                    Convert.ToByte(hex.Substring(2, 2), 16),
                    Convert.ToByte(hex.Substring(4, 2), 16),
                    255);
            }
            return new ColorRgba(200, 200, 200);
        }

        public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
    }

    [Serializable]
    public class MeshData
    {
        public string Name { get; set; } = "Mesh";
        public List<Vec3> Vertices { get; set; } = new List<Vec3>();
        public List<Vec3> RestVertices { get; set; } = new List<Vec3>();
        public List<Vec3> Normals { get; set; } = new List<Vec3>();
        public List<Vec2> UVs { get; set; } = new List<Vec2>();
        public List<int> Triangles { get; set; } = new List<int>();

        public void EnsureRestVerticesInitialized()
        {
            if (RestVertices.Count == Vertices.Count) return;
            RestVertices.Clear();
            RestVertices.AddRange(Vertices);
        }

        /// <summary>
        /// Danh sách mặt đa giác gốc (mặc định là Tứ giác 4 đỉnh [Quad], hoặc N-giác sau khi cắt bo tròn).
        /// </summary>
        public List<List<int>> Faces { get; set; } = new List<List<int>>();

        /// <summary>
        /// Ánh xạ chỉ số mỗi tam giác trong Triangles về chỉ số mặt tương ứng trong Faces.
        /// </summary>
        public List<int> TriangleToFace { get; set; } = new List<int>();

        public void RebuildTrianglesFromFaces()
        {
            Triangles.Clear();
            TriangleToFace.Clear();

            for (int fIdx = 0; fIdx < Faces.Count; fIdx++)
            {
                var face = Faces[fIdx];
                if (face == null || face.Count < 3) continue;

                if (face.Count == 3)
                {
                    Triangles.Add(face[0]);
                    Triangles.Add(face[1]);
                    Triangles.Add(face[2]);
                    TriangleToFace.Add(fIdx);
                }
                else if (face.Count == 4)
                {
                    Triangles.Add(face[0]);
                    Triangles.Add(face[1]);
                    Triangles.Add(face[2]);
                    TriangleToFace.Add(fIdx);

                    Triangles.Add(face[0]);
                    Triangles.Add(face[2]);
                    Triangles.Add(face[3]);
                    TriangleToFace.Add(fIdx);
                }
                else
                {
                    TriangulatePolygonEarClip(face, fIdx);
                }
            }
        }

        private void TriangulatePolygonEarClip(List<int> face, int faceIndex)
        {
            // Chiếu đa giác lên mặt phẳng tốt nhất theo pháp tuyến trung bình của mặt
            Vec3 normal = Vec3.Zero;
            for (int i = 0; i < face.Count; i++)
            {
                Vec3 curr = Vertices[face[i]];
                Vec3 next = Vertices[face[(i + 1) % face.Count]];
                normal.X += (curr.Y - next.Y) * (curr.Z + next.Z);
                normal.Y += (curr.Z - next.Z) * (curr.X + next.X);
                normal.Z += (curr.X - next.X) * (curr.Y + next.Y);
            }

            if (normal.LengthSquared < 1e-8f) normal = Vec3.Forward;
            normal = normal.Normalized;

            Vec3 upRef = Math.Abs(Vec3.Dot(normal, Vec3.Up)) > 0.9f ? Vec3.Right : Vec3.Up;
            Vec3 uAxis = Vec3.Cross(upRef, normal).Normalized;
            Vec3 vAxis = Vec3.Cross(normal, uAxis).Normalized;

            var remaining = new List<int>(face);
            var pts2D = new Dictionary<int, Vec2>(face.Count);
            foreach (int vIdx in remaining)
            {
                Vec3 p = Vertices[vIdx];
                pts2D[vIdx] = new Vec2(Vec3.Dot(p, uAxis), Vec3.Dot(p, vAxis));
            }

            // Đảm bảo chiều quấn 2D là CCW
            float signedArea = 0f;
            for (int i = 0; i < remaining.Count; i++)
            {
                Vec2 a = pts2D[remaining[i]];
                Vec2 b = pts2D[remaining[(i + 1) % remaining.Count]];
                signedArea += (a.X * b.Y - b.X * a.Y);
            }
            bool ccw = signedArea >= 0f;

            int guard = 0;
            while (remaining.Count > 3 && guard++ < face.Count * 3)
            {
                bool earFound = false;
                int n = remaining.Count;
                for (int i = 0; i < n; i++)
                {
                    int prevIdx = remaining[(i + n - 1) % n];
                    int currIdx = remaining[i];
                    int nextIdx = remaining[(i + 1) % n];

                    Vec2 a = pts2D[prevIdx];
                    Vec2 b = pts2D[currIdx];
                    Vec2 c = pts2D[nextIdx];

                    float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                    bool isConvex = ccw ? cross > 1e-7f : cross < -1e-7f;
                    if (!isConvex) continue;

                    bool anyInside = false;
                    for (int k = 0; k < n; k++)
                    {
                        int testIdx = remaining[k];
                        if (testIdx == prevIdx || testIdx == currIdx || testIdx == nextIdx) continue;
                        if (PointInTriangle2D(pts2D[testIdx], a, b, c))
                        {
                            anyInside = true;
                            break;
                        }
                    }

                    if (!anyInside)
                    {
                        Triangles.Add(prevIdx);
                        Triangles.Add(currIdx);
                        Triangles.Add(nextIdx);
                        TriangleToFace.Add(faceIndex);
                        remaining.RemoveAt(i);
                        earFound = true;
                        break;
                    }
                }

                if (!earFound) break;
            }

            // Fallback nếu đa giác suy biến
            if (remaining.Count >= 3)
            {
                for (int i = 1; i + 1 < remaining.Count; i++)
                {
                    Triangles.Add(remaining[0]);
                    Triangles.Add(remaining[i]);
                    Triangles.Add(remaining[i + 1]);
                    TriangleToFace.Add(faceIndex);
                }
            }
        }

        private static bool PointInTriangle2D(Vec2 p, Vec2 a, Vec2 b, Vec2 c)
        {
            float s1 = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            float s2 = (c.X - b.X) * (p.Y - b.Y) - (c.Y - b.Y) * (p.X - b.X);
            float s3 = (a.X - c.X) * (p.Y - c.Y) - (a.Y - c.Y) * (p.X - c.X);
            bool hasNeg = (s1 < -1e-6f) || (s2 < -1e-6f) || (s3 < -1e-6f);
            bool hasPos = (s1 > 1e-6f) || (s2 > 1e-6f) || (s3 > 1e-6f);
            return !(hasNeg && hasPos);
        }

        public void RecalculateNormals()
        {
            Normals.Clear();
            for (int i = 0; i < Vertices.Count; i++)
                Normals.Add(Vec3.Zero);

            for (int i = 0; i + 2 < Triangles.Count; i += 3)
            {
                int i0 = Triangles[i];
                int i1 = Triangles[i + 1];
                int i2 = Triangles[i + 2];

                Vec3 v0 = Vertices[i0];
                Vec3 v1 = Vertices[i1];
                Vec3 v2 = Vertices[i2];

                Vec3 faceNormal = Vec3.Cross(v1 - v0, v2 - v0);
                Normals[i0] = Normals[i0] + faceNormal;
                Normals[i1] = Normals[i1] + faceNormal;
                Normals[i2] = Normals[i2] + faceNormal;
            }

            for (int i = 0; i < Normals.Count; i++)
            {
                Normals[i] = Normals[i].Normalized;
            }
        }
    }
}
