using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using SunSharpUtils.Ext.Bin;
using SunSharpUtils.Ext.Exceptions;
using SunSharpUtils.Ext.Linq;
using SunSharpUtils.Ext.Math;
using SunSharpUtils.Ids;
using SunSharpUtils.Threading;
using SunSharpUtils.UniversalBin;
using SunSharpUtils.WinSvc;

//TODO Maybe change the namespace using compiler directives?
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
#pragma warning disable CS0436 // Type conflicts with imported type
#pragma warning restore IDE0079 // Remove unnecessary suppression

namespace SunSharpUtils.DataStash;

//TODO Things left until initial version:
// - Reading data (including both pending and sealed files) per client request
// - Filling in data from an older format (to upgrade VRCT to use DataStash)
// - Split this file into multiple?

// ===

//TODO A common pattern to be made convenient:
// - Some data that is part of typed models is dupped, so it should be stored in the file as a separate block (assigning value to key)
// - Multiple values for the same key can be in the same file, when value is updated. Solved when reading by looking at timings
// - Redundant updates is the main thing that is optimized by consolidation. Otherwise consolidation doesn't really make sense

//TODO Backup files during each consolidation merge
// - Keep last N merges saved, delete older ones

//TODO How do I version the generated EBlockKind???
// - I need to somehow hold the memory of all block kinds from prev generations, so that values don't shift
// - In the first place, I need to decide what to do with blocks that don't exist anymore
// - I think I want to first find the use case
// - After implementing [AbstractData], I think I just need something similar with attributes referencing old version of the TypedContent
// - And then I should also add a custom file header part

/// <summary>
/// Marks data stash for auto-generation of implementation boilerplate
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AutoDataStashAttribute : Attribute;

/// <summary>
/// Configuration for how the files in data stash should be merged over time to optimize disk space usage
/// </summary>
/// <param name="file_time_target_exp_base">Base of the target exponential growth for merged file sizes. Must be between 1 and 2 for reasonable results</param>
public readonly struct DataStashConsolidationConfig(Double file_time_target_exp_base)
{
    /// <summary>
    /// </summary>
    public Double FileTimeTargetExpBase { get; } = file_time_target_exp_base;
    /// <summary>
    /// </summary>
    public Double FileTimeEffectiveExpBase { get; } = Math.Log(newBase: 4, a: file_time_target_exp_base) + 1;

    // > B' = FileTimeTargetExpBase
    // > B = FileTimeEffectiveExpBase
    //
    // We merge a file into its older neighbor if the distance between them is less 1 in logarithmic space of base B
    // This results in the list of files being divided into sections
    // Each next (older) section has files spanning exactly 2x longer recorded time
    // Each section maintains roughly the same number of files, so average section size is also constant
    // - In practice, the yongest section tends to be longer by 2-3 files because file's span cannot go below 1*gen_period
    // - But that doesn't affect B<->B' relation, and overall is a good trait in this use case, preserving original state for a bit longer
    // Each merge turns 2 files from the end of a section into 1 file at the start of the next section
    // (conjecture: this is the best merge-only heuristic for log-distribution of files)
    //
    // Let's say, right after merge, a file is added to section of files that span [s] time, file takes up time span [t1 .. t1+s]
    // Since it was just merged, the distance in log-space in now exactly 1
    // > log_B( (t1+s) / t1 ) = 1
    // > (t1+s) / t1 = B
    // > t1 = s / (B-1)
    // The previous section's average merge time is [t0 = t1/2], since each next section is 2x the time span on average
    // And so each section should also span [r = t1/2 = s / (2*(B-1))]
    // Each section would have [n = r/s = 1/(2*(B-1))] files on average
    //
    // All [c] sections add up to total time of the whole data stash [t]
    // But they also form a geometric series
    // > r0 * (1 + 2 + 4 + ... + 2^(c-1)) = t
    // > r0 * (2^c - 1) = t
    // > c = log_2(t/r0 + 1)
    // Assuming the files are generated with a period of 1
    // > c = log_2(t/n + 1)
    // And so the total number of kept files [N] is:
    // > N = c * n
    // > N = log_2(t/n + 1) * n
    // > N/n = log_2(t/n + 1)
    // > 2^(N/n) = t/n + 1
    // > 2^(N/n) - 1 = t/n
    // > t = n * (2^(N/n) - 1)
    // > t = n * (2^(N*2*(B-1)) - 1)
    // > t = n * (4^(N*(B-1)) - 1)
    // At the limit of [N -> ∞], this approaches
    // > t ~= (4^(B-1)) ^ N
    // > t ~= B' ^ N
    // > B' = 4^(B-1)
    // > B = log_4(B') + 1
    // In practice, even at low [N], [t ~= B' ^ N] holds very well

    /// <summary>
    /// B' = 1.3
    /// </summary>
    public static DataStashConsolidationConfig Default { get; } = new(file_time_target_exp_base: 1.3);

    private static Double ExpectedMergeTime(Double B, Double t1, Double t2)
    {
        // We merge at time [t] when the distance in log-space is 1
        // > LogN(B, (t-t1)/(t-t2)) = 1
        //
        // > (t-t1)/(t-t2) = B
        // > t-t1 = B*(t-t2)
        // > t-B*t = t1-B*t2
        // > t = (t1-B*t2)/(1-B)

        return (t1 - B * t2) / (1 - B);
    }
    /// <summary>
    /// Returns time at which file created at t2 should be merged into the file created at t1
    /// </summary>
    /// <param name="t1"></param>
    /// <param name="t2"></param>
    /// <returns></returns>
    public Double ExpectedMergeTime(Double t1, Double t2) =>
        ExpectedMergeTime(this.FileTimeEffectiveExpBase, t1, t2);

    /// <summary>
    /// Runs a simulation of the file distribution over time, logging the results to the provided report function (or Prompt.Notify if null)
    /// </summary>
    /// <param name="step_count"></param>
    /// <param name="report"></param>
    public void Simulate(Int32 step_count, Action<String>? report = null)
    {
        report ??= s => Prompt.Notify($"[{nameof(DataStashConsolidationConfig)}] {s}");
        report($"Started simulation");

        var sw = Stopwatch.StartNew();
        var B = this.FileTimeEffectiveExpBase;

        var next_spawn_time = 1;
        var files = new List<Int32>(); // spawn time of each file

        var file_count_to_min_time = new List<Int32> { 0 };
        var file_count_to_max_time = new List<Double> { 0 };

        while (next_spawn_time <= step_count)
        {
            var next_merge = FindMergeTimes().MinBy(m => m?.merge_time);
            IEnumerable<(Int32 index, Double merge_time)?> FindMergeTimes()
            {
                for (var i = 1; i < files.Count; ++i)
                {
                    var t1 = files[i - 1];
                    var t2 = files[i - 0];
                    var merge_time = ExpectedMergeTime(B, t1, t2);
                    yield return (i, merge_time);
                }
            }

            if (next_merge is { index: var index, merge_time: var merge_time } && merge_time <= next_spawn_time)
            {
                file_count_to_max_time[files.Count] = merge_time;
                files.RemoveAt(index);
            }
            else
            {
                files.Add(next_spawn_time);
                if (file_count_to_min_time.Count == files.Count)
                {
                    file_count_to_min_time.Add(next_spawn_time);
                    file_count_to_max_time.Add(next_spawn_time);
                }
                else
                    file_count_to_max_time[files.Count] = next_spawn_time;
                next_spawn_time += 1;
            }

        }
        file_count_to_max_time[files.Count] = step_count;
        sw.Stop();

        report($"Finished simulation of {step_count} steps in {sw.Elapsed}");
        report($"Effective B = {B}");
        report($"Target    B = {this.FileTimeTargetExpBase}");
        report($"Simulated B = {Math.Pow(step_count, 1.0d / files.Count)}");
        report($"===");
        report($"Final {files.Count} files spawn times: {files.JoinToString()}");
        report($"===");
        report($"Expected average file count in each section: {1/(2*(B-1))}");
        report($"Final sections: {files.Pairwise((t1, t2) => t2-t1).AdjacentGroup().Select(g => $"{g.count}x{g.item}").JoinToString()}");
        report($"===");
        report($"File counts were seen at these times (log-time):");
        var N_max_str_len = (file_count_to_min_time.Count-1).ToString().Length;
        for (var N = 1; N < file_count_to_min_time.Count; ++N)
        {
            var time_graph_len = 100;

            var pos_min = TimeToGraphPos(file_count_to_min_time[N]);
            var pos_max = TimeToGraphPos(file_count_to_max_time[N]);

            var time_graph = String.Create(time_graph_len, 0, (span, _) =>
            {
                span.Fill(' ');
                span.Slice(pos_min, pos_max-pos_min+1).Fill('#');
            });

            report($"- {N.ToString().PadLeft(N_max_str_len)} files | {time_graph} | {file_count_to_min_time[N]} .. {file_count_to_max_time[N]}");

            Int32 TimeToGraphPos(Double time) =>
                (Int32)Math.Round(Math.Log(time+1, 2) * (time_graph_len-1) / Math.Log(step_count+1, 2));
        }

    }

}

