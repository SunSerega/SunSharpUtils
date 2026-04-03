using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

using SunSharpUtils.Threading;

namespace SunSharpUtils.WinSvc;

/// <summary>
/// </summary>
public static class FileLog
{
    private static readonly String LogFileName = Process.GetCurrentProcess().ProcessName;
    private static readonly ConcurrentQueue<LogMessage> msg_queue = [];
    private static readonly ManualResetEventSlim wh_new_msg = new(initialState: false);
    private static readonly ManualResetEventSlim wh_all_msg_written = new(initialState: false);

    private const Int64 MaxLogSize = 50 * 1024 * 1024;
    private const Int32 MaxRotatedLogFiles = 10;

    static FileLog()
    {
        var thr = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = $"{nameof(FileLog)} Thread",
        };
        thr.Start();

    }

    private static void WriteLoop()
    {
        var log_dir = "Logs";
        Directory.CreateDirectory(log_dir);
        var log_path = Path.Combine(log_dir, LogFileName+".log");

        while (true)
            try
            {
                if (msg_queue.IsEmpty)
                {
                    wh_new_msg.Wait();
                    wh_new_msg.Reset();
                    continue;
                }

                ThreadingCommon.RunWithBackgroundReset(() =>
                {

                    Err.Handle(RotateLogsIfNeeded);
                    void RotateLogsIfNeeded()
                    {
                        var log_fi = new FileInfo(log_path);
                        if (!log_fi.Exists || log_fi.Length <= MaxLogSize)
                            return;
                        String RotatedLogPath(Int32 index) =>
                            Path.Combine(log_dir, $"{LogFileName}.{index:00}.zip");

                        var need_move_count = 0;
                        for (var i = 1; i <= MaxRotatedLogFiles - 1; i++)
                        {
                            var src_path = RotatedLogPath(i);
                            if (!File.Exists(src_path))
                                break;
                            need_move_count = i;
                        }

                        if (need_move_count == MaxRotatedLogFiles-1)
                        {
                            var last_rotated_path = RotatedLogPath(MaxRotatedLogFiles);
                            if (File.Exists(last_rotated_path))
                                File.Delete(last_rotated_path);
                        }

                        for (var i = need_move_count; i >= 1; i--)
                        {
                            var src_path = RotatedLogPath(i);
                            var dst_path = RotatedLogPath(i+1);
                            File.Move(src_path, dst_path, overwrite: false);
                        }

                        {
                            var dst_path = RotatedLogPath(1);
                            using var zip_stream = File.Create(dst_path);
                            using var zip_archive = new ZipArchive(zip_stream, ZipArchiveMode.Create);
                            var zip_entry = zip_archive.CreateEntry(Path.GetFileName(log_path), CompressionLevel.Optimal);
                            using var entry_stream = zip_entry.Open();
                            using var log_file_stream = File.OpenRead(log_path);
                            log_file_stream.CopyTo(entry_stream);
                        }
                        File.Delete(log_path);

                    }

                    using var writer = new StreamWriter(log_path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    while (msg_queue.TryDequeue(out var msg))
                    {
                        var lines = msg.Text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
                        foreach (var line in lines)
                            writer.WriteLine($"[{msg.Time:yyyy-MM-dd HH:mm:ss}] [{msg.Type}] {line}");
                    }

                    wh_all_msg_written.Set();
                }, new_is_background: false);
            }
            catch (Exception ex)
            {
                Err.Handle(ex);
            }
    }

    /// <summary>
    /// </summary>
    public enum MessageType
    {

        /// <summary>
        /// Message
        /// </summary>
        MESSG,

        /// <summary>
        /// Error
        /// </summary>
        ERROR,

    }

    /// <summary>
    /// </summary>
    public static void Log(MessageType msg_type, String msg)
    {
        msg_queue.Enqueue(new(DateTime.Now, msg_type, msg));
        wh_new_msg.Set();
    }

    private record struct LogMessage(DateTime Time, MessageType Type, String Text);

    /// <summary>
    /// Waits for all messages to be written to the file
    /// </summary>
    public static void FlushAll()
    {
        wh_all_msg_written.Reset();
        // Kind of junky, but this makes sure at least one message gets processed
        Log(MessageType.MESSG, $"Flushing logs...");
        wh_all_msg_written.Wait();
    }

}
