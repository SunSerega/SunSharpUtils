using System;
using System.IO;
using System.Linq;

using SunSharpUtils.Ext.Linq;
using SunSharpUtils.WinSvc;

namespace SunSharpUtils.SimpleBackup;

/// <summary>
/// </summary>
public sealed class BackupItem
{
    private readonly BackupContainer container;
    private readonly String target_dir;
    private readonly BackupContainer.BackupItemConfig config;

    private const String BackupDirNameFormat = "yyyy-MM-dd HH-mm-ss";

    internal BackupItem(BackupContainer container, String target_dir, BackupContainer.BackupItemConfig config)
    {
        this.container = container;
        this.target_dir = target_dir;
        this.config = config;
        Directory.CreateDirectory(target_dir);
    }

    internal void BackupOnce()
    {
        var existing_backup_dirs = Directory.EnumerateDirectories(this.target_dir).OrderByDescending(dir_path =>
        {
            if (!Err.TryCatch(() => DateTime.ParseExact(Path.GetFileName(dir_path), BackupDirNameFormat, provider: null), out var dt_utc, out var ex))
                WinSvcCommon.HandleCriticalError(ex, when_doing: $"parsing backup directory name '{dir_path}'");
            return dt_utc;
        }).ToArray();
        foreach (var old_dir_path in existing_backup_dirs.Skip(this.config.MaxBackupsToKeep))
            Directory.Delete(old_dir_path, recursive: true);

        var new_dt_utc = DateTime.UtcNow;
        var new_dir_name = new_dt_utc.ToString(BackupDirNameFormat);
        var new_dir_path = Path.Combine(this.target_dir, new_dir_name);

        if (Directory.Exists(new_dir_path))
            throw new InvalidOperationException($"Backup directory already exists: '{new_dir_path}'");
        Directory.CreateDirectory(new_dir_path);

        this.config.ExecuteReadLocked.Invoke(() =>
        {
            if (File.Exists(this.config.Path))
            {
                File.Copy(this.config.Path, Path.Combine(new_dir_path, Path.GetFileName(this.config.Path)), overwrite: false);
            }
            else if (Directory.Exists(this.config.Path))
            {
                foreach (var subdir_path in Directory.EnumerateDirectories(this.config.Path, "*", SearchOption.AllDirectories))
                {
                    var subdir_rel_path = Path.GetRelativePath(this.config.Path, subdir_path);
                    var new_subdir_path = Path.Combine(new_dir_path, subdir_rel_path);
                    Directory.CreateDirectory(new_subdir_path);
                }
                foreach (var file_path in Directory.EnumerateFiles(this.config.Path, "*", SearchOption.AllDirectories))
                {
                    var file_rel_path = Path.GetRelativePath(this.config.Path, file_path);
                    var new_file_path = Path.Combine(new_dir_path, file_rel_path);
                    File.Copy(file_path, new_file_path, overwrite: false);
                }
            }
            else
            {
                Prompt.Notify($"Backup source does not exist: '{this.config.Path}'");
            }
        });
    }

    /// <summary>
    /// </summary>
    public void RequestBackup() =>
        this.container.RequestBackup(this, this.config.BackupDelay);

}
