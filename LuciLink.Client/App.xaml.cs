using System.Windows;
using Velopack;

namespace LuciLink.Client;

/// <summary>
/// Interaction logic for App.xaml
/// Velopack 자동 업데이트 초기화 포함
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Velopack 설치/업데이트 훅 처리 (설치/제거 시 자동 호출)
        VelopackApp.Build().Run();
    }
}
