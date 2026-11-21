using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WindowsIntelligence;

internal sealed class QqDetectionSettings
{
    public bool FilesEnabled { get; set; }
    public bool NotificationsEnabled { get; set; }
    public bool AllowSpeakerRead { get; set; }
    public string DownloadDirectory { get; set; } = "";
}
internal sealed record QqActivity(string Kind, string Text, string Path, DateTimeOffset ReceivedAt);

internal sealed class QqDetection : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<QqActivity> recent = new();
    private readonly Dictionary<string, (long Bytes, DateTime Stamp, DateTime Seen)> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> notifications = [];
    private readonly System.Threading.Timer debounce;
    private FileSystemWatcher? watcher;
    private string root = "";
    private bool disposed;
    internal string FileStatus { get; private set; } = "未启用文件检测";
    internal string NotificationStatus { get; private set; } = "未启用 QQ 通知检测";
    internal void ReportFileError(string message) => FileStatus = message;
    internal QqDetection() => debounce = new(_ => DrainFiles(), null, Timeout.Infinite, Timeout.Infinite);
    internal void ConfigureFiles(QqDetectionSettings settings)
    {
        lock (gate)
        {
            watcher?.Dispose(); watcher = null; pending.Clear();
            debounce.Change(Timeout.Infinite, Timeout.Infinite);
            if (!settings.FilesEnabled) { FileStatus = "未启用文件检测"; return; }
            root = System.IO.Path.GetFullPath(settings.DownloadDirectory);
            if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new UserError("请选择实际存在的 QQ 下载目录，不能是目录链接。");
            watcher = new(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            watcher.Created += Changed; watcher.Changed += Changed;
            watcher.Renamed += (_, args) => QueueFile(args.FullPath);
            watcher.Error += (_, _) => FileStatus = "文件变更过多或监听中断，请重新启用；不保证事件完整";
            watcher.EnableRaisingEvents = true; FileStatus = "正在检测所选目录的文件变化";
        }
    }
    private void Changed(object sender, FileSystemEventArgs args) => QueueFile(args.FullPath);
    private void QueueFile(string path)
    {
        lock (gate)
        {
            if (disposed || pending.Count >= 256 ||
                new[] { ".tmp", ".temp", ".part", ".crdownload", ".db", ".db-wal", ".db-shm" }.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                if (!WithinRoot(path)) return;
                var file = new FileInfo(path);
                if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) return;
                pending[path] = (file.Length, file.LastWriteTimeUtc, DateTime.UtcNow);
                debounce.Change(500, 500);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private bool WithinRoot(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        for (var parent = Directory.GetParent(full); parent is not null && parent.FullName.Length >= root.Length; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private void DrainFiles()
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var pair in pending.ToArray())
            {
                try
                {
                    var file = new FileInfo(pair.Key);
                    if (!file.Exists || !WithinRoot(file.FullName)) { pending.Remove(pair.Key); continue; }
                    if (DateTime.UtcNow - pair.Value.Seen < TimeSpan.FromSeconds(1)) continue;
                    if (file.Length != pair.Value.Bytes || file.LastWriteTimeUtc != pair.Value.Stamp)
                    { pending[pair.Key] = (file.Length, file.LastWriteTimeUtc, DateTime.UtcNow); continue; }
                    using var probe = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Add(new("file", $"{file.Name}，{file.Length} 字节；文件当前稳定可读取，未确认发送人及下载完整性", file.FullName, DateTimeOffset.Now));
                    pending.Remove(pair.Key);
                }
                catch (IOException) { if (DateTime.UtcNow - pair.Value.Seen > TimeSpan.FromMinutes(1)) pending.Remove(pair.Key); }
                catch (UnauthorizedAccessException) { pending.Remove(pair.Key); }
            }
            if (pending.Count == 0) debounce.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }
    internal async Task<string> RequestNotificationAccessAsync()
    {
        try { NotificationStatus = (await UserNotificationListener.Current.RequestAccessAsync()).ToString(); }
        catch (Exception) { NotificationStatus = "系统未提供通知访问；请使用已安装版本并由本人授权"; }
        return NotificationStatus;
    }
    internal async Task ReadNotificationsAsync()
    {
        try
        {
            var listener = UserNotificationListener.Current;
            if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            { NotificationStatus = "尚未获得 Windows 通知访问权限"; return; }
            foreach (var notification in await listener.GetNotificationsAsync(NotificationKinds.Toast))
            {
                string name = notification.AppInfo?.DisplayInfo?.DisplayName ?? "";
                if (!name.Equals("QQ", StringComparison.OrdinalIgnoreCase)) continue;
                lock (gate)
                {
                    if (disposed) return;
                    if (!notifications.Add(notification.Id)) continue;
                    if (notifications.Count > 512) { notifications.Clear(); notifications.Add(notification.Id); }
                    string text = string.Join(" · ", notification.Notification.Visual.Bindings.SelectMany(b => b.GetTextElements()).Select(t => t.Text));
                    if (text.Length > 1500) text = text[..1500];
                    Add(new("notification", text, "", notification.CreationTime));
                }
            }
            NotificationStatus = "已读取 QQ 的系统通知；不包含完整聊天历史";
        }
        catch (Exception) { NotificationStatus = "QQ 通知暂不可用；不会改读聊天数据库"; }
    }
    private void Add(QqActivity value) { recent.Enqueue(value); while (recent.Count > 100) recent.Dequeue(); }
    internal IReadOnlyList<QqActivity> Snapshot() { lock (gate) return recent.ToArray(); }
    internal string Describe(string query)
    {
        bool files = query.Contains("文件");
        var events = Snapshot().Where(e => e.Kind == (files ? "file" : "notification")).TakeLast(5).ToArray();
        return events.Length == 0 ? files ? "当前没有检测到新的 QQ 文件变化。" : "当前没有可读取的 QQ 系统通知；不能据此判断没有新消息。" :
            string.Join("。", events.Select(e => $"{e.ReceivedAt:HH:mm}，{e.Text}"));
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; watcher?.Dispose(); debounce.Dispose(); pending.Clear(); recent.Clear(); notifications.Clear(); }
    }
}
