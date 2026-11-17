using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using CrimiradTailoring.Core.Engine;
using CrimiradTailoring.Core.Models;

namespace CrimiradTailoring.Studio.IO
{
    /// <summary>
    /// Bộ nạp Model 3D Nhân vật cho CharacterViewport (thuần C#, không phụ thuộc thư viện C++ ngoài).
    /// Hỗ trợ .GLB, .GLTF (kèm Node Hierarchy & Skinning Pose), .OBJ, Binary .FBX và ASCII .FBX.
    /// </summary>
    public static class CharacterModelImporter
    {
        public static CharacterModelEntity LoadFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("Không tìm thấy file model.", path);

            string ext = Path.GetExtension(path).ToLowerInvariant();
            CharacterModelEntity entity = ext switch
            {
                ".glb" => LoadGltfOrGlb(path, isGlb: true),
                ".gltf" => LoadGltfOrGlb(path, isGlb: false),
                ".obj" => LoadObj(path),
                ".fbx" => LoadFbx(path),
                _ => throw new NotSupportedException($"Định dạng '{ext}' chưa được hỗ trợ. Hãy dùng .GLB, .GLTF, .OBJ hoặc ASCII .FBX.")
            };

            entity.SourcePath = path;
            entity.Name = Path.GetFileNameWithoutExtension(path);
            CharacterEngine.NormalizeCharacterMesh(entity);
            return entity;
        }

        private static CharacterModelEntity LoadObj(string path)
        {
            var clothTmp = ClothSculptEngine.ImportClothFromObj(path, 1);
            return new CharacterModelEntity
            {
                Mesh = clothTmp.Mesh
            };
        }

        private static CharacterModelEntity LoadFbx(string path)
        {
            byte[] header = new byte[Math.Min(24, (int)new FileInfo(path).Length)];
            using (var fs = File.OpenRead(path))
                fs.ReadExactly(header, 0, header.Length);

            string headStr = Encoding.ASCII.GetString(header);
            if (headStr.StartsWith("Kaydara FBX Binary", StringComparison.Ordinal))
                return LoadBinaryFbx(path);

            // ASCII .FBX parser (Vertices + PolygonVertexIndex)
            string text = File.ReadAllText(path);
            var mesh = new MeshData();
            var rawVerts = ReadAsciiFbxFloatArray(text, "Vertices:");
            for (int i = 0; i + 2 < rawVerts.Count; i += 3)
            {
                mesh.Vertices.Add(new Vec3(rawVerts[i], rawVerts[i + 1], rawVerts[i + 2]));
                mesh.UVs.Add(new Vec2(0f, 0f));
            }

            var rawPoly = ReadAsciiFbxIntArray(text, "PolygonVertexIndex:");
            var poly = new List<int>(4);
            for (int i = 0; i < rawPoly.Count; i++)
            {
                int idx = rawPoly[i];
                bool isEnd = idx < 0;
                int vIdx = isEnd ? ~idx : idx;
                if (vIdx >= 0 && vIdx < mesh.Vertices.Count)
                    poly.Add(vIdx);
                if (isEnd)
                {
                    for (int k = 1; k + 1 < poly.Count; k++)
                    {
                        mesh.Triangles.Add(poly[0]);
                        mesh.Triangles.Add(poly[k]);
                        mesh.Triangles.Add(poly[k + 1]);
                    }
                    poly.Clear();
                }
            }

            if (mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0)
                throw new InvalidDataException("Không tìm thấy dữ liệu lưới hợp lệ trong file ASCII .FBX.");

            return new CharacterModelEntity { Mesh = mesh };
        }

