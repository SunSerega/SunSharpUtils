using System;
using System.Collections.Generic;
using System.IO;

using SunSharpUtils.Threading;

namespace SunSharpUtils.SimpleBackup;

/// <summary>
/// </summary>
public sealed class BackupContainer
{
    private readonly String container_dir_path;
    private readonly List<BackupItem> items = new();
    private readonly DelayedMultiUpdater<BackupItem> item_updater;

    /// <summary>
    /// </summary>
    public BackupContainer(String container_dir_path)
    {
        this.container_dir_path = container_dir_path;
        this.item_updater = new(new()
        {
            Update = item => item.BackupOnce(),
            Description = $"{this} item updates",
            IsBackground = false,
        });
        Directory.CreateDirectory(container_dir_path);
    }

    /// <summary>
    /// </summary>
    public readonly struct BackupItemConfig()
    {
        /// <summary>
        /// Used for the subdirectory name of the backup item in the container directory
        /// </summary>
        public required String Name { get; init; }
        /// <summary>
        /// Source path to backup (file or directory)
        /// </summary>
        public required String Path { get; init; }
        /// <summary>
        /// Delay between requesting a backup and executing it
        /// <para/>
        /// Extra requests during the delay will be ignored
        /// </summary>
        public required TimeSpan BackupDelay { get; init; }
        /// <summary>
        /// </summary>
        public required Int32 MaxBackupsToKeep { get; init; }
        /// <summary>
        /// All file read operations on the source path will be done inside this action
        /// </summary>
        public required Action<Action> ExecuteReadLocked { get; init; }
    }

    /// <summary>
    /// </summary>
    public BackupItem AddItem(BackupItemConfig config)
    {
        var item = new BackupItem(this, Path.Combine(this.container_dir_path, config.Name), config);
        this.items.Add(item);
        return item;
    }

    internal void RequestBackup(BackupItem item, TimeSpan delay) =>
        this.item_updater.TriggerUrgent(item, delay);

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(BackupContainer)}[{this.container_dir_path}]({this.items.Count} items)";

}
