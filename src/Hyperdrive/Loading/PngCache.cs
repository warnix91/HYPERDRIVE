using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Hyperdrive.Core;
using UnityEngine;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// FastLoader's PNG texture cache (idea from KSPCommunityFixes' PNG cache, MIT; own format and code). A PNG costs a
    /// decode and a DXT compression on every launch; the texture it ends up as (format, size, mip count, the final bytes of
    /// every mip, the TextureInfo flags) is written once to PluginData/PngCache by a worker thread, and later launches load
    /// those bytes the way DDS files are loaded, when the PNG still has the size and write time it had. Same bytes, so the
    /// same texture; a cache file that does not match (other PNG, other version, other URL, damaged) is ignored and the PNG
    /// is decoded as before, which writes it again.
    /// File: 52-byte header, the texture URL (UTF-8), then the raw texture data.
    /// </summary>
    internal static class PngCache
    {
        private const uint Magic = 0x43504448;   // "HDPC"
        private const int Version = 1;
        internal const int HeaderSize = 52;

        internal struct Header
        {
            public long SourceLength, SourceTicks;
            public int Width, Height, Format, MipCount, Flags, UrlBytes, DataLength;
        }

        internal const int FlagNormal = 1, FlagInfoReadable = 2, FlagCompressed = 4, FlagTextureReadable = 8;

        private static string s_dir;
        private static bool s_write;
        private static int s_pendingWrites;
        private static int s_written;
        private static long s_writtenBytes;

        internal static string Directory => s_dir;
        internal static int Written => s_written;
        internal static long WrittenBytes => Interlocked.Read(ref s_writtenBytes);

        /// <summary>Main thread, before loading: the cache folder, and whether new files may be written.</summary>
        internal static void Init(string modDirectory, float minFreeGB)
        {
            s_dir = Path.Combine(Path.Combine(modDirectory, "PluginData"), "PngCache");
            try
            {
                System.IO.Directory.CreateDirectory(s_dir);
                foreach (string tmp in System.IO.Directory.GetFiles(s_dir, "*.tmp"))
                    File.Delete(tmp);   // left by an interrupted write
                long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(s_dir))).AvailableFreeSpace;
                s_write = free > (long)(minFreeGB * 1024 * 1024 * 1024);
                if (!s_write)
                    Log.Info("FastLoader: PNG cache not written this time (disk: " + (free / (1024 * 1024)) + " MB free, keeping at least " + minFreeGB + " GB)");
            }
            catch (Exception e)
            {
                s_write = false;
                Log.Info("FastLoader: PNG cache folder not usable (" + e.Message + ")");
            }
        }

        /// <summary>
        /// Housekeeping on its own: cache files of PNGs that are no longer installed (a mod removed, a texture renamed) are
        /// deleted on a worker thread. keep: the cache paths of every PNG of this load.
        /// </summary>
        internal static void Prune(HashSet<string> keep)
        {
            string dir = s_dir;
            if (string.IsNullOrEmpty(dir) || keep == null)
                return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int removed = 0;
                long bytes = 0;
                try
                {
                    foreach (string file in System.IO.Directory.GetFiles(dir))
                    {
                        if (file.EndsWith(".tmp", StringComparison.Ordinal) || keep.Contains(file))
                            continue;
                        try
                        {
                            bytes += new FileInfo(file).Length;
                            File.Delete(file);
                            removed++;
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
                if (removed > 0)
                    Log.Info("FastLoader: " + removed + " PNG cache files of textures no longer installed removed (" + (bytes / (1024 * 1024)) + " MB)");
            });
        }

        /// <summary>The player declined the cache: its files are deleted on a worker thread (disk space given back).</summary>
        internal static void DeleteAll()
        {
            string dir = s_dir;
            s_write = false;
            if (string.IsNullOrEmpty(dir))
                return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (System.IO.Directory.Exists(dir))
                        System.IO.Directory.Delete(dir, true);
                }
                catch (Exception) { }
            });
        }

        internal static string PathFor(string url)
        {
            // FNV-1a 64 of the URL: a short, stable file name (the URL inside the file guards against collisions).
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < url.Length; i++)
            {
                h ^= url[i];
                h *= 1099511628211UL;
            }
            return Path.Combine(s_dir, h.ToString("x16") + ".hdtex");
        }

        /// <summary>Checks a cache file read into <paramref name="buffer"/> against the PNG it stands for.</summary>
        /// <summary>True when the first bytes of a cache file hold its whole header and URL (FastLoader's direct path).</summary>
        internal static bool HeaderFits(byte[] buffer, int length) =>
            length >= HeaderSize && BitConverter.ToUInt32(buffer, 0) == Magic && BitConverter.ToInt32(buffer, 44) >= 0
            && HeaderSize + (long)BitConverter.ToInt32(buffer, 44) <= length;

        internal static bool TryParse(byte[] buffer, int length, string url, long sourceLength, long sourceTicks, out Header header, out int dataOffset)
            => TryParse(buffer, length, length, url, sourceLength, sourceTicks, out header, out dataOffset);

        /// <summary>fileLength: the whole file's; buffer holds at least the header and the URL.</summary>
        internal static bool TryParse(byte[] buffer, int length, long fileLength, string url, long sourceLength, long sourceTicks, out Header header, out int dataOffset)
        {
            header = default;
            dataOffset = 0;
            if (length < HeaderSize || BitConverter.ToUInt32(buffer, 0) != Magic || BitConverter.ToInt32(buffer, 4) != Version)
                return false;
            header.SourceLength = BitConverter.ToInt64(buffer, 8);
            header.SourceTicks = BitConverter.ToInt64(buffer, 16);
            header.Width = BitConverter.ToInt32(buffer, 24);
            header.Height = BitConverter.ToInt32(buffer, 28);
            header.Format = BitConverter.ToInt32(buffer, 32);
            header.MipCount = BitConverter.ToInt32(buffer, 36);
            header.Flags = BitConverter.ToInt32(buffer, 40);
            header.UrlBytes = BitConverter.ToInt32(buffer, 44);
            header.DataLength = BitConverter.ToInt32(buffer, 48);
            if (header.SourceLength != sourceLength || header.SourceTicks != sourceTicks || header.Width <= 0 || header.Height <= 0
                || header.MipCount <= 0 || header.UrlBytes < 0 || header.DataLength <= 0
                || (long)HeaderSize + header.UrlBytes + header.DataLength != fileLength || (long)HeaderSize + header.UrlBytes > length)
                return false;
            if (Encoding.UTF8.GetString(buffer, HeaderSize, header.UrlBytes) != url)
                return false;
            dataOffset = HeaderSize + header.UrlBytes;
            return true;
        }

        /// <summary>
        /// Main thread, right after a PNG became a texture: copies its final bytes and writes them on a worker thread.
        /// Only full mip chains or single levels (what Texture2D's constructor can rebuild) and readable textures.
        /// </summary>
        internal static void Save(UrlDir.UrlFile file, GameDatabase.TextureInfo info)
        {
            Texture2D texture = info?.texture;
            if (!s_write || texture == null || !texture.isReadable)
                return;
            try
            {
                int fullChain = 1 + (int)Math.Floor(Math.Log(Math.Max(texture.width, texture.height), 2));
                if (texture.mipmapCount != 1 && texture.mipmapCount != fullChain)
                    return;
                var source = new FileInfo(file.fullPath);
                byte[] data = texture.GetRawTextureData();
                var header = new Header
                {
                    SourceLength = source.Length,
                    SourceTicks = source.LastWriteTimeUtc.Ticks,
                    Width = texture.width,
                    Height = texture.height,
                    Format = (int)texture.format,
                    MipCount = texture.mipmapCount,
                    Flags = (info.isNormalMap ? FlagNormal : 0) | (info.isReadable ? FlagInfoReadable : 0)
                            | (info.isCompressed ? FlagCompressed : 0) | FlagTextureReadable,
                    DataLength = data.Length,
                };
                string url = file.url;
                string path = PathFor(url);
                Interlocked.Increment(ref s_pendingWrites);
                ThreadPool.QueueUserWorkItem(_ => Write(path, url, header, data));
            }
            catch (Exception e)
            {
                Log.Debug("FastLoader: PNG cache skipped for " + file.url + " (" + e.Message + ")");
            }
        }

        // Worker thread: plain file IO only.
        private static void Write(string path, string url, Header header, byte[] data)
        {
            string tmp = path + ".tmp";
            try
            {
                byte[] urlBytes = Encoding.UTF8.GetBytes(url);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Magic);
                    w.Write(Version);
                    w.Write(header.SourceLength);
                    w.Write(header.SourceTicks);
                    w.Write(header.Width);
                    w.Write(header.Height);
                    w.Write(header.Format);
                    w.Write(header.MipCount);
                    w.Write(header.Flags);
                    w.Write(urlBytes.Length);
                    w.Write(data.Length);
                    w.Write(urlBytes);
                    w.Write(data);
                }
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmp, path);
                Interlocked.Increment(ref s_written);
                Interlocked.Add(ref s_writtenBytes, HeaderSize + urlBytes.Length + data.Length);
            }
            catch (Exception)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            }
            finally
            {
                Interlocked.Decrement(ref s_pendingWrites);
            }
        }
    }
}
