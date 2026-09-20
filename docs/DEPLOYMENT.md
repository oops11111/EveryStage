# EveryStage Windows 部署

## 发布包

在仓库根目录运行：

```powershell
pwsh ./scripts/Build-ReleasePackages.ps1
```

输出位于 `artifacts/release`，包含 Terminal/Caster 的 win-x64 ZIP、部署脚本、`manifest.json`，以及可直接
交给部署系统的 `EveryStage-DeploymentBundle-win-x64.zip` 完整合集。
发布脚本会强制检查 `EveryStage-Terminal-win-x64/x64/pdfium.dll`，缺失时直接失败；清单同时记录
Pdfium SHA-256，便于部署后核验。包为 framework-dependent，需要目标机安装 .NET 8 Windows Desktop Runtime x64。

## WPS 前置条件

Terminal 需要安装并授权与试点验收版本一致的 WPS。自动部署不会购买、激活、复制或绕过 WPS License。
部署负责人必须：

1. 与 WPS 官方或授权代理确认每台终端的授权方式和数量；
2. 锁定试点验证通过的 WPS 版本并关闭自动升级；
3. 验证 `KWPS.Application`、`KET.Application`、`KWPP.Application` 三个 COM ProgID；
4. 完成授权后，部署 Terminal 时显式传入 `-WpsLicenseConfirmed`。

缺少 COM 组件或未确认 License 时，安装脚本会停止，不会留下“已安装但文档功能不可用”的机器。

## 单机与批量部署

解压发布目录后，在管理员 PowerShell 中执行：

```powershell
./deployment/Install-EveryStage.ps1 -Component Terminal -WpsLicenseConfirmed
./deployment/Install-EveryStage.ps1 -Component Caster
```

默认安装到 `%ProgramFiles%\EveryStage\Terminal|Caster` 并写入机器级开机启动。使用 `-NoAutoStart`
可禁用开机启动。批量部署系统（Intune、SCCM、域启动脚本等）应使用同一命令，并以退出码判断结果；
不要把 `-WpsLicenseConfirmed` 当作技术探测，它代表部署负责人已经完成商业授权确认。

卸载：

```powershell
./deployment/Uninstall-EveryStage.ps1 -Component Terminal
```

卸载保留 `%ProgramData%\EveryStage` 的配置、内容索引和日志，便于审计与重装恢复；需要清理这些数据时
应由运维单独审批并明确删除范围。
