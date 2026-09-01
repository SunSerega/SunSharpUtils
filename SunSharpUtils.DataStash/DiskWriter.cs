using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using SunSharpUtils.Ext.Bin;
using SunSharpUtils.Ext.Linq;
using SunSharpUtils.Ext.UniversalBin;
using SunSharpUtils.Ids;
using SunSharpUtils.Logs;
using SunSharpUtils.Threading;
using SunSharpUtils.WinSvc;

namespace SunSharpUtils.DataStash;

#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
#pragma warning restore IDE0079 // Remove unnecessary suppression

internal abstract class DiskWriter
{
    protected readonly String file_ext;

    private static readonly String states_dir = Path.Combine("SaveData", "States");
    private static FileId current_file_id;
    private static readonly OneToManyLock l_current_file = new();

    private static readonly List<FileId> all_state_files;

    static DiskWriter()
    {
        all_state_files = Directory.EnumerateFiles(states_dir)
            .Select(Path.GetFileName)
            .Select(file_name =>
            {
                var spl = file_name!.Split('.', 2);
                if (spl.Length != 2)
                    throw new InvalidOperationException($"Invalid state file name: {file_name}");
                var file_id = FileId.Parse(spl[0]);
                return (file_id, ext: spl[1]);
            })
            .GroupBy(t => t.file_id, t => t.ext)
            .Select(g =>
            {
                if (g.Distinct().Count() != g.Count())
                    throw new InvalidOperationException($"Duplicate extensions for state file {g.Key}: {g.JoinToString(';')}");
                return g.Key;
            })
            .Order().ToList();
        var last_state_file_id = all_state_files.Cast<FileId?>().LastOrDefault();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var current_file_ind = 1;
        if (last_state_file_id?.Date >= today)
        {
            today = last_state_file_id.Value.Date;
            current_file_ind = last_state_file_id.Value.Index + 1;
        }
        current_file_id = new() { Date = today, Index = current_file_ind };
    }

    public DiskWriter(String file_ext)
    {
        if (!file_ext.StartsWith('.'))
            throw new ArgumentException($"File extension must start with a dot: {file_ext}", nameof(file_ext));
        this.file_ext = file_ext[1..];
    }

    private static void TryUpdateCurrentFile()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (current_file_id.Date < today)
            return;
        using var lock_scope = l_current_file.NewOneState();
        lock_scope.Begin(with_priority: false);
        if (current_file_id.Date < today)
            return;

        current_file_id = new() { Date = today, Index = 1 };
    }

    protected static String MakeFilePath(FileId file_id, String ext) => Path.Combine(states_dir, $"{file_id}.{ext}");

    protected static WriteResult EnqueueBlock(FileId? file_id, Func<FileId, BlockId> act)
    {
        TryUpdateCurrentFile();

        using var lock_scope = l_current_file.NewManyState();
        lock_scope.Begin();
        var res_file_id = file_id ?? current_file_id;

        var block_id = act.Invoke(res_file_id);

        return new()
        {
            FileId = res_file_id,
            BlockId = block_id,
        };
    }

    public readonly struct WriteResult
    {
        public required FileId FileId { get; init; }
        public required BlockId BlockId { get; init; }
    }

    public readonly record struct FileId : IEquatable<FileId>, IComparable<FileId>
    {
        public required DateOnly Date { get; init; }
        public required Int32 Index { get; init; }

        public static Int32 Compare(FileId f1, FileId f2)
        {
            var date_cmp = f1.Date.CompareTo(f2.Date);
            if (date_cmp != 0)
                return date_cmp;
            return f1.Index.CompareTo(f2.Index);
        }
        public Int32 CompareTo(FileId other) => Compare(this, other);

        public static FileId Parse(String str)
        {
            var parts = str.Split('_');
            if (parts.Length != 2)
                throw new FormatException($"Invalid file ID format: {str}");
            var date = DateOnly.ParseExact(parts[0], "yyyy-MM-dd");
            var index = Int32.Parse(parts[1]);
            return new() { Date = date, Index = index };
        }
        public override String ToString() => $"{this.Date:yyyy-MM-dd}_{this.Index:000}";

    }

    public readonly record struct BlockId(UInt64 value) : IAllocatableId<BlockId>
    {
        public UInt64 Value { get; } = value;
        public static BlockId Invalid => new(0);
        public static BlockId MinValue => new(1);
        public static BlockId MaxValue => new(UInt64.MaxValue);
        public static Boolean operator >(BlockId a, BlockId b) => a.Value > b.Value;
        public static Boolean operator <(BlockId a, BlockId b) => a.Value < b.Value;
        public static Int32 Compare(BlockId a, BlockId b) => a.Value.CompareTo(b.Value);
        public static BlockId operator +(BlockId a, IdOffsetOfOne b) => new(checked(a.Value + 1));
        public static BlockId operator -(BlockId a, IdOffsetOfOne b) => new(checked(a.Value - 1));
        public override String ToString() => $"{nameof(BlockId)}({this.Value})";
    }

}

internal interface IBlockFileState<TCommand>
    where TCommand : struct, Enum
{
    public void ReadBlock(BinaryReader br, DiskWriter.BlockId block_id, TCommand command);
}

