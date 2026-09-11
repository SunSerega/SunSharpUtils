using System;
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
using SunSharpUtils.Ext.UniversalBin;
using SunSharpUtils.Ids;
using SunSharpUtils.Threading;
using SunSharpUtils.WinSvc;

//TODO Maybe change the namespace using compiler directives?
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
#pragma warning disable CS0436 // Type conflicts with imported type
#pragma warning restore IDE0079 // Remove unnecessary suppression

namespace SunSharpUtils.DataStash;

//TODO Things left:
// - Implementation for some code-generated functions
// --- The whole thing with parent-child references is not even touched yet
// --- Catch SealingStartedException and retry choosing write location
// - Versioning in universal binary format
// --- Require block structs to have versioning attribute
// --- Add versioning to file header
// - Explicit support for DateTime in binary format (use .ToBinary and .FromBinary)
// - Add system to mark some blocks as open when created
// --- RPC method to close one instance of model that is openable
// --- RPC method to compare list of open models client side to server side (close forgotten blocks and report to client what isn't actually open server side)
// - Reading data (including both pending and sealed files) per client request
// - Filling in data from an older format (to upgrade VRCT to use DataStash)
// - Check out how consolidation config sim looks in logs
// - Split this file into multiple?

// ===

//TODO I really need to decide what I actually want as a result, and what info I want in non-generated part
// - Write it out until there is a logical line of reasoning between original goal and specific implementation details

//TODO Fundamental goals for "data stash" system:
// - Main idea is to have a forever-store containing binary log of events
// - Events exist linearly in time (no branching), as they happened
// --- Files should be append-only
// --- Except when consolidating old files together
// - Client can read events from any point in time
// - Resistance to corruption (e.g. power loss) is important
// --- The file being written should have an actively maintained backup, which is deleted when file is sealed
// - Old files should be consolidated together to remain exponentially bigger and sparser the older they are
// --- Exponential base should be configurable

//TODO Implementation details:
// - Each file is a list of blocks
// - Blocks are organized into chains/trees. Dependant blocks must be in the same file as root block

//TODO Info that has to be provided to the generator:
// - File stores a list of blocks 
// --- Each block needs a struct representing raw data that can be written directly
// --- But also attribute can show dependence on another block
// - Whole file when open should have a typed content representation
// --- Need a class holding a more structured representation of everything in the file
// --- Need a function to apply each next block from file to a newly created typed content representation
// --- Need a function to create the initial typed content representation (just empty ctor?)
// --- Need a way to resave typed content after merges (full resave or something that allows merging with prev block)
//TODO Look at the old implementatio again
//TODO How do I represent what block is original (e.g. add chat tree) and what is dependant?
// - Just attributes ig?
// - For now went with gen-args of an interface implemented by the typed state class

// ===

//TODO A common pattern to be made convenient:
// - Some data that is part of typed models is dupped, so it should be stored in the file as a separate block (assigning value to key)
// - Multiple values for the same key can be in the same file, when value is updated. Solved when reading by looking at timings
// - Redundant updates is the main thing that is optimized by consolidation. Otherwise consolidation doesn't really make sense

//TODO Backup files during each consolidation merge
// - Keep last N merges saved, delete older ones

/// <summary>
/// Marks data stash implementation for auto-generation of implementation boilerplate
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AutoDataStashAttribute : Attribute;

