# 应用代理管理器 1.4.4

Windows 10/11 x64 按应用代理工具。C#、WPF、.NET 8、WPF-UI，网络内核为未修改的官方 sing-box 1.14.3。普通模式同一时间运行一个实例，TUN 需要管理员权限。

完整用法及能力边界见 [使用说明.md](使用说明.md)。

## 开发与发布

需要 .NET 8 SDK、PowerShell 7，以及 Inno Setup 6（发布安装器时）。

```powershell
# 校验/获取锁定版本内核与对应源代码
pwsh -File tools/prepare-engine.ps1

dotnet restore tests/ProxyManager.Tests/ProxyManager.Tests.csproj
dotnet build ProxyManager.sln -c Release -p:MSBuildEnableWorkloadResolver=false

# 隔离回归：本地模拟 API/代理、进程与 Job，不启用真实 TUN
dotnet run --project tests/ProxyManager.Tests/ProxyManager.Tests.csproj -c Release -- --safe-checks

# 在明确允许临时改变路由/DNS的测试环境中，单独执行真实 TUN 验收
dotnet run --project tests/ProxyManager.Tests/ProxyManager.Tests.csproj -c Release -- --network-check

# 环境没有 IPv6 公网出口时，可单独验收 IPv4；结果会明确标注 IPv6 未测
dotnet run --project tests/ProxyManager.Tests/ProxyManager.Tests.csproj -c Release -- --network-check-ipv4

# 测试通过后生成便携 ZIP 与注册安装 EXE
pwsh -File tools/release.ps1

# 在管理员 PowerShell 7 中验收最终包，不开启 TUN
pwsh -File tools/verify-release.ps1
```

输出在 release/，中间产物在 artifacts/。发布脚本执行隔离回归、检查命令退出码，生成内容清单、第三方许可证、对应源码和 SHA256SUMS.txt。真实网络测试独立执行，不会被普通构建或发布隐式触发。

真实网络验收会检查普通及增强 DNS 模式的按应用分流、未选中程序的 IPv4/IPv6 可用直连、停止后的网卡/DNS恢复，并经本地认证代理验证真实 TLS 与 HTTPS。需在管理员 PowerShell 中显式运行，完整验收需要本机 IPv6 可用，且微软联网检测及 HTTPS 站点可达。完整的路由快照还应在验收前后单独核对。

## 实现与维护

- 配置：UI 草稿与运行快照分离，原子持久化、备份恢复，DPAPI 凭据。关闭窗口默认驻留托盘（v2 配置一次性迁移），首次驻留有气泡提示退出入口；启动时按配置重新登记升级后丢失的自启动计划任务。
- 引擎：候选校验、串行切换、失败回滚、实例身份校验、API Bearer 认证、限时执行；切换进行中到达的配置保存会在切换完成后自动补跑，不会静默丢失。启动就绪除 API 探测外还包括确认内核进程识别已生效（sing-box 启动初期约十秒内查不到进程归属，不等待会导致选中应用短暂直连）。挂起启动并先绑定 kill-on-close Job，避免先接管网络后绑定清理策略的空窗。fake-IP 直连按真实域名的可用地址解析，支持虚拟 IPv6 到真实 IPv4 的连接；每个地址族查询限时 3 秒，代理规则仍由上游解析域名。
- 界面：页面负责自身滚动，导航不再添加无限高度滚动容器；规则和日志虚拟化，图标后台读取并在 UI 线程更新；主题资源统一。
- 便携：portable.marker + 同目录 Data。安装：LocalAppData；旧 Roaming 配置首次迁移且保留原件。
- 自启动：当前用户登录任务，只删除本安装路径拥有的任务；安装/更新不按进程名称全局终止内核。
- 日志：后台限量写入、轮转和常见凭据脱敏。

回归程序只使用项目内专用目录。`--isolated --data-dir <绝对路径> --no-auto-start` 可运行独立 UI 验收实例，禁止修改系统自启动。此入口不会自动开启分流。

.NET 8 的维护期限为 2026-11-10；后续版本应升级受支持的 LTS，并随自包含发布包更新运行时。源码依赖以 packages.lock.json 固定，网络内核由 tools/engine.lock.json 固定。