        private static List<float> ReadAsciiFbxFloatArray(string text, string key)
        {
            var result = new List<float>();
            int pos = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return result;
            int openBrace = text.IndexOf('{', pos);
            int aStart = text.IndexOf("a:", openBrace);
            int closeBrace = text.IndexOf('}', aStart > 0 ? aStart : pos);
            if (openBrace < 0 || closeBrace < 0) return result;
            int dataStart = aStart >= 0 ? aStart + 2 : openBrace + 1;
            string slice = text.Substring(dataStart, closeBrace - dataStart);
            foreach (string tok in slice.Split(new[] { ',', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (float.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    result.Add(v);
            }
            return result;
        }

        private static List<int> ReadAsciiFbxIntArray(string text, string key)
        {
            var result = new List<int>();
            int pos = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) return result;
            int openBrace = text.IndexOf('{', pos);
            int aStart = text.IndexOf("a:", openBrace);
            int closeBrace = text.IndexOf('}', aStart > 0 ? aStart : pos);
            if (openBrace < 0 || closeBrace < 0) return result;
            int dataStart = aStart >= 0 ? aStart + 2 : openBrace + 1;
            string slice = text.Substring(dataStart, closeBrace - dataStart);
            foreach (string tok in slice.Split(new[] { ',', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    result.Add(v);
            }
            return result;
        }

        private static CharacterModelEntity LoadGltfOrGlb(string path, bool isGlb)
        {
            byte[] jsonBytes;
            byte[]? binChunk = null;

            if (isGlb)
            {
                using var fs = File.OpenRead(path);
                using var br = new BinaryReader(fs);
                uint magic = br.ReadUInt32();
                uint version = br.ReadUInt32();
                uint totalLen = br.ReadUInt32();
                if (magic != 0x46546C67 || version != 2)
                    throw new InvalidDataException("File .GLB không đúng chuẩn glTF 2.0.");

                byte[]? jBytes = null;
                while (fs.Position + 8 <= totalLen && fs.Position + 8 <= fs.Length)
                {
                    uint chunkLen = br.ReadUInt32();
                    uint chunkType = br.ReadUInt32();
                    byte[] payload = br.ReadBytes((int)chunkLen);
                    if (chunkType == 0x4E4F534A) jBytes = payload;      // JSON
                    else if (chunkType == 0x004E4942) binChunk = payload; // BIN
                }
                jsonBytes = jBytes ?? throw new InvalidDataException("File .GLB thiếu JSON chunk.");
            }
            else
            {
                jsonBytes = File.ReadAllBytes(path);
            }

            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;
            string baseDir = Path.GetDirectoryName(path) ?? "";

            // 1. Nạp Buffers
            var buffers = new List<byte[]>();
            if (root.TryGetProperty("buffers", out var bufArr))
            {
                foreach (var bElem in bufArr.EnumerateArray())
                {
                    if (bElem.TryGetProperty("uri", out var uriProp))
                    {
                        string uri = uriProp.GetString() ?? "";
                        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        {
                            int comma = uri.IndexOf(',');
                            buffers.Add(comma >= 0 ? Convert.FromBase64String(uri.Substring(comma + 1)) : Array.Empty<byte>());
                        }
                        else
                        {
                            string binPath = Path.Combine(baseDir, Uri.UnescapeDataString(uri));
                            buffers.Add(File.Exists(binPath) ? File.ReadAllBytes(binPath) : Array.Empty<byte>());
                        }
                    }
                    else
                    {
                        buffers.Add(binChunk ?? Array.Empty<byte>());
                    }
                }
            }

            var bufferViews = root.TryGetProperty("bufferViews", out var bvEl) ? bvEl : default;
            var accessors = root.TryGetProperty("accessors", out var accEl) ? accEl : default;
            var meshesEl = root.TryGetProperty("meshes", out var mEl) ? mEl : default;
            var nodesEl = root.TryGetProperty("nodes", out var nEl) ? nEl : default;
            var skinsEl = root.TryGetProperty("skins", out var skEl) ? skEl : default;

            if (meshesEl.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("File glTF/GLB không có dữ liệu Mesh.");

            // 2. Tính ma trận World của toàn bộ cây Node (tính sẵn Pose đang có trong file)
            int nodeCount = nodesEl.ValueKind == JsonValueKind.Array ? nodesEl.GetArrayLength() : 0;
            var localMats = new Matrix4x4[nodeCount];
            var parentIdx = new int[nodeCount];
            Array.Fill(parentIdx, -1);

            for (int i = 0; i < nodeCount; i++)
            {
                var node = nodesEl[i];
                localMats[i] = ReadNodeLocalMatrix(node);
                if (node.TryGetProperty("children", out var chArr))
                {
                    foreach (var ch in chArr.EnumerateArray())
                    {
                        int ci = ch.GetInt32();
                        if (ci >= 0 && ci < nodeCount) parentIdx[ci] = i;
                    }
                }
            }

            var worldMats = new Matrix4x4[nodeCount];
            var computed = new bool[nodeCount];
            Matrix4x4 GetWorldMatrix(int idx)
            {
                if (idx < 0 || idx >= nodeCount) return Matrix4x4.Identity;
                if (computed[idx]) return worldMats[idx];
                Matrix4x4 pm = parentIdx[idx] >= 0 ? GetWorldMatrix(parentIdx[idx]) : Matrix4x4.Identity;
                worldMats[idx] = localMats[idx] * pm; // System.Numerics row-vector convention
                computed[idx] = true;
                return worldMats[idx];
            }
            for (int i = 0; i < nodeCount; i++) GetWorldMatrix(i);

            // 3. Duyệt các Node gắn Mesh (kèm Skin nếu có) để hợp nhất thành MeshData chuẩn
            var outMesh = new MeshData();
            bool hasPlacedAnyNode = false;

            for (int i = 0; i < nodeCount; i++)
            {
                var node = nodesEl[i];
                if (!node.TryGetProperty("mesh", out var mProp)) continue;
                int meshIdx = mProp.GetInt32();
                int skinIdx = node.TryGetProperty("skin", out var sProp) ? sProp.GetInt32() : -1;
                Matrix4x4[]? jointMats = skinIdx >= 0 ? BuildSkinMatrices(skinIdx, skinsEl, accessors, bufferViews, buffers, worldMats, worldMats[i]) : null;
                AppendGltfMesh(outMesh, meshesEl[meshIdx], worldMats[i], jointMats, accessors, bufferViews, buffers);
                hasPlacedAnyNode = true;
            }

            if (!hasPlacedAnyNode)
            {
                for (int m = 0; m < meshesEl.GetArrayLength(); m++)
                    AppendGltfMesh(outMesh, meshesEl[m], Matrix4x4.Identity, null, accessors, bufferViews, buffers);
            }

            if (outMesh.Vertices.Count == 0 || outMesh.Triangles.Count == 0)
                throw new InvalidDataException("Không trích xuất được đỉnh/tam giác nào từ file glTF/GLB.");

            return new CharacterModelEntity { Mesh = outMesh };
        }

        private static Matrix4x4 ReadNodeLocalMatrix(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out var mArr) && mArr.GetArrayLength() == 16)
            {
                float[] m = new float[16];
                int k = 0;
                foreach (var v in mArr.EnumerateArray()) m[k++] = v.GetSingle();
                return new Matrix4x4(
                    m[0], m[1], m[2], m[3],
                    m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11],
                    m[12], m[13], m[14], m[15]);
            }

            Vector3 t = Vector3.Zero;
            Quaternion r = Quaternion.Identity;
            Vector3 s = Vector3.One;

            if (node.TryGetProperty("translation", out var tArr) && tArr.GetArrayLength() >= 3)
                t = new Vector3(tArr[0].GetSingle(), tArr[1].GetSingle(), tArr[2].GetSingle());
            if (node.TryGetProperty("rotation", out var rArr) && rArr.GetArrayLength() >= 4)
                r = Quaternion.Normalize(new Quaternion(rArr[0].GetSingle(), rArr[1].GetSingle(), rArr[2].GetSingle(), rArr[3].GetSingle()));
            if (node.TryGetProperty("scale", out var sArr) && sArr.GetArrayLength() >= 3)
                s = new Vector3(sArr[0].GetSingle(), sArr[1].GetSingle(), sArr[2].GetSingle());

            return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
        }

        private static Matrix4x4[]? BuildSkinMatrices(
            int skinIdx,
            JsonElement skinsEl,
            JsonElement accessors,
            JsonElement bufferViews,
            List<byte[]> buffers,
            Matrix4x4[] worldMats,
            Matrix4x4 meshWorld)
        {
            if (skinsEl.ValueKind != JsonValueKind.Array || skinIdx < 0 || skinIdx >= skinsEl.GetArrayLength())
                return null;
            var skin = skinsEl[skinIdx];
            if (!skin.TryGetProperty("joints", out var jointsEl)) return null;

            int jCount = jointsEl.GetArrayLength();
            var mats = new Matrix4x4[jCount];
            Matrix4x4[]? invBinds = null;
            if (skin.TryGetProperty("inverseBindMatrices", out var ibmProp))
                invBinds = ReadMatrixArray(ibmProp.GetInt32(), accessors, bufferViews, buffers);

            Matrix4x4.Invert(meshWorld, out Matrix4x4 invMeshWorld);
            int k = 0;
            foreach (var j in jointsEl.EnumerateArray())
            {
                int nodeId = j.GetInt32();
                Matrix4x4 jWorld = nodeId >= 0 && nodeId < worldMats.Length ? worldMats[nodeId] : Matrix4x4.Identity;
                Matrix4x4 ibm = invBinds != null && k < invBinds.Length ? invBinds[k] : Matrix4x4.Identity;
                mats[k] = ibm * jWorld * invMeshWorld;
                k++;
            }
            return mats;
        }

        private static void AppendGltfMesh(
            MeshData dst,
            JsonElement meshEl,
            Matrix4x4 worldMat,
            Matrix4x4[]? jointMats,
            JsonElement accessors,
            JsonElement bufferViews,
            List<byte[]> buffers)
        {
            if (!meshEl.TryGetProperty("primitives", out var primsEl)) return;

            foreach (var prim in primsEl.EnumerateArray())
            {
                int mode = prim.TryGetProperty("mode", out var modeProp) ? modeProp.GetInt32() : 4;
                if (mode != 4) continue; // Only TRIANGLES
                if (!prim.TryGetProperty("attributes", out var attrs)) continue;
                if (!attrs.TryGetProperty("POSITION", out var posAccEl)) continue;

                Vector3[] positions = ReadVec3Array(posAccEl.GetInt32(), accessors, bufferViews, buffers);
                Vector2[]? uvs = attrs.TryGetProperty("TEXCOORD_0", out var uvAccEl)
                    ? ReadVec2Array(uvAccEl.GetInt32(), accessors, bufferViews, buffers)
                    : null;
                int[]? joints = (jointMats != null && attrs.TryGetProperty("JOINTS_0", out var jAccEl))
                    ? ReadVec4IntArray(jAccEl.GetInt32(), accessors, bufferViews, buffers)
                    : null;
                Vector4[]? weights = (jointMats != null && attrs.TryGetProperty("WEIGHTS_0", out var wAccEl))
                    ? ReadVec4FloatArray(wAccEl.GetInt32(), accessors, bufferViews, buffers)
                    : null;

                int baseVert = dst.Vertices.Count;
                for (int i = 0; i < positions.Length; i++)
                {
                    Vector3 p = positions[i];
                    if (jointMats != null && joints != null && weights != null && i * 4 + 3 < joints.Length)
                    {
                        Vector4 w = weights[i];
                        float sum = w.X + w.Y + w.Z + w.W;
                        if (sum > 1e-5f)
                        {
                            Vector3 skinned = Vector3.Zero;
                            if (w.X > 0f && joints[i * 4] < jointMats.Length) skinned += Vector3.Transform(p, jointMats[joints[i * 4]]) * w.X;
                            if (w.Y > 0f && joints[i * 4 + 1] < jointMats.Length) skinned += Vector3.Transform(p, jointMats[joints[i * 4 + 1]]) * w.Y;
                            if (w.Z > 0f && joints[i * 4 + 2] < jointMats.Length) skinned += Vector3.Transform(p, jointMats[joints[i * 4 + 2]]) * w.Z;
                            if (w.W > 0f && joints[i * 4 + 3] < jointMats.Length) skinned += Vector3.Transform(p, jointMats[joints[i * 4 + 3]]) * w.W;
                            p = skinned / sum;
                        }
                    }

                    Vector3 wp = Vector3.Transform(p, worldMat);
                    dst.Vertices.Add(new Vec3(wp.X, wp.Y, wp.Z));
                    dst.UVs.Add(uvs != null && i < uvs.Length ? new Vec2(uvs[i].X, uvs[i].Y) : new Vec2(0f, 0f));
                }

                if (prim.TryGetProperty("indices", out var idxAccEl))
                {
                    int[] indices = ReadScalarIntArray(idxAccEl.GetInt32(), accessors, bufferViews, buffers);
                    for (int k = 0; k + 2 < indices.Length; k += 3)
                    {
                        dst.Triangles.Add(baseVert + indices[k]);
                        dst.Triangles.Add(baseVert + indices[k + 1]);
                        dst.Triangles.Add(baseVert + indices[k + 2]);
                    }
                }
                else
                {
                    for (int k = 0; k + 2 < positions.Length; k += 3)
                    {
                        dst.Triangles.Add(baseVert + k);
                        dst.Triangles.Add(baseVert + k + 1);
                        dst.Triangles.Add(baseVert + k + 2);
                    }
                }
            }
        }

        private static (byte[] buf, int offset, int stride, int count, int compType) ResolveAccessor(
            int accIdx, JsonElement accessors, JsonElement bufferViews, List<byte[]> buffers, int elemBytesFallback)
        {
            var acc = accessors[accIdx];
            int count = acc.GetProperty("count").GetInt32();
            int compType = acc.GetProperty("componentType").GetInt32();
            int accOff = acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0;
            int bvIdx = acc.GetProperty("bufferView").GetInt32();
            var bv = bufferViews[bvIdx];
            int bufIdx = bv.TryGetProperty("buffer", out var bi) ? bi.GetInt32() : 0;
            int bvOff = bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
            int stride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : elemBytesFallback;
            return (buffers[bufIdx], bvOff + accOff, stride, count, compType);
        }

        private static Vector3[] ReadVec3Array(int accIdx, JsonElement accessors, JsonElement bufferViews, List<byte[]> buffers)
        {
            var (buf, off, stride, count, _) = ResolveAccessor(accIdx, accessors, bufferViews, buffers, 12);
            var arr = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int o = off + i * stride;
                if (o + 12 > buf.Length) break;
                arr[i] = new Vector3(
                    BitConverter.ToSingle(buf, o),
                    BitConverter.ToSingle(buf, o + 4),
                    BitConverter.ToSingle(buf, o + 8));
            }
            return arr;
        }

        private static Vector2[] ReadVec2Array(int accIdx, JsonElement accessors, JsonElement bufferViews, List<byte[]> buffers)
        {
            var (buf, off, stride, count, _) = ResolveAccessor(accIdx, accessors, bufferViews, buffers, 8);
            var arr = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                int o = off + i * stride;
                if (o + 8 > buf.Length) break;
                arr[i] = new Vector2(BitConverter.ToSingle(buf, o), BitConverter.ToSingle(buf, o + 4));
            }
            return arr;
        }