/// <summary>
/// A forever-store containing a binary log of events
/// <para/>
/// Does not support branching, can only store linear history of events
/// <para/>
/// History is stored in multiple files, old files are describing exponentially bigger time spans
/// (through continuous consolidation)
/// </summary>
/// <typeparam name="TDataStash"></typeparam>
/// <typeparam name="TTypedContent"></typeparam>
public abstract class DataStash<TDataStash, TTypedContent>
    where TDataStash : DataStash<TDataStash, TTypedContent>
    where TTypedContent : class, DataStash<TDataStash, TTypedContent>.ITypedContent<TTypedContent>, new()
{
    private readonly DirectoryInfo root_dir;
    private readonly CancellationToken svc_stop_token;
    private readonly Boolean log_all_added;

    private readonly DirectoryInfo pending_dir;

    private static readonly String file_ext = ".bin";

    private readonly OneToManyLock l_all_sealed_state_files = new();
    private readonly List<FileId> all_sealed_state_files;

    private readonly OneToManyLock l_all_pending_state_files = new();
    private readonly ConcurrentDictionary<FileId, PendingFileGroup> all_pending_state_files = [];

    private readonly PendingSealer pending_sealer;
    private readonly SealedConsolidator sealed_consolidator;

    /// <summary>
    /// </summary>
    /// <param name="data_dir">A directory to store all the data. Must not have any other files</param>
    /// <param name="svc_stop_token"></param>
    /// <param name="on_content_inited"></param>
    /// <param name="log_all_added"></param>
    /// <param name="consolidation_config"></param>
    protected DataStash(String data_dir, CancellationToken svc_stop_token, Action<TTypedContent>? on_content_inited = null, Boolean log_all_added = false, DataStashConsolidationConfig? consolidation_config = null)
    {
        Prompt.Notify($"Initializing {this} in: {data_dir}");
        this.root_dir = Directory.CreateDirectory(data_dir);
        this.svc_stop_token = svc_stop_token;
        this.log_all_added = log_all_added;

        this.pending_dir = this.root_dir.CreateSubdirectory("Pending");

        this.all_sealed_state_files = this.root_dir.EnumerateFiles()
            .Select(fi =>
            {
                var file_name = fi.Name;
                if (fi.Extension != file_ext)
                    throw new InvalidOperationException($"Invalid extension of state file: {file_name}");
                file_name = Path.GetFileNameWithoutExtension(file_name);
                var file_id = FileId.Parse(Path.GetFileNameWithoutExtension(file_name));
                return file_id;
            })
            .Order().ToList();
        foreach (var file_id in this.all_sealed_state_files)
        {
            Prompt.Notify($"Validating sealed state file {file_id}");
            var file_path = Path.Combine(this.root_dir.FullName, $"{file_id}{file_ext}");
            using var fs = File.OpenRead(file_path);
            var content = this.ReadSealedFileContent(file_id);
            on_content_inited?.Invoke(content);
        }
        Prompt.Notify($"Validated {this.all_sealed_state_files.Count} sealed state files");

        foreach (var sub_dir in this.pending_dir.EnumerateDirectories())
        {
            var pending_file_group = new PendingFileGroup(this, sub_dir, expect_existing_content: true, svc_stop_token);
            if (this.all_sealed_state_files.Contains(pending_file_group.Id))
                throw new InvalidOperationException($"Pending state {pending_file_group.Id} conflicts with a sealed file with the same id");
            if (this.all_pending_state_files.ContainsKey(pending_file_group.Id))
                throw new InvalidOperationException($"Pending state {pending_file_group.Id} exists multiple times");
            on_content_inited?.Invoke(pending_file_group.Content);
            this.all_pending_state_files[pending_file_group.Id] = pending_file_group;
        }

        this.pending_sealer = new PendingSealer(this);
        this.pending_sealer.Start(svc_stop_token);

        this.sealed_consolidator = new SealedConsolidator(this, consolidation_config ?? DataStashConsolidationConfig.Default);
        this.sealed_consolidator.Start(svc_stop_token);

        Prompt.Notify($"Initialized {nameof(DataStash<,>)} ({this.GetType().Name})");
    }

    #region ReadFile

    private TTypedContent ReadSealedFileContent(FileId file_id)
    {
        var file_path = Path.Combine(this.root_dir.FullName, $"{file_id}{file_ext}");
        using var fs = File.OpenRead(file_path);
        return ReadSealedFileContent($"{file_id}", file_id, fs);
    }

    private static TTypedContent ReadSealedFileContent(String description, FileId file_id, FileStream fs)
    {
        var content = new TTypedContent();
        foreach (var (common_info, br) in ReadFileBlocks(description, fs, trim_corrupted: false, location_factory: id => new SealedBlockLocation(file_id, id), block_open_status_consumers: null))
            content.ApplyBlock(common_info, new(fs.Position, common_info.Location, br));
        return content;
    }

    private void ReadAppendSealedFileContent(FileId file_id, TTypedContent content)
    {
        var file_path = Path.Combine(this.root_dir.FullName, $"{file_id}{file_ext}");
        using var fs = File.OpenRead(file_path);
        foreach (var (common_info, br) in ReadFileBlocks($"{file_id}", fs, trim_corrupted: false, location_factory: id => new SealedBlockLocation(file_id, id), block_open_status_consumers: null))
            content.ApplyBlock(common_info, new(fs.Position, common_info.Location, br));
    }

    private static IEnumerable<(CommonTypedModelInfo common_info, BinaryReader block_br)> ReadFileBlocks<TLocation>(
        String description, Stream stream, Boolean trim_corrupted, Func<BlockId, TLocation> location_factory, (Action<TLocation> on_open, Action<TLocation> on_close)? block_open_status_consumers)
        where TLocation : BlockLocation
    {
        var br = new BinaryReader(stream);

        var header = br.ReadData<FileHeader>();
        if (header.MagicNumber != FileHeader.ExpectedMagicNumber)
            throw new InvalidDataException($"File {description} corrupted: Invalid magic number in header: {header.MagicNumber:X8} != {FileHeader.ExpectedMagicNumber:X8}");

        var last_valid_stream_pos = trim_corrupted ? stream.Position : 0;

        //TODO Creating a new MemoryStream for each read op is very inefficient
        // - What if I create something like a child stream?
        // - When I do, actually test the performance (in lab setting and in terms of DataStash init times)

        Boolean first_read_in_block = false;
        var buffer = new Byte[1024];
        var finished_reading = false;
        Boolean TryReadRaw(String read_description, Int32 len)
        {
            if (len > buffer.Length)
                buffer = new Byte[len.ClampBottom(buffer.Length*2)];
            var read_len = br.Read(buffer.AsSpan()[..len]);
            if (read_len != len)
            {
                if (read_len != 0 || !first_read_in_block) // If not EOF at the block boundary
                    Prompt.Notify($"File {description} corrupted in non-critical way: Only found {read_len}/{len} bytes for {read_description}");
                finished_reading = true;
                return false;
            }
            first_read_in_block = false;
            return true;
        }
        Boolean TryRead<T>(String read_description, Int32 len, Func<BinaryReader, T> read_act, [NotNullWhen(true)] out T? result) where T : notnull
        {
            if (!TryReadRaw(read_description, len))
            {
                result = default;
                return false;
            }
            var stream = new MemoryStream(buffer, 0, len, writable: false);
            var child_br = new BinaryReader(stream);
            result = read_act.Invoke(child_br);
            return true;
        }

        using var trimmer = trim_corrupted ? new LambdaDisposable(() =>
        {
            if (!finished_reading)
                return;
            if (stream.Length == last_valid_stream_pos)
                return;
            Prompt.Notify($"File {description} corrupted in non-critical way: Trimming file bytes {stream.Length} => {last_valid_stream_pos}");
            stream.SetLength(last_valid_stream_pos);
        }) : null;

        while (true)
        {
            first_read_in_block = true;
            if (block_open_status_consumers is { on_close: var on_close })
            {
                if (!TryRead("kind of the next block", sizeof(EBlockKind), br => br.ReadEnum<EBlockKind>(), out var kind))
                    yield break;
                switch (kind)
                {
                    case EBlockKind.Data:
                        break; // Just continue reading
                    case EBlockKind.Close:
                        if (!TryRead("id of the block being closed", Marshal.SizeOf(BlockId.Invalid.Value), br => br.ReadData<BlockId>(), out var closed_block_id))
                            yield break;
                        on_close.Invoke(location_factory.Invoke(closed_block_id));
                        continue;
                    default:
                        throw new InvalidDataException($"File {description} corrupted: Invalid block kind: {kind}");
                }
            }
            if (!TryRead("length of the next block", sizeof(Int32), br => br.ReadInt32(), out var len))
                yield break;
            if (len is -1)
            {
                Prompt.Notify($"File {description} corrupted in non-critical way: The last block didn't finish writing");
                yield break;
            }
            len -= sizeof(Int32);
            if (!TryReadRaw("the next block", len))
                yield break;
            var block_stream = new MemoryStream(buffer, 0, len, writable: false);
            var block_br = new BinaryReader(block_stream);
            var record_time = block_br.ReadData<DateTime>();
            var block_id = block_br.ReadData<BlockId>();
            var location = location_factory.Invoke(block_id);
            if (block_open_status_consumers is { on_open: var on_open })
            {
                var hold_open = block_br.ReadBoolean();
                if (hold_open)
                    on_open.Invoke(location);
            }
            var common_info = new CommonTypedModelInfo { RecordTime = record_time, Location = location };
            yield return (common_info, block_br);
            if (block_stream.Position != len)
                throw new InvalidOperationException($"File {description} corrupted: Block {block_id} was not fully read: {block_stream.Position}/{len}");
            if (trim_corrupted)
                last_valid_stream_pos = stream.Position;
        }

    }

    #endregion

    /// <summary>
    /// </summary>
    protected T UseNewWriteLocation<T>(Func<BlockLocation, T> use) => this.l_all_pending_state_files.ManyLocked(() =>
    {
        var file_id = FileId.Current;
        var pending_file_group = this.all_pending_state_files.GetOrAdd(file_id, id => new PendingFileGroup(this, this.pending_dir.CreateSubdirectory(id.ToString()), expect_existing_content: false, this.svc_stop_token));
        var location = pending_file_group.ChooseWriteLocation();
        return use.Invoke(location);
    });

    #region PendingCollect

    /// <summary>
    /// </summary>
    protected delegate Boolean PendingCollectCallback<T>(TTypedContent content, [MaybeNullWhen(false)] out T result);

    /// <summary>
    /// </summary>
    protected T PendingCollectOne<T>(String collect_op_description, PendingCollectCallback<T> try_get, Func<T>? on_not_found = null)
    {
        var results = this.PendingCollect(try_get);
        on_not_found ??= () =>
            throw new InvalidOperationException($"{collect_op_description}: Value of type {typeof(T)} not found in pending files");
        if (results.Length == 0)
            return on_not_found.Invoke();
        if (results.Length > 1)
            throw new InvalidOperationException($"{collect_op_description}: Value of type {typeof(T)} found in multiple pending files: {results.Select(r => r.file_id).JoinToString("; ")}");
        return results[0].item;
    }

    /// <summary>
    /// </summary>
    protected Dictionary<TKey, (IFileId file_id, TValue value)> PendingCollectAndOrganize<TKey, TValue>(PendingCollectCallback<TValue[]> try_get, Func<TValue, TKey> get_key)
        where TKey : notnull
    {
        var collected_items = this.PendingCollect(try_get);
        var dict = new Dictionary<TKey, (IFileId file_id, TValue value)>();
        foreach (var (file_id, values) in collected_items)
        {
            foreach (var value in values)
            {
                var key = get_key(value);
                if (!dict.TryAdd(key, (file_id, value)))
                    throw new InvalidOperationException($"Duplicate key [{key}] when organizing collected {typeof(TValue)} items. Found in {dict[key].file_id} and {file_id}");
            }
        }
        return dict;
    }
    /// <summary>
    /// </summary>
    protected (IFileId file_id, T item)[] PendingCollect<T>(PendingCollectCallback<T> try_get) => this.l_all_pending_state_files.ManyLocked(() =>
    {
        var results = new List<(IFileId file_id, T ret)>(this.all_pending_state_files.Count);
        foreach (var pending_file_group in this.all_pending_state_files.Values)
        {
            if (try_get.Invoke(pending_file_group.Content, out var result))
                results.Add((pending_file_group.Id, result));
        }
        return results.ToArray();
    });

    #endregion

    internal void TriggerPendingSealer() => this.pending_sealer.Recheck();
    internal void TriggerSealedConsolidator() => this.sealed_consolidator.RecomputeNextMergeTime();

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(DataStash<,>)} ({this.GetType().Name})";

    /// <summary>
    /// </summary>
    public readonly struct CommonTypedModelInfo
    {
        /// <summary>
        /// </summary>
        public required DateTime RecordTime { get; init; }
        /// <summary>
        /// </summary>
        public required BlockLocation Location { get; init; }
    }

    #region ITypedContent

    /// <summary>
    /// </summary>
    public interface ITypedModel<TFileData>
    {
        /// <summary>
        /// </summary>
        public TFileData ConvertToFileData();
    }

    /// <summary>
    /// Implement by typed file content type to convert all of its content back into file blocks
    /// </summary>
    /// <typeparam name="TSelf"></typeparam>
    public interface ITypedContent<TSelf>
        where TSelf : class, ITypedContent<TSelf>, new()
    {

        /// <summary>
        /// Should be implemented by code-generation with <see cref="AutoDataStashAttribute"/>
        /// </summary>
        public abstract void ApplyBlock(CommonTypedModelInfo common_info, ReadContext context);

        /// <summary>
        /// Should be implemented by code-generation with <see cref="AutoDataStashAttribute"/>
        /// </summary>
        /// <param name="location"></param>
        public abstract void CloseModel(BlockLocation location);

        /// <summary>
        /// Called when sealing is skipped due to open blocks
        /// <para/>
        /// Should be implemented by code-generation with <see cref="AutoDataStashAttribute"/>
        /// </summary>
        /// <param name="file_group_description"></param>
        /// <param name="block_locations"></param>
        public abstract void LogSealHeldByBlocks(String file_group_description, BlockLocation[] block_locations);

        internal void Resave(Stream stream) =>
            this.Resave(new ResaveContext(stream));
        /// <summary>
        /// Should be implemented by code-generation with <see cref="AutoDataStashAttribute"/>
        /// </summary>
        public abstract void Resave(ResaveContext context);

        /// <summary>
        /// Called after merging (when sealing or consolidating) to verify that content after save and load is the same as before
        /// </summary>
        /// <param name="content1"></param>
        /// <param name="content2"></param>
        public static abstract void ValidateEqual(TSelf content1, TSelf content2);

        /// <summary>
        /// Throws if dictionaries are not equal. Supposed to be called from <see cref="ValidateEqual(TSelf, TSelf)"/>
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="path_description"></param>
        /// <param name="d1"></param>
        /// <param name="d2"></param>
        /// <param name="validate_value"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public static void ValidateDictEqual<TKey, TValue>(String path_description, Dictionary<TKey, TValue> d1, Dictionary<TKey, TValue> d2, Action<String, TValue, TValue> validate_value)
            where TKey : notnull
        {
            if (d1.Keys.Except(d2.Keys).ToArray() is { Length: not 0 } extra_keys1)
                throw new InvalidOperationException($"{path_description}: Keys only in first dict: {extra_keys1.JoinToString("; ")}");
            if (d2.Keys.Except(d1.Keys).ToArray() is { Length: not 0 } extra_keys2)
                throw new InvalidOperationException($"{path_description}: Keys only in second dict: {extra_keys2.JoinToString("; ")}");
            foreach (var key in d1.Keys)
            {
                var item1 = d1[key];
                var item2 = d2[key];
                validate_value.Invoke($"{path_description} => {item1}", item1, item2);
            }
        }
    }

    /// <summary>
    /// <inheritdoc cref="ITypedContent{TSelf}"/>
    /// </summary>
    /// <typeparam name="TSelf"></typeparam>
    /// <typeparam name="TResaveContext"></typeparam>
    public interface ITypedContent<TSelf, TResaveContext> : ITypedContent<TSelf>
        where TSelf : class, ITypedContent<TSelf, TResaveContext>, new()
    {
        /// <summary>
        /// Calls different overloads of context.AddBlock for each object stored in this typed content
        /// <para/>
        /// This is used at the end of merging multiple files into one, to generate the new (merged) file
        /// </summary>
        /// <param name="context"></param>
        public void Resave(TResaveContext context);
    }

    /// <summary>
    /// </summary>
    public interface ITypedContentWithBlock<TNetworkData, TFileData>
        where TFileData : struct
    {
        /// <summary>
        /// Turns network data into file data
        /// <para/>
        /// The result will be written to file and immediately passed to ReadBlock
        /// </summary>
        /// <param name="data_stash"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public static abstract TFileData ParseNetworkPacket(TDataStash data_stash, TNetworkData data);
    }

    /// <summary>
    /// Implement by typed file content type to add block type with no parent relationship
    /// </summary>
    /// <typeparam name="TNetworkData"></typeparam>
    /// <typeparam name="TFileData"></typeparam>
    /// <typeparam name="TModel"></typeparam>
    public interface ITypedContentWithRootBlock<TNetworkData, TFileData, TModel> : ITypedContentWithBlock<TNetworkData, TFileData>
        where TFileData : struct
        where TModel : class, ITypedModel<TFileData>
    {
        /// <summary>
        /// Adds block's content from file to this instance, and returns newly created representation of this block
        /// </summary>
        /// <param name="common_info"></param>
        /// <param name="content"></param>
        /// <returns></returns>
        public TModel ReadBlock(CommonTypedModelInfo common_info, TFileData content);
    }
    /// <summary>
    /// Implement by typed file content type to add block type with a parent
    /// <para/>
    /// This block being a child doesn't stop another block from being child of this block
    /// </summary>
    /// <typeparam name="TNetworkData"></typeparam>
    /// <typeparam name="TFileData"></typeparam>
    /// <typeparam name="TParentModel"></typeparam>
    /// <typeparam name="TModel"></typeparam>
    public interface ITypedContentWithChildBlock<TNetworkData, TFileData, TParentModel, TModel> : ITypedContentWithBlock<TNetworkData, TFileData>
        where TFileData : struct
        where TParentModel : class?
        where TModel : class, ITypedModel<TFileData>
    {
        /// <summary>
        /// </summary>
        public Boolean TryGetModelByLocation(BlockLocation location, [MaybeNullWhen(false)] out TParentModel model);
        /// <summary>
        /// </summary>
        public Boolean TryGetParent(TNetworkData data, [MaybeNullWhen(false)] out TParentModel result);
        /// <inheritdoc cref="ITypedContentWithRootBlock{TNetworkData, TFileData, TTyped}.ReadBlock"/>
        public TModel ReadBlock(CommonTypedModelInfo common_info, TParentModel parent, TFileData content);
    }

    /// <summary>
    /// Implemented by typed file content type to mark a model as openable (open when created, closed through an explicit call)
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TModel"></typeparam>
    public interface ITypedContentWithCloseableBlock<TKey, TModel>
        where TKey : IEquatable<TKey>
        where TModel : class
    {
        /// <summary>
        /// </summary>
        public abstract Boolean TryGetModelByLocation(BlockLocation location, [MaybeNullWhen(false)] out TModel result);
        /// <summary>
        /// </summary>
        public abstract Boolean TryGetModelByKey(TKey key, [MaybeNullWhen(false)] out TModel result);
        /// <summary>
        /// </summary>
        public abstract Boolean CollectAllOpenModels(out TModel[] results);
        /// <summary>
        /// </summary>
        public static abstract TKey GetModelKey(TModel model);
    }

    #endregion

    #region FileId

    /// <summary>
    /// </summary>
    public interface IFileId : IEquatable<IFileId>
    {
        /// <summary>
        /// </summary>
        public Boolean TryUsePendingContent(DataStash<TDataStash, TTypedContent> data_stash, Action<TTypedContent> act);
    }

    private readonly record struct FileId : IFileId, IEquatable<FileId>, IComparable<FileId>
    {
        public required DateOnly Date { get; init; }
        public required Int32 Hour { get; init; }

        public Boolean TryUsePendingContent(DataStash<TDataStash, TTypedContent> data_stash, Action<TTypedContent> act)
        {
            var file_id = this;
            return data_stash.l_all_pending_state_files.ManyLocked(() =>
            {
                if (!data_stash.all_pending_state_files.TryGetValue(file_id, out var pending_file_group))
                    return false;
                act.Invoke(pending_file_group.Content);
                return true;
            });
        }

        public Boolean Equals(IFileId? other) =>
            other is FileId other_id && this.Equals(other_id);

        public static Int32 Compare(FileId f1, FileId f2)
        {
            var date_cmp = f1.Date.CompareTo(f2.Date);
            if (date_cmp != 0)
                return date_cmp;
            return f1.Hour.CompareTo(f2.Hour);
        }
        public Int32 CompareTo(FileId other) => Compare(this, other);

        public static FileId Current
        {
            get
            {
                var now = DateTime.UtcNow;
                var date = DateOnly.FromDateTime(now);
                var hour = now.Hour;
                return new() { Date = date, Hour = hour };
            }
        }

        public DateTime ToDateTime() => this.Date.ToDateTime(new TimeOnly(this.Hour, 0));

        public static FileId Parse(String str)
        {
            var parts = str.Split('_');
            if (parts.Length != 2)
                throw new FormatException($"Invalid file ID format: {str}");
            var date = DateOnly.ParseExact(parts[0], "yyyy-MM-dd");
            var hour = Int32.Parse(parts[1]);
            return new() { Date = date, Hour = hour };
        }
        public override String ToString() => $"{this.Date:yyyy-MM-dd}_{this.Hour:00}";

    }

    #endregion

    private sealed class PendingFileGroup
    {
        private static readonly Int32 max_writers = Environment.ProcessorCount;
        private readonly DataStash<TDataStash, TTypedContent> data_stash;
        private readonly DirectoryInfo dir;
        private readonly FileId id;
        private Int32 file_count;

        private readonly IdAllocator<BlockId> block_id_allocator;

        private readonly CancellationTokenSource write_cts;
        private readonly List<ProcessingQueue<Action<BinaryWriter>>> writers = [];

        private readonly Lock l_typed_content = new();
        private readonly TTypedContent typed_content = new();

        private readonly Lock l_sealing = new();
        private readonly HashSet<PendingBlockLocation> open_blocks = [];
        private Boolean sealing_started = false;

        public PendingFileGroup(DataStash<TDataStash, TTypedContent> data_stash, DirectoryInfo dir, Boolean expect_existing_content, CancellationToken svc_stop_token)
        {
            this.data_stash = data_stash;
            this.dir = dir;
            this.id = FileId.Parse(dir.Name);
            this.file_count = dir.EnumerateFiles().Count();
            this.write_cts = CancellationTokenSource.CreateLinkedTokenSource(svc_stop_token);

            var used_ids = new List<BlockId>();
            if (this.file_count != 0)
            {
                if (!expect_existing_content)
                    throw new InvalidOperationException($"Pending state {this.id} already exists, but was not expected to exist");
                Prompt.Notify($"Loading pending state {this.id} from {this.file_count} parallel files");

                var expected_files = Enumerable.Range(0, this.file_count).Select(i => $"{i}{file_ext}").ToHashSet();
                if (!expected_files.SetEquals(dir.EnumerateFiles().Select(fi => fi.Name)))
                    throw new InvalidOperationException($"Pending state {this.id} is corrupted: Expected to only have these files: {expected_files.JoinToString()}");

                var files = new List<(FileStream fs, Int32 ind)>(this.file_count);
                try
                {
                    for (var ind = 0; ind < this.file_count; ++ind)
                    {
                        var file_name = $"{ind}{file_ext}";
                        var file_path = Path.Combine(dir.FullName, file_name);
                        var fs = File.Open(file_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                        files.Add((fs, ind));
                    }
                    var block_enumerators = files.ToArray(file =>
                    {
                        return ReadFileBlocks(
                            $"{this.id}[{file.ind}]",
                            file.fs,
                            trim_corrupted: true,
                            id => new PendingBlockLocation(this, file.ind, id),
                            block_open_status_consumers:
                            (
                                on_open: location =>
                                {
                                    if (!this.open_blocks.Add(location))
                                        throw new InvalidOperationException($"Pending state is corrupted: Block at {location} is already open");
                                },
                                on_close: location =>
                                {
                                    if (!this.open_blocks.Remove(location))
                                        throw new InvalidOperationException($"Pending state is corrupted: Block at {location} is not open");
                                    this.typed_content.CloseModel(location);
                                }
                        )
                        ).GetEnumerator();
                    });
                    var inds_with_next = Enumerable.Range(0, this.file_count).Where(ind => block_enumerators[ind].MoveNext()).ToList();

                    while (inds_with_next.Count != 0)
                    {
                        var ind = inds_with_next.MinBy(ind => block_enumerators[ind].Current.common_info.RecordTime);

                        var (common_info, block_br) = block_enumerators[ind].Current;
                        used_ids.Add(common_info.Location.BlockId);
                        this.typed_content.ApplyBlock(common_info, new(files[ind].fs.Position, common_info.Location, block_br));

                        if (!block_enumerators[ind].MoveNext())
                            inds_with_next.Remove(ind);
                    }

                }
                finally
                {
                    foreach (var (fs, _) in files)
                        fs.Dispose();
                }

                for (var ind = 0; ind < this.file_count; ++ind)
                    this.EnsureWriterInitialized(ind);

                Prompt.Notify($"Loaded pending state {this.id} from {used_ids.Count} blocks");
            }

            this.block_id_allocator = new(used_ids);
        }

        public FileId Id => this.id;
        public TTypedContent Content => this.typed_content;

        public BlockLocation ChooseWriteLocation()
        {
            var ind = this.ChooseWriteIndex();
            return new PendingBlockLocation(this, ind, BlockId.NullParent);
        }

        public TResult AddNewBlock<TCommand, TData, TResult>(Boolean hold_open, Int32 Index, TCommand command, BlockId? parent_block_id, TData data, Func<TTypedContent, CommonTypedModelInfo, TData, TResult> apply_to_state)
            where TCommand : struct, Enum
            where TData : struct
        {
            if (this.data_stash.log_all_added)
                Prompt.Notify($"Adding new block to pending state {this.id}: {nameof(hold_open)}={hold_open}, {nameof(Index)}={Index}, {nameof(command)}={command}={Convert.ToInt64(command)}, {nameof(parent_block_id)}={parent_block_id?.ToString() ?? "<null>"}, {nameof(data)}={data}");

            // Everything starting with deciding record_time needs to be locked, to ensure data is added to this.typed_content in the same order as timestamps
            using var lock_scope = this.l_typed_content.EnterScope();

            var record_time = DateTime.UtcNow;
            var new_id = this.block_id_allocator.AllocateId();
            var location = new PendingBlockLocation(this, Index, new_id);

            lock (this.l_sealing)
            {
                if (this.sealing_started)
                    throw new InvalidOperationException($"Pending state {this.id} is already being sealed, cannot add new block {new_id}. A lock should have prevented this");
                if (hold_open)
                {
                    if (!this.open_blocks.Add(location))
                        throw new InvalidOperationException($"Pending state is corrupted: Block at {location} is already open");
                }

                this.writers[Index].Enqueue(bw =>
                {
                    bw.WriteEnum(EBlockKind.Data);
                    var pos1 = bw.BaseStream.Position;
                    bw.Write(-1); // block len placeholder
                    bw.WriteData(record_time);
                    bw.WriteData(new_id);
                    bw.Write(hold_open);
                    bw.WriteEnum(command);
                    if (parent_block_id is { } id)
                        bw.WriteData(id);
                    bw.WriteData(data);
                    var pos2 = bw.BaseStream.Position;
                    bw.BaseStream.Position = pos1;
                    bw.Write(checked((Int32)(pos2 - pos1)));
                    bw.BaseStream.Position = pos2;
                    if (this.data_stash.log_all_added)
                        Prompt.Notify($"{location}: Written block @ {pos1} .. {pos2} ({pos2-pos1} bytes)");
                    bw.Flush();
                });
            }

            var result = apply_to_state.Invoke(this.typed_content, new CommonTypedModelInfo { Location = location, RecordTime = record_time }, data);

            return result;
        }

        public void CloseBlock(PendingBlockLocation location)
        {
            if (!this.l_sealing.LockedGet(() => this.open_blocks.Remove(location)))
                throw new InvalidOperationException($"Pending state is corrupted: Block at {location} was not open");
            this.writers[location.FileIndex].Enqueue(bw =>
            {
                bw.WriteEnum(EBlockKind.Close);
                if (this.data_stash.log_all_added)
                    Prompt.Notify($"{location}: Writing block close @ {bw.BaseStream.Position}");
                bw.WriteData(location.BlockId);
                bw.Flush();
            });
            if (this.open_blocks.Count == 0 && FileId.Current != this.id)
                this.data_stash.TriggerPendingSealer();
        }

        public Boolean TrySeal()
        {
            lock (this.l_sealing)
            {
                if (this.sealing_started)
                    throw new InvalidOperationException($"Pending state {this.id} is already being sealed");
                var open_blocks = this.open_blocks.ToArray();
                if (open_blocks.Length != 0)
                {
                    //this.typed_content.LogSealHeldByBlocks($"pending file group {this.id}", open_blocks);
                    return false;
                }
                this.sealing_started = true;
            }

            Prompt.Notify($"Sealing pending state {this.id}: Waiting for all writers to stop");
            this.write_cts.Cancel();
            // No need to explicitly wait, because all writers already register a wait for the cancellation token

            Prompt.Notify($"Sealing pending state {this.id}: Creating merged file");
            var merge_dir = this.data_stash.root_dir.CreateSubdirectory("Sealing");
            var merge_file_path = Path.Combine(merge_dir.FullName, $"{this.id}{file_ext}");
            var final_file_path = Path.Combine(this.data_stash.root_dir.FullName, $"{this.id}{file_ext}");
            using (var fs = File.Open(merge_file_path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                this.typed_content.Resave(fs);
            File.Move(merge_file_path, final_file_path, overwrite: false);
            merge_dir.Delete(recursive: false);

            Prompt.Notify($"Sealing pending state {this.id}: Validating merged file {final_file_path}");
            try
            {
                var resaved_content = this.data_stash.ReadSealedFileContent(this.id);
                TTypedContent.ValidateEqual(this.typed_content, resaved_content);
            }
            catch (Exception ex)
            {
                WinSvcCommon.HandleCriticalError(ex, when_doing: $"validating after sealing {this.id}");
            }

            Prompt.Notify($"Sealing pending state {this.id}: Cleanup");
            this.dir.Delete(recursive: true);

            Prompt.Notify($"Sealing pending state {this.id}: Done");
            return true;
        }

        private Int32 ChooseWriteIndex()
        {
            var min_pending_count = 0;
            var min_pending_writer_index = 0;
            for (var i = 0; i < max_writers; ++i)
            {
                this.EnsureWriterInitialized(i);
                var writer = this.writers[i];
                var pending_count = writer.PendingCount;
                if (pending_count == 0)
                    return i;
                if (i == 0 || pending_count < min_pending_count)
                {
                    min_pending_count = pending_count;
                    min_pending_writer_index = i;
                }
            }
            return min_pending_writer_index;
        }

        private readonly Lock l_writer_init = new();
        private void EnsureWriterInitialized(Int32 index)
        {
            if (this.writers.Count != index)
                return;
            using var lock_scope = this.l_writer_init.EnterScope();
            if (this.writers.Count != index)
                return;

            var new_writer = new ProcessingQueue<Action<BinaryWriter>>();
            this.writers.Add(new_writer);
            if (this.file_count == index)
                this.file_count += 1;

            // Hold svc shutdown & sealing until everything is written to disk
            this.write_cts.Token.Register(() => new_writer.WaitUntilProcessingFullyStopped(should_clear: false));

            var file_path = Path.Combine(this.dir.FullName, $"{index}{file_ext}");
            var tmp_file_path = file_path + ".tmp";

            new_writer.StartProcessingThread(new()
            {
                UsedFor = $"{nameof(DataStash<,>)}.{nameof(PendingFileGroup)}({this.id}) Writer#{index}",
                OnNewItems = actions =>
                {
                    try
                    {
                        if (File.Exists(file_path))
                            File.Copy(file_path, tmp_file_path, overwrite: false);
                        using (var fs = File.Open(tmp_file_path, FileMode.Append, FileAccess.Write, FileShare.None))
                        {
                            var bw = new BinaryWriter(fs);
                            if (fs.Position == 0)
                                bw.WriteData(new FileHeader());
                            foreach (var action in actions)
                                action.Invoke(bw);
                            bw.Flush();
                        }
                        File.Move(tmp_file_path, file_path, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        WinSvcCommon.HandleCriticalError(ex, when_doing: $"processing writes to {file_path}");
                    }
                },
                CancelToken = this.write_cts.Token,
            });
        }

    }

    [AutoSerializedData]
    [VersionedData(Version = 1)]
    private readonly struct FileHeader()
    {
        public static readonly UInt32 ExpectedMagicNumber = BinaryPrimitives.ReverseEndianness(0xDA7A57A5);
        public readonly UInt32 MagicNumber = ExpectedMagicNumber;
    }

    /// <summary>
    /// Unique within one <see cref="FileId"/>
    /// </summary>
    /// <param name="Value"></param>
    internal readonly record struct BlockId(UInt64 Value) : IAllocatableId<BlockId>
    {
        public UInt64 Value { get; } = Value;
        public static BlockId Invalid => new(0);
        public static BlockId NullParent => new(1);
        public static BlockId MinValue => new(2);
        public static BlockId MaxValue => new(UInt64.MaxValue);
        public static Boolean operator >(BlockId a, BlockId b) => a.Value > b.Value;
        public static Boolean operator <(BlockId a, BlockId b) => a.Value < b.Value;
        public static Int32 Compare(BlockId a, BlockId b) => a.Value.CompareTo(b.Value);
        public static BlockId operator +(BlockId a, IdOffsetOfOne b) => new(checked(a.Value + 1));
        public static BlockId operator -(BlockId a, IdOffsetOfOne b) => new(checked(a.Value - 1));
        public override String ToString() => $"{nameof(BlockId)}({this.Value})";
    }

    private enum EBlockKind : Byte
    {
        Data = 1,
        Close = 2,
    }

    #region BlockLocation

    /// <summary>
    /// </summary>
    public abstract class BlockLocation : IEquatable<BlockLocation>
    {
        internal BlockId BlockId { get; }

        internal BlockLocation(BlockId block_id)
        {
            this.BlockId = block_id;
        }

        internal abstract BlockLocation WithId(BlockId new_id);

        /// <summary>
        /// </summary>
        public abstract TResult AddNewBlock<TCommand, TData, TResult>(Boolean hold_open, TCommand command, Boolean add_parent_ref, TData data, Func<TTypedContent, CommonTypedModelInfo, TData, TResult> apply_to_state)
            where TCommand : struct, Enum where TData : struct;

        /// <summary>
        /// </summary>
        public abstract void CloseBlock();

        /// <summary>
        /// </summary>
        public abstract Boolean Equals(BlockLocation? other);
        private protected abstract Int32 GetHashCodeCore();

        /// <summary>
        /// </summary>
        public override Boolean Equals(Object? obj) =>
            obj is BlockLocation other && this.Equals(other);
        /// <summary>
        /// </summary>
        public override Int32 GetHashCode() =>
            HashCode.Combine(this.BlockId, this.GetHashCodeCore());

    }

    private sealed class PendingBlockLocation(PendingFileGroup file_group, Int32 file_index, BlockId block_id) : BlockLocation(block_id)
    {
        public PendingFileGroup FileGroup { get; } = file_group;
        public Int32 FileIndex { get; } = file_index;

        internal override BlockLocation WithId(BlockId new_id) =>
            new PendingBlockLocation(this.FileGroup, this.FileIndex, new_id);

        public override TResult AddNewBlock<TCommand, TData, TResult>(Boolean hold_open, TCommand command, Boolean add_parent_ref, TData data, Func<TTypedContent, CommonTypedModelInfo, TData, TResult> apply_to_state)
        {
            if (!add_parent_ref && this.BlockId != BlockId.NullParent)
                throw new InvalidOperationException($"{nameof(DataStash<,>)}: Explicit {this} should not be used when adding a new block without a parent reference. Invoke DataStash.UseNewWriteLocation to get a new location");
            return this.FileGroup.AddNewBlock(hold_open, this.FileIndex, command, add_parent_ref ? this.BlockId : null, data, apply_to_state);
        }

        public override void CloseBlock() =>
            this.FileGroup.CloseBlock(this);

        public override Boolean Equals(BlockLocation? other) =>
            other is PendingBlockLocation other_pending &&
            this.FileGroup == other_pending.FileGroup &&
            this.FileIndex == other_pending.FileIndex &&
            this.BlockId == other_pending.BlockId;

        private protected override Int32 GetHashCodeCore() =>
            HashCode.Combine(this.FileGroup, this.FileIndex);

        public override String ToString() => $"{nameof(PendingBlockLocation)}({this.FileGroup.Id}[{this.FileIndex}] => {this.BlockId})";
    }

    private sealed class SealedBlockLocation(FileId file_id, BlockId block_id) : BlockLocation(block_id)
    {
        public FileId FileId { get; } = file_id;

        internal override BlockLocation WithId(BlockId new_id) =>
            new SealedBlockLocation(this.FileId, new_id);

        public override TResult AddNewBlock<TCommand, TData, TResult>(Boolean hold_open, TCommand command, Boolean add_parent_ref, TData data, Func<TTypedContent, CommonTypedModelInfo, TData, TResult> apply_to_state) =>
            throw new InvalidOperationException($"{nameof(DataStash<,>)}: Cannot add new block to {this}");

        public override void CloseBlock() =>
            throw new InvalidOperationException($"{nameof(DataStash<,>)}: Cannot close block at {this}");

        public override Boolean Equals(BlockLocation? other) =>
            other is SealedBlockLocation other_sealed &&
            this.FileId == other_sealed.FileId &&
            this.BlockId == other_sealed.BlockId;

        private protected override Int32 GetHashCodeCore() =>
            HashCode.Combine(this.FileId, this.BlockId);

        public override String ToString() => $"{nameof(SealedBlockLocation)}({this.FileId} => {this.BlockId})";
    }

    #endregion

    /// <summary>
    /// </summary>
    public sealed class ReadContext
    {
        private readonly Int64 block_file_end_pos;
        private readonly BlockLocation location;
        private readonly BinaryReader br;

        internal ReadContext(Int64 block_file_end_pos, BlockLocation location, BinaryReader br)
        {
            this.block_file_end_pos = block_file_end_pos;
            this.location = location;
            this.br = br;
        }

        /// <summary>
        /// </summary>
        public String Description => $"{this.location} @ {this.block_file_end_pos - this.br.BaseStream.Length} .. {this.block_file_end_pos} @ {this.br.BaseStream.Position}";

        /// <summary>
        /// </summary>
        public TCommand ReadCommand<TCommand>() where TCommand : struct, Enum =>
            this.br.ReadEnum<TCommand>();

        /// <summary>
        /// </summary>
        public BlockLocation ReadParentLocation()
        {
            var parent_id = this.br.ReadData<BlockId>();
            return this.location.WithId(parent_id);
        }

        /// <summary>
        /// </summary>
        public TData ReadFileData<TData>() where TData : notnull =>
            this.br.ReadData<TData>();

    }

    /// <summary>
    /// </summary>
    public sealed class ResaveContext
    {
        private readonly BinaryWriter bw;
        private DateTime last_record_time = DateTime.MinValue;

        // Resave can combine multiple files. BlockId inside the location is unique only within one file, so we need to allocate new ids for the resaved file
        private readonly IdAllocator<BlockId> new_id_allocator = new();
        private readonly Dictionary<BlockLocation, BlockId> location_to_new_id = [];

        internal ResaveContext(Stream stream)
        {
            this.bw = new BinaryWriter(stream);
            this.bw.WriteData(new FileHeader());
        }

        /// <summary>
        /// </summary>
        public void WriteBlock<TCommand, TFileData>(CommonTypedModelInfo common_info, TCommand command, BlockLocation? parent_location, TFileData file_data)
            where TCommand : struct, Enum
            where TFileData : struct
        {
            if (common_info.RecordTime < this.last_record_time)
                throw new InvalidOperationException($"{nameof(DataStash<,>)}.{nameof(ResaveContext)}: Cannot write block with record time {common_info.RecordTime} before last written record time {this.last_record_time}");
            this.last_record_time = common_info.RecordTime;

            var pos1 = this.bw.BaseStream.Position;
            this.bw.Write(-1); // block len placeholder
            this.bw.WriteData(common_info.RecordTime);
            this.bw.WriteData(this.GetNewIdForLocation(common_info.Location));
            this.bw.WriteEnum(command);
            if (parent_location is { } loc)
                this.bw.WriteData(this.GetExistingIdForLocation(loc));
            this.bw.WriteData(file_data);
            var pos2 = this.bw.BaseStream.Position;
            this.bw.BaseStream.Position = pos1;
            this.bw.Write(checked((Int32)(pos2 - pos1)));
            this.bw.BaseStream.Position = pos2;
            //this.bw.Flush(); // Don't flush in the middle of resave, because whole resave is an atomic operation
        }

        private BlockId GetNewIdForLocation(BlockLocation location)
        {
            var id = this.new_id_allocator.AllocateId();
            if (!this.location_to_new_id.TryAdd(location, id))
                throw new InvalidOperationException($"{nameof(DataStash<,>)}.{nameof(ResaveContext)}: Duplicate {location} when allocating new {id}");
            return id;
        }

        private BlockId GetExistingIdForLocation(BlockLocation location)
        {
            if (!this.location_to_new_id.TryGetValue(location, out var id))
                throw new InvalidOperationException($"{nameof(DataStash<,>)}.{nameof(ResaveContext)}: {location} has no new allocated {nameof(BlockId)}");
            return id;
        }

    }

    private static class TimeFormat
    {

        public static String DateTime(DateTime dt) => dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        public static String TimeSpan(TimeSpan ts) => $"{Math.Truncate(ts.TotalHours)}:{ts.Minutes:00}:{ts.Seconds:00}";

    }

    private sealed class PendingSealer(DataStash<TDataStash, TTypedContent> data_stash)
    {
        private readonly DataStash<TDataStash, TTypedContent> data_stash = data_stash;

        private readonly ManualResetEventSlim wh_recheck = new(true);

        public void Start(CancellationToken svc_stop_token)
        {
            var thr = new Thread(() =>
            {
                var next_sealing_attempt = DateTime.UtcNow;
                while (!svc_stop_token.IsCancellationRequested)
                {
                    try
                    {
                        this.wh_recheck.Reset();

                        var current_file_id = FileId.Current;
                        var need_sealing = this.data_stash.all_pending_state_files.Keys.Where(id => id.CompareTo(current_file_id) < 0).ToList();
                        var need_sealing_count = need_sealing.Count;

                        if (need_sealing_count != 0)
                        {
                            this.data_stash.l_all_pending_state_files.OneLocked(() =>
                            {
                                //Prompt.Notify($"{this.data_stash} => {nameof(PendingSealer)}: Attempting to seal {need_sealing_count} pending states: {need_sealing.JoinToString()}");
                                var sealed_count = need_sealing.RemoveAll(file_id =>
                                {
                                    var pending_file_group = this.data_stash.all_pending_state_files[file_id];
                                    if (!pending_file_group.TrySeal())
                                        return false;

                                    if (!this.data_stash.all_pending_state_files.Remove(file_id, out _))
                                        // Can't throw out of .RemoveAll, it leaves inconsistent state
                                        Err.Handle($"{this.data_stash}: Failed to remove pending state file {file_id}");

                                    // New sealed file has been added, need to reset consolidation schedule
                                    this.data_stash.TriggerSealedConsolidator();

                                    return true;
                                });
                                //Prompt.Notify($"{this.data_stash} => {nameof(PendingSealer)}: Sealed {sealed_count}/{need_sealing_count} pending states");
                            }, with_priority: false);
                        }

                        var now = DateTime.UtcNow;
                        next_sealing_attempt = new DateTime(DateOnly.FromDateTime(now), new TimeOnly(now.Hour, minute: 5), DateTimeKind.Utc).AddHours(1);
                        if (need_sealing.Count != 0)
                            next_sealing_attempt = next_sealing_attempt.ClampTop(now.AddMinutes(5));
                        if (next_sealing_attempt > now)
                        {
                            var wait_time = next_sealing_attempt - now;
                            //Prompt.Notify($"{this.data_stash} => {nameof(PendingSealer)}: Waiting {TimeFormat.TimeSpan(wait_time)} until next sealing attempt (at {TimeFormat.DateTime(next_sealing_attempt)})");
                            if (this.wh_recheck.Wait(wait_time, svc_stop_token))
                                Prompt.Notify($"{this.data_stash} => {nameof(PendingSealer)}: Wait was interrupted, rechecking pending states");
                            continue;
                        }

                    }
                    catch (Exception ex) when (svc_stop_token.IsCancellationRequested && ex.GetNestedExceptions().All(ex => ex is OperationCanceledException))
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Err.Handle(ex);
                    }
                }
            })
            {
                Name = $"{this.data_stash} {nameof(PendingSealer)}",
                IsBackground = true,
            };
            thr.Start();
        }

        public void Recheck() => this.wh_recheck.Set();

    }

    private sealed class SealedConsolidator(DataStash<TDataStash, TTypedContent> data_stash, DataStashConsolidationConfig consolidation_config)
    {
        private readonly DataStash<TDataStash, TTypedContent> data_stash = data_stash;
        private readonly DataStashConsolidationConfig consolidation_config = consolidation_config;

        private readonly ManualResetEventSlim wh_recompute = new(true);

        public void Start(CancellationToken svc_stop_token)
        {
            this.consolidation_config.Simulate(step_count: 10_000);

            var thr = new Thread(() =>
            {
                while (!svc_stop_token.IsCancellationRequested)
                {
                    try
                    {
                        this.wh_recompute.Reset();

                        var file_ids = this.data_stash.l_all_sealed_state_files
                            .ManyLocked(() => this.data_stash.all_sealed_state_files.ToArray());
                        if (file_ids.Length < 2)
                        {
                            Prompt.Notify($"{this.data_stash} => {nameof(SealedConsolidator)}: Not enough ({file_ids.Length} < 2) sealed files for consolidation");
                            this.wh_recompute.Wait(svc_stop_token);
                            continue;
                        }

                        var merge_times = file_ids
                            .Select(file_id => (Double)file_id.ToDateTime().Ticks)
                            .Pairwise((t1, t2) => this.consolidation_config.ExpectedMergeTime(t1, t2))
                            .Prepend(Double.PositiveInfinity);
                        var (next_merge_ind, next_merge_time_double) = Enumerable.Range(0, file_ids.Length)
                            .Zip(merge_times, (file_id, merge_time) => (file_id, merge_time))
                            .MinBy(t => t.merge_time);
                        if (next_merge_time_double > DateTime.MaxValue.Ticks)
                        {
                            Prompt.Notify($"{this.data_stash} => {nameof(SealedConsolidator)}: Closest merge time is beyond DateTime.MaxValue: {next_merge_time_double}");
                            this.wh_recompute.Wait(svc_stop_token);
                            continue;
                        }

                        var id_merge = file_ids[next_merge_ind];
                        var id_keep = file_ids[next_merge_ind - 1];

                        var next_merge_dt = new DateTime((Int64)next_merge_time_double, DateTimeKind.Utc);
                        var now = DateTime.UtcNow;
                        if (next_merge_dt > now)
                        {
                            var wait_time = next_merge_dt - now;
                            Prompt.Notify($"{this.data_stash} => {nameof(SealedConsolidator)}: Next consolidation is planned to merge {id_merge} => {id_keep} at {TimeFormat.DateTime(next_merge_dt)} (in {TimeFormat.TimeSpan(wait_time)})");
                            if (this.wh_recompute.Wait(wait_time, svc_stop_token))
                                Prompt.Notify($"{this.data_stash} => {nameof(SealedConsolidator)}: Wait was interrupted, recomputing merge schedule");
                            continue;
                        }

                        Prompt.Notify($"{this.data_stash} => {nameof(SealedConsolidator)}: Consolidating {id_merge} => {id_keep}");
                        this.data_stash.l_all_sealed_state_files.OneLocked(() =>
                        {
                            var content = new TTypedContent();
                            this.data_stash.ReadAppendSealedFileContent(id_keep, content);
                            this.data_stash.ReadAppendSealedFileContent(id_merge, content);

                            var merge_dir = this.data_stash.root_dir.CreateSubdirectory($"Consolidation");
                            if (merge_dir.EnumerateFileSystemInfos().Any())
                                WinSvcCommon.HandleCriticalError(new MessageException($"Directory {merge_dir.FullName} is not empty"), when_doing: $"attempting consolidation of {id_merge} => {id_keep}");

                            var merge_file_path = Path.Combine(merge_dir.FullName, $"{id_keep}{file_ext}");
                            using (var fs = File.Open(merge_file_path, FileMode.CreateNew))
                                content.Resave(fs);

                            Prompt.Notify($"{this.data_stash} => {nameof(SealedConsolidator)}: Validating after consolidating {id_merge} => {id_keep}");
                            try
                            {
                                using var fs = File.OpenRead(merge_file_path);
                                var resaved_content = ReadSealedFileContent($"merge result {id_merge} => {id_keep}", id_keep, fs);
                                TTypedContent.ValidateEqual(content, resaved_content);
                            }
                            catch (Exception ex)
                            {
                                WinSvcCommon.HandleCriticalError(ex, when_doing: $"validating after consolidating {id_merge} => {id_keep}");
                            }

                            Prompt.Notify($"{this.data_stash} => {nameof(SealedConsolidator)}: Cleanup after merging {id_merge} => {id_keep}");
                            var final_file_path = Path.Combine(this.data_stash.root_dir.FullName, $"{id_keep}{file_ext}");
                            File.Move(merge_file_path, final_file_path, overwrite: true);
                            File.Delete(Path.Combine(this.data_stash.root_dir.FullName, $"{id_merge}{file_ext}"));
                            this.data_stash.all_sealed_state_files.RemoveAt(next_merge_ind);
                            merge_dir.Delete(recursive: false);
                        }, with_priority: false);
                        Prompt.Notify($"{this.data_stash} => {nameof(SealedConsolidator)}: Done consolidating {id_merge} => {id_keep}");
                    }
                    catch (Exception ex) when (svc_stop_token.IsCancellationRequested && ex.GetNestedExceptions().All(ex => ex is OperationCanceledException))
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Err.Handle(ex);
                    }
                }
            })
            {
                Name = $"{this.data_stash} {nameof(SealedConsolidator)}",
                IsBackground = true,
            };
            thr.Start();
        }

        public void RecomputeNextMergeTime() => this.wh_recompute.Set();

    }

}
