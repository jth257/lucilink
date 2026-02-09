using Velopack;
using Velopack.Sources;

namespace LuciLink.Client;

/// <summary>
/// Velopack 기반 자동 업데이트 서비스.
/// GitHub Releases 또는 자체 서버에서 업데이트 확인.
/// </summary>
public class UpdateService
{
    // 업데이트 확인 URL (GitHub Releases 또는 자체 서버)
    // GitHub: https://github.com/{owner}/{repo}/releases
    // 자체 서버: https://updates.lucitella.com/lucilink
    private const string UpdateUrl = "https://github.com/jth257/lucilink/releases";

    private UpdateManager? _manager;

    /// <summary>업데이트 확인</summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(UpdateUrl, null, false));
            
            if (!_manager.IsInstalled)
            {
                // 개발 중이거나 직접 실행 시 업데이트 건너뜀
                return null;
            }

            var updateInfo = await _manager.CheckForUpdatesAsync();
            return updateInfo;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>업데이트 다운로드 및 적용</summary>
    public async Task<bool> DownloadAndApplyAsync(UpdateInfo updateInfo, Action<int>? progressCallback = null)
    {
        if (_manager == null) return false;

        try
        {
            await _manager.DownloadUpdatesAsync(updateInfo, progress => progressCallback?.Invoke(progress));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>앱 재시작하여 업데이트 적용</summary>
    public void ApplyAndRestart(UpdateInfo updateInfo)
    {
        _manager?.ApplyUpdatesAndRestart(updateInfo);
    }

    /// <summary>현재 앱 버전</summary>
    public string? GetCurrentVersion()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(UpdateUrl, null, false));
            return manager.IsInstalled ? manager.CurrentVersion?.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}