        private static int[] ReadScalarIntArray(int accIdx, JsonElement accessors, JsonElement bufferViews, List<byte[]> buffers)
        {
            var acc = accessors[accIdx];
            int compType = acc.GetProperty("componentType").GetInt32();
            int elemBytes = compType == 5125 ? 4 : (compType == 5123 ? 2 : 1);
            var (buf, off, stride, count, _) = ResolveAccessor(accIdx, accessors, bufferViews, buffers, elemBytes);
            var arr = new int[count];
            for (int i = 0; i < count; i++)
            {
                int o = off + i * stride;
                if (o + elemBytes > buf.Length) break;
                arr[i] = compType switch
                {
                    5121 => buf[o],
                    5123 => BitConverter.ToUInt16(buf, o),
                    5125 => (int)BitConverter.ToUInt32(buf, o),
                    5122 => BitConverter.ToInt16(buf, o),
                    _ => buf[o]
                };
            }
            return arr;
        }

        private static int[] ReadVec4IntArray(int accIdx, JsonElement accessors, JsonElement bufferViews, List<byte[]> buffers)
        {
            var acc = accessors[accIdx];
            int compType = acc.GetProperty("componentType").GetInt32();
            int compBytes = compType == 5123 ? 2 : (compType == 5125 ? 4 : 1);
            var (buf, off, stride, count, _) = ResolveAccessor(accIdx, accessors, bufferViews, buffers, compBytes * 4);
            var arr = new int[count * 4];
            for (int i = 0; i < count; i++)
            {
                int o = off + i * stride;
                if (o + compBytes * 4 > buf.Length) break;
                for (int k = 0; k < 4; k++)
                {
                    int co = o + k * compBytes;
                    arr[i * 4 + k] = compType == 5123 ? BitConverter.ToUInt16(buf, co) : buf[co];
                }
            }
            return arr;
        }

