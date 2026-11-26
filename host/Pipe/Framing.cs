using System;
using System.IO;

namespace EngineeringPlatform.Host.Pipe
{
    /// <summary>分帧读取状态。</summary>
    internal enum FrameStatus
    {
        /// <summary>读到完整一帧。</summary>
        Ok = 0,
        /// <summary>长度前缀超过 1 MiB：已把 len 字节读完丢弃，可继续使用连接。</summary>
        TooLarge = 1,
        /// <summary>len == 0：协议违规，必须关连接。</summary>
        Empty = 2,
        /// <summary>帧体未读满就 EOF / 读流出错：协议违规。</summary>
        ProtocolViolation = 3,
        /// <summary>帧边界处读到 EOF（正常断开）。</summary>
        Eof = 4
    }

    /// <summary>
    /// 管道分帧：4 字节小端 uint32 长度前缀 + UTF-8 JSON 载荷（设计 §2.1）。
    /// 上限 1 MiB；超限时必须先把 len 字节读完丢弃，否则流会错位。
    /// 本文件禁止引用任何 AutoCAD 类型（T02 判据 5）。
    /// </summary>
    internal static class Framing
    {
        /// <summary>单帧载荷上限：1 MiB。</summary>
        public const int MaxPayloadBytes = 1024 * 1024;

        /// <summary>丢弃超限帧时的暂存缓冲（不按 len 分配大缓冲）。</summary>
        private const int DiscardBufferBytes = 8 * 1024;

        /// <summary>写入帧；载荷超限或为空抛异常（调用方必须先自检）。</summary>
        public static void WriteFrame(Stream stream, byte[] payload)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }
            if (payload == null || payload.Length == 0)
            {
                throw new FrameProtocolException("empty_payload");
            }
            if (payload.Length > MaxPayloadBytes)
            {
                throw new FrameTooLargeException(payload.Length);
            }

            byte[] header = new byte[4];
            int len = payload.Length;
            header[0] = (byte)(len & 0xFF);
            header[1] = (byte)((len >> 8) & 0xFF);
            header[2] = (byte)((len >> 16) & 0xFF);
            header[3] = (byte)((len >> 24) & 0xFF);

            stream.Write(header, 0, 4);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        /// <summary>序列化对象并写帧。</summary>
        public static void WriteJsonFrame(Stream stream, object value)
        {
            string json = Protocol.JsonCodec.Serialize(value);
            WriteFrame(stream, Protocol.JsonCodec.Utf8(json));
        }

        /// <summary>
        /// 读取一帧。返回 null 时 <paramref name="status"/> 说明原因（Eof / TooLarge / Empty / ProtocolViolation）。
        /// 半包由循环 Read 处理；粘包由调用方循环调用本方法自然处理。
        /// </summary>
        public static byte[] ReadFrame(Stream stream, out FrameStatus status)
        {
            if (stream == null)
            {
                throw new ArgumentNullException("stream");
            }

            status = FrameStatus.Ok;
            byte[] header = new byte[4];
            int headerRead = 0;
            while (headerRead < 4)
            {
                int n = stream.Read(header, headerRead, 4 - headerRead);
                if (n <= 0)
                {
                    status = headerRead == 0 ? FrameStatus.Eof : FrameStatus.ProtocolViolation;
                    return null;
                }
                headerRead += n;
            }

            uint length = (uint)(header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24));
            if (length == 0)
            {
                status = FrameStatus.Empty;
                return null;
            }

            if (length > MaxPayloadBytes)
            {
                // 必须读完丢弃，保证后续帧不错位
                if (!Discard(stream, (long)length))
                {
                    status = FrameStatus.ProtocolViolation;
                    return null;
                }
                status = FrameStatus.TooLarge;
                return null;
            }

            byte[] payload = new byte[(int)length];
            int read = 0;
            while (read < payload.Length)
            {
                int n = stream.Read(payload, read, payload.Length - read);
                if (n <= 0)
                {
                    status = FrameStatus.ProtocolViolation;
                    return null;
                }
                read += n;
            }
            return payload;
        }

        private static bool Discard(Stream stream, long count)
        {
            byte[] buffer = new byte[DiscardBufferBytes];
            long remaining = count;
            while (remaining > 0)
            {
                int want = (int)Math.Min(remaining, buffer.Length);
                int n = stream.Read(buffer, 0, want);
                if (n <= 0)
                {
                    return false;
                }
                remaining -= n;
            }
            return true;
        }
    }

    /// <summary>载荷超限。</summary>
    internal sealed class FrameTooLargeException : Exception
    {
        public int Length { get; private set; }

        public FrameTooLargeException(int length)
            : base("frame_too_large:" + length)
        {
            Length = length;
        }
    }

    /// <summary>分帧协议违规。</summary>
    internal sealed class FrameProtocolException : Exception
    {
        public FrameProtocolException(string message)
            : base(message)
        {
        }
    }
}
