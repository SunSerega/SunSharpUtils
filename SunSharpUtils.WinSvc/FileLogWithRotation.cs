using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

using SunSharpUtils.Threading;

namespace SunSharpUtils.WinSvc;

/// <summary>
/// </summary>
public sealed class FileLogWithRotation : IDisposable
{
    private readonly String file_path_without_ext;
    private readonly Int64 max_log_size;
    private readonly Int32 max_rotated_files_count;
    private readonly Int32 rotated_index_len;
    private readonly CancellationTokenSource cts_log_existence = new();

    /// <summary>
    /// </summary>
    /// <param name="rel_file_path"></param>
    /// <param name="max_log_size"></param>
    /// <param name="max_rotated_files_count"></param>
    public FileLogWithRotation(String rel_file_path, Int64 max_log_size = 50 * 1024 * 1024, Int32 max_rotated_files_count = 10)
    {
        var log_dir = "Logs";
        Directory.CreateDirectory(log_dir);
        this.file_path_without_ext = Path.GetFullPath(Path.Combine(log_dir, rel_file_path));
        this.max_log_size = max_log_size;
        this.max_rotated_files_count = max_rotated_files_count;
        this.rotated_index_len = max_rotated_files_count.ToString().Length;

        var thr = new Thread(this.WriteLoop)
        {
            IsBackground = true,
            Name = $"{nameof(FileLogWithRotation)} Thread: {this.file_path_without_ext}",
        };
        thr.Start();

    }

    /// <summary>
    /// Full file path of this log file without file extension
    /// </summary>
    public String FilePathWithoutExt => this.file_path_without_ext;
    /// <summary>
    /// Full file path of this log file
    /// </summary>
    public String FilePathWithExt => this.file_path_without_ext + ".log";

    private readonly ConcurrentQueue<LogMessage> msg_queue = [];
    private readonly ManualResetEventSlim wh_new_msg = new(initialState: false);
    private readonly ManualResetEventSlim wh_all_msg_written = new(initialState: false);

    private void WriteLoop()
    {
        var log_dir = "Logs";
        Directory.CreateDirectory(log_dir);
        var file_path_with_ext = this.file_path_without_ext + ".log";

        while (true)
            try
            {
                if (this.msg_queue.IsEmpty)
                {
                    this.wh_new_msg.Wait();
                    this.wh_new_msg.Reset();
                    continue;
                }

                ThreadingCommon.RunWithBackgroundReset(() =>
                {

                    Err.Handle(RotateLogsIfNeeded);
                    void RotateLogsIfNeeded()
                    {
                        var log_fi = new FileInfo(this.FilePathWithExt);
                        if (!log_fi.Exists || log_fi.Length <= this.max_log_size)
                            return;
                        String RotatedLogPath(Int32 index) =>
                            $"{this.file_path_without_ext}.{index.ToString().PadLeft(this.rotated_index_len, '0')}.zip";

                        var need_move_count = 0;
                        for (var i = 1; i <= this.max_rotated_files_count - 1; i++)
                        {
                            var src_path = RotatedLogPath(i);
                            if (!File.Exists(src_path))
                                break;
                            need_move_count = i;
                        }

                        if (need_move_count == this.max_rotated_files_count-1)
                        {
                            var last_rotated_path = RotatedLogPath(this.max_rotated_files_count);
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
                            var zip_entry = zip_archive.CreateEntry(Path.GetFileName(this.FilePathWithExt), CompressionLevel.Optimal);
                            using var entry_stream = zip_entry.Open();
                            using var log_file_stream = File.OpenRead(this.FilePathWithExt);
                            log_file_stream.CopyTo(entry_stream);
                        }
                        File.Delete(this.FilePathWithExt);

                    }

                    using var writer = new StreamWriter(this.FilePathWithExt, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    while (this.msg_queue.TryDequeue(out var msg))
                    {
                        if (msg.Text is null)
                        {
                            if (msg.Type is not MessageType.FLUSH)
                                throw new InvalidOperationException($"{msg.Type} message had null text");
                            continue;
                        }
                        if (msg.Type is MessageType.FLUSH)
                            throw new InvalidOperationException($"{MessageType.FLUSH} message had text: {msg.Text}");
                        var lines = msg.Text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
                        foreach (var line in lines)
                            writer.WriteLine($"[{msg.Time:yyyy-MM-dd HH:mm:ss}] [{msg.Type}] {line}");
                    }

                    this.wh_all_msg_written.Set();
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
        /// Don't send anything, just flush
        /// </summary>
        FLUSH,

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
    public void EnqueueMessage(MessageType msg_type, String? msg)
    {
        this.msg_queue.Enqueue(new(DateTime.Now, msg_type, msg));
        this.wh_new_msg.Set();
    }

    private record struct LogMessage(DateTime Time, MessageType Type, String? Text);

    /// <summary>
    /// Waits for all messages to be written to the file
    /// </summary>
    public void FlushAll()
    {
        this.wh_all_msg_written.Reset();
        this.EnqueueMessage(MessageType.FLUSH, null);
        this.wh_all_msg_written.Wait();
    }

    /// <summary>
    /// </summary>
    /// <returns></returns>
    public override String ToString() =>
        $"{nameof(FileLogWithRotation)}[{this.FilePathWithExt}]";

    /// <summary>
    /// </summary>
    public void Dispose()
    {
        this.cts_log_existence.Cancel();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// </summary>
    ~FileLogWithRotation()
    {
        if (this.cts_log_existence.IsCancellationRequested)
            return;
        Err.Handle($"{this} was not properly disposed");
    }

}