        private static Vector4[] ReadVec4FloatArray(int accIdx, JsonElement accessors, JsonElement bufferViews, List<byte[]> buffers)
        {
            var (buf, off, stride, count, _) = ResolveAccessor(accIdx, accessors, bufferViews, buffers, 16);
            var arr = new Vector4[count];
            for (int i = 0; i < count; i++)
            {
                int o = off + i * stride;
                if (o + 16 > buf.Length) break;
                arr[i] = new Vector4(
                    BitConverter.ToSingle(buf, o),
                    BitConverter.ToSingle(buf, o + 4),
                    BitConverter.ToSingle(buf, o + 8),
                    BitConverter.ToSingle(buf, o + 12));
            }
            return arr;
        }

        private static Matrix4x4[] ReadMatrixArray(int accIdx, JsonElement accessors, JsonElement bufferViews, List<byte[]> buffers)
        {
            var (buf, off, stride, count, _) = ResolveAccessor(accIdx, accessors, bufferViews, buffers, 64);
            var arr = new Matrix4x4[count];
            for (int i = 0; i < count; i++)
            {
                int o = off + i * stride;
                if (o + 64 > buf.Length) break;
                arr[i] = new Matrix4x4(
                    BitConverter.ToSingle(buf, o + 0), BitConverter.ToSingle(buf, o + 4), BitConverter.ToSingle(buf, o + 8), BitConverter.ToSingle(buf, o + 12),
                    BitConverter.ToSingle(buf, o + 16), BitConverter.ToSingle(buf, o + 20), BitConverter.ToSingle(buf, o + 24), BitConverter.ToSingle(buf, o + 28),
                    BitConverter.ToSingle(buf, o + 32), BitConverter.ToSingle(buf, o + 36), BitConverter.ToSingle(buf, o + 40), BitConverter.ToSingle(buf, o + 44),
                    BitConverter.ToSingle(buf, o + 48), BitConverter.ToSingle(buf, o + 52), BitConverter.ToSingle(buf, o + 56), BitConverter.ToSingle(buf, o + 60));
            }
            return arr;
        }

