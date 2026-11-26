using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace EngineeringPlatform.Host.Tools
{
    /// <summary>
    /// 纯 C# 零依赖 NumPy .npy (v1.0) / .npz (ZIP) 写入器。
    /// 与 Python route_solver/cost_field.py 的 numpy.load(..., allow_pickle=False) 字节级兼容。
    /// </summary>
    internal static class NpzWriter
    {
        private static readonly byte[] NpyMagic = { 0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y' };

        /// <summary>将高程栅格 (ny, nx) 与升序坐标轴 x_coords / y_coords 写入 .npz 文件。</summary>
        public static void WriteTerrainNpz(string filePath, double[,] elevation, double[] xCoords, double[] yCoords)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("filePath required");
            }
            if (elevation == null || xCoords == null || yCoords == null)
            {
                throw new ArgumentNullException("elevation/xCoords/yCoords");
            }

            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create, false))
            {
                WriteEntry2D(archive, "elevation.npy", elevation);
                WriteEntry1D(archive, "x_coords.npy", xCoords);
                WriteEntry1D(archive, "y_coords.npy", yCoords);
            }
        }

        private static void WriteEntry1D(ZipArchive archive, string entryName, double[] values)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using (Stream stream = entry.Open())
            {
                string shapeText = string.Format(CultureInfo.InvariantCulture, "({0},)", values.Length);
                WriteNpyHeader(stream, shapeText);
                byte[] buffer = new byte[values.Length * 8];
                Buffer.BlockCopy(values, 0, buffer, 0, buffer.Length);
                stream.Write(buffer, 0, buffer.Length);
            }
        }

        private static void WriteEntry2D(ZipArchive archive, string entryName, double[,] values)
        {
            int ny = values.GetLength(0);
            int nx = values.GetLength(1);
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using (Stream stream = entry.Open())
            {
                string shapeText = string.Format(CultureInfo.InvariantCulture, "({0}, {1})", ny, nx);
                WriteNpyHeader(stream, shapeText);

                byte[] rowBuf = new byte[nx * 8];
                for (int r = 0; r < ny; r++)
                {
                    for (int c = 0; c < nx; c++)
                    {
                        byte[] bytes = BitConverter.GetBytes(values[r, c]);
                        Buffer.BlockCopy(bytes, 0, rowBuf, c * 8, 8);
                    }
                    stream.Write(rowBuf, 0, rowBuf.Length);
                }
            }
        }

        private static void WriteNpyHeader(Stream stream, string shapeText)
        {
            string dict = "{'descr': '<f8', 'fortran_order': False, 'shape': " + shapeText + ", }";
            // 10 bytes preamble (6 magic + 2 version + 2 headerLen) + header must be divisible by 64
            int rawLen = dict.Length + 1; // +1 for trailing '\n'
            int total = 10 + rawLen;
            int pad = (64 - (total % 64)) % 64;
            string padded = dict + new string(' ', pad) + "\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(padded);
            ushort headerLen = (ushort)headerBytes.Length;

            stream.Write(NpyMagic, 0, NpyMagic.Length);
            stream.WriteByte(0x01); // major version 1
            stream.WriteByte(0x00); // minor version 0
            stream.WriteByte((byte)(headerLen & 0xFF));
            stream.WriteByte((byte)((headerLen >> 8) & 0xFF));
            stream.Write(headerBytes, 0, headerBytes.Length);
        }
    }
}