/// <summary>
/// Configuration for how the files in data stash should be merged over time to optimize disk space usage
/// </summary>
/// <param name="file_time_target_exp_base">Base of the target exponential growth for merged file sizes. Must be between 1 and 2 for reasonable results</param>
public readonly struct DataStashConsolidationConfig(Double file_time_target_exp_base = 1.3)
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
        report($"File counts were seen at these times:");
        var N_max_str_len = (file_count_to_min_time.Count-1).ToString().Length;
        for (var N = 1; N < file_count_to_min_time.Count; ++N)
        {
            var time_graph_len = 50;
            var pos_min = (Int32)Math.Round(file_count_to_min_time[N] * (time_graph_len-1) / (Double)step_count);
            var pos_max = (Int32)Math.Round(file_count_to_max_time[N] * (time_graph_len-1) / (Double)step_count);

            var time_graph = String.Create(time_graph_len, 0, (span, _) =>
            {
                span.Fill(' ');
                span.Slice(pos_min, pos_max-pos_min+1).Fill('#');
            });

            report($"- {N.ToString().PadLeft(N_max_str_len)} files | {time_graph} | {file_count_to_min_time[N]} .. {file_count_to_max_time[N]}");
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
public abstract class DataStash
{

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
        public abstract void ApplyBlock(BinaryReader br, DateTime record_time, DataStash<TSelf>.BlockLocation location);

        /// <summary>
        /// Called when sealing is skipped due to open blocks
        /// <para/>
        /// Should be implemented by code-generation with <see cref="AutoDataStashAttribute"/>
        /// </summary>
        /// <param name="block_locations"></param>
        public abstract void LogSealHeldByBlocks(DataStash<TSelf>.BlockLocation[] block_locations);

        /// <summary>
        /// Should be implemented by code-generation with <see cref="AutoDataStashAttribute"/>
        /// </summary>
        public abstract void Resave(Stream stream);

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
        /// <param name="d1"></param>
        /// <param name="d2"></param>
        /// <param name="validate_value"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public static void ValidateDictEqual<TKey, TValue>(Dictionary<TKey, TValue> d1, Dictionary<TKey, TValue> d2, Action<TValue, TValue> validate_value)
            where TKey : notnull
        {
            if (d1.Keys.Except(d2.Keys).ToArray() is { Length: not 0 } extra_keys1)
                throw new InvalidOperationException($"Keys only in first dict: {extra_keys1.JoinToString("; ")}");
            if (d2.Keys.Except(d1.Keys).ToArray() is { Length: not 0 } extra_keys2)
                throw new InvalidOperationException($"Keys only in first dict: {extra_keys2.JoinToString("; ")}");
            foreach (var key in d1.Keys)
                validate_value.Invoke(d1[key], d2[key]);
        }
    }

}