internal sealed class DiskWriter<TCommand, TFileState>(String file_ext, Func<TFileState> new_file_state) : DiskWriter(file_ext)
    where TCommand : struct, Enum
    where TFileState : class, IBlockFileState<TCommand>
{
    private readonly Func<TFileState> new_file_state = new_file_state;

    //TODO Unload files that weren't used for a while
    private readonly ConcurrentDictionary<FileId, BlockFile> open_block_files = [];
    public WriteResult Write<TData>(FileId? file_id, TCommand command, TData data, Action<TFileState, BlockId> update_state) => EnqueueBlock(file_id, file_id =>
    {
        var block_file = this.open_block_files.GetOrAdd(file_id, file_id => new BlockFile(this, file_id));
        return block_file.EnqueueBlock(bw =>
        {
            bw.WriteEnum(command);
            bw.WriteData(data);
        }, update_state);
    });

    public override String ToString() =>
        $"{nameof(DiskWriter<,>)}<{typeof(TCommand).Name}, {typeof(TFileState).Name}>(.{this.file_ext})";

    private sealed class BlockFile
    {
        private readonly DiskWriter<TCommand, TFileState> disk_writer;
        private readonly FileId id;

        private InitResult init_res = default;
        private Boolean has_inited = false;
        private readonly Lock l_init = new();
        private readonly ProcessingQueue<Action> write_acts = [];

        public BlockFile(DiskWriter<TCommand, TFileState> disk_writer, FileId id)
        {
            this.disk_writer = disk_writer;
            this.id = id;

            this.write_acts.StartProcessingThread(new()
            {
                UsedFor = $"{this} write actions",
                OnNewItems = new_write_acts =>
                {
                    foreach (var act in new_write_acts)
                    {
                        try
                        {
                            act.Invoke();
                        }
                        catch (Exception ex)
                        {
                            WinSvcCommon.HandleCriticalError(ex, when_doing: $"writing to {this}");
                        }
                    }
                },
                CancelToken = CancellationToken.None,
            });
        }

        public BlockId EnqueueBlock(Action<BinaryWriter> act, Action<TFileState, BlockId> update_state)
        {
            var init_res = this.TryInit();

            //TODO AllocateId is currently just locked
            // - I can definitely make it lock-free, by adding parameter expecting the concurency degree
            var block_id = init_res.BlockIdAllocator.AllocateId();
            this.write_acts.Enqueue(() =>
            {
                var fs = init_res.Stream;
                var bw = init_res.Writer;
                var start_pos = fs.Position;
                bw.Write(block_id.Value);
                act.Invoke(bw);
                bw.Write(fs.Position - start_pos);
                fs.Flush();
                update_state.Invoke(init_res.FileState, block_id);
            });

            return block_id;
        }

        private InitResult TryInit()
        {
            if (this.has_inited)
                return this.init_res;
            using var lock_scope = this.l_init.EnterScope();
            if (this.has_inited)
                return this.init_res;
            GlobalLog.AddMessage($"Initializing {this}");

            var file_path = MakeFilePath(this.id, this.disk_writer.file_ext);
            var fs = new FileStream(file_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            var bw = new BinaryWriter(fs);
            var file_state = this.disk_writer.new_file_state.Invoke();

            var used_ids = new HashSet<BlockId>();
            if (fs.Length != 0)
            {
                GlobalLog.AddMessage($"{this} already has {fs.Length} bytes, reading existing blocks");
                var br = new BinaryReader(fs);
                while (true)
                {
                    var block_start_pos = fs.Position;
                    if (block_start_pos == fs.Length)
                        break;

                    try
                    {
                        var id = br.ReadData<BlockId>();
                        if (!used_ids.Add(id))
                            throw new InvalidOperationException($"Duplicate block ID {id} in {this}");
                        var command = br.ReadEnum<TCommand>();

                        file_state.ReadBlock(br, id, command);

                        var block_read_size = fs.Position - block_start_pos;
                        var block_written_size = br.ReadInt64();
                        if (block_read_size != block_written_size)
                            throw new InvalidOperationException($"Block size mismatch: read {block_read_size} bytes, expected {block_written_size} bytes");
                    }
                    catch (EndOfStreamException)
                    {
                        fs.Position = block_start_pos;
                        break;
                    }
                    catch (Exception ex)
                    {
                        WinSvcCommon.HandleCriticalError(ex, when_doing: $"reading existing blocks from {this}");
                    }
                }
                if (fs.Position != fs.Length)
                {
                    //TODO This can still happen if the reader is broken and e.g. tries to read a string too long
                    // - Do a backup in this case
                    GlobalLog.AddError($"Trimming {this} from {fs.Length} to {fs.Position} bytes due to unfinished block at the end of the file");
                    fs.SetLength(fs.Position);
                    fs.Flush();
                }
            }

            this.init_res = new()
            {
                BlockIdAllocator = new(used_ids),
                Stream = fs,
                Writer = bw,
                FileState = file_state,
            };
            this.has_inited = true;
            return this.init_res;
        }

        public override String ToString() => $"{this.disk_writer} {this.id}";

        private readonly struct InitResult
        {
            public required IdAllocator<BlockId> BlockIdAllocator { get; init; }
            public required FileStream Stream { get; init; }
            public required BinaryWriter Writer { get; init; }
            public required TFileState FileState { get; init; }
        }

    }

}