        private sealed class FbxNode
        {
            public string Name = "";
            public List<object?> Props = new();
            public List<FbxNode> Children = new();
            public FbxNode? FindChild(string name)
            {
                for (int i = 0; i < Children.Count; i++)
                    if (Children[i].Name == name) return Children[i];
                return null;
            }
        }

        private sealed class FbxModelNode
        {
            public long Id;
            public long ParentId;
            public Vector3 LclTranslation;
            public Vector3 LclRotation;
            public Vector3 LclScaling = Vector3.One;
            public Vector3 GeometricTranslation;
            public Vector3 GeometricRotation;
            public Vector3 GeometricScaling = Vector3.One;
        }

        private sealed class FbxGeometryData
        {
            public long Id;
            public double[]? Vertices;
            public float[]? VerticesF;
            public int[]? PolygonVertexIndex;
        }

        private static CharacterModelEntity LoadBinaryFbx(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 27)
                throw new InvalidDataException("File Binary .FBX không hợp lệ.");

            uint version = BitConverter.ToUInt32(data, 23);
            bool is64 = version >= 7500;
            int pos = 27;
            var rootNodes = new List<FbxNode>();
            while (pos < data.Length)
            {
                var node = ReadBinaryFbxNode(data, ref pos, is64);
                if (node == null) break;
                rootNodes.Add(node);
            }

