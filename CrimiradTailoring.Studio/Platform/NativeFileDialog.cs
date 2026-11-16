using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace CrimiradTailoring.Studio.Platform
{
    internal static class NativeFileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string? lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string? lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string? lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

        private const int OfnOverwritePrompt = 0x00000002;
        private const int OfnNoChangeDir = 0x00000008;
        private const int OfnPathMustExist = 0x00000800;
        private const int OfnFileMustExist = 0x00001000;
        private const int OfnExplorer = 0x00080000;

        public static string? ShowOpenDialog(string title, string filterNullSeparated)
        {
            return RunDialog(isSave: false, title, filterNullSeparated, defaultName: "", defaultExt: null);
        }

        public static string? ShowSaveDialog(string title, string filterNullSeparated, string defaultName, string? defaultExt = null)
        {
            return RunDialog(isSave: true, title, filterNullSeparated, defaultName, defaultExt);
        }

        private static string? RunDialog(bool isSave, string title, string filter, string defaultName, string? defaultExt)
        {
            const int maxChars = 4096;
            IntPtr fileBuf = Marshal.AllocHGlobal(maxChars * 2);
            try
            {
                // Clear buffer & write initial file name if any
                for (int i = 0; i < maxChars * 2; i += 2)
                    Marshal.WriteInt16(fileBuf, i, 0);

                if (!string.IsNullOrEmpty(defaultName))
                {
                    char[] chars = defaultName.ToCharArray();
                    int len = Math.Min(chars.Length, maxChars - 1);
                    Marshal.Copy(chars, 0, fileBuf, len);
                }

                var ofn = new OpenFileName
                {
                    lStructSize = Marshal.SizeOf<OpenFileName>(),
                    hwndOwner = IntPtr.Zero,
                    lpstrFilter = filter,
                    nFilterIndex = 1,
                    lpstrFile = fileBuf,
                    nMaxFile = maxChars,
                    lpstrTitle = title,
                    lpstrDefExt = defaultExt,
                    Flags = OfnExplorer | OfnNoChangeDir | OfnPathMustExist |
                            (isSave ? OfnOverwritePrompt : OfnFileMustExist)
                };

                bool ok = isSave ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
                return ok ? Marshal.PtrToStringUni(fileBuf) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(fileBuf);
            }
        }

        /// <summary>
        /// Ghi buffer RGBA từ OpenGL (gốc dưới-trái) ra file PNG chuẩn bằng .NET ZLibStream (không cần WPF/System.Drawing).
        /// </summary>
        public static void WriteGlRgbaPng(string path, int width, int height, byte[] glRgbaBottomUp)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            int stride = width * 4;
            byte[] rawScanlines = new byte[height * (1 + stride)];
            for (int y = 0; y < height; y++)
            {
                int srcRow = (height - 1 - y) * stride;
                int dstRow = y * (1 + stride);
                rawScanlines[dstRow] = 0; // filter None
                Buffer.BlockCopy(glRgbaBottomUp, srcRow, rawScanlines, dstRow + 1, stride);
            }

            byte[] compressedIdat;
            using (var ms = new MemoryStream())
            {
                using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    zlib.Write(rawScanlines, 0, rawScanlines.Length);
                }
                compressedIdat = ms.ToArray();
            }

            using var fs = File.Create(path);
            // PNG Signature
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            byte[] ihdr = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
            ihdr[8] = 8; // bit depth
            ihdr[9] = 6; // RGBA
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;

            WriteChunk(fs, "IHDR"u8, ihdr);
            WriteChunk(fs, "IDAT"u8, compressedIdat);
            WriteChunk(fs, "IEND"u8, ReadOnlySpan<byte>.Empty);
        }

        private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> lenBuf = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lenBuf, data.Length);
            s.Write(lenBuf);
            s.Write(type);
            if (!data.IsEmpty) s.Write(data);

            uint crc = 0xFFFFFFFFu;
            crc = UpdateCrc(crc, type);
            crc = UpdateCrc(crc, data);
            crc ^= 0xFFFFFFFFu;

            Span<byte> crcBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
            s.Write(crcBuf);
        }

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                crc ^= bytes[i];
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
            }
            return crc;
        }
    }
}
