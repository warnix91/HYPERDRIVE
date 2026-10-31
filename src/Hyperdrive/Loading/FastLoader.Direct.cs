using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using DDSHeaders;
using Hyperdrive.Core;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace Hyperdrive.Loading
{
    /// <summary>
    /// Direct DDS path (ddsDirect, on by default). Measured on a heavy modpack (2674 DDS, 3.4 GB) the main thread spent
    /// 0.7 s in new Texture2D (Unity fills the new pixel buffer with zeros) and 1 s in LoadRawTextureData (a copy of every
    /// byte from our buffer into the texture's). Here the reader threads only read each file's header; the main thread
    /// creates the texture with the same size, graphics format, mip count and flags as stock's constructor plus Unity's
    /// DontInitializePixels flag (the buffer is about to be overwritten), and Unity's own reader (AsyncReadManager) writes
    /// the file's pixel data straight into the texture's buffer (GetRawTextureData), off the main thread. Once the read is
    /// complete the texture is applied exactly as stock does (Apply(false, true)) and added to the database in file order.
    /// The pointer is taken ddsFrameWait frames after the creation, once the render thread is done with the new texture
    /// (as KSPCF does), so Unity has no reason to copy the buffer. Anything unexpected (format not supported by the GPU,
    /// a file shorter than its header says, a failed read) leaves the file to the stock loader, as before.
    /// ddsVerify (developer): every texture is also built the stock way and compared (format, size, mips, every byte).
    /// </summary>
    internal sealed partial class FastLoader
    {
        private const TextureCreationFlags DontInitializePixels = (TextureCreationFlags)4;   // Unity's internal flag
        private const int HeaderBytes = 4 + 124 + 20;   // magic, DDS_HEADER, DDS_HEADER_DXT10
        private const int MaxDirect = 256;
        private const int CacheProbeBytes = 4096;   // a PNG cache file's header and URL

        private static bool s_ddsDirect = true, s_ddsVerify, s_ddsUnityReader;
        private static int s_ddsFrameWait = 2;
        private static long s_ddsInFlightBytes = 384L << 20;
        private static NativeArray<ReadCommand> s_commands;
        private static readonly Stack<int> s_freeCommands = new Stack<int>();

        private bool _directActive;   // decided at the start of the texture phase, read by the reader threads

        private enum DirectState { Created, Reading, Cached, Dropped }

        /// <summary>The texture phase's direct DDS queue, in file order; counters for the summary line.</summary>
        private sealed class DirectQueue
        {
            public readonly Queue<Item> Items = new Queue<Item>();
            public long Bytes;            // file bytes in the queue
            public int Created;           // items waiting for their frame
            public int Loaded, LeftToStock, CachedLoaded;
            public long LoadedBytes;
            public long CreateTicks, PointerTicks, ApplyTicks;
            public int Same, Different;
            public readonly List<string> Details = new List<string>();
            public readonly List<UrlDir.UrlFile> RetryPng = new List<UrlDir.UrlFile>();

            public bool Full => Items.Count >= MaxDirect || Bytes >= s_ddsInFlightBytes;
        }

        private static void LoadDirectSettings(SettingsSection settings)
        {
            s_ddsDirect = settings.GetBool("ddsDirect", true);
            s_ddsFrameWait = Mathf.Clamp(settings.GetInt("ddsFrameWait", 2), 0, 10);
            s_ddsInFlightBytes = (long)Mathf.Clamp(settings.GetInt("ddsInFlightMB", 384), 32, 4096) << 20;
            s_ddsVerify = settings.GetBool("ddsVerify", false);
            s_ddsUnityReader = settings.GetString("ddsReader", "threads").Trim().ToLowerInvariant() == "unity";
        }

        private static bool DirectAvailable()
        {
            if (!s_ddsDirect)
                return false;
            try
            {
                if (!s_commands.IsCreated)
                {
                    s_commands = new NativeArray<ReadCommand>(MaxDirect, Allocator.Persistent);
                    s_freeCommands.Clear();
                    for (int i = MaxDirect - 1; i >= 0; i--)
                        s_freeCommands.Push(i);
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Warn("FastLoader: direct DDS reads not available (" + e.Message + "), DDS read as before");
                return false;
            }
        }

        // Same checks as Build: KSP's DDSHeader, DXT1/DXT5 only, stock's mip and normal map flags.
        private static bool ParseDds(Item item, out DDSHeader header, out long dataOffset, out TextureFormat format, out bool mipChain, out bool isNormalMap)
        {
            header = null;
            dataOffset = 0;
            format = TextureFormat.DXT1;
            mipChain = isNormalMap = false;
            using (var stream = new MemoryStream(item.Buffer, 0, item.Length, false))
            using (var br = new BinaryReader(stream))
            {
                if (item.Length < 128 || br.ReadUInt32() != DDSValues.uintMagic)
                    return false;
                header = new DDSHeader(br);
                if (header.ddspf.dwFourCC == DDSValues.uintDX10)
                    return false;
                dataOffset = stream.Position;
            }
            if (header.ddspf.dwFourCC == DDSValues.uintDXT1) format = TextureFormat.DXT1;
            else if (header.ddspf.dwFourCC == DDSValues.uintDXT5) format = TextureFormat.DXT5;
            else return false;   // DXT2/3/4, DX10, uncompressed: stock logs its own message for these
            mipChain = (header.dwCaps & DDSPixelFormatCaps.MIPMAP) != 0;
            isNormalMap = (header.ddspf.dwFlags & 0x80000u) != 0 || (header.ddspf.dwFlags & 0x80000000u) != 0;
            return true;
        }

        // Main thread: the stock log line and the texture, its pixels not initialized. False: left to stock.
        private static bool BeginDirect(GameDatabase db, Item item, DirectQueue q)
        {
            if (!ParseDds(item, out DDSHeader header, out long dataOffset, out TextureFormat format, out bool mipChain, out bool isNormalMap))
                return false;
            // As stock's constructor: GetGraphicsFormat(format, !linear) with linear = false, mipCount -1 or 1.
            GraphicsFormat graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat(format, true);
            if (!SystemInfo.IsFormatSupported(graphicsFormat, FormatUsage.Sample))
                return false;
            string url = item.File.url;
            UnityEngine.Debug.Log("Load(Texture): " + url);
            if (s_progressTitle != null)
                s_progressTitle(db) = url;
            long t0 = Stopwatch.GetTimestamp();
            Texture2D texture;
            try
            {
                TextureCreationFlags flags = (mipChain ? TextureCreationFlags.MipChain : TextureCreationFlags.None) | DontInitializePixels;
                texture = new Texture2D((int)header.dwWidth, (int)header.dwHeight, graphicsFormat, mipChain ? -1 : 1, flags);
            }
            catch (Exception e)
            {
                Log.Warn("FastLoader: " + url + " failed (" + e.Message + "), stock loader will handle it");
                return false;
            }
            q.CreateTicks += Stopwatch.GetTimestamp() - t0;
            if (texture.format != format)
            {
                UnityEngine.Object.Destroy(texture);
                return false;
            }
            item.IsNormalMap = isNormalMap;
            item.KeepReadable = item.InfoReadable = false;
            item.InfoCompressed = true;
            Queue(item, texture, dataOffset, q);
            return true;
        }

        // Main thread, a PNG cache file (same checks as CreateCachedInfo): the stock log line and the texture. False: the
        // PNG is decoded again, as for a stale cache file.
        private static bool BeginDirectCached(GameDatabase db, Item item, DirectQueue q)
        {
            UrlDir.UrlFile file = item.File;
            if (!PngCache.TryParse(item.Buffer, item.Length, item.FileLength, file.url, item.SourceLength, item.SourceTicks, out PngCache.Header h, out int offset))
                return false;
            var format = (TextureFormat)h.Format;
            if (!Enum.IsDefined(typeof(TextureFormat), format) || GraphicsFormatUtility.IsCrunchFormat(format))
                return false;
            GraphicsFormat graphicsFormat = GraphicsFormatUtility.GetGraphicsFormat(format, true);
            if (!SystemInfo.IsFormatSupported(graphicsFormat, FormatUsage.Sample))
                return false;
            long t0 = Stopwatch.GetTimestamp();
            Texture2D texture;
            try
            {
                bool mipChain = h.MipCount > 1;
                TextureCreationFlags flags = (mipChain ? TextureCreationFlags.MipChain : TextureCreationFlags.None) | DontInitializePixels;
                texture = new Texture2D(h.Width, h.Height, graphicsFormat, mipChain ? -1 : 1, flags);
            }
            catch (Exception e)
            {
                Log.Debug("FastLoader: PNG cache of " + file.url + " not used (" + e.Message + ")");
                return false;
            }
            q.CreateTicks += Stopwatch.GetTimestamp() - t0;
            if (texture.mipmapCount != h.MipCount || texture.format != format)
            {
                UnityEngine.Object.Destroy(texture);
                return false;
            }
            UnityEngine.Debug.Log("Load(Texture): " + file.url);
            if (s_progressTitle != null)
                s_progressTitle(db) = file.url;
            item.IsNormalMap = (h.Flags & PngCache.FlagNormal) != 0;
            item.InfoReadable = (h.Flags & PngCache.FlagInfoReadable) != 0;
            item.InfoCompressed = (h.Flags & PngCache.FlagCompressed) != 0;
            item.KeepReadable = (h.Flags & PngCache.FlagTextureReadable) != 0;
            Queue(item, texture, offset, q);
            return true;
        }

        private static void Queue(Item item, Texture2D texture, long dataOffset, DirectQueue q)
        {
            item.Texture = texture;
            item.DataOffset = dataOffset;
            item.Frame = Time.frameCount;
            item.State = DirectState.Created;
            q.Items.Enqueue(item);
            q.Bytes += item.FileLength;
            q.Created++;
        }

        // Main thread: reads started for the textures old enough, finished textures applied and added in file order.
        private bool PumpDirect(GameDatabase db, DirectQueue q)
        {
            bool worked = false;
            if (q.Created > 0)
            {
                int frame = Time.frameCount;
                foreach (Item item in q.Items)
                {
                    if (item.State != DirectState.Created || frame < item.Frame + s_ddsFrameWait)
                        continue;
                    q.Created--;
                    StartDirectRead(item, q);
                    worked = true;
                }
            }
            while (q.Items.Count > 0)
            {
                Item head = q.Items.Peek();
                if (head.State == DirectState.Created)
                    break;
                if (head.State == DirectState.Reading)
                {
                    ReadStatus status;
                    if (head.Slot >= 0)
                        status = head.Handle.Status;
                    else
                    {
                        int pixelState = System.Threading.Volatile.Read(ref head.PixelState);
                        status = pixelState == 0 ? ReadStatus.InProgress : pixelState == 1 ? ReadStatus.Complete : ReadStatus.Failed;
                    }
                    if (status == ReadStatus.InProgress)
                        break;
                    FinishDirect(db, head, status, q);
                }
                else if (head.State == DirectState.Cached)
                {
                    if (BuildCached(db, head))
                    {
                        q.Loaded++;
                        q.LoadedBytes += head.Length;
                        q.CachedLoaded++;
                    }
                    else
                        q.RetryPng.Add(head.File);
                    Release(head);
                }
                else if (head.ReadPath != null)
                    q.RetryPng.Add(head.File);
                else
                    q.LeftToStock++;
                q.Items.Dequeue();
                q.Bytes -= head.FileLength;
                head.Texture = null;
                worked = true;
            }
            return worked;
        }

        private void StartDirectRead(Item item, DirectQueue q)
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                NativeArray<byte> pixels = item.Texture.GetRawTextureData<byte>();
                // Stock's LoadRawTextureData refuses a file shorter than the texture: left to stock (same error then).
                long available = item.FileLength - item.DataOffset;
                if (pixels.Length <= 0 || available < pixels.Length || (item.ReadPath != null && available != pixels.Length)
                    || (s_ddsUnityReader && s_freeCommands.Count == 0))
                {
                    Drop(item);
                    return;
                }
                if (s_ddsUnityReader)
                {
                    item.Slot = s_freeCommands.Pop();
                    item.Handle = Read(item.ReadPath ?? item.File.fullPath, pixels, item.DataOffset, item.Slot);
                }
                else
                {
                    item.Slot = -1;
                    item.Pixels = Pointer(pixels);
                    item.PixelLength = pixels.Length;
                    item.PixelState = 0;
                    QueuePixels(item);
                }
                item.State = DirectState.Reading;
            }
            catch (Exception e)
            {
                Log.Warn("FastLoader: " + item.File.url + " could not be read directly (" + e.Message + "), stock loader will handle it");
                Drop(item);
            }
            finally
            {
                q.PointerTicks += Stopwatch.GetTimestamp() - t0;
            }
        }

        private static unsafe ReadHandle Read(string path, NativeArray<byte> pixels, long offset, int slot)
        {
            ReadCommand* command = (ReadCommand*)s_commands.GetUnsafePtr() + slot;
            command->Buffer = pixels.GetUnsafePtr();
            command->Offset = offset;
            command->Size = pixels.Length;
            return AsyncReadManager.Read(path, command, 1);
        }

        private static unsafe IntPtr Pointer(NativeArray<byte> pixels) => (IntPtr)pixels.GetUnsafePtr();

        // ---------------------------------------------------------------- pixel threads (pure C#, no Unity API)
        // Several files read at once (one FileStream each), through a small buffer per thread copied into the texture.

        private readonly Queue<Item> _pixelJobs = new Queue<Item>();
        private int _pixelThreads;
        private bool _pixelStop;

        private void QueuePixels(Item item)
        {
            lock (_pixelJobs)
            {
                if (_pixelThreads == 0)
                {
                    _pixelStop = false;
                    int threads = s_readerThreads > 0 ? s_readerThreads : Math.Max(1, Math.Min(6, Environment.ProcessorCount / 2 - 1));
                    _pixelThreads = threads;
                    for (int t = 0; t < threads; t++)
                        new System.Threading.Thread(PixelReader) { IsBackground = true, Name = "Hyperdrive.FastLoader.Pixels" }.Start();
                }
                _pixelJobs.Enqueue(item);
                System.Threading.Monitor.Pulse(_pixelJobs);
            }
        }

        private void StopPixelThreads()
        {
            lock (_pixelJobs)
            {
                _pixelStop = true;
                System.Threading.Monitor.PulseAll(_pixelJobs);
            }
        }

        private void PixelReader()
        {
            var buffer = new byte[1 << 20];
            while (true)
            {
                Item item;
                lock (_pixelJobs)
                {
                    while (_pixelJobs.Count == 0 && !_pixelStop)
                        System.Threading.Monitor.Wait(_pixelJobs);
                    if (_pixelJobs.Count == 0)
                    {
                        _pixelThreads--;
                        return;
                    }
                    item = _pixelJobs.Dequeue();
                }
                int state = 1;
                try
                {
                    using (var fs = new FileStream(item.ReadPath ?? item.File.fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                    {
                        fs.Position = item.DataOffset;
                        long done = 0;
                        while (done < item.PixelLength)
                        {
                            int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, item.PixelLength - done));
                            if (n <= 0)
                                throw new EndOfStreamException();
                            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, new IntPtr(item.Pixels.ToInt64() + done), n);
                            done += n;
                        }
                    }
                }
                catch (Exception e)
                {
                    item.PixelError = e;
                    state = 2;
                }
                System.Threading.Volatile.Write(ref item.PixelState, state);
            }
        }

        private static void FinishDirect(GameDatabase db, Item item, ReadStatus status, DirectQueue q)
        {
            if (item.Slot >= 0)
            {
                item.Handle.Dispose();
                s_freeCommands.Push(item.Slot);
            }
            if (item.PixelError != null)
                Log.Warn("FastLoader: " + item.File.url + ": " + item.PixelError.Message);
            if (status != ReadStatus.Complete)
            {
                Log.Warn("FastLoader: " + item.File.url + " direct read " + status + ", loaded the usual way instead");
                Failed(item, q);
                return;
            }
            Texture2D texture = item.Texture;
            if (s_ddsVerify)
                VerifyDirect(item, q);
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                texture.Apply(false, !item.KeepReadable);
            }
            catch (Exception e)
            {
                Log.Warn("FastLoader: " + item.File.url + " failed (" + e.Message + "), loaded the usual way instead");
                Failed(item, q);
                return;
            }
            q.ApplyTicks += Stopwatch.GetTimestamp() - t0;
            string url = item.File.url;
            var info = new GameDatabase.TextureInfo(item.File, texture, item.IsNormalMap, item.InfoReadable, item.InfoCompressed);
            info.name = url;
            texture.name = url;
            db.databaseTexture.Add(info);
            q.Loaded++;
            q.LoadedBytes += item.FileLength;
            if (item.ReadPath != null)
                q.CachedLoaded++;
        }

        // A DDS goes to the stock loader, a cached PNG is decoded again (as before for a stale cache file).
        private static void Failed(Item item, DirectQueue q)
        {
            Drop(item);
            if (item.ReadPath != null)
                q.RetryPng.Add(item.File);
            else
                q.LeftToStock++;
        }

        private static void Drop(Item item)
        {
            if (item.Texture != null)
                UnityEngine.Object.Destroy(item.Texture);
            item.Texture = null;
            item.State = DirectState.Dropped;
        }

        // Developer check: the texture built the stock way from the whole file, compared with the direct one before Apply.
        private static void VerifyDirect(Item item, DirectQueue q)
        {
            string why = null;
            Texture2D reference = null;
            try
            {
                byte[] file = File.ReadAllBytes(item.ReadPath ?? item.File.fullPath);
                byte[] pixels;
                if (item.ReadPath == null)
                {
                    var whole = new Item { File = item.File, Buffer = file, Length = file.Length };
                    ParseDds(whole, out DDSHeader header, out long dataOffset, out TextureFormat format, out bool mipChain, out _);
                    reference = new Texture2D((int)header.dwWidth, (int)header.dwHeight, format, mipChain);
                    pixels = new byte[file.Length - dataOffset];
                    Array.Copy(file, dataOffset, pixels, 0, pixels.Length);
                }
                else
                {
                    PngCache.TryParse(file, file.Length, item.File.url, item.SourceLength, item.SourceTicks, out PngCache.Header h, out int offset);
                    reference = new Texture2D(h.Width, h.Height, (TextureFormat)h.Format, h.MipCount > 1);
                    pixels = new byte[h.DataLength];
                    Array.Copy(file, offset, pixels, 0, pixels.Length);
                }
                reference.LoadRawTextureData(pixels);
                Texture2D mine = item.Texture;
                if (mine.width != reference.width || mine.height != reference.height) why = "size";
                else if (mine.format != reference.format || mine.graphicsFormat != reference.graphicsFormat) why = "format " + mine.graphicsFormat + " vs " + reference.graphicsFormat;
                else if (mine.mipmapCount != reference.mipmapCount) why = "mips " + mine.mipmapCount + " vs " + reference.mipmapCount;
                else
                {
                    NativeArray<byte> a = mine.GetRawTextureData<byte>(), b = reference.GetRawTextureData<byte>();
                    if (a.Length != b.Length) why = "data length " + a.Length + " vs " + b.Length;
                    else
                        for (int i = 0; i < a.Length; i++)
                            if (a[i] != b[i]) { why = "byte " + i + " of " + a.Length; break; }
                }
            }
            catch (Exception e)
            {
                why = "exception " + e.Message;
            }
            if (reference != null)
                UnityEngine.Object.Destroy(reference);
            if (why == null) q.Same++;
            else
            {
                q.Different++;
                if (q.Details.Count < 10)
                    q.Details.Add(item.File.url + ": " + why);
            }
        }

        private static string DirectSummary(DirectQueue q)
        {
            double f = 1000.0 / Stopwatch.Frequency;
            string s = "FastLoader: direct path: " + (q.Loaded - q.CachedLoaded) + " DDS and " + q.CachedLoaded + " cached PNG read straight into their textures ("
                       + (s_ddsUnityReader ? "Unity's reader" : "reader threads") + "); main thread: create "
                       + (q.CreateTicks * f).ToString("F0") + " ms, buffer pointers " + (q.PointerTicks * f).ToString("F0") + " ms, Apply "
                       + (q.ApplyTicks * f).ToString("F0") + " ms";
            if (s_ddsVerify)
            {
                s += "; verify: " + q.Same + " identical, " + q.Different + " different";
                foreach (string d in q.Details)
                    s += "\n  " + d;
            }
            return s;
        }
    }
}