            int upAxis = 1;
            int upSign = 1;
            var geometries = new Dictionary<long, FbxGeometryData>();
            var models = new Dictionary<long, FbxModelNode>();
            var geomToModel = new Dictionary<long, long>();

            foreach (var rn in rootNodes)
            {
                if (rn.Name == "GlobalSettings")
                {
                    var p70 = rn.FindChild("Properties70");
                    if (p70 != null)
                    {
                        foreach (var prop in p70.Children)
                        {
                            if (prop.Props.Count >= 5 && prop.Props[0] is string pname)
                            {
                                if (pname == "UpAxis" && prop.Props[4] != null) upAxis = Convert.ToInt32(prop.Props[4], CultureInfo.InvariantCulture);
                                else if (pname == "UpAxisSign" && prop.Props[4] != null) upSign = Convert.ToInt32(prop.Props[4], CultureInfo.InvariantCulture);
                            }
                        }
                    }
                }
                else if (rn.Name == "Objects")
                {
                    foreach (var obj in rn.Children)
                    {
                        if (obj.Name == "Geometry" && obj.Props.Count >= 1 && obj.Props[0] != null)
                        {
                            long id = Convert.ToInt64(obj.Props[0], CultureInfo.InvariantCulture);
                            var g = new FbxGeometryData { Id = id };
                            var vNode = obj.FindChild("Vertices");
                            if (vNode != null && vNode.Props.Count >= 1)
                            {
                                if (vNode.Props[0] is double[] da) g.Vertices = da;
                                else if (vNode.Props[0] is float[] fa) g.VerticesF = fa;
                            }
                            var pNode = obj.FindChild("PolygonVertexIndex");
                            if (pNode != null && pNode.Props.Count >= 1 && pNode.Props[0] is int[] ia)
                                g.PolygonVertexIndex = ia;
                            if ((g.Vertices != null || g.VerticesF != null) && g.PolygonVertexIndex != null)
                                geometries[id] = g;
                        }
                        else if (obj.Name == "Model" && obj.Props.Count >= 1 && obj.Props[0] != null)
                        {
                            long id = Convert.ToInt64(obj.Props[0], CultureInfo.InvariantCulture);
                            var mNode = new FbxModelNode { Id = id };
                            var p70 = obj.FindChild("Properties70");
                            if (p70 != null)
                            {
                                foreach (var prop in p70.Children)
                                {
                                    if (prop.Props.Count >= 7 && prop.Props[0] is string pname)
                                    {
                                        var v = new Vector3(
                                            Convert.ToSingle(prop.Props[4], CultureInfo.InvariantCulture),
                                            Convert.ToSingle(prop.Props[5], CultureInfo.InvariantCulture),
                                            Convert.ToSingle(prop.Props[6], CultureInfo.InvariantCulture));
                                        switch (pname)
                                        {
                                            case "Lcl Translation": mNode.LclTranslation = v; break;
                                            case "Lcl Rotation": mNode.LclRotation = v; break;
                                            case "Lcl Scaling": mNode.LclScaling = v; break;
                                            case "GeometricTranslation": mNode.GeometricTranslation = v; break;
                                            case "GeometricRotation": mNode.GeometricRotation = v; break;
                                            case "GeometricScaling": mNode.GeometricScaling = v; break;
                                        }
                                    }
                                }
                            }
                            models[id] = mNode;
                        }
                    }
                }
                else if (rn.Name == "Connections")
                {
                    foreach (var cn in rn.Children)
                    {
                        if (cn.Props.Count >= 3 && cn.Props[0] is string ctype && ctype == "OO")
                        {
                            long childId = Convert.ToInt64(cn.Props[1], CultureInfo.InvariantCulture);
                            long parentId = Convert.ToInt64(cn.Props[2], CultureInfo.InvariantCulture);
                            if (geometries.ContainsKey(childId) && models.ContainsKey(parentId))
                                geomToModel[childId] = parentId;
                            else if (models.TryGetValue(childId, out var childModel) && parentId != 0)
                                childModel.ParentId = parentId;
                        }
                    }
                }
            }