/// <summary>
/// <inheritdoc cref="DataStash"/>
/// </summary>
/// <typeparam name="TTypedContent"></typeparam>
public abstract class DataStash<TTypedContent> : DataStash
    where TTypedContent : class, DataStash.ITypedContent<TTypedContent>, new()
{
    private readonly DirectoryInfo root_dir;
    private readonly CancellationToken svc_stop_token;

    private readonly DirectoryInfo pending_dir;

    private static readonly String file_ext = ".bin";

    private readonly OneToManyLock l_all_sealed_state_files = new();
    private readonly List<FileId> all_sealed_state_files;
    private readonly ConcurrentDictionary<FileId, PendingFileGroup> all_pending_state_files = [];

    private readonly PendingSealer pending_sealer;
    private readonly SealedConsolidator sealed_consolidator;

    /// <summary>
    /// </summary>
    /// <param name="data_dir">A directory to store all the data. Must not have any other files</param>
    /// <param name="svc_stop_token"></param>
    /// <param name="consolidation_config"></param>
    protected DataStash(String data_dir, CancellationToken svc_stop_token, DataStashConsolidationConfig? consolidation_config = null)
    {
        Prompt.Notify($"Initializing {this} in: {data_dir}");
        this.root_dir = Directory.CreateDirectory(data_dir);
        this.svc_stop_token = svc_stop_token;

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
            this.ReadSealedFileContent(file_id);
        }
        Prompt.Notify($"Validated {this.all_sealed_state_files.Count} sealed state files");

        foreach (var sub_dir in this.pending_dir.EnumerateDirectories())
        {
            var pending_file_group = new PendingFileGroup(this, sub_dir, svc_stop_token);
            if (this.all_sealed_state_files.Contains(pending_file_group.Id))
                throw new InvalidOperationException($"Pending state {pending_file_group.Id} conflicts with a sealed file with the same id");
            if (this.all_pending_state_files.ContainsKey(pending_file_group.Id))
                throw new InvalidOperationException($"Pending state {pending_file_group.Id} exists multiple times");
            this.all_pending_state_files[pending_file_group.Id] = pending_file_group;
        }

        this.pending_sealer = new PendingSealer(this);
        this.pending_sealer.Start(svc_stop_token);

        this.sealed_consolidator = new SealedConsolidator(this, consolidation_config ?? new());
        this.sealed_consolidator.Start(svc_stop_token);

        Prompt.Notify($"Initialized {nameof(DataStash<>)} ({this.GetType().Name})");
    }

    private TTypedContent ReadSealedFileContent(FileId file_id)
    {
        var file_path = Path.Combine(this.root_dir.FullName, $"{file_id}{file_ext}");
        using var fs = File.OpenRead(file_path);
        return ReadSealedFileContent($"{file_id}", file_id, fs);
    }

    private static TTypedContent ReadSealedFileContent(String description, FileId file_id, FileStream fs)
    {
        var content = new TTypedContent();
        foreach (var (br, record_time, location) in ReadFileBlocks(description, fs, trim_corrupted: false, location_factory: id => new SealedBlockLocation(file_id, id), block_open_status_consumer: null))
            content.ApplyBlock(br, record_time, location);
        return content;
    }

    private void ReadAppendSealedFileContent(FileId file_id, TTypedContent content)
    {
        var file_path = Path.Combine(this.root_dir.FullName, $"{file_id}{file_ext}");
        using var fs = File.OpenRead(file_path);
        foreach (var (br, record_time, location) in ReadFileBlocks($"{file_id}", fs, trim_corrupted: false, location_factory: id => new SealedBlockLocation(file_id, id), block_open_status_consumer: null))
            content.ApplyBlock(br, record_time, location);
    }

    private static IEnumerable<(BinaryReader block_br, DateTime record_time, BlockLocation location)> ReadFileBlocks(
        String description, Stream stream, Boolean trim_corrupted, Func<BlockId, BlockLocation> location_factory, Action<BlockId, Int32>? block_open_status_consumer)
    {
        var br = new BinaryReader(stream);

        var header = br.ReadData<FileHeader>();
        if (header.MagicNumber != FileHeader.ExpectedMagicNumber)
            throw new InvalidDataException($"File {description} corrupted: Invalid magic number in header: {header.MagicNumber:X8} != {FileHeader.ExpectedMagicNumber:X8}");

        var last_valid_stream_pos = trim_corrupted ? stream.Position : 0;

        Boolean first_read_in_block = false;
        var buffer = new Byte[1024];
        Boolean TryReadRaw(String read_description, Int32 len)
        {
            if (len > buffer.Length)
                buffer = new Byte[len.ClampBottom(buffer.Length*2)];
            var read_len = br.Read(buffer.AsSpan()[..len]);
            if (read_len != len)
            {
                if (read_len != 0 || !first_read_in_block) // If not EOF at the block boundary
                    Prompt.Notify($"File {description} corrupted in non-critical way: Only found {read_len}/{len} bytes for {read_description}");
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
            Prompt.Notify($"File {description} corrupted in non-critical way: Trimming file to {last_valid_stream_pos} bytes");
            stream.SetLength(last_valid_stream_pos);
        }) : null;

        while (true)
        {
            first_read_in_block = true;
            if (block_open_status_consumer is not null)
            {
                if (!TryRead("kind of the next block", sizeof(EBlockKind), br => br.ReadEnum<EBlockKind>(), out var kind))
                    yield break;
                switch (kind)
                {
                    case EBlockKind.Data:
                        break; // Just continue reading
                    case EBlockKind.Close:
                        if (!TryRead("id of the block being closed", Marshal.SizeOf<BlockId>(), br => br.ReadData<BlockId>(), out var closed_block_id))
                            yield break;
                        block_open_status_consumer.Invoke(closed_block_id, -1);
                        break;
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
            var record_time = DateTime.FromBinary(block_br.ReadInt64());
            var block_id = block_br.ReadData<BlockId>();
            if (block_open_status_consumer is not null)
            {
                var hold_open = block_br.ReadBoolean();
                if (hold_open)
                    block_open_status_consumer.Invoke(block_id, +1);
            }
            var location = location_factory.Invoke(block_id);
            yield return (block_br, record_time, location);
            if (trim_corrupted)
                last_valid_stream_pos = stream.Position;
        }
        
    }

    /// <summary>
    /// </summary>
    protected BlockLocation ChooseWriteLocation()
    {
        var file_id = FileId.Current;
        var pending_file_group = this.all_pending_state_files.GetOrAdd(file_id, id => new PendingFileGroup(this, this.pending_dir.CreateSubdirectory(id.ToString()), this.svc_stop_token));
        return pending_file_group.ChooseWriteLocation();
    }

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(DataStash<>)} ({this.GetType().Name})";

    #region ITypedContent

    /// <summary>
    /// <inheritdoc cref="DataStash.ITypedContent{TSelf}"/>
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
    /// Implement by typed file content type to add TData block with no parent relationship
    /// </summary>
    /// <typeparam name="TNetworkData"></typeparam>
    /// <typeparam name="TFileData"></typeparam>
    /// <typeparam name="TTyped"></typeparam>
    public interface ITypedContentWithRootBlock<TNetworkData, TFileData, TTyped>
        where TNetworkData : struct
        where TFileData : struct
        where TTyped : class
    {
        /// <summary>
        /// Turns network data into file data
        /// <para/>
        /// The result will be written to file and immediately passed to <see cref="ReadBlock"/>
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public TFileData ParseNetworkPacket(TNetworkData data);
        /// <summary>
        /// Adds block's content from file to this instance, and returns newly created representation of this block
        /// </summary>
        /// <param name="record_time"></param>
        /// <param name="content"></param>
        /// <returns></returns>
        public TTyped ReadBlock(DateTime record_time, TFileData content);
    }
    /// <summary>
    /// Implement by typed file content type to add TData block with a parent
    /// <para/>
    /// This block being a child doesn't stop another block from being child of this block
    /// </summary>
    /// <typeparam name="TNetworkData"></typeparam>
    /// <typeparam name="TFileData"></typeparam>
    /// <typeparam name="TTypedParent"></typeparam>
    /// <typeparam name="TTyped"></typeparam>
    public interface ITypedContentWithChildBlock<TNetworkData, TFileData, TTypedParent, TTyped>
        where TNetworkData : struct
        where TFileData : struct
        where TTypedParent : class?
        where TTyped : class
    {
        /// <inheritdoc cref="ITypedContentWithRootBlock{TNetworkData, TFileData, TTyped}.ParseNetworkPacket(TNetworkData)"/>
        public TFileData ParseNetworkPacket(TNetworkData data, out TTypedParent found_parent);
        /// <inheritdoc cref="ITypedContentWithRootBlock{TNetworkData, TFileData, TTyped}.ReadBlock"/>
        public TTyped ReadBlock(DateTime record_time, TTypedParent parent, TFileData content);
    }

    #endregion

    private readonly record struct FileId : IEquatable<FileId>, IComparable<FileId>
    {
        public required DateOnly Date { get; init; }
        public required Int32 Hour { get; init; }

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

    private sealed class PendingFileGroup
    {
        private static readonly Int32 max_writers = Environment.ProcessorCount;
        private readonly DataStash<TTypedContent> data_stash;
        private readonly DirectoryInfo dir;
        private readonly FileId id;
        private Int32 file_count;

        private readonly IdAllocator<BlockId> block_id_allocator;

        private readonly CancellationTokenSource write_cts;
        private readonly List<ProcessingQueue<Action<BinaryWriter>>> writers = [];

        private readonly TTypedContent typed_content = new();

        private readonly Lock l_sealing = new();
        private readonly HashSet<(Int32 index, BlockId id)> open_blocks = [];
        private Boolean sealing_started = false;

        public PendingFileGroup(DataStash<TTypedContent> data_stash, DirectoryInfo dir, CancellationToken svc_stop_token)
        {
            this.data_stash = data_stash;
            this.dir = dir;
            this.id = FileId.Parse(dir.Name);
            this.file_count = dir.EnumerateFiles().Count();
            this.write_cts = CancellationTokenSource.CreateLinkedTokenSource(svc_stop_token);

            var used_ids = new List<BlockId>();
            if (this.file_count != 0)
            {
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
                            (id, bl_open_diff) =>
                            {
                                var key = (file.ind, id);
                                var old_open_count = this.open_blocks.Contains(key) ? 1 : 0;
                                var new_open_count = old_open_count + bl_open_diff;
                                if (!new_open_count.InRange(0, 1))
                                    throw new InvalidOperationException($"Pending state {this.id} is corrupted: Block {id} in file {file.ind} has open count {new_open_count}");
                                if (new_open_count == 1)
                                    this.open_blocks.Add(key);
                                else
                                    this.open_blocks.Remove(key);
                            }
                        ).GetEnumerator();
                    });
                    var inds_with_next = Enumerable.Range(0, this.file_count).Where(ind => block_enumerators[ind].MoveNext()).ToList();

                    while (inds_with_next.Count != 0)
                    {
                        var ind = inds_with_next.MinBy(ind => block_enumerators[ind].Current.record_time);

                        var (block_br, record_time, location) = block_enumerators[ind].Current;
                        used_ids.Add(location.BlockId);
                        this.typed_content.ApplyBlock(block_br, record_time, location);

                        if (!block_enumerators[ind].MoveNext())
                            inds_with_next.Remove(ind);
                    }

                }
                finally
                {
                    foreach (var (fs, _) in files)
                        fs.Dispose();
                }

                Prompt.Notify($"Loaded pending state {this.id} from {used_ids.Count} blocks");
            }

            this.block_id_allocator = new(used_ids);
        }

        public FileId Id => this.id;

        public BlockLocation ChooseWriteLocation()
        {
            var ind = this.ChooseWriteIndex();
            return new PendingBlockLocation(this, ind, BlockId.NullParent);
        }

        public PendingBlockLocation AddNewBlock<TCommand, TData>(Boolean hold_open, Int32 Index, TCommand command, BlockId? parent_block_id, TData data, Action<TTypedContent, TData> apply_to_state)
            where TCommand : struct, Enum
            where TData : struct
        {
            var record_time = DateTime.UtcNow;
            var new_id = this.block_id_allocator.AllocateId();

            lock (this.l_sealing)
            {
                if (this.sealing_started)
                    throw new SealingStartedException();
                if (hold_open)
                {
                    if (!this.open_blocks.Add((Index, new_id)))
                        throw new InvalidOperationException($"Pending state {this.id} is corrupted: Block {new_id} in file {Index} is already open");
                }

                this.writers[Index].Enqueue(bw =>
                {
                    var pos1 = bw.BaseStream.Position;
                    bw.WriteEnum(EBlockKind.Data);
                    bw.Write(-1); // block len placeholder
                    bw.Write(record_time.ToBinary());
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
                    bw.Flush();
                });
            }

            apply_to_state.Invoke(this.typed_content, data);

            return new PendingBlockLocation(this, Index, new_id);
        }

        public void ReportBlockClosed(Int32 Index, BlockId block_id)
        {
            if (!this.l_sealing.LockedGet(() => this.open_blocks.Remove((Index, block_id))))
                throw new InvalidOperationException($"Pending state {this.id} is corrupted: Block {block_id} in file {Index} was not open");
            this.writers[Index].Enqueue(bw =>
            {
                bw.WriteEnum(EBlockKind.Close);
                bw.WriteData(block_id);
                bw.Flush();
            });
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
                    this.typed_content.LogSealHeldByBlocks(open_blocks.ToArray(key => new PendingBlockLocation(this, key.index, key.id)));
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
                UsedFor = $"{nameof(DataStash<>)}.{nameof(PendingFileGroup)}({this.id}) Writer#{index}",
                OnNewItems = actions =>
                {
                    File.Copy(file_path, tmp_file_path, overwrite: false);
                    using (var fs = File.Open(file_path, FileMode.Append, FileAccess.Write, FileShare.None))
                    {
                        var bw = new BinaryWriter(fs);
                        if (fs.Position == 0)
                            bw.WriteData(new FileHeader());
                        foreach (var action in actions)
                            action.Invoke(bw);
                        bw.Flush();
                    }
                    File.Move(tmp_file_path, file_path, overwrite: true);
                },
                CancelToken = this.write_cts.Token,
            });
        }

        public sealed class SealingStartedException : Exception;

    }

    private readonly struct FileHeader()
    {
        public const UInt32 ExpectedMagicNumber = 0xDA7A57A5;
        public readonly UInt32 MagicNumber = ExpectedMagicNumber;
    }

    /// <summary>
    /// Unique within one <see cref="FileId"/>
    /// </summary>
    /// <param name="value"></param>
    internal readonly record struct BlockId(UInt64 value) : IAllocatableId<BlockId>
    {
        public UInt64 Value { get; } = value;
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

        /// <summary>
        /// </summary>
        public abstract BlockLocation AddNewBlock<TCommand, TData>(Boolean hold_open, TCommand command, Boolean add_parent_ref, TData data, Action<TTypedContent, TData> apply_to_state)
            where TCommand : struct, Enum where TData : struct;

        /// <summary>
        /// </summary>
        public abstract Boolean Equals(BlockLocation? other);
        /// <summary>
        /// </summary>
        protected abstract Int32 GetHashCodeCore();

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
        private PendingFileGroup FileGroup { get; } = file_group;
        private Int32 FileIndex { get; } = file_index;

        public override BlockLocation AddNewBlock<TCommand, TData>(Boolean hold_open, TCommand command, Boolean add_parent_ref, TData data, Action<TTypedContent, TData> apply_to_state)
        {
            if (!add_parent_ref && this.BlockId != BlockId.NullParent)
                throw new InvalidOperationException($"Explicit location should not be used when adding a new block without a parent reference. Use ChooseWriteLocation to get a new location");
            return this.FileGroup.AddNewBlock(hold_open, this.FileIndex, command, add_parent_ref ? this.BlockId : null, data, apply_to_state);
        }

        public override Boolean Equals(BlockLocation? other) =>
            other is PendingBlockLocation other_pending &&
            this.FileGroup == other_pending.FileGroup &&
            this.FileIndex == other_pending.FileIndex &&
            this.BlockId == other_pending.BlockId;

        protected override Int32 GetHashCodeCore() =>
            HashCode.Combine(this.FileGroup, this.FileIndex);

        public override String ToString() => $"{nameof(PendingBlockLocation)}({this.FileGroup.Id}[{this.FileIndex}] => {this.BlockId})";
    }

    private sealed class SealedBlockLocation(FileId file_id, BlockId block_id) : BlockLocation(block_id)
    {
        private FileId FileId { get; } = file_id;

        public override BlockLocation AddNewBlock<TCommand, TData>(Boolean hold_open, TCommand command, Boolean add_parent_ref, TData data, Action<TTypedContent, TData> apply_to_state) =>
            throw new InvalidOperationException($"Cannot add new block to {this}");

        public override Boolean Equals(BlockLocation? other) =>
            other is SealedBlockLocation other_sealed &&
            this.FileId == other_sealed.FileId &&
            this.BlockId == other_sealed.BlockId;

        protected override Int32 GetHashCodeCore() =>
            HashCode.Combine(this.FileId, this.BlockId);

        public override String ToString() => $"{nameof(SealedBlockLocation)}({this.FileId} => {this.BlockId})";
    }

    #endregion

    private sealed class PendingSealer(DataStash<TTypedContent> data_stash)
    {
        private readonly DataStash<TTypedContent> data_stash = data_stash;

        public void Start(CancellationToken svc_stop_token)
        {
            var thr = new Thread(() =>
            {
                var next_sealing_attempt = DateTime.UtcNow;
                while (!svc_stop_token.IsCancellationRequested)
                {
                    try
                    {
                        var now = DateTime.UtcNow;
                        if (next_sealing_attempt > now)
                        {
                            var wait_time = next_sealing_attempt - now;
                            Prompt.Notify($"{this.data_stash}: Waiting {wait_time} until next sealing attempt (at {next_sealing_attempt})");
                            svc_stop_token.WaitHandle.WaitOne(wait_time);
                            continue;
                        }
                        
                        var current_file_id = FileId.Current;
                        var need_sealing = this.data_stash.all_pending_state_files.Keys.Where(id => id.CompareTo(current_file_id) < 0).ToList();
                        var need_sealing_count = need_sealing.Count;

                        if (need_sealing_count != 0)
                        {
                            Prompt.Notify($"{this.data_stash}: Attempting to seal {need_sealing_count} pending states: {need_sealing.JoinToString()}");
                            var sealed_count = need_sealing.RemoveAll(file_id =>
                            {
                                var pending_file_group = this.data_stash.all_pending_state_files[file_id];
                                if (!pending_file_group.TrySeal())
                                    return false;

                                if (!this.data_stash.all_pending_state_files.Remove(file_id, out _))
                                    // Can't throw out of .RemoveAll, it leaves inconsistent state
                                    Prompt.Notify($"{this.data_stash}: Failed to remove pending state file {file_id}");

                                // New sealed file has been added, need to reset consolidation schedule
                                this.data_stash.sealed_consolidator.RecomputeNextMergeTime();

                                return true;
                            });
                            Prompt.Notify($"{this.data_stash}: Sealed {sealed_count}/{need_sealing_count} pending states");
                        }

                        next_sealing_attempt = new DateTime(DateOnly.FromDateTime(now), new TimeOnly(now.Hour, minute: 5), DateTimeKind.Utc).AddHours(1);
                        if (need_sealing.Count != 0)
                            next_sealing_attempt = next_sealing_attempt.ClampTop(now.AddMinutes(5));
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

    }

    private sealed class SealedConsolidator(DataStash<TTypedContent> data_stash, DataStashConsolidationConfig consolidation_config)
    {
        private readonly DataStash<TTypedContent> data_stash = data_stash;
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
                        var file_ids = this.data_stash.l_all_sealed_state_files
                            .ManyLocked(() => this.data_stash.all_sealed_state_files.ToArray());
                        if (file_ids.Length < 2)
                        {
                            Prompt.Notify($"{this.data_stash}: Not enough files for consolidation");
                            this.wh_recompute.Wait(svc_stop_token);
                            this.wh_recompute.Reset();
                            continue;
                        }

                        var merge_times = file_ids
                            .Select(file_id => (Double)file_id.ToDateTime().Ticks)
                            .Pairwise((t1, t2) => this.consolidation_config.ExpectedMergeTime(t1, t2))
                            .Prepend(Double.PositiveInfinity);
                        var (next_merge_ind, next_merge_time_double) = Enumerable.Range(0, file_ids.Length - 1)
                            .Zip(merge_times, (file_id, merge_time) => (file_id, merge_time))
                            .MinBy(t => t.merge_time);
                        if (next_merge_time_double > DateTime.MaxValue.Ticks)
                        {
                            Prompt.Notify($"{this.data_stash}: Closest merge time is beyond DateTime.MaxValue: {next_merge_time_double}");
                            this.wh_recompute.Wait(svc_stop_token);
                            this.wh_recompute.Reset();
                            continue;
                        }

                        var id_merge = file_ids[next_merge_ind];
                        var id_keep = file_ids[next_merge_ind - 1];

                        var next_merge_dt = new DateTime((Int64)next_merge_time_double, DateTimeKind.Utc);
                        var now = DateTime.UtcNow;
                        if (next_merge_dt > now)
                        {
                            var wait_time = next_merge_dt - now;
                            Prompt.Notify($"{this.data_stash}: Next consolidation is planned to merge {id_merge} => {id_keep} at {next_merge_dt} (in {wait_time})");
                            this.wh_recompute.Wait(wait_time, svc_stop_token);
                            this.wh_recompute.Reset();
                            continue;
                        }

                        Prompt.Notify($"{this.data_stash}: Consolidating {id_merge} => {id_keep}");
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

                            Prompt.Notify($"{this.data_stash}: Validating after consolidating {id_merge} => {id_keep}");
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

                            Prompt.Notify($"{this.data_stash}: Cleanup after merging {id_merge} => {id_keep}");
                            var final_file_path = Path.Combine(this.data_stash.root_dir.FullName, $"{id_keep}{file_ext}");
                            File.Move(merge_file_path, final_file_path, overwrite: true);
                            merge_dir.Delete(recursive: false);
                        }, with_priority: false);
                        Prompt.Notify($"{this.data_stash}: Done consolidating {id_merge} => {id_keep}");
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