            static Matrix4x4 BuildEulerXYZ(Vector3 deg)
            {
                const float d2r = (float)(Math.PI / 180.0);
                return Matrix4x4.CreateRotationX(deg.X * d2r) *
                       Matrix4x4.CreateRotationY(deg.Y * d2r) *
                       Matrix4x4.CreateRotationZ(deg.Z * d2r);
            }

            static Matrix4x4 GetModelLocalMatrix(FbxModelNode m) =>
                Matrix4x4.CreateScale(m.LclScaling) * BuildEulerXYZ(m.LclRotation) * Matrix4x4.CreateTranslation(m.LclTranslation);

            Matrix4x4 GetModelWorldMatrix(long modelId, int guard = 0)
            {
                if (guard > 64 || !models.TryGetValue(modelId, out var m))
                    return Matrix4x4.Identity;
                var local = Matrix4x4.CreateScale(m.GeometricScaling) *
                            BuildEulerXYZ(m.GeometricRotation) *
                            Matrix4x4.CreateTranslation(m.GeometricTranslation) *
                            GetModelLocalMatrix(m);
                if (m.ParentId != 0 && models.ContainsKey(m.ParentId))
                    return local * GetModelWorldMatrix(m.ParentId, guard + 1);
                return local;
            }

            Matrix4x4 upAxisMat = upAxis == 2
                ? Matrix4x4.CreateRotationX((float)(-upSign * Math.PI * 0.5))
                : Matrix4x4.Identity;

            var mesh = new MeshData();
            foreach (var kv in geometries)
            {
                var g = kv.Value;
                int vCount = g.Vertices != null ? g.Vertices.Length / 3 : (g.VerticesF!.Length / 3);
                int[] polyIdx = g.PolygonVertexIndex!;
                if (vCount == 0 || polyIdx.Length == 0) continue;

                Matrix4x4 xf = geomToModel.TryGetValue(g.Id, out long mId)
                    ? GetModelWorldMatrix(mId) * upAxisMat
                    : upAxisMat;

                int baseVert = mesh.Vertices.Count;
                for (int i = 0; i < vCount; i++)
                {
                    float vx = g.Vertices != null ? (float)g.Vertices[i * 3] : g.VerticesF![i * 3];
                    float vy = g.Vertices != null ? (float)g.Vertices[i * 3 + 1] : g.VerticesF![i * 3 + 1];
                    float vz = g.Vertices != null ? (float)g.Vertices[i * 3 + 2] : g.VerticesF![i * 3 + 2];
                    var tp = Vector3.Transform(new Vector3(vx, vy, vz), xf);
                    mesh.Vertices.Add(new Vec3(tp.X, tp.Y, tp.Z));
                    mesh.UVs.Add(new Vec2(0f, 0f));
                }

                var poly = new List<int>(4);
                for (int i = 0; i < polyIdx.Length; i++)
                {
                    int raw = polyIdx[i];
                    bool isEnd = raw < 0;
                    int idx = isEnd ? ~raw : raw;
                    if (idx >= 0 && idx < vCount)
                        poly.Add(baseVert + idx);
                    if (isEnd)
                    {
                        for (int k = 1; k + 1 < poly.Count; k++)
                        {
                            mesh.Triangles.Add(poly[0]);
                            mesh.Triangles.Add(poly[k]);
                            mesh.Triangles.Add(poly[k + 1]);
                        }
                        poly.Clear();
                    }
                }
            }

            if (mesh.Vertices.Count == 0 || mesh.Triangles.Count == 0)
                throw new InvalidDataException("Không tìm thấy dữ liệu lưới hợp lệ trong file Binary .FBX.");

            return new CharacterModelEntity { Mesh = mesh };
        }

        private static FbxNode? ReadBinaryFbxNode(byte[] buf, ref int pos, bool is64)
        {
            int hdrSize = is64 ? 25 : 13;
            if (pos + hdrSize > buf.Length) return null;

            long endOff, numProps, propListLen;
            int nameLen;
            if (is64)
            {
                endOff = BitConverter.ToInt64(buf, pos);
                numProps = BitConverter.ToInt64(buf, pos + 8);
                propListLen = BitConverter.ToInt64(buf, pos + 16);
                nameLen = buf[pos + 24];
            }
            else
            {
                endOff = BitConverter.ToUInt32(buf, pos);
                numProps = BitConverter.ToUInt32(buf, pos + 4);
                propListLen = BitConverter.ToUInt32(buf, pos + 8);
                nameLen = buf[pos + 12];
            }

            if (endOff == 0)
            {
                pos += hdrSize;
                return null;
            }

            int p = pos + hdrSize;
            string name = Encoding.UTF8.GetString(buf, p, nameLen);
            p += nameLen;

            var node = new FbxNode { Name = name };
            for (long i = 0; i < numProps; i++)
                node.Props.Add(ReadBinaryFbxProperty(buf, ref p));

            int childPos = pos + hdrSize + nameLen + (int)propListLen;
            while (childPos < endOff)
            {
                var child = ReadBinaryFbxNode(buf, ref childPos, is64);
                if (child == null) break;
                node.Children.Add(child);
            }

            pos = (int)endOff;
            return node;
        }

        private static object? ReadBinaryFbxProperty(byte[] buf, ref int pos)
        {
            char tc = (char)buf[pos++];
            switch (tc)
            {
                case 'Y': { short v = BitConverter.ToInt16(buf, pos); pos += 2; return v; }
                case 'C': { bool v = buf[pos] != 0; pos += 1; return v; }
                case 'I': { int v = BitConverter.ToInt32(buf, pos); pos += 4; return v; }
                case 'F': { float v = BitConverter.ToSingle(buf, pos); pos += 4; return v; }
                case 'D': { double v = BitConverter.ToDouble(buf, pos); pos += 8; return v; }
                case 'L': { long v = BitConverter.ToInt64(buf, pos); pos += 8; return v; }
                case 'S':
                case 'R':
                {
                    int len = BitConverter.ToInt32(buf, pos);
                    pos += 4;
                    object val = tc == 'S' ? Encoding.UTF8.GetString(buf, pos, len) : buf.AsSpan(pos, len).ToArray();
                    pos += len;
                    return val;
                }
                case 'f':
                case 'd':
                case 'l':
                case 'i':
                case 'b':
                case 'c':
                {
                    int arrayLen = BitConverter.ToInt32(buf, pos);
                    int encoding = BitConverter.ToInt32(buf, pos + 4);
                    int compLen = BitConverter.ToInt32(buf, pos + 8);
                    pos += 12;

                    byte[] raw;
                    if (encoding == 1)
                    {
                        using var ms = new MemoryStream(buf, pos, compLen, writable: false);
                        using var z = new ZLibStream(ms, CompressionMode.Decompress);
                        using var outMs = new MemoryStream();
                        z.CopyTo(outMs);
                        raw = outMs.ToArray();
                    }
                    else
                    {
                        raw = new byte[compLen];
                        Buffer.BlockCopy(buf, pos, raw, 0, compLen);
                    }
                    pos += compLen;

                    if (tc == 'd')
                    {
                        var arr = new double[arrayLen];
                        Buffer.BlockCopy(raw, 0, arr, 0, Math.Min(raw.Length, arrayLen * 8));
                        return arr;
                    }
                    if (tc == 'f')
                    {
                        var arr = new float[arrayLen];
                        Buffer.BlockCopy(raw, 0, arr, 0, Math.Min(raw.Length, arrayLen * 4));
                        return arr;
                    }
                    if (tc == 'i')
                    {
                        var arr = new int[arrayLen];
                        Buffer.BlockCopy(raw, 0, arr, 0, Math.Min(raw.Length, arrayLen * 4));
                        return arr;
                    }
                    if (tc == 'l')
                    {
                        var arr = new long[arrayLen];
                        Buffer.BlockCopy(raw, 0, arr, 0, Math.Min(raw.Length, arrayLen * 8));
                        return arr;
                    }
                    return raw;
                }
                default:
                    throw new InvalidDataException($"Unknown FBX property type '{tc}'.");
            }
        }
    }
}
